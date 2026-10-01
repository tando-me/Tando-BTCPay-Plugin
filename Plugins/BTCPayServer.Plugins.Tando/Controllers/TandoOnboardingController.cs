using System;
using System.Linq;
using BTCPayServer.Plugins.Tando.Helper;
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
[Authorize(
    Policy = Policies.CanModifyStoreSettingsUnscoped,
    AuthenticationSchemes = AuthenticationSchemes.Greenfield
)]
[IgnoreAntiforgeryToken]
public class TandoOnboardingController(StoreRepository storeRepository, TandoSubscriptionService subscriptionService,
    TandoProductProvisioningService productProvisioningService, TandoLightningProvisionerFactory lightningProvisionerFactor,
    PhoneVerificationService phoneVerification) : Controller
{
    private const string PreferredRateSource = "bitcoinkenya";
    private const string DefaultCurrency = "KES";
    private const string PhoneMetadataKey = "tandoPhoneNumber";
    private const string PlanMetadataKey = "tandoSubscriptionPlanId";


    [HttpGet("subscription/status")]
    public async Task<IActionResult> SubscriptionStatus([FromQuery] string phoneNumber)
    {
        var normalizedPhone = KenyanPhoneNumber.Normalize(phoneNumber);
        if (normalizedPhone == null)
            return BadRequest(new { error = "invalid_phone_number", detail = "Expected a Safaricom MSISDN, e.g. 0712345678 or +254712345678." });

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

    [HttpGet("verification/status")]
    public async Task<IActionResult> VerificationStatus()
    {
        var verifier = phoneVerification.Active;
        return Ok(new
        {
            enabled = verifier is not null,
            mode = verifier?.Name,
            configured = verifier is not null && await verifier.IsConfigured()
        });
    }

    [HttpPost("signup")]
    public async Task<IActionResult> Signup([FromBody] TandoSignupRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.PhoneNumber))
            return BadRequest(new { error = "phone_number_required" });

        var normalizedPhone = KenyanPhoneNumber.Normalize(request.PhoneNumber);
        if (normalizedPhone is null)
            return BadRequest(new { error = "invalid_phone_number", detail = "Expected a Kenyan MSISDN, e.g. 0712345678 or +254712345678." });

        var verification = await phoneVerification.Verify(normalizedPhone, request.IdType, request.IdNumber, cancellationToken);
        if (VerificationError(verification) is { } verificationError)
            return verificationError;

        var phoneNumberVerified = verification == PhoneVerificationOutcome.Verified;
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
                CartAppId = cartAppId,
                PhoneNumberVerified = phoneNumberVerified
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
            CartAppId = newCartAppId,
            PhoneNumberVerified = phoneNumberVerified
        });
    }


    [HttpPut("stores/{storeId}/lightning/connect")]
    public async Task<IActionResult> ConnectLightning(string storeId, [FromBody] TandoConnectLightningRequest request)
    {
        string connectionString;
        if (!string.IsNullOrWhiteSpace(request?.ConnectionString))
        {
            connectionString = request.ConnectionString;
        }
        else if (request?.LightningProvision is not null)
        {
            var provisioner = lightningProvisionerFactor.Get(request.LightningProvision.ProviderType);
            if (provisioner is null)
                return BadRequest(new { error = "unsupported_provider_type" });

            var result = await provisioner.Provision(request.LightningProvision);
            if (!result.IsSuccess)
                return BadRequest(new { error = result.Error });

            connectionString = result.ConnectionString!;
        }
        else
        {
            return BadRequest(new { error = "connection_string_required" });
        }

        var callerId = User.GetId();
        var ownedStores = await storeRepository.GetStoresByUserId(callerId);
        // 404, not 403: don't reveal to a non-owner whether storeId exists at all.
        var store = ownedStores.FirstOrDefault(s => s.Id == storeId);
        if (store is null)
            return NotFound(new { error = "store_not_found" });

        var paymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var config = new LightningPaymentMethodConfig { ConnectionString = connectionString };
        store.SetPaymentMethodConfig(paymentMethodId, JToken.FromObject(config));
        var blob = store.GetStoreBlob();
        blob.SetExcluded(paymentMethodId, false);
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);
        return Ok(new { storeId, paymentMethodId = paymentMethodId.ToString() });
    }

    private IActionResult? VerificationError(PhoneVerificationOutcome outcome) => outcome switch
    {
        PhoneVerificationOutcome.Skipped or PhoneVerificationOutcome.Verified => null,
        PhoneVerificationOutcome.IdNumberRequired => BadRequest(new { error = "id_number_required", detail = "An ID number is required to verify the phone number." }),
        PhoneVerificationOutcome.InvalidIdType => BadRequest(new { error = "invalid_id_type", detail = "Expected 01 (National ID), 02 (Military ID) or 05 (Passport)." }),
        PhoneVerificationOutcome.Mismatch => BadRequest(new { error = "phone_id_mismatch", detail = "The phone number is not registered under this ID." }),
        PhoneVerificationOutcome.NotConfigured => StatusCode(503, new { error = "phone_verification_not_configured", message = "Phone verification isn't set up yet. Please contact the Tando team." }),
        _ => StatusCode(503, new { error = "phone_verification_unavailable", message = "Couldn't verify the phone number right now. Please try again later." })
    };

    private async Task RefreshPlanMetadata(StoreData store, string? currentPlanId)
    {
        var blob = store.GetStoreBlob();
        if (blob.AdditionalData[PlanMetadataKey]?.ToString() == currentPlanId) return;

        blob.AdditionalData[PlanMetadataKey] = currentPlanId;
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);
    }
}
