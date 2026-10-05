using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.MassStoreGenerator;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Tando.Tests;

public class DevelopmentVerificationTests
{
    private static ServiceProvider Build(string environment, string? mode)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment { EnvironmentName = environment });
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Tando:PhoneVerification:Mode"] = mode }).Build());
        services.AddLogging();
        services.AddTandoPhoneVerification();
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void MockCannotBeResolvedOutsideDevelopment(string environment)
    {
        using var services = Build(environment, "Mock");
        Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<PhoneVerificationProvider>());
        Assert.Throws<InvalidOperationException>(() => new DevelopmentPhoneVerificationProvider(new TestEnvironment { EnvironmentName = environment }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Daraja")]
    [InlineData("Typo")]
    public void MockIsNeverAnImplicitFallback(string? mode)
    {
        using var services = Build("Development", mode);
        // Real adapter dependencies are deliberately absent: resolving it must fail, never use a mock.
        Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<PhoneVerificationProvider>());
    }

    [Fact]
    public async Task ExplicitMockNeedsNoDarajaDependenciesAndOnlyAcceptsTheFixture()
    {
        using var services = Build("Development", "Mock");
        var provider = services.GetRequiredService<PhoneVerificationProvider>();
        Assert.True(provider.IsMock);
        Assert.Equal("Mock", provider.Mode);
        Assert.True((await provider.ValidateMobileNumber("254701234567", "01", "mock-verified")).Matches);
        Assert.True((await provider.ValidateMobileNumber("254712345678", "01", "mock-verified")).Matches);
        Assert.False((await provider.ValidateMobileNumber("254701234567", "05", "mock-verified")).Matches);
        Assert.False((await provider.ValidateMobileNumber("254701234567", "01", "arbitrary")).Matches);
    }

    [Theory]
    [InlineData("mock-mismatch", 400)]
    [InlineData("mock-unavailable", 503)]
    [InlineData("mock-unconfigured", 503)]
    public async Task SignupUsesDevelopmentFixtures(string id, int expected)
    {
        using var services = Build("Development", "Mock");
        var controller = new TandoOnboardingController(null!, null!, null!, services.GetRequiredService<PhoneVerificationProvider>(), null!);
        var signup = await controller.Signup(new TandoSignupRequest { PhoneNumber = "0701234567", IdNumber = id }, CancellationToken.None);
        Assert.Equal(expected, Assert.IsAssignableFrom<ObjectResult>(signup).StatusCode);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tando.Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
