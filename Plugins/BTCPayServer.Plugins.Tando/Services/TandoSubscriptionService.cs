using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using System.Linq;
using BTCPayServer.Data;
using BTCPayServer.Data.Subscriptions;

namespace BTCPayServer.Plugins.Tando.Services;

public record TandoPlan(string Id, string Name, decimal Price, string Currency, string RecurringType, string? Description);

public record TandoSubscriptionStatus(bool Active, bool Configured, string? PlanId, string? Phase);

public class TandoSubscriptionService(ApplicationDbContextFactory dbContextFactory, ISettingsRepository settingsRepository)
{
    public async Task<TandoSettings> GetSettings() => await settingsRepository.GetSettingAsync<TandoSettings>("Tando") ?? new TandoSettings();

    public Task SaveSettings(TandoSettings settings) => settingsRepository.UpdateSetting(settings, "Tando");

    public async Task<TandoPlan[]?> GetAvailablePlans()
    {
        var offeringId = (await GetSettings()).SubscriptionOfferingId;
        if (string.IsNullOrEmpty(offeringId)) return null;

        await using var ctx = dbContextFactory.CreateContext();
        var offering = await ctx.Offerings.GetOfferingData(offeringId);
        if (offering is null) return null;

        return offering.Plans.Where(p => p.Status == PlanData.PlanStatus.Active)
            .Select(p => new TandoPlan(p.Id, p.Name, p.Price, p.Currency, p.RecurringType.ToString(), p.Description)).ToArray();
    }

    public async Task<TandoSubscriptionStatus> GetStatus(string normalizedPhone)
    {
        var offeringId = (await GetSettings()).SubscriptionOfferingId;
        if (string.IsNullOrEmpty(offeringId))
            return new TandoSubscriptionStatus(false, false, null, null);

        await using var ctx = dbContextFactory.CreateContext();
        var offering = await ctx.Offerings.GetOfferingData(offeringId);
        if (offering is null)
            return new TandoSubscriptionStatus(false, false, null, null);

        var subscriber = await ctx.Subscribers.GetBySelector(offeringId, CustomerSelector.ByExternalRef(normalizedPhone));
        if (subscriber is null)
            return new TandoSubscriptionStatus(false, true, null, null);

        return new TandoSubscriptionStatus(
            Active: subscriber is { IsActive: true, IsSuspended: false },
            Configured: true,
            PlanId: subscriber.PlanId,
            Phase: subscriber.Phase.ToString());
    }
}
