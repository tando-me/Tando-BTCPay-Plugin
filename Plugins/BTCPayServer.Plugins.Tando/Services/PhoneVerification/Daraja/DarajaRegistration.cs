using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BTCPayServer.Plugins.Tando.Services;

public static class DarajaRegistration
{
    public static IServiceCollection AddDarajaPhoneVerification(this IServiceCollection services)
    {
        services.AddTandoPhoneVerification();
        services.TryAddSingleton<DarajaPhoneVerifier>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPhoneVerifier, DarajaPhoneVerifier>(sp => sp.GetRequiredService<DarajaPhoneVerifier>()));
        services.AddSingleton<IUIExtension>(new UIExtension("TandoDarajaNav", "server-nav"));
        return services;
    }
}