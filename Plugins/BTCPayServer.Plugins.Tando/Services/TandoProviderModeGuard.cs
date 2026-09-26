using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.Tando.Services;

/// <summary>Validate the verification adapter before the server accepts traffic. A mock configuration
/// can never be ignored or silently accepted in Production or Staging.</summary>
public sealed class TandoProviderModeGuard(IConfiguration configuration, IHostEnvironment environment) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Validate(configuration["Tando:PhoneVerification:Mode"] ?? "Daraja", "Daraja", environment);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static void Validate(string mode, string productionMode, IHostEnvironment environment)
    {
        if (!string.Equals(mode, productionMode, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "Mock", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Invalid Tando provider mode '{mode}'.");
        if (string.Equals(mode, "Mock", StringComparison.OrdinalIgnoreCase) && !environment.IsDevelopment())
            throw new InvalidOperationException("Tando mock providers are allowed only in the Development environment.");
    }
}
