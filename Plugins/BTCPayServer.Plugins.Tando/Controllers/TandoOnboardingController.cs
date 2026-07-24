using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
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
public class TandoOnboardingController(StoreRepository storeRepository, TandoSubscriptionService subscriptionService) : Controller
{
    private const string PreferredRateSource = "bitcoinkenya";
    private const string DefaultCurrency = "KES";
    private const string PhoneMetadataKey = "tandoPhoneNumber";
    private static readonly Regex KenyanMsisdn = new(@"^(?:\+254|0)([17]\d{8})$", RegexOptions.Compiled);

    [HttpGet("subscription/status")]
    public async Task<IActionResult> SubscriptionStatus([FromQuery] string phoneNumber)
    {
        var normalizedPhone = NormalizePhone(phoneNumber, out var error);
        if (normalizedPhone is null) return error!;

        var status = await subscriptionService.GetStatus(normalizedPhone);
        return Ok(status);
    }

    [HttpPost("signup")]
    public async Task<IActionResult> Signup([FromBody] TandoSignupRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.PhoneNumber))
            return BadRequest(new { error = "phone_number_required" });

        var normalizedPhone = NormalizePhone(request.PhoneNumber, out var error);
        if (normalizedPhone is null) return error!;

        var status = await subscriptionService.GetStatus(normalizedPhone);
        if (!status.Configured)
            return StatusCode(503, new { error = "subscription_not_configured" });

        if (!status.Active)
            return StatusCode(402, new { error = "subscription_inactive", phase = status.Phase });

        var callerId = User.GetId();
        var userStore = await storeRepository.GetStoresByUserId(callerId);
        var existingStore = userStore.FirstOrDefault(s => s.StoreName == normalizedPhone);
        if (existingStore is not null)
        {
            return Ok(new TandoSignupResponse
            {
                StoreId = existingStore.Id,
                PhoneNumber = normalizedPhone,
                AlreadyExisted = true
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
        store.SetStoreBlob(blob);
        var result = await storeRepository.CreateStore(callerId, store);
        if (result != StoreRepository.CreateStoreResult.Created)
            return BadRequest(new { error = "store_creation_failed", detail = result.ToString() });

        return Ok(new TandoSignupResponse
        {
            StoreId = store.Id,
            PhoneNumber = normalizedPhone,
            AlreadyExisted = false
        });
    }

    [HttpPut("stores/{storeId}/lightning/connect")]
    public async Task<IActionResult> ConnectLightning(string storeId, [FromBody] TandoConnectLightningRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.ConnectionString))
            return BadRequest(new { error = "connection_string_required" });

        var store = await storeRepository.FindStore(storeId);
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
}
