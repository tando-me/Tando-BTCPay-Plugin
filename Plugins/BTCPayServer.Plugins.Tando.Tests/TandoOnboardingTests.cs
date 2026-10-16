using System.Text.Json;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Services.Stores;
using BTCPayServer.Tests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Playwright;

namespace BTCPayServer.Plugins.Tando.Tests;

[Collection("Plugin Tests")]
[Trait("Category", "PlaywrightUITest")]
public class TandoOnboardingTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public TandoOnboardingTests(SharedPluginTestFixture fixture, ITestOutputHelper helper) : base(helper)
    {
        _fixture = fixture;
        if (_fixture.ServerTester == null) _fixture.Initialize(this);
        ServerTester = _fixture.ServerTester;
    }

    public ServerTester ServerTester { get; }

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

    private BTCPayServerClient MerchantClient(string storeId) => new(ServerUri, _merchantKeys[storeId]);


    [Fact]
    public async Task SubscriptionStatus_InvalidPhone_ReturnsBadRequest()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);

        var status = await api.GetAsync("/plugins/api/tando/subscription/status?phoneNumber=not-a-phone");
        Assert.Equal(400, status.Status);
        var body = await ParseJson(status);
        Assert.Equal("invalid_phone_number", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SubscriptionStatus_ValidPhone_NotYetSubscribed_ReturnsInactive()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);

        var status = await api.GetAsync($"/plugins/api/tando/subscription/status?phoneNumber={RandomKenyanPhone()}");
        Assert.Equal(200, status.Status);
        var body = await ParseJson(status);
        Assert.False(body.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task SubscriptionPlans_WhenConfigured_ReturnsConfiguredTrue()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);

        var plans = await api.GetAsync("/plugins/api/tando/subscription/plans");
        Assert.Equal(200, plans.Status);
        var body = await ParseJson(plans);
        Assert.True(body.GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task Signup_MissingPhoneNumber_ReturnsBadRequest()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);

        var signup = await api.PostAsync("/plugins/api/tando/signup",
            new APIRequestContextOptions { DataObject = new { phoneNumber = "" } });
        Assert.Equal(400, signup.Status);
        var body = await ParseJson(signup);
        Assert.Equal("phone_number_required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Signup_SamePhoneTwice_SecondCallReturnsAlreadyExistedTrue()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var phone = RandomKenyanPhone();

        var (firstStoreId, _) = await SignupMerchant(api, phone);

        var second = await api.PostAsync("/plugins/api/tando/signup",
            new APIRequestContextOptions { DataObject = new { phoneNumber = phone } });
        Assert.Equal(200, second.Status);
        var secondBody = await ParseJson(second);
        Assert.True(secondBody.GetProperty("alreadyExisted").GetBoolean());
        Assert.Equal(firstStoreId, secondBody.GetProperty("storeId").GetString());
    }

    [Fact]
    public async Task Signup_ProvisionsPosAndCartApps()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);

        var response = await api.PostAsync("/plugins/api/tando/signup",
            new APIRequestContextOptions { DataObject = new { phoneNumber = RandomKenyanPhone() } });
        Assert.Equal(200, response.Status);
        var body = await ParseJson(response);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("posAppId").GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("cartAppId").GetString()));
    }

    [Fact]
    public async Task ConnectLightning_MissingConnectionString_ReturnsBadRequest()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        var connect = await merchantApi.PutAsync($"/plugins/api/tando/stores/{storeId}/lightning/connect",
            new APIRequestContextOptions { DataObject = new { connectionString = "" } });
        Assert.Equal(400, connect.Status);
        var body = await ParseJson(connect);
        Assert.Equal("connection_string_required", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ConnectLightning_OtherMerchantsStore_IsForbidden()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, _) = await SignupMerchant(api);
        var (_, otherMerchantApi) = await SignupMerchant(api);

        var connect = await otherMerchantApi.PutAsync($"/plugins/api/tando/stores/{storeId}/lightning/connect",
            new APIRequestContextOptions { DataObject = new { connectionString = "type=clightning;server=unix://tmp/socket" } });
        Assert.Equal(403, connect.Status);
    }

    [Fact]
    public async Task ConnectLightning_ValidConnectionString_Succeeds()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        var connect = await merchantApi.PutAsync($"/plugins/api/tando/stores/{storeId}/lightning/connect",
            new APIRequestContextOptions { DataObject = new { connectionString = "type=clightning;server=unix://tmp/socket" } });
        Assert.Equal(200, connect.Status);
        var body = await ParseJson(connect);
        Assert.Equal(storeId, body.GetProperty("storeId").GetString());
    }

    [Fact]
    public async Task Signup_MakesTheMerchantTheOnlyOwner()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);

        var signup = await api.PostAsync("/plugins/api/tando/signup",
            new APIRequestContextOptions { DataObject = new { phoneNumber = RandomKenyanPhone() } });
        Assert.Equal(200, signup.Status);
        var body = await ParseJson(signup);
        var storeId = body.GetProperty("storeId").GetString();
        var merchantId = body.GetProperty("userId").GetString();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("apiKey").GetString()));

        var users = await ServerTester.PayTester.GetService<StoreRepository>().GetStoreUsers(storeId);
        var member = Assert.Single(users);
        Assert.Equal(merchantId, member.Id);
        Assert.Equal(StoreRoleId.Owner.Role, member.StoreRole.Role);
    }

    [Fact]
    public async Task Signup_RequiresTheBackendKey()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (_, merchantApi) = await SignupMerchant(api);

        var signup = await merchantApi.PostAsync("/plugins/api/tando/signup",
            new APIRequestContextOptions { DataObject = new { phoneNumber = RandomKenyanPhone() } });
        Assert.Equal(403, signup.Status);
    }

    [Fact]
    public async Task IssueAppKey_ForInvitedEmployee_ReturnsKeyAndEmployeeRole()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);
        var (storeId, merchantApi) = await SignupMerchant(api);

        var employeePhone = RandomKenyanPhone();
        var invite = await merchantApi.PostAsync($"/plugins/api/tando/stores/{storeId}/employees",
            new APIRequestContextOptions { DataObject = new { phoneNumber = employeePhone } });
        Assert.Equal(200, invite.Status);

        var issue = await api.PostAsync("/plugins/api/tando/users/api-keys",
            new APIRequestContextOptions { DataObject = new { phoneNumber = employeePhone, deviceName = "till-1" } });
        Assert.Equal(200, issue.Status);
        var body = await ParseJson(issue);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("apiKey").GetString()));
        var store = Assert.Single(body.GetProperty("stores").EnumerateArray());
        Assert.Equal(storeId, store.GetProperty("storeId").GetString());
        Assert.Equal("Employee", store.GetProperty("role").GetString());
    }

    [Fact]
    public async Task IssueAppKey_UnknownPhone_ReturnsNotFound()
    {
        await InitializePlaywright(ServerTester);
        var admin = await NewAdminAccount();
        await EnsureTandoSubscriptionConfigured(admin);
        var adminKey = await MintApiKey(admin);
        var api = await ApiContextFor(adminKey);

        var issue = await api.PostAsync("/plugins/api/tando/users/api-keys",
            new APIRequestContextOptions { DataObject = new { phoneNumber = RandomKenyanPhone() } });
        Assert.Equal(404, issue.Status);
    }
}
