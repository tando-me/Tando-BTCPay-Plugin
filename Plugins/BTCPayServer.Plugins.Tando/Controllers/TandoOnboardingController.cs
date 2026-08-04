using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.MassStoreGenerator;

[Route("~/plugins/api/tando/")]
[Authorize(Policy = Policies.CanModifyStoreSettingsUnscoped, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[IgnoreAntiforgeryToken]
public class TandoOnboardingController(StoreRepository storeRepository, TandoSubscriptionService subscriptionService, 
    TandoProductProvisioningService productProvisioningService) : Controller
{
    private const string PreferredRateSource = "bitcoinkenya";
    private const string DefaultCurrency = "KES";
    private const string PhoneMetadataKey = "tandoPhoneNumber";
    private const string PlanMetadataKey = "tandoSubscriptionPlanId";

    private static readonly Regex KenyanMsisdn = new(@"^(?:\+254|0)([17]\d{8})$", RegexOptions.Compiled);

    [HttpGet("subscription/status")]
    public async Task<IActionResult> SubscriptionStatus([FromQuery] string phoneNumber)
    {
        var normalizedPhone = NormalizePhone(phoneNumber, out var error);
        if (normalizedPhone is null) return error!;

        var status = await subscriptionService.GetStatus(normalizedPhone);
        return Ok(status);
    }

    [HttpGet("subscription/plans")]
    public async Task<IActionResult> SubscriptionPlans()
    {
        var plans = await subscriptionService.GetAvailablePlans();
        if (plans is null)
            return Ok(new { configured = false, plans = Array.Empty<TandoPlan>() });

        return Ok(new { configured = true, plans });
    }

    [HttpPost("signup")]
    public async Task<IActionResult> Signup([FromBody] TandoSignupRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.PhoneNumber))
            return BadRequest(new { error = "phone_number_required" });

        var normalizedPhone = NormalizePhone(request.PhoneNumber, out var error);
        if (normalizedPhone is null) return error!;

        var status = await subscriptionService.GetStatus(normalizedPhone);
        if (!status.Configured)
        {
            return StatusCode(503, new
            {
                error = "subscription_not_configured",
                message = "Subscriptions aren't set up yet. Please contact the Tando team before trying to sign up."
            });
        }
        if (!status.Active)
        {
            try
            {
                status = await subscriptionService.CreateFreeTrialSubscriber(normalizedPhone, Request.GetRequestBaseUrl(), cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return StatusCode(503, new { error = "subscription_not_configured", message = ex.Message });
            }

            if (!status.Active)
                return StatusCode(500, new { error = "subscriber_creation_failed" });
        }
        var callerId = User.GetId();
        var userStore = await storeRepository.GetStoresByUserId(callerId);
        var existingStore = userStore.FirstOrDefault(s => s.StoreName == normalizedPhone);
        if (existingStore is not null)
        {
            await RefreshPlanMetadata(existingStore, status.PlanId);
            var (posAppId, cartAppId) = await productProvisioningService.ProvisionDefaultApps(existingStore);
            return Ok(new TandoSignupResponse
            {
                StoreId = existingStore.Id,
                PhoneNumber = normalizedPhone,
                AlreadyExisted = true,
                PosAppId = posAppId,
                CartAppId = cartAppId
            });
        }
        var store = await storeRepository.GetDefaultStoreTemplate();
        store.StoreName = normalizedPhone;
        var blob = store.GetStoreBlob();
        blob.DefaultCurrency = DefaultCurrency;
        var rate = blob.GetOrCreateRateSettings(false);
        rate.PreferredExchange = PreferredRateSource;
        rate.RateScripting = false;
        blob.AdditionalData[PhoneMetadataKey] = normalizedPhone;
        blob.AdditionalData[PlanMetadataKey] = status.PlanId;
        store.SetStoreBlob(blob);
        var result = await storeRepository.CreateStore(callerId, store);
        if (result != StoreRepository.CreateStoreResult.Created)
            return BadRequest(new { error = "store_creation_failed", detail = result.ToString() });

        var (newPosAppId, newCartAppId) = await productProvisioningService.ProvisionDefaultApps(store);
        return Ok(new TandoSignupResponse
        {
            StoreId = store.Id,
            PhoneNumber = normalizedPhone,
            AlreadyExisted = false,
            PosAppId = newPosAppId,
            CartAppId = newCartAppId
        });
    }

    private async Task RefreshPlanMetadata(StoreData store, string? currentPlanId)
    {
        var blob = store.GetStoreBlob();
        if (blob.AdditionalData[PlanMetadataKey]?.ToString() == currentPlanId) return; 

        blob.AdditionalData[PlanMetadataKey] = currentPlanId;
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);
    }

    private string? NormalizePhone(string phoneNumber, out IActionResult? error)
    {
        var match = KenyanMsisdn.Match((phoneNumber ?? string.Empty).Trim());
        if (!match.Success)
        {
            error = BadRequest(new { error = "invalid_phone_number", detail = "Expected a Kenyan MSISDN, e.g. 0712345678 or +254712345678." });
            return null;
        }
        error = null;
        return "254" + match.Groups[1].Value;
    }

    [HttpPut("stores/{storeId}/lightning/connect")]
    public async Task<IActionResult> ConnectLightning(string storeId, [FromBody] TandoConnectLightningRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.ConnectionString))
            return BadRequest(new { error = "connection_string_required" });

        var callerId = User.GetId();
        var ownedStores = await storeRepository.GetStoresByUserId(callerId);
        // 404, not 403: don't reveal to a non-owner whether storeId exists at all.
        var store = ownedStores.FirstOrDefault(s => s.Id == storeId);
        if (store is null)
            return NotFound(new { error = "store_not_found" });

        var paymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var config = new LightningPaymentMethodConfig { ConnectionString = request.ConnectionString };
        store.SetPaymentMethodConfig(paymentMethodId, JToken.FromObject(config));
        var blob = store.GetStoreBlob();
        blob.SetExcluded(paymentMethodId, false);
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);
        return Ok(new { storeId, paymentMethodId = paymentMethodId.ToString() });
    }
}