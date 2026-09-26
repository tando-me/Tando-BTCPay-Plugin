using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.MassStoreGenerator;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Tando.Tests;

public class ProviderTests
{
    [Theory]
    [InlineData(false, false, true, 400)]
    [InlineData(false, true, true, 503)]
    [InlineData(false, true, false, 503)]
    [InlineData(true, true, true, 503)]
    public async Task SignupBlocksBeforeSubscriptionOrStoreCreation(bool matches, bool unavailable, bool configured, int status)
    {
        var provider = new MockDarajaProvider(new(matches, unavailable, "", configured));
        var controller = new TandoOnboardingController(null!, null!, null!, provider, null!);
        var signup = await controller.Signup(new TandoSignupRequest { PhoneNumber = "0701234567", IdNumber = "test-id" }, CancellationToken.None);
        Assert.Equal(status, Assert.IsAssignableFrom<ObjectResult>(signup).StatusCode);
        Assert.Equal(1, provider.Calls);
    }

    [Theory]
    [InlineData("0712345678")]
    [InlineData("+254712345678")]
    public async Task Safaricom071NumbersReachTheProvider(string phone)
    {
        var provider = new MockDarajaProvider(new(false, true, ""));
        var controller = new TandoOnboardingController(null!, null!, null!, provider, null!);
        var result = await controller.Signup(new TandoSignupRequest { PhoneNumber = phone, IdNumber = "test-id" }, CancellationToken.None);
        Assert.Equal(503, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task MatchingIdentityPassesSharedSignupGate()
    {
        var provider = new MockDarajaProvider(new(true, false, ""));
        var controller = new TandoOnboardingController(null!, null!, null!, provider, null!);
        var method = typeof(TandoOnboardingController).GetMethod("ValidateKyc", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var result = await (Task<(bool verified, IActionResult? error)>)method.Invoke(controller,
            new object[] { "254701234567", "test-id", "01" })!;
        Assert.True(result.verified);
        Assert.Null(result.error);
    }
}
