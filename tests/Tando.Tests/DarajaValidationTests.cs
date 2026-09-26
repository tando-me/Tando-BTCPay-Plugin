using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.Tando.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tando.Tests;

public class DarajaValidationTests
{
    [Theory]
    [InlineData(200, "{\"status\":true}", true, false)]
    [InlineData(200, "{\"status\":\"true\"}", true, false)]
    [InlineData(200, "{\"status\":false}", false, false)]
    [InlineData(200, "{}", false, true)]
    [InlineData(200, "{\"status\":\"unknown\"}", false, true)]
    [InlineData(200, "invalid-json", false, true)]
    [InlineData(400, "{}", false, true)]
    [InlineData(401, "{}", false, true)]
    [InlineData(403, "{}", false, true)]
    [InlineData(429, "{}", false, true)]
    [InlineData(503, "{}", false, true)]
    public async Task OnlyExplicitSuccessfulResponsesDetermineIdentity(int code, string body, bool matches, bool unavailable)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var factory = new FakeHttp(code, body);
        var service = Create(factory, new Settings(), cache);
        var result = await service.ValidateMobileNumber("254712345678", "01", "sensitive-id");
        Assert.Equal(matches, result.Matches);
        Assert.Equal(unavailable, result.ServiceError);
        Assert.DoesNotContain("sensitive-id", result.Detail);
    }

    [Fact]
    public async Task MissingConfigurationMakesNoHttpRequests()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var factory = new FakeHttp(200, "{}");
        var service = Create(factory, new Settings { Value = new() }, cache);
        var result = await service.ValidateMobileNumber("254712345678", "01", "id");
        Assert.False(result.Configured);
        Assert.True(result.ServiceError);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task TokenIsReusedAndSettingsSaveInvalidatesIt()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var factory = new FakeHttp(200, "{\"status\":true}");
        var settings = new Settings();
        var service = Create(factory, settings, cache);
        await service.ValidateMobileNumber("254712345678", "01", "id");
        await service.ValidateMobileNumber("254712345678", "01", "id");
        Assert.Equal(1, factory.TokenCalls);
        await service.UpdateSettings(settings.Value);
        await service.ValidateMobileNumber("254712345678", "01", "id");
        Assert.Equal(2, factory.TokenCalls);
    }

    [Fact]
    public async Task EachCredentialSetGetsItsOwnTokenAndRotationFetchesFresh()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var factory = new FakeHttp(200, "{\"status\":true}");
        var oldCreds = new TandoDarajaSettings { ConsumerKey = "old", ConsumerSecret = "old-s", ShortCode = "123" };
        var newCreds = new TandoDarajaSettings { ConsumerKey = "new", ConsumerSecret = "new-s", ShortCode = "123" };
        var settings = new Settings { Value = oldCreds };
        var service = Create(factory, settings, cache);

        // First fetch with old creds.
        await service.ValidateMobileNumber("254712345678", "01", "id");
        Assert.Equal(1, factory.TokenCalls);

        // Rotate to new creds → old token evicted.
        await service.UpdateSettings(newCreds);
        await service.ValidateMobileNumber("254712345678", "01", "id");
        Assert.Equal(2, factory.TokenCalls);

        // Token for new creds is cached — no extra fetch.
        await service.ValidateMobileNumber("254712345678", "01", "id");
        Assert.Equal(2, factory.TokenCalls);

        // Rotate back to old creds → new token evicted; old creds need a fresh fetch too.
        await service.UpdateSettings(oldCreds);
        await service.ValidateMobileNumber("254712345678", "01", "id");
        Assert.Equal(3, factory.TokenCalls);
    }


    // Short expiry: a 30-second token must not be cached for the old 60-second floor.
    [Fact]
    public async Task ShortLivedTokenIsNotCachedBeyondItsRealExpiry()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var factory = new FakeHttp(200, "{\"status\":true}", tokenExpiresIn: 30);
        var service = Create(factory, new Settings(), cache);

        await service.ValidateMobileNumber("254712345678", "01", "id");
        Assert.Equal(1, factory.TokenCalls);

        // Wait longer than the 1-second TTL (max(1, 30-120) = 1) — the cache entry must expire.
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        await service.ValidateMobileNumber("254712345678", "01", "id");
        Assert.Equal(2, factory.TokenCalls);
    }

    private static DarajaMobileNumberValidationService Create(IHttpClientFactory factory, Settings settings, IMemoryCache cache) =>
        new(factory, settings, cache, NullLogger<DarajaMobileNumberValidationService>.Instance);

    private sealed class Settings : ISettingsRepository
    {
        public TandoDarajaSettings Value = new() { ConsumerKey = "key", ConsumerSecret = "secret", ShortCode = "123" };
        public Task<T?> GetSettingAsync<T>(string? name = null) where T : class => Task.FromResult(Value as T);
        public Task UpdateSetting<T>(T obj, string? name = null) where T : class
        {
            Value = (TandoDarajaSettings)(object)obj;
            return Task.CompletedTask;
        }
        public Task<T> WaitSettingsChanged<T>(CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();
    }

    private sealed class FakeHttp(int code, string body, int tokenExpiresIn = 3600) : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls;
        public int TokenCalls;
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var token = request.RequestUri!.AbsolutePath.Contains("oauth");
            if (token) TokenCalls++;
            return Task.FromResult(new HttpResponseMessage(token ? HttpStatusCode.OK : (HttpStatusCode)code)
            {
                Content = new StringContent(token ? $"{{\"access_token\":\"token\",\"expires_in\":{tokenExpiresIn}}}" : body)
            });
        }
    }

}
