using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.Tando.Services;

public class TandoPlanFallbackHostedService(TandoSubscriptionService subscriptionService) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndSwapIfNeeded();
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch { }
        }
    }

    private async Task CheckAndSwapIfNeeded()
    {
        var settings = await subscriptionService.GetSettings();
        if (string.IsNullOrEmpty(settings.SubscriptionOfferingId) || string.IsNullOrEmpty(settings.SubscriptionPlanId))
            return;

        if (string.IsNullOrEmpty(settings.FallbackSubscriptionPlanId) || settings.SubscriptionPlanId == settings.FallbackSubscriptionPlanId)
            return;

        var activePlans = await subscriptionService.GetActivePlans(settings.SubscriptionOfferingId);
        if (activePlans.Any(p => p.Id == settings.SubscriptionPlanId)) 
            return;

        if (activePlans.All(c => c.Id != settings.FallbackSubscriptionPlanId))
            return;

        settings.SubscriptionPlanId = settings.FallbackSubscriptionPlanId;
        await subscriptionService.SaveSettings(settings);
    }
}