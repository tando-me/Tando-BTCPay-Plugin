using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Data;
using BTCPayServer.Data.Subscriptions;
using BTCPayServer.Plugins.Subscriptions;
using BTCPayServer.Plugins.Tando.ViewModels;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Tando.Services;

public record TandoPlan(string Id, string Name, decimal Price, string Currency, string RecurringType, string? Description);

public record TandoSubscriptionStatus(bool Active, bool Configured, string? PlanId, string? Phase);

public record ExistingOffering(string Id, string Name, string StoreId, string StoreName);

public class TandoSubscriptionService(ApplicationDbContextFactory dbContextFactory, ISettingsRepository settingsRepository, SubscriptionHostedService subscriptionHostedService)
{
    public async Task<TandoSettings> GetSettings() => await settingsRepository.GetSettingAsync<TandoSettings>("Tando") ?? new TandoSettings();

    public Task SaveSettings(TandoSettings settings) => settingsRepository.UpdateSetting(settings, "Tando");

    public async Task<ExistingOffering[]> GetAllOfferings()
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.Offerings.IncludeAll().Select(o => new ExistingOffering(o.Id, o.App.Name, o.App.StoreDataId, o.App.StoreData.StoreName)).ToArrayAsync();
    }

    public async Task<TandoPlan[]?> GetAvailablePlans()
    {
        var offeringId = (await GetSettings()).SubscriptionOfferingId;
        if (string.IsNullOrEmpty(offeringId)) return null;

        return await GetActivePlans(offeringId);
    }

    public async Task<TandoPlan[]> GetActivePlans(string offeringId)
    {
        if (string.IsNullOrEmpty(offeringId)) return Array.Empty<TandoPlan>();

        await using var ctx = dbContextFactory.CreateContext();
        var offering = await ctx.Offerings.GetOfferingData(offeringId);
        if (offering is null) return Array.Empty<TandoPlan>();

        return offering.Plans.Where(p => p.Status == PlanData.PlanStatus.Active)
            .Select(p => new TandoPlan(p.Id, p.Name, p.Price, p.Currency, p.RecurringType.ToString(), p.Description)).ToArray();
    }

    public async Task<TandoSubscriptionStatus> GetStatus(string normalizedPhone)
    {
        var settings = await GetSettings();
        var offeringId = settings.SubscriptionOfferingId;
        var designatedPlanId = settings.SubscriptionPlanId;
        if (string.IsNullOrEmpty(offeringId))
            return new TandoSubscriptionStatus(false, false, null, null);

        await using var ctx = dbContextFactory.CreateContext();
        var subscriber = await ctx.Subscribers.GetBySelector(offeringId, CustomerSelector.ByExternalRef(normalizedPhone));
        if (subscriber is null)
        {
            var offering = await ctx.Offerings.GetOfferingData(offeringId);
            var designatedPlan = offering?.Plans.FirstOrDefault(p => p.Id == designatedPlanId && p.Status == PlanData.PlanStatus.Active);
            return designatedPlan is null ? new TandoSubscriptionStatus(false, false, null, null) : new TandoSubscriptionStatus(false, true, designatedPlanId, null);
        }
        return new TandoSubscriptionStatus(
            Active: subscriber is { IsActive: true, IsSuspended: false },
            Configured: true,
            PlanId: subscriber.PlanId,
            Phase: subscriber.Phase.ToString());
    }

    public async Task<TandoSubscriptionStatus> CreateFreeTrialSubscriber(string normalizedPhone, RequestBaseUrl baseUrl, CancellationToken cancellationToken)
    {
        var settings = await GetSettings();
        var offeringId = settings.SubscriptionOfferingId!;
        var designatedPlanId = settings.SubscriptionPlanId!;

        await using var ctx = dbContextFactory.CreateContext();
        var existingSubscriber = await ctx.Subscribers.GetBySelector(offeringId, CustomerSelector.ByExternalRef(normalizedPhone));
        if (existingSubscriber is not null)
            return await GetStatus(normalizedPhone);

        var plan = await ctx.Plans.GetPlanFromId(designatedPlanId, offeringId);
        if (plan is null)
            throw new InvalidOperationException("Designated plan is missing or inactive.");
        if (plan.TrialDays <= 0)
            throw new InvalidOperationException("Designated plan has no trial period set, set trial days on it first.");

        var syntheticEmail = $"{normalizedPhone}@bitcoin.co.ke";
        var checkout = new PlanCheckoutData
        {
            PlanId = plan.Id,
            Plan = plan,
            NewSubscriber = true,
            NewSubscriberEmail = syntheticEmail,
            IsTrial = true,
            NewSubscriberMetadata = JObject.FromObject(new { tandoPhoneNumber = normalizedPhone }).ToString(),
            BaseUrl = baseUrl,
            Expiration = DateTimeOffset.UtcNow.AddDays(1)
        };
        ctx.PlanCheckouts.Add(checkout);
        await ctx.SaveChangesAsync(cancellationToken);
        await subscriptionHostedService.ProceedToSubscribe(checkout.Id, cancellationToken);
        await ctx.Entry(checkout).ReloadAsync(cancellationToken);
        if (checkout.SubscriberId is null)
            throw new InvalidOperationException("Trial subscriber creation did not complete.");

        var subscriber = await ctx.Subscribers.FindAsync([checkout.SubscriberId.Value], cancellationToken);
        var customer = await ctx.Customers.FindAsync([subscriber!.CustomerId], cancellationToken);
        if (customer is not null && customer.ExternalRef != normalizedPhone)
        {
            customer.ExternalRef = normalizedPhone;
            try
            {
                await ctx.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("external_ref") == true)
            {
                throw new InvalidOperationException(
                    $"Phone number {normalizedPhone} is already linked to a different customer record on this store. " +
                    "Check for a stale/duplicate customer with this external_ref and remove it.", ex);
            }
        }
        return await GetStatus(normalizedPhone);
    }
}