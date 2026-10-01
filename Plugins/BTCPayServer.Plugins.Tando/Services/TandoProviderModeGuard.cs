using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.Tando.Services;

public sealed class TandoProviderModeGuard(IConfiguration configuration, IHostEnvironment environment) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Validate(configuration[PhoneVerificationRegistration.ModeConfigKey], environment);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public static bool IsMock(string? mode) => string.Equals(mode, "Mock", StringComparison.OrdinalIgnoreCase);

    public static void Validate(string? mode, IHostEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(mode) && !IsMock(mode) &&
            !string.Equals(mode, "Daraja", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Invalid Tando phone verification mode '{mode}'.");
        if (IsMock(mode) && !environment.IsDevelopment())
            throw new InvalidOperationException("Tando mock providers are allowed only in the Development environment.");
    }
}