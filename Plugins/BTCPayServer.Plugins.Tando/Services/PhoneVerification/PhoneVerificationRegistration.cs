using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.Tando.Services;

public static class PhoneVerificationRegistration
{
    public static IServiceCollection AddTandoPhoneVerification(this IServiceCollection services)
    {
        services.TryAddSingleton<PhoneVerificationService>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPhoneVerifier, MockPhoneVerifier>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, PhoneVerificationStartupCheck>());
        return services;
    }

    private sealed class PhoneVerificationStartupCheck(IServiceProvider serviceProvider) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            serviceProvider.GetRequiredService<PhoneVerificationService>();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
