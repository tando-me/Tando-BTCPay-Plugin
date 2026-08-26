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
[Authorize(
    Policy = Policies.CanModifyStoreSettingsUnscoped,
    AuthenticationSchemes = AuthenticationSchemes.Greenfield
)]
[IgnoreAntiforgeryToken]
public class TandoOnboardingController(
    StoreRepository storeRepository,
    TandoSubscriptionService subscriptionService,
    TandoProductProvisioningService productProvisioningService,
    DarajaMobileNumberValidationService darajaValidation
) : Controller
{
    private const string PreferredRateSource = "bitcoinkenya";
    private const string DefaultCurrency = "KES";
    private const string PhoneMetadataKey = "tandoPhoneNumber";
    private const string PlanMetadataKey = "tandoSubscriptionPlanId";

    // Safaricom-allocated prefixes (Communications Authority of Kenya numbering plan).
    // Excludes Airtel (071x, 073x, 075x, 078x) and Telkom (077x).
    // Daraja KYC then confirms the number is registered under the supplied ID.
    private static readonly Regex SafaricomMsisdn = new(
        @"^(?:\+254|254|0)(7(?:0\d|2\d|4\d|5[7-9]|6[89]|9\d)\d{6}|11[0-5]\d{6})$",
        RegexOptions.Compiled
    );

    [HttpGet("daraja/status")]
    public async Task<IActionResult> DarajaStatus()
    {
        var settings = await darajaValidation.GetSettings();
        return Ok(
            new
            {
                configured = settings.IsConfigured(),
                sandbox = settings.UseSandbox,
                shortCode = settings.IsConfigured() ? settings.ShortCode : null,
                consumerKeySet = !string.IsNullOrWhiteSpace(settings.ConsumerKey),
                consumerSecretSet = !string.IsNullOrWhiteSpace(settings.ConsumerSecret),
            }
        );
    }

    [HttpGet("subscription/status")]
    public async Task<IActionResult> SubscriptionStatus([FromQuery] string phoneNumber)
    {
        var normalizedPhone = NormalizePhone(phoneNumber, out var error);
        if (normalizedPhone is null)
            return error!;

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

    /// Step 1: Validate phone + ID via Daraja KYC, then return subscription plans
    /// (or go straight to store creation if the merchant already has an active subscription).
    [HttpPost("signup")]
    public async Task<IActionResult> Signup(
        [FromBody] TandoSignupRequest request,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request?.PhoneNumber))
            return BadRequest(
                new
                {
                    error = "phone_number_required",
                    message = "A Safaricom phone number is required to sign up.",
                }
            );

        if (string.IsNullOrWhiteSpace(request.IdNumber))
            return BadRequest(
                new
                {
                    error = "id_number_required",
                    message = "Your National ID or Passport number is required for mobile number verification.",
                }
            );

        var normalizedPhone = NormalizePhone(request.PhoneNumber, out var error);
        if (normalizedPhone is null)
            return error!;

        // KYC runs first — an invalid phone/ID pair is rejected before anything else.
        var (phoneNumberVerified, kycError) = await ValidateKyc(
            normalizedPhone,
            request.IdNumber,
            request.IdType
        );
        if (kycError is not null)
            return kycError;

        var status = await subscriptionService.GetStatus(normalizedPhone);

        if (!status.Configured)
            return StatusCode(
                503,
                new
                {
                    error = "subscription_not_configured",
                    message = "Tando is not yet available for sign-up. Please try again later.",
                }
            );

        if (status.Active)
            return await CreateOrReturnStore(
                normalizedPhone,
                status,
                phoneNumberVerified,
                cancellationToken
            );

        var plans = await subscriptionService.GetAvailablePlans();
        return Ok(
            new
            {
                action = "select_plan",
                phoneNumber = normalizedPhone,
                phoneNumberVerified,
                plans,
            }
        );
    }

    /// Step 2: Merchant has chosen a plan. KYC is re-validated, a trial subscription is created,then the store is created.
    [HttpPost("signup/subscribe")]
    public async Task<IActionResult> Subscribe(
        [FromBody] TandoSubscribeRequest request,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(request?.PhoneNumber))
            return BadRequest(
                new
                {
                    error = "phone_number_required",
                    message = "A Safaricom phone number is required to sign up.",
                }
            );

        if (string.IsNullOrWhiteSpace(request.IdNumber))
            return BadRequest(
                new
                {
                    error = "id_number_required",
                    message = "Your National ID or Passport number is required for mobile number verification.",
                }
            );

        var normalizedPhone = NormalizePhone(request.PhoneNumber, out var error);
        if (normalizedPhone is null)
            return error!;

        // KYC runs before creating the subscription so an invalid ID never gets a store.
        var (phoneNumberVerified, kycError) = await ValidateKyc(
            normalizedPhone,
            request.IdNumber,
            request.IdType
        );
        if (kycError is not null)
            return kycError;

        var status = await subscriptionService.GetStatus(normalizedPhone);

        if (!status.Configured)
            return StatusCode(
                503,
                new
                {
                    error = "subscription_not_configured",
                    message = "Tando is not yet available for sign-up. Please try again later.",
                }
            );

        if (status.Active)
            return await CreateOrReturnStore(
                normalizedPhone,
                status,
                phoneNumberVerified,
                cancellationToken
            );

        try
        {
            status = await subscriptionService.CreateFreeTrialSubscriber(
                normalizedPhone,
                Request.GetRequestBaseUrl(),
                cancellationToken
            );
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(503, new { error = "subscription_failed", message = ex.Message });
        }

        if (!status.Active)
            return StatusCode(500, new { error = "subscriber_creation_failed" });

        return await CreateOrReturnStore(
            normalizedPhone,
            status,
            phoneNumberVerified,
            cancellationToken
        );
    }

    /// Runs Daraja Mobile Number Validation KYC.
    /// Returns (true, null) on success, (false, null) when Daraja is not configured (skip),
    /// or (false, errorResult) when the phone/ID pair is rejected or the API is unavailable.
    private async Task<(bool verified, IActionResult? error)> ValidateKyc(
        string normalizedPhone,
        string idNumber,
        string idType
    )
    {
        var idTypeTrimmed = string.IsNullOrWhiteSpace(idType) ? "01" : idType.Trim();
        if (idTypeTrimmed is not ("01" or "02" or "05"))
            return (
                false,
                BadRequest(
                    new
                    {
                        error = "invalid_id_type",
                        message = "id_type must be 01 (National ID), 02 (Military ID), or 05 (Passport).",
                    }
                )
            );

        var settings = await darajaValidation.GetSettings();
        if (!settings.IsConfigured())
            return (false, StatusCode(503, new { error = "kyc_not_configured", message = "Phone number verification is not set up yet. The server admin must configure Daraja credentials before merchants can sign up." }));

        var validation = await darajaValidation.ValidateMobileNumber(
            normalizedPhone,
            idTypeTrimmed,
            idNumber.Trim()
        );

        if (validation.ServiceError)
            return (false, StatusCode(503, new { error = "phone_validation_unavailable", message = "Could not verify your phone number with Safaricom. Please try again later." }));

        if (!validation.Matches)
            return (
                false,
                BadRequest(
                    new
                    {
                        error = "phone_id_mismatch",
                        message = "The phone number is not registered under the provided ID. Please check your details and try again.",
                    }
                )
            );

        return (true, null);
    }

    private async Task<IActionResult> CreateOrReturnStore(
        string normalizedPhone,
        TandoSubscriptionStatus status,
        bool phoneNumberVerified,
        CancellationToken cancellationToken
    )
    {
        var callerId = User.GetId();
        var userStore = await storeRepository.GetStoresByUserId(callerId);
        var existingStore = userStore.FirstOrDefault(s => s.StoreName == normalizedPhone);
        if (existingStore is not null)
        {
            await RefreshPlanMetadata(existingStore, status.PlanId);
            var (posAppId, cartAppId) = await productProvisioningService.ProvisionDefaultApps(
                existingStore
            );
            return Ok(
                new TandoSignupResponse
                {
                    StoreId = existingStore.Id,
                    PhoneNumber = normalizedPhone,
                    AlreadyExisted = true,
                    PosAppId = posAppId,
                    CartAppId = cartAppId,
                    PhoneNumberVerified = phoneNumberVerified,
                }
            );
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

        var (newPosAppId, newCartAppId) = await productProvisioningService.ProvisionDefaultApps(
            store
        );
        return Ok(
            new TandoSignupResponse
            {
                StoreId = store.Id,
                PhoneNumber = normalizedPhone,
                AlreadyExisted = false,
                PosAppId = newPosAppId,
                CartAppId = newCartAppId,
                PhoneNumberVerified = phoneNumberVerified,
            }
        );
    }

    private async Task RefreshPlanMetadata(StoreData store, string? currentPlanId)
    {
        var blob = store.GetStoreBlob();
        if (blob.AdditionalData[PlanMetadataKey]?.ToString() == currentPlanId)
            return;

        blob.AdditionalData[PlanMetadataKey] = currentPlanId;
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);
    }

    private string? NormalizePhone(string phoneNumber, out IActionResult? error)
    {
        var match = SafaricomMsisdn.Match((phoneNumber ?? string.Empty).Trim());
        if (!match.Success)
        {
            error = BadRequest(
                new
                {
                    error = "invalid_phone_number",
                    message = "Expected a Safaricom number (e.g. 0724xxxxxx). Airtel and Telkom numbers are not supported.",
                }
            );
            return null;
        }
        error = null;
        return "254" + match.Groups[1].Value;
    }

    [HttpPut("stores/{storeId}/lightning/connect")]
    public async Task<IActionResult> ConnectLightning(
        string storeId,
        [FromBody] TandoConnectLightningRequest request
    )
    {
        if (string.IsNullOrWhiteSpace(request?.ConnectionString))
            return BadRequest(new { error = "connection_string_required" });

        var store = await storeRepository.FindStore(storeId);
        if (store is null)
            return NotFound(new { error = "store_not_found" });

        var paymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC");
        var config = new LightningPaymentMethodConfig
        {
            ConnectionString = request.ConnectionString,
        };
        store.SetPaymentMethodConfig(paymentMethodId, JToken.FromObject(config));
        var blob = store.GetStoreBlob();
        blob.SetExcluded(paymentMethodId, false);
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);
        return Ok(new { storeId, paymentMethodId = paymentMethodId.ToString() });
    }
}