using System.Text.Json;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Plugins.Tando.Helper;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Services.Stores;
using BTCPayServer.Tests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Playwright;

namespace BTCPayServer.Plugins.Tando.Tests;

[Collection("Plugin Tests")]
[Trait("Category", "PlaywrightUITest")]
public class TandoEmployeesTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public TandoEmployeesTests(SharedPluginTestFixture fixture, ITestOutputHelper helper) : base(helper)
    {
        _fixture = fixture;
        if (_fixture.ServerTester == null) _fixture.Initialize(this);
        ServerTester = _fixture.ServerTester;
    }

    public ServerTester ServerTester { get; }

    // --- Helpers mirrored from TandoSplitTests to keep this file self-contained ---

    private async Task EnsureTandoSubscriptionConfigured(TestAccount admin)
    {
        var client = await admin.CreateClient();
        var offering = await client.CreateOffering(admin.StoreId, new OfferingModel { AppName = "Tando Test Offering" });
        var plan = await client.CreateOfferingPlan(admin.StoreId, offering.Id, new() { Name = "Trial Plan", Price = 0m, TrialDays = 7 });
        var subscriptionService = ServerTester.PayTester.GetService<TandoSubscriptionService>();
        await subscriptionService.SaveSettings(new TandoSettings
        {
            SubscriptionOfferingId = offering.Id,
            SubscriptionPlanId = plan.Id
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


    [Fact]
    public async Task Invite_WithValidPhone_CreatesEmployeeAndAssignsRole()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        var phone = RandomKenyanPhone();
        var invite = await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/employees",
            new APIRequestContextOptions { DataObject = new { phoneNumber = phone } });

        Assert.Equal(200, invite.Status);
        var body = await ParseJson(invite);
        Assert.False(body.GetProperty("alreadyExisted").GetBoolean());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("userId").GetString()));
    }

    [Fact]
    public async Task Invite_WithInvalidPhone_ReturnsBadRequest()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        var invite = await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/employees",
            new APIRequestContextOptions { DataObject = new { phoneNumber = "not-a-phone-number" } });

        Assert.Equal(400, invite.Status);
        var body = await ParseJson(invite);
        Assert.Equal("invalid_phone_number", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Invite_SamePhoneTwice_SecondCallReturnsAlreadyExistedTrue()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);
        var phone = RandomKenyanPhone();

        var first = await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/employees",
            new APIRequestContextOptions { DataObject = new { phoneNumber = phone } });
        Assert.Equal(200, first.Status);
        var firstBody = await ParseJson(first);
        Assert.False(firstBody.GetProperty("alreadyExisted").GetBoolean());

        var second = await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/employees",
            new APIRequestContextOptions { DataObject = new { phoneNumber = phone } });
        Assert.Equal(200, second.Status);
        var secondBody = await ParseJson(second);
        Assert.True(secondBody.GetProperty("alreadyExisted").GetBoolean());
        Assert.Equal(firstBody.GetProperty("userId").GetString(), secondBody.GetProperty("userId").GetString());
    }

    [Fact]
    public async Task List_ReturnsInvitedEmployeesForStore()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        var phoneA = RandomKenyanPhone();
        var phoneB = RandomKenyanPhone();
        await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/employees",
            new APIRequestContextOptions { DataObject = new { phoneNumber = phoneA } });
        await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/employees",
            new APIRequestContextOptions { DataObject = new { phoneNumber = phoneB } });

        var list = await merchantApi.GetAsync($"/plugins/api/tando/stores/{storeId}/employees");
        Assert.Equal(200, list.Status);
        var records = await ParseJson(list);

        var phones = records.EnumerateArray().Select(r => r.GetProperty("phoneNumber").GetString()).ToList();
        Assert.Contains(KenyanPhoneNumber.Normalize(phoneA), phones);
        Assert.Contains(KenyanPhoneNumber.Normalize(phoneB), phones);
    }

    [Fact]
    public async Task Revoke_ExistingEmployee_RemovesThemFromStore()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        var phone = RandomKenyanPhone();
        var invite = await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/employees",
            new APIRequestContextOptions { DataObject = new { phoneNumber = phone } });
        var userId = (await ParseJson(invite)).GetProperty("userId").GetString();

        var revoke = await merchantApi.DeleteAsync($"/plugins/api/tando/stores/{storeId}/employees/{userId}");
        Assert.Equal(200, revoke.Status);

        var list = await merchantApi.GetAsync($"/plugins/api/tando/stores/{storeId}/employees");
        var records = await ParseJson(list);
        var userIds = records.EnumerateArray().Select(r => r.GetProperty("userId").GetString()).ToList();
        Assert.DoesNotContain(userId, userIds);
    }

    [Fact]
    public async Task Revoke_UnknownUserId_ReturnsBadRequest()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        var revoke = await merchantApi.DeleteAsync($"/plugins/api/tando/stores/{storeId}/employees/does-not-exist");
        Assert.Equal(400, revoke.Status);
        var body = await ParseJson(revoke);
        Assert.Equal("cannot_remove_last_owner_or_user_not_found", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Revoke_Owner_IsRefused()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        // Second owner, so BTCPay's last-owner guard can't be what blocks removal.
        var coOwner = ServerTester.NewAccount();
        await coOwner.GrantAccessAsync();
        var storeRepository = ServerTester.PayTester.GetService<StoreRepository>();
        await storeRepository.AddOrUpdateStoreUser(storeId, coOwner.UserId, StoreRoleId.Owner);

        var revoke = await merchantApi.DeleteAsync($"/plugins/api/tando/stores/{storeId}/employees/{coOwner.UserId}");
        Assert.Equal(400, revoke.Status);
        var body = await ParseJson(revoke);
        Assert.Equal("cannot_remove_last_owner_or_user_not_found", body.GetProperty("error").GetString());

        var owners = await storeRepository.GetStoreUsers(storeId, new[] { StoreRoleId.Owner });
        Assert.Contains(owners, u => u.Id == coOwner.UserId);
    }
}
