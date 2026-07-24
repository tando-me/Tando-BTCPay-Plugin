using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Models.ServerViewModels;
using BTCPayServer.Plugins.MassStoreGenerator;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Template.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Template;

public class Plugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.3.7" }
    };

    public override void Execute(IServiceCollection services)
    {
        services.AddSingleton<IUIExtension>(new UIExtension("TandoPluginHeaderNav", "header-nav"));
        services.AddHostedService<ApplicationPartsLogger>();

        services.AddSingleton<TandoSubscriptionService>();
        services.AddSingleton(new ServicesViewModel.OtherExternalService()
        {
            Name = "Tando",
            ControllerName = "UITandoSettings",
            ActionName = nameof(UITandoSettingsController.Settings)
        });
    }
}
