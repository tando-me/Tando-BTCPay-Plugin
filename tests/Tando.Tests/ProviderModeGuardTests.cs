using System;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Tando.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Tando.Tests;

public class ProviderModeGuardTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void ProductionModesRemainReal(string environmentName)
    {
        var environment = new TestEnvironment { EnvironmentName = environmentName };
        TandoProviderModeGuard.Validate("Daraja", "Daraja", environment);
        Assert.Throws<InvalidOperationException>(() => TandoProviderModeGuard.Validate("Mock", "Daraja", environment));
    }

    [Fact]
    public void DevelopmentAllowsOnlyExplicitMockOrProductionAdapterModes()
    {
        var environment = new TestEnvironment();
        TandoProviderModeGuard.Validate("Mock", "Daraja", environment);
        TandoProviderModeGuard.Validate("Daraja", "Daraja", environment);
    }

    [Fact]
    public async Task HostedGuardChecksTheLoadedConfigurationBeforeServerStartup()
    {
        var environment = new TestEnvironment { EnvironmentName = "Production" };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new[]
        {
            new System.Collections.Generic.KeyValuePair<string, string?>("Tando:PhoneVerification:Mode", "Mock"),
        }).Build();
        var guard = new TandoProviderModeGuard(config, environment);
        await Assert.ThrowsAsync<InvalidOperationException>(() => guard.StartAsync(default));
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tando.Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
