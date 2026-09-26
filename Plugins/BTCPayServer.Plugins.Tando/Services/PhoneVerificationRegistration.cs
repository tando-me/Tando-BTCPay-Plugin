using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Tando.Services;

public static class PhoneVerificationRegistration
{
    public static IServiceCollection AddTandoPhoneVerification(this IServiceCollection services)
    {
        services.AddHostedService<TandoProviderModeGuard>();
        services.AddSingleton<DarajaMobileNumberValidationService>();
        services.AddSingleton<PhoneVerificationProvider>(sp =>
        {
            var mode = sp.GetRequiredService<IConfiguration>()["Tando:PhoneVerification:Mode"] ?? "Daraja";
            TandoProviderModeGuard.Validate(mode, "Daraja", sp.GetRequiredService<IHostEnvironment>());
            if (string.Equals(mode, "Daraja", StringComparison.OrdinalIgnoreCase))
                return sp.GetRequiredService<DarajaMobileNumberValidationService>();
            var provider = new DevelopmentPhoneVerificationProvider(sp.GetRequiredService<IHostEnvironment>());
            sp.GetRequiredService<ILogger<DevelopmentPhoneVerificationProvider>>()
                .LogWarning("Tando mock phone verification enabled. Synthetic fixtures can create local test stores.");
            return provider;
        });
        return services;
    }
}
