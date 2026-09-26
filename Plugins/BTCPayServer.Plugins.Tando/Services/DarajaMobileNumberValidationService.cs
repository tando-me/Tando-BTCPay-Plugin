using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Tando.Services;

/// Checks whether an phone no is registered under a given ID number on Safaricom's KYC database.
public class DarajaMobileNumberValidationService(
    IHttpClientFactory httpClientFactory,
    ISettingsRepository settingsRepository,
    IMemoryCache memoryCache,
    ILogger<DarajaMobileNumberValidationService> logger) : PhoneVerificationProvider
{
    private const string SettingsKey = "TandoDarajaSettings";
    private const string SandboxBaseUrl = "https://sandbox.safaricom.co.ke";
    private const string ProductionBaseUrl = "https://api.safaricom.co.ke";

    // Cache key is credential-specific so an in-flight token fetch for old credentials
    // cannot overwrite the slot after a settings change clears it.
    private static string TokenCacheKeyFor(TandoDarajaSettings s)
    {
        var raw = Encoding.UTF8.GetBytes($"{s.ConsumerKey}:{s.ConsumerSecret}:{s.UseSandbox}");
        return $"TandoDarajaToken:{Convert.ToHexString(SHA256.HashData(raw))[..16]}";
    }

    public async Task<TandoDarajaSettings> GetSettings() =>
        await settingsRepository.GetSettingAsync<TandoDarajaSettings>(SettingsKey) ?? new TandoDarajaSettings();

    public async Task UpdateSettings(TandoDarajaSettings settings)
    {
        var old = await GetSettings();
        await settingsRepository.UpdateSetting(settings, SettingsKey);
        memoryCache.Remove(TokenCacheKeyFor(old));
    }

    ///msisdn: Normalized phone number, 254XXXXXXXXX
    /// idType>01 = National ID
    /// idNumber: Kenyan ID the phone was registered under
    public override async Task<PhoneVerificationResult> ValidateMobileNumber(string msisdn, string idType, string idNumber)
    {
        var settings = await GetSettings();
        if (!settings.IsConfigured())
        {
            logger.LogWarning("[Daraja] Not configured — signup blocked.");
            return new PhoneVerificationResult(Matches: false, ServiceError: true, Detail: "Daraja credentials are not configured.", Configured: false);
        }

        var baseUrl = settings.UseSandbox ? SandboxBaseUrl : ProductionBaseUrl;
        logger.LogInformation("[Daraja] Requesting identity verification via {BaseUrl}", baseUrl);

        try
        {
            var token = await GetAccessToken(settings, baseUrl);
            var client = httpClientFactory.CreateClient(nameof(DarajaMobileNumberValidationService));
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/KYC-validation/validateID");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var body = new JObject
            {
                ["requestRefID"] = Guid.NewGuid().ToString("N"),
                ["shortCode"] = settings.ShortCode,
                ["msisdn"] = msisdn,
                ["idType"] = idType,
                ["idNumber"] = idNumber
            };
            request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();

            logger.LogInformation("[Daraja] Response HTTP {StatusCode}", (int)response.StatusCode);
            if (!response.IsSuccessStatusCode)
            {
                // HTTP errors do not establish that a phone/ID pair mismatches.
                return new PhoneVerificationResult(false, true, $"Daraja returned HTTP {(int)response.StatusCode}.");
            }

            var json = JObject.Parse(content);
            var responseCode = json["responseCode"]?.Value<string>();
            var responseMessage = json["responseMessage"]?.Value<string>();
            logger.LogInformation("[Daraja] responseCode={ResponseCode} responseMessage={ResponseMessage}", responseCode, responseMessage);
            var status = json["status"];
            if (status is null || (status.Type != JTokenType.Boolean && status.Type != JTokenType.String) ||
                !bool.TryParse(status.Value<string>(), out var matches))
                return new PhoneVerificationResult(false, true, "Daraja returned an unrecognized verification result.");
            return new PhoneVerificationResult(matches, false, matches ? "Identity matched." : "Identity mismatch.");
        }
        catch (Exception ex)
        {
            // Exception messages and response bodies may contain identity data.
            logger.LogError("[Daraja] Verification failed ({ExceptionType})", ex.GetType().Name);
            return new PhoneVerificationResult(Matches: false, ServiceError: true, Detail: "Could not reach the Daraja API.");
        }
    }

    private async Task<string> GetAccessToken(TandoDarajaSettings settings, string baseUrl)
    {
        var cacheKey = TokenCacheKeyFor(settings);
        if (memoryCache.TryGetValue(cacheKey, out string cached))
            return cached;

        var client = httpClientFactory.CreateClient(nameof(DarajaMobileNumberValidationService));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/oauth/v1/generate?grant_type=client_credentials");
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.ConsumerKey}:{settings.ConsumerSecret}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        using var response = await client.SendAsync(request);
        var tokenBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("[Daraja] OAuth token fetch failed {StatusCode}", (int)response.StatusCode);
            throw new HttpRequestException($"Daraja OAuth {(int)response.StatusCode}");
        }
        var json = JObject.Parse(tokenBody);
        var token = json["access_token"]?.Value<string>()
            ?? throw new InvalidOperationException("Daraja token response did not contain access_token.");
        var expiresIn = json["expires_in"]?.Value<int?>() ?? 3599;
        memoryCache.Set(cacheKey, token, TimeSpan.FromSeconds(Math.Max(1, expiresIn - 120)));
        return token;
    }
}
