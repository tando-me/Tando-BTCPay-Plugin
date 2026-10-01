using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Tando.Services;

public static class PhoneVerificationRegistration
{
    public const string ModeConfigKey = "Tando:PhoneVerification:Mode";

    public static IServiceCollection AddTandoPhoneVerification(this IServiceCollection services)
    {
        services.AddHostedService<TandoProviderModeGuard>();
        services.TryAddSingleton<PhoneVerificationProvider>(sp => Resolve(sp, () => new NoPhoneVerificationProvider()));
        return services;
    }

    /// Safaricom Daraja Mobile Number Validation. Remove this registration to sign up without it.
    public static IServiceCollection AddTandoDarajaPhoneVerification(this IServiceCollection services)
    {
        services.AddSingleton<DarajaMobileNumberValidationService>();
        services.Replace(ServiceDescriptor.Singleton<PhoneVerificationProvider>(sp =>
            Resolve(sp, () => sp.GetRequiredService<DarajaMobileNumberValidationService>())));
        return services;
    }

    private static PhoneVerificationProvider Resolve(IServiceProvider sp, Func<PhoneVerificationProvider> registered)
    {
        var environment = sp.GetRequiredService<IHostEnvironment>();
        var mode = sp.GetRequiredService<IConfiguration>()[ModeConfigKey];
        TandoProviderModeGuard.Validate(mode, environment);
        if (!TandoProviderModeGuard.IsMock(mode))
            return registered();

        sp.GetRequiredService<ILogger<DevelopmentPhoneVerificationProvider>>()
            .LogWarning("Tando mock phone verification enabled. Synthetic fixtures can create local test stores.");
        return new DevelopmentPhoneVerificationProvider(environment);
    }
}
