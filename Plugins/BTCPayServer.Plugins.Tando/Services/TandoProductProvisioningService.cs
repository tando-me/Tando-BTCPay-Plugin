#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Data;
using BTCPayServer.Plugins.PointOfSale;
using BTCPayServer.Services.Apps;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Tando.Services;

public class TandoProductProvisioningService(ApplicationDbContextFactory dbContextFactory, AppService appService)
{
    public const string KeypadAppName = "Keypad";
    public const string CartAppName = "Cart";

    public async Task<(bool HasPos, bool HasCart)> GetProvisioningStatus(string storeId)
    {
        var (posAppId, cartAppId) = await GetExistingAppIds(storeId);
        return (posAppId is not null, cartAppId is not null);
    }

    public async Task<(string? PosAppId, string? CartAppId)> ProvisionDefaultApps(StoreData store)
    {
        var (posAppId, cartAppId) = await GetExistingAppIds(store.Id);
        var currency = store.GetStoreBlob().DefaultCurrency;

        posAppId ??= await CreatePosApp(store, KeypadAppName, PosViewType.Light, currency, enableCart: false);
        cartAppId ??= await CreatePosApp(store, CartAppName, PosViewType.Cart, currency, enableCart: true);

        return (posAppId, cartAppId);
    }

    public async Task<Client.Models.AppItem[]> ListProducts(string storeId)
    {
        var (posAppId, cartAppId) = await GetExistingAppIds(storeId);
        var appId = cartAppId ?? posAppId;
        if (appId is null) return [];

        var app = await appService.GetApp(appId, PointOfSaleAppType.AppType);
        if (app is null) return [];

        var settings = app.GetSettings<PointOfSaleSettings>();
        return AppService.Parse(settings.Template, includeDisabled: true);
    }

    public async Task<Client.Models.AppItem> AddProduct(string storeId, string name, decimal price, string? imageUrl)
    {
        var item = new Client.Models.AppItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = name,
            Price = price,
            PriceType = Client.Models.AppItemPriceType.Fixed,
            Image = imageUrl
        };
        await MutateBothApps(storeId, items => [.. items, item]);
        return item;
    }

    public async Task<bool> RemoveProduct(string storeId, string itemId)
    {
        var removed = false;
        await MutateBothApps(storeId, items =>
        {
            var filtered = items.Where(i => i.Id != itemId).ToArray();
            removed = filtered.Length != items.Length;
            return filtered;
        });
        return removed;
    }

    private async Task<(string? PosAppId, string? CartAppId)> GetExistingAppIds(string storeId)
    {
        await using var ctx = dbContextFactory.CreateContext();
        var apps = await ctx.Apps.Where(a => a.StoreDataId == storeId && a.AppType == PointOfSaleAppType.AppType && !a.Archived)
            .Select(a => new { a.Id, a.Name }).ToListAsync();

        return (apps.FirstOrDefault(a => a.Name == KeypadAppName)?.Id, apps.FirstOrDefault(a => a.Name == CartAppName)?.Id);
    }

    private async Task<string> CreatePosApp(StoreData store, string name, PosViewType viewType, string currency, bool enableCart)
    {
        var app = new AppData
        {
            StoreDataId = store.Id,
            AppType = PointOfSaleAppType.AppType,
            Name = name
        };
        var settings = new PointOfSaleSettings
        {
            Title = name,
            Currency = currency,
            Template = AppService.SerializeTemplate([]),
            DefaultView = viewType,
            EnableShoppingCart = enableCart,
            ShowItems = enableCart,
            ShowCustomAmount = !enableCart,
            ShowSearch = enableCart,
            ShowCategories = false,
            ShowDiscount = false,
            EnableTips = false
        };
        app.SetSettings(settings);
        await appService.UpdateOrCreateApp(app);
        return app.Id;
    }

    private async Task MutateBothApps(string storeId, Func<Client.Models.AppItem[], Client.Models.AppItem[]> mutate)
    {
        var (posAppId, cartAppId) = await GetExistingAppIds(storeId);
        foreach (var appId in new[] { posAppId, cartAppId })
        {
            if (appId is null) continue;

            var app = await appService.GetApp(appId, PointOfSaleAppType.AppType);
            if (app is null) continue;

            var settings = app.GetSettings<PointOfSaleSettings>();
            var items = AppService.Parse(settings.Template, includeDisabled: true);
            settings.Template = AppService.SerializeTemplate(mutate(items));
            app.SetSettings(settings);
            await appService.UpdateOrCreateApp(app);
        }
    }
}