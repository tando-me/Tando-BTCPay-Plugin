using System.Text.Json;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Events;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Tests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Playwright;
using NBitcoin;

namespace BTCPayServer.Plugins.Tando.Tests;

[Collection("Plugin Tests")]
[Trait("Category", "PlaywrightUITest")]
public class TandoSplitTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public TandoSplitTests(SharedPluginTestFixture fixture, ITestOutputHelper helper) : base(helper)
    {
        _fixture = fixture;
        if (_fixture.ServerTester == null) _fixture.Initialize(this);
        ServerTester = _fixture.ServerTester;
    }

    public ServerTester ServerTester { get; }

    private async Task EnsureTandoSubscriptionConfigured(TestAccount admin, string treasuryLightningAddress = "treasury@tando.example")
    {
        var client = await admin.CreateClient();
        var offering = await client.CreateOffering(admin.StoreId, new OfferingModel { AppName = "Tando Test Offering" });
        var plan = await client.CreateOfferingPlan(admin.StoreId, offering.Id, new() { Name = "Trial Plan", Price = 0m, TrialDays = 7 });
        var subscriptionService = ServerTester.PayTester.GetService<TandoSubscriptionService>();
        await subscriptionService.SaveSettings(new TandoSettings
        {
            SubscriptionOfferingId = offering.Id,
            SubscriptionPlanId = plan.Id,
            TreasuryLightningAddress = treasuryLightningAddress
        });
    }

    private async Task<TestAccount> NewAdminAccount()
    {
        var a = ServerTester.NewAccount();
        await a.GrantAccessAsync();
        await a.MakeAdmin();
        return a;
    }

    private async Task<string> MintApiKey(TestAccount account, string storeId = null)
    {
        var manageController = ServerTester.PayTester.GetController<UIManageController>(
            account.UserId, account.StoreId, account.IsAdmin);

        var permissionValue = new UIManageController.AddApiKeyViewModel.PermissionValueItem
        {
            Permission = Policies.CanModifyStoreSettings,
            Value = true,
            StoreMode = storeId is null
                ? UIManageController.AddApiKeyViewModel.ApiKeyStoreMode.AllStores
                : UIManageController.AddApiKeyViewModel.ApiKeyStoreMode.Specific,
            SpecificStores = storeId is null ? new List<string>() : new List<string> { storeId }
        };

        var label = $"tando-test-{Guid.NewGuid():N}";
        var createResult = await manageController.AddApiKey(new UIManageController.AddApiKeyViewModel
        {
            Label = label,
            PermissionValues = storeId is null
                ? new List<UIManageController.AddApiKeyViewModel.PermissionValueItem>
                {
                    permissionValue,
                    new() { Permission = Policies.CanModifyServerSettings, Value = true }
                }
                : new List<UIManageController.AddApiKeyViewModel.PermissionValueItem> { permissionValue }
        });
        Assert.IsType<RedirectToActionResult>(createResult);

        var listResult = await manageController.APIKeys();
        var view = Assert.IsType<ViewResult>(listResult);
        var vm = Assert.IsType<UIManageController.ApiKeysViewModel>(view.Model);
        return vm.ApiKeyDatas.Single(k => k.Label == label).Id;
    }

    private async Task<IAPIRequestContext> ApiContextFor(string apiKey)
    {
        return await Playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions
        {
            BaseURL = ServerUri.ToString(),
            ExtraHTTPHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"token {apiKey}",
                ["Content-Type"] = "application/json"
            },
            IgnoreHTTPSErrors = true
        });
    }

    private static string RandomKenyanPhone() => $"07{Random.Shared.Next(10000000, 99999999)}";

    private static async Task<JsonElement> ParseJson(IAPIResponse response)
        => JsonDocument.Parse(await response.TextAsync()).RootElement;

    private readonly Dictionary<string, string> _merchantKeys = new();

    private async Task<(string StoreId, IAPIRequestContext Api)> SignupMerchant(IAPIRequestContext backendApi, string phone = null)
    {
        var response = await backendApi.PostAsync("/plugins/api/tando/signup",
            new APIRequestContextOptions { DataObject = new { phoneNumber = phone ?? RandomKenyanPhone() } });
        var body = await response.TextAsync();
        Assert.True(response.Ok, $"Signup failed ({response.Status}): {body}");
        var root = JsonDocument.Parse(body).RootElement;
        var storeId = root.GetProperty("storeId").GetString();
        var apiKey = root.GetProperty("apiKey").GetString();
        _merchantKeys[storeId] = apiKey;
        return (storeId, await ApiContextFor(apiKey));
    }

    private BTCPayServerClient MerchantClient(string storeId) => new(ServerUri, _merchantKeys[storeId]);

    private async Task<string> CreateInvoice(TestAccount admin, string storeId, decimal amount)
    {
        var client = MerchantClient(storeId);
        await EnsureStoreWallet(client, storeId);
        var invoice = await client.CreateInvoice(storeId, new CreateInvoiceRequest
        {
            Amount = amount,
            Currency = "KES"
        });
        return invoice.Id;
    }

    private async Task<string> CreateSettledInvoice(TestAccount admin, string storeId, decimal amount)
    {
        var invoiceId = await CreateInvoice(admin, storeId, amount);
        var client = MerchantClient(storeId);
        await client.MarkInvoiceStatus(invoiceId, new MarkInvoiceStatusRequest { Status = InvoiceStatus.Settled });
        return invoiceId;
    }

    private async Task<string> CreatePaidInvoice(string storeId, decimal amount)
    {
        var client = MerchantClient(storeId);
        await EnsureStoreWallet(client, storeId);
        var invoice = await client.CreateInvoice(storeId, new CreateInvoiceRequest { Amount = amount, Currency = "KES" });
        var method = (await client.GetInvoicePaymentMethods(invoice.Id)).First(m => m.PaymentMethodId == "BTC-CHAIN");
        await ServerTester.ExplorerNode.SendToAddressAsync(
            BitcoinAddress.Create(method.Destination, ServerTester.NetworkProvider.BTC.NBitcoinNetwork),
            Money.Coins(method.Due));
        await ServerTester.ExplorerNode.GenerateAsync(1);
        await TestUtils.EventuallyAsync(async () =>
            Assert.Equal(InvoiceStatus.Settled, (await client.GetInvoice(invoice.Id)).Status));
        return invoice.Id;
    }

    private static async Task EnableSplit(IAPIRequestContext api, string storeId, decimal mpesaPercentage)
    {
        var mpesa = await api.PutAsync($"/plugins/api/tando/stores/{storeId}/mpesa",
            new APIRequestContextOptions { DataObject = new { destinationType = "MobileNumber", destination = RandomKenyanPhone() } });
        Assert.Equal(200, mpesa.Status);
        var split = await api.PutAsync($"/plugins/api/tando/stores/{storeId}/split-config",
            new APIRequestContextOptions { DataObject = new { mpesaPercentage } });
        Assert.Equal(200, split.Status);
    }

    private static async Task<JsonElement> WaitForSplit(IAPIRequestContext api, string storeId, string invoiceId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var get = await api.GetAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split");
            if (get.Status == 200)
                return await ParseJson(get);
            await Task.Delay(200);
        }
        throw new Xunit.Sdk.XunitException($"No split was recorded for invoice {invoiceId} after it settled.");
    }

    [Fact]
    public async Task ComputeSplit_AtZeroPercent_DefaultsToNotTriggeredAndSettled()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);
        var invoiceId = await CreateSettledInvoice(admin, storeId, 1000m);

        // split-config defaults to 0% (no PUT called), so ComputeAndRecordSplit
        // should never reach CreateAndClaimPayout at all.
        var compute = await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split");
        Assert.Equal(200, compute.Status);
        var record = await ParseJson(compute);
        Assert.Equal("NotTriggered", record.GetProperty("mpesaPayoutStatus").GetString());
        Assert.True(record.GetProperty("mpesaSettled").GetBoolean());
        Assert.Equal(0m, record.GetProperty("mpesaPortionAmount").GetDecimal());
    }

    [Fact]
    public async Task UpdateMpesaPayoutStatus_ToConfirmed_MarksRecordSettledWithReference()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);
        var invoiceId = await CreateSettledInvoice(admin, storeId, 500m);

        var compute = await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split");
        Assert.Equal(200, compute.Status);

        var update = await api.PutAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split/mpesa-payout-status",
            new APIRequestContextOptions
            {
                DataObject = new { status = "Confirmed", payoutReference = "TANDO-REF-123" }
            });
        Assert.Equal(200, update.Status);

        var get = await merchantApi.GetAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split");
        Assert.Equal(200, get.Status);
        var record = await ParseJson(get);

        Assert.Equal("Confirmed", record.GetProperty("mpesaPayoutStatus").GetString());
        Assert.Equal("TANDO-REF-123", record.GetProperty("mpesaPayoutReference").GetString());
        Assert.True(record.GetProperty("mpesaSettled").GetBoolean());
        Assert.False(record.GetProperty("mpesaSettledAt").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task UpdateMpesaPayoutStatus_UnknownInvoice_ReturnsNotFound()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, _) = await SignupMerchant(api);

        var update = await api.PutAsync($"/plugins/api/tando/stores/{storeId}/invoices/does-not-exist/split/mpesa-payout-status",
            new APIRequestContextOptions
            {
                DataObject = new { status = "Confirmed", payoutReference = (string)null }
            });
        Assert.Equal(404, update.Status);
    }

    [Fact]
    public async Task ListSplits_ReturnsComputedRecordsForStore()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        var invoiceAId = await CreateSettledInvoice(admin, storeId, 200m);
        var invoiceBId = await CreateSettledInvoice(admin, storeId, 300m);
        await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceAId}/split");
        await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceBId}/split");

        var list = await merchantApi.GetAsync($"/plugins/api/tando/stores/{storeId}/splits");
        Assert.Equal(200, list.Status);
        var records = await ParseJson(list);

        Assert.True(records.GetArrayLength() >= 2,
            $"Expected at least 2 split records, got {records.GetArrayLength()}.");
        var totals = records.EnumerateArray().Select(r => r.GetProperty("totalAmount").GetDecimal()).ToList();
        Assert.Contains(200m, totals);
        Assert.Contains(300m, totals);
    }

    [Fact]
    public async Task ListSplits_PagesNewestFirst_AndSkipsInvoicesWithoutASplit()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);
        var amounts = new[] { 100m, 200m, 300m };
        foreach (var amount in amounts)
        {
            var invoiceId = await CreateSettledInvoice(admin, storeId, amount);
            var compute = await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split");
            Assert.Equal(200, compute.Status);
        }
        await CreateSettledInvoice(admin, storeId, 999m);

        async Task<List<decimal>> Page(int skip, int take)
        {
            var list = await merchantApi.GetAsync($"/plugins/api/tando/stores/{storeId}/splits?skip={skip}&take={take}");
            Assert.Equal(200, list.Status);
            return (await ParseJson(list)).EnumerateArray().Select(r => r.GetProperty("totalAmount").GetDecimal()).ToList();
        }

        Assert.Equal(new[] { 300m, 200m }, await Page(0, 2));
        Assert.Equal(new[] { 100m }, await Page(2, 2));
        Assert.Equal(new[] { 300m, 200m, 100m }, await Page(0, 50));
    }

    [Fact]
    public async Task ComputeSplit_OnUnsettledInvoice_IsRefusedAndCreatesNoPullPayment()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var api = await ApiContextFor(await MintApiKey(admin));
        var (storeId, merchantApi) = await SignupMerchant(api);
        await EnableSplit(merchantApi, storeId, 50m);
        var invoiceId = await CreateInvoice(admin, storeId, 1000m);

        var compute = await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split");
        Assert.Equal(400, compute.Status);
        Assert.Equal("invoice_not_settled", (await ParseJson(compute)).GetProperty("error").GetString());

        var get = await merchantApi.GetAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split");
        Assert.Equal(404, get.Status);

        var client = MerchantClient(storeId);
        Assert.Empty(await client.GetPullPayments(storeId));
    }

    [Fact]
    public async Task SettlingInvoice_RecordsSplitAutomatically_Once()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var api = await ApiContextFor(await MintApiKey(admin));
        var (storeId, merchantApi) = await SignupMerchant(api);
        await EnableSplit(merchantApi, storeId, 30m);
        var invoiceId = await CreatePaidInvoice(storeId, 1000m);

        var record = await WaitForSplit(merchantApi, storeId, invoiceId);
        Assert.Equal(30m, record.GetProperty("mpesaPercentage").GetDecimal());
        Assert.Equal(300m, record.GetProperty("mpesaPortionAmount").GetDecimal());
        Assert.Equal(700m, record.GetProperty("btcPortionAmount").GetDecimal());
        var recordedAt = record.GetProperty("recordedAt").GetString();

        var invoice = await ServerTester.PayTester.GetService<InvoiceRepository>().GetInvoice(invoiceId);
        ServerTester.PayTester.GetService<EventAggregator>().Publish(new InvoiceEvent(invoice, InvoiceEvent.PaidInFull));
        await Task.Delay(1000);

        var after = await WaitForSplit(merchantApi, storeId, invoiceId);
        Assert.Equal(recordedAt, after.GetProperty("recordedAt").GetString());
    }

    [Fact]
    public async Task ComputeSplit_WhenTreasuryRemovedAfterConfiguring_IsRefused()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);

        var (storeId, merchantApi) = await SignupMerchant(api);
        await EnableSplit(merchantApi, storeId, 50m);
        var subscriptionService = ServerTester.PayTester.GetService<TandoSubscriptionService>();
        var settings = await subscriptionService.GetSettings();
        settings.TreasuryLightningAddress = null;
        await subscriptionService.SaveSettings(settings);

        var invoiceId = await CreateSettledInvoice(admin, storeId, 1000m);
        var compute = await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split");
        Assert.Equal(400, compute.Status);
        Assert.Equal("treasury_not_configured", (await ParseJson(compute)).GetProperty("error").GetString());
        Assert.Empty(await MerchantClient(storeId).GetPullPayments(storeId));
    }

    private readonly HashSet<string> _storesWithWallet = new();
    private async Task EnsureStoreWallet(BTCPayServerClient client, string storeId)
    {
        if (_storesWithWallet.Add(storeId))
        {
            await client.GenerateOnChainWallet(storeId, "BTC-CHAIN", new GenerateOnChainWalletRequest());
            await client.UpdateStoreRateConfiguration(storeId, new StoreRateConfiguration
            {
                IsCustomScript = true,
                EffectiveScript = "BTC_KES = 10000000;"
            });
        }
    }
}