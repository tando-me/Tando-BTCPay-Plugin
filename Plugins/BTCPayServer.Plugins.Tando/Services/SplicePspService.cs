using System;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Tando.Services;

public class SplicePspService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<SplicePspService> logger)
{
    // Safaricom-allocated prefixes only — STK Push only works on Safaricom numbers.
    private static readonly Regex SafaricomMsisdn = new(
        @"^(?:\+254|254|0)(7(?:0\d|2\d|4\d|5[7-9]|6[89]|9\d)\d{6}|11[0-5]\d{6})$",
        RegexOptions.Compiled);

    private SpliceSettings GetSettings()
    {
        var settings = new SpliceSettings();
        configuration.GetSection("Splice").Bind(settings);
        return settings;
    }

    /// <summary>
    /// Validates a raw Kenyan MSISDN and returns the normalized 254XXXXXXXXX form.
    /// Returns a human-readable error string on failure, null on success.
    /// </summary>
    public string? NormalizePhone(string rawPhone, out string? normalized)
    {
        var match = SafaricomMsisdn.Match((rawPhone ?? "").Trim());
        if (!match.Success)
        {
            normalized = null;
            return "Invalid M-Pesa number. Must be a Safaricom number, e.g. 0712345678 or +254712345678.";
        }
        normalized = "254" + match.Groups[1].Value;
        return null;
    }

    /// <summary>
    /// Initiates an STK Push via Splice.
    /// Splice will prompt the customer's phone with the M-Pesa PIN entry screen,
    /// then call <paramref name="callbackUrl"/> when the customer approves or declines.
    /// </summary>
    /// <param name="customerMsisdn">Customer phone in 254XXXXXXXXX format</param>
    /// <param name="merchantDestination">Merchant's registered M-Pesa identifier (personal number, till, or paybill)</param>
    /// <param name="amountKes">Amount in KES — rounded up to the nearest whole shilling</param>
    /// <param name="orderId">Unique reference ID for this transaction</param>
    /// <param name="callbackUrl">URL Splice will POST the result to</param>
    public async Task<StkPushResult> InitiateStkPush(
        string customerMsisdn,
        string merchantDestination,
        decimal amountKes,
        string orderId,
        string callbackUrl,
        CancellationToken cancellationToken = default)
    {
        var settings = GetSettings();
        if (!settings.IsConfigured())
            return new StkPushResult(false, null, "Splice is not configured. Set Splice__ApiKey in environment.");

        var baseUrl = settings.UseSandbox ? "https://sandbox.splice.africa" : settings.BaseUrl;

        try
        {
            var client = httpClientFactory.CreateClient(nameof(SplicePspService));
            client.DefaultRequestHeaders.Add("Authorization", $"Basic {settings.BasicAuthHeader()}");

            var payload = new
            {
                phone = customerMsisdn,
                amount = (int)Math.Ceiling(amountKes),
                merchantIdentifier = merchantDestination,
                reference = orderId,
                callbackUrl
            };

            var json = JsonConvert.SerializeObject(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"{baseUrl}/v1/stk-push", content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Splice STK Push HTTP {Status}: {Body}", (int)response.StatusCode, body);
                return new StkPushResult(false, null, $"Splice returned HTTP {(int)response.StatusCode}.");
            }

            var responseJson = JObject.Parse(body);
            var checkoutRequestId = responseJson["checkoutRequestId"]?.Value<string>();
            return new StkPushResult(true, checkoutRequestId, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Splice STK Push error for {Msisdn}", customerMsisdn);
            return new StkPushResult(false, null, "Could not reach Splice.");
        }
    }
}

public record StkPushResult(bool Success, string? CheckoutRequestId, string? Error);
