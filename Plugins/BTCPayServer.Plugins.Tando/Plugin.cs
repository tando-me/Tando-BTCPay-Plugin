using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Lightning;
using BTCPayServer.Models.ServerViewModels;
using BTCPayServer.Plugins.Subscriptions;
using BTCPayServer.Plugins.Tando;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.Services.Lightning;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Tando;

public class Plugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }
    };

    public override void Execute(IServiceCollection services)
    {
        /*services.AddSingleton<IUIExtension>(new UIExtension("TandoPluginHeaderNav", "header-nav"));*/
        services.AddSingleton<IUIExtension>(new UIExtension("TandoServerNav", "server-nav"));
        services.AddScoped<TandoSplitService>();
        services.AddSingleton<TandoSubscriptionService>();
        services.AddSingleton<SubscriptionHostedService>();
        services.AddScoped<TandoMerchantSettingsService>();
        services.AddScoped<TandoProductProvisioningService>();
        services.AddHostedService<TandoPlanFallbackHostedService>();
        services.AddSingleton<TandoLightningProvisionerFactory>();
        services.AddSingleton<ITandoLightningProvisioner, NwcLightningProvisioner>();
        services.AddSingleton<ITandoLightningProvisioner, PhoenixdLightningProvisioner>();
        services.AddSingleton<ITandoMpesaPayoutClient, UnconfiguredTandoMpesaPayoutClient>();
        services.AddTandoPhoneVerification();
        services.AddSingleton(new ServicesViewModel.OtherExternalService()
        {
            Name = "Tando",
            ControllerName = "UITandoSettings",
            ActionName = nameof(UITandoSettingsController.Settings)
        });
    }
}
