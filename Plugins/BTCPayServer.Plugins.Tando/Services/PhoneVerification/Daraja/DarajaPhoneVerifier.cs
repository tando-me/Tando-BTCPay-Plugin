using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Tando.Services;

public class DarajaPhoneVerifier(IHttpClientFactory httpClientFactory, ISettingsRepository settingsRepository, 
    IMemoryCache memoryCache) : IPhoneVerifier
{
    private const string SettingsKey = "TandoDarajaSettings";

    public string Name => "Daraja";

    public async Task<bool> IsConfigured() => (await GetSettings()).IsConfigured();

    public async Task<DarajaSettings> GetSettings() =>
        await settingsRepository.GetSettingAsync<DarajaSettings>(SettingsKey) ?? new DarajaSettings();

    public async Task UpdateSettings(DarajaSettings settings)
    {
        var current = await GetSettings();
        if (string.IsNullOrEmpty(settings.ConsumerSecret))
            settings.ConsumerSecret = current.ConsumerSecret;

        await settingsRepository.UpdateSetting(settings, SettingsKey);
        memoryCache.Remove(TokenCacheKey(current));
    }

    public async Task<PhoneVerificationOutcome> Verify(PhoneVerificationRequest request, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettings();
        if (!settings.IsConfigured())
            return PhoneVerificationOutcome.NotConfigured;

        try
        {
            var client = httpClientFactory.CreateClient(nameof(DarajaPhoneVerifier));
            var body = new JObject
            {
                ["requestRefID"] = Guid.NewGuid().ToString("N"),
                ["shortCode"] = settings.ShortCode,
                ["msisdn"] = request.Msisdn,
                ["idType"] = request.IdType,
                ["idNumber"] = request.IdNumber
            };
            using var message = new HttpRequestMessage(HttpMethod.Post, $"{settings.GetBaseUrl()}/v1/KYC-validation/validateID")
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessToken(settings, client, cancellationToken));

            using var response = await client.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return PhoneVerificationOutcome.Unavailable;
            }

            var status = JObject.Parse(await response.Content.ReadAsStringAsync(cancellationToken))["status"];
            if (status?.Type is not (JTokenType.Boolean or JTokenType.String) || !bool.TryParse(status.ToString(), out var matches))
                return PhoneVerificationOutcome.Unavailable;
            return matches ? PhoneVerificationOutcome.Verified : PhoneVerificationOutcome.Mismatch;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return PhoneVerificationOutcome.Unavailable;
        }
    }

    private async Task<string> GetAccessToken(DarajaSettings settings, HttpClient client, CancellationToken cancellationToken)
    {
        var cacheKey = TokenCacheKey(settings);
        if (memoryCache.TryGetValue(cacheKey, out string? cached) && cached is not null)
            return cached;

        using var message = new HttpRequestMessage(HttpMethod.Get, $"{settings.GetBaseUrl()}/oauth/v1/generate?grant_type=client_credentials");
        message.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.ConsumerKey}:{settings.ConsumerSecret}")));

        using var response = await client.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = JObject.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var token = json["access_token"]?.Value<string>()
            ?? throw new InvalidOperationException("Daraja token response has no access_token.");

        var expiresIn = json["expires_in"]?.Value<int?>() ?? 3599;
        memoryCache.Set(cacheKey, token, TimeSpan.FromSeconds(Math.Max(1, expiresIn - 120)));
        return token;
    }

    private static string TokenCacheKey(DarajaSettings settings) =>
        "TandoDarajaToken:" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{settings.ConsumerKey}:{settings.ConsumerSecret}:{settings.UseSandbox}")))[..16];
}