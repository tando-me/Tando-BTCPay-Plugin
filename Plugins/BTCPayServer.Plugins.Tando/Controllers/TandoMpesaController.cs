using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.MassStoreGenerator;

[Route("~/plugins/api/tando/stores/{storeId}/mpesa")]
[IgnoreAntiforgeryToken]
public class TandoMpesaController(
    StoreRepository storeRepository,
    SplicePspService splicePsp,
    ILogger<TandoMpesaController> logger
) : Controller
{
    private const string MpesaDestinationKey = "tandoMpesaDestination";

    /// Called by the mobile app to initiate an STK Push for a customer payment.
    [HttpPost("pay")]
    [Authorize(
        Policy = Policies.CanModifyStoreSettingsUnscoped,
        AuthenticationSchemes = AuthenticationSchemes.Greenfield
    )]
    public async Task<IActionResult> InitiatePay(
        string storeId,
        [FromBody] MpesaPayRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request is null || string.IsNullOrWhiteSpace(request.CustomerPhone))
            return BadRequest(new { error = "customer_phone_required" });
        if (request.AmountKes <= 0)
            return BadRequest(
                new { error = "invalid_amount", detail = "Amount must be greater than zero." }
            );
        if (string.IsNullOrWhiteSpace(request.OrderId))
            return BadRequest(new { error = "order_id_required" });

        var phoneError = splicePsp.NormalizePhone(request.CustomerPhone, out var normalizedPhone);
        if (phoneError is not null)
            return BadRequest(new { error = "invalid_phone", detail = phoneError });

        var store = await storeRepository.FindStore(storeId);
        if (store is null)
            return NotFound(new { error = "store_not_found" });

        var blob = store.GetStoreBlob();
        var merchantDestination = blob.AdditionalData[MpesaDestinationKey]?.Value<string>();
        if (string.IsNullOrWhiteSpace(merchantDestination))
            return StatusCode(
                503,
                new
                {
                    error = "merchant_mpesa_not_configured",
                    detail = "The merchant has not set up their M-Pesa destination yet.",
                }
            );

        var callbackUrl = Url.Action(
            nameof(SpliceCallback),
            "TandoMpesa",
            new { storeId },
            Request.Scheme
        )!;

        var result = await splicePsp.InitiateStkPush(
            normalizedPhone!,
            merchantDestination,
            request.AmountKes,
            request.OrderId,
            callbackUrl,
            cancellationToken
        );

        if (!result.Success)
            return StatusCode(503, new { error = "stk_push_failed", detail = result.Error });

        return Ok(new { checkoutRequestId = result.CheckoutRequestId });
    }

    /// Saves or updates the merchant's M-Pesa destination identifier on their store.
    [HttpPut("settings")]
    [Authorize(
        Policy = Policies.CanModifyStoreSettingsUnscoped,
        AuthenticationSchemes = AuthenticationSchemes.Greenfield
    )]
    public async Task<IActionResult> SaveMpesaSettings(
        string storeId,
        [FromBody] TandoMpesaSettingsRequest request
    )
    {
        if (string.IsNullOrWhiteSpace(request?.Destination))
            return BadRequest(new { error = "destination_required" });

        var store = await storeRepository.FindStore(storeId);
        if (store is null)
            return NotFound(new { error = "store_not_found" });

        var destination =
            request.DestinationType == TandoMpesaDestinationType.PayBill
            && !string.IsNullOrWhiteSpace(request.AccountNumber)
                ? $"{request.Destination}/{request.AccountNumber}"
                : request.Destination;

        var blob = store.GetStoreBlob();
        blob.AdditionalData[MpesaDestinationKey] = destination;
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);

        return Ok(new { storeId, destination });
    }

    /// Webhook Splice calls when an STK Push completes (approved or declined).
    /// No auth - Splice posts here from their servers.
    [HttpPost("callback")]
    [AllowAnonymous]
    public IActionResult SpliceCallback(string storeId, [FromBody] JObject payload)
    {
        var checkoutRequestId = payload["checkoutRequestId"]?.Value<string>();
        var status = payload["status"]?.Value<string>();
        var amount = payload["amount"]?.Value<decimal?>();

        logger.LogInformation(
            "Splice callback for store {StoreId}: checkoutRequestId={CheckoutRequestId} status={Status} amount={Amount}",
            storeId,
            checkoutRequestId,
            status,
            amount
        );

        // TODO: match checkoutRequestId to a BTCPay invoice and mark it settled or failed.

        return Ok();
    }
}

/// Institution-level Splice webhook endpoints registered during Splice onboarding.
/// These are the URLs you supply in the Splice registration JSON:
///   "identity_endpoint":            ~/plugins/api/tando/splice/identity
///   "recv_payment_notif_endpoint":  ~/plugins/api/tando/splice/webhook/received
///   "send_payment_notif_endpoint":  ~/plugins/api/tando/splice/webhook/sent
[Route("~/plugins/api/tando/splice")]
[IgnoreAntiforgeryToken]
[AllowAnonymous]
public class TandoSpliceWebhookController(ILogger<TandoSpliceWebhookController> logger) : Controller
{
    /// Splice calls this to verify a customer's identity before processing a transaction.
    /// Respond with the customer's details if found, 404 if unknown.
    [HttpGet("identity")]
    public IActionResult Identity([FromQuery] string phone)
    {
        logger.LogInformation("Splice identity query for {Phone}", phone);

        // TODO: look up the customer by phone in BTCPay store records and return their profile.
        // For now, acknowledge the query so Splice registration doesn't fail.
        return Ok(new { phone, status = "known" });
    }

    /// Splice POSTs here when your institution receives an incoming payment (STK Push settled).
    /// Maps to "recv_payment_notif_endpoint" in the Splice registration JSON.
    [HttpPost("webhook/received")]
    public IActionResult PaymentReceived([FromBody] JObject payload)
    {
        var reference = payload["reference"]?.Value<string>();
        var amount = payload["amount"]?.Value<decimal?>();
        var phone = payload["phone"]?.Value<string>();

        logger.LogInformation(
            "Splice payment received: reference={Reference} amount={Amount} phone={Phone}",
            reference,
            amount,
            phone
        );

        // TODO: match reference to a BTCPay invoice and mark it settled.

        return Ok();
    }

    /// Splice POSTs here when an outgoing B2C disbursement to a merchant has settled.
    /// Maps to "send_payment_notif_endpoint" in the Splice registration JSON.
    [HttpPost("webhook/sent")]
    public IActionResult PaymentSent([FromBody] JObject payload)
    {
        var reference = payload["reference"]?.Value<string>();
        var amount = payload["amount"]?.Value<decimal?>();
        var recipient = payload["recipient"]?.Value<string>();

        logger.LogInformation(
            "Splice B2C disbursement settled: reference={Reference} amount={Amount} recipient={Recipient}",
            reference,
            amount,
            recipient
        );

        // TODO: update merchant ledger once B2C field names are confirmed with Splice.

        return Ok();
    }
}
