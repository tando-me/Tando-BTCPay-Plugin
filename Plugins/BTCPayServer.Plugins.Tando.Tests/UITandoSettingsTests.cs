using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Tests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Playwright;

namespace BTCPayServer.Plugins.Tando.Tests;

[Collection("Plugin Tests")]
[Trait("Category", "PlaywrightUITest")]
public class UITandoSettingsTests : PlaywrightBaseTest
{
    private readonly SharedPluginTestFixture _fixture;

    public UITandoSettingsTests(SharedPluginTestFixture fixture, ITestOutputHelper helper) : base(helper)
    {
        _fixture = fixture;
        if (_fixture.ServerTester == null) _fixture.Initialize(this);
        ServerTester = _fixture.ServerTester;
    }

    public ServerTester ServerTester { get; }

    private async Task<TestAccount> NewAdminAccount()
    {
        var a = ServerTester.NewAccount();
        await a.GrantAccessAsync();
        await a.MakeAdmin();
        return a;
    }

    [Fact]
    public async Task Settings_Get_ReturnsView()
    {
        var admin = await NewAdminAccount();
        var controller = ServerTester.PayTester.GetController<UITandoSettingsController>(
            admin.UserId, admin.StoreId, admin.IsAdmin);

        var result = await controller.Settings((string)null);

        var view = Assert.IsType<ViewResult>(result);
        Assert.IsType<TandoSettingsViewModel>(view.Model);
    }

    [Fact]
    public async Task Settings_Post_SavesSubscriptionConfig()
    {
        var admin = await NewAdminAccount();
        var client = await admin.CreateClient();
        var offering = await client.CreateOffering(admin.StoreId, new() { AppName = "Settings Test Offering" });
        var plan = await client.CreateOfferingPlan(admin.StoreId, offering.Id, new() { Name = "Plan", Price = 0m, TrialDays = 7 });

        var controller = ServerTester.PayTester.GetController<UITandoSettingsController>(
            admin.UserId, admin.StoreId, admin.IsAdmin);

        var result = await controller.Settings(new TandoSettingsViewModel
        {
            SubscriptionOfferingId = offering.Id,
            SubscriptionPlanId = plan.Id,
            MaxPayoutAmount = 500m
        });

        Assert.IsType<RedirectToActionResult>(result);
        var saved = await ServerTester.PayTester.GetService<TandoSubscriptionService>().GetSettings();
        Assert.Equal(offering.Id, saved.SubscriptionOfferingId);
        Assert.Equal(plan.Id, saved.SubscriptionPlanId);
        Assert.Equal(500m, saved.MaxPayoutAmount);
    }

    [Fact]
    public async Task Docs_RendersLocalViewer()
    {
        var admin = await NewAdminAccount();
        var controller = ServerTester.PayTester.GetController<UITandoSettingsController>(
            admin.UserId, admin.StoreId, admin.IsAdmin);

        Assert.IsType<ViewResult>(controller.Docs());
    }

    [Fact]
    public async Task OpenApiSpec_IsReachableWithoutAuthentication()
    {
        // [AllowAnonymous] on this one specifically - worth testing over real HTTP,
        // not a direct controller call, since that's the part actually being verified.
        await InitializePlaywright(ServerTester);
        var api = await Playwright.APIRequest.NewContextAsync(new APIRequestNewContextOptions
        {
            BaseURL = ServerUri.ToString(),
            IgnoreHTTPSErrors = true
        });

        var response = await api.GetAsync("/server/tando/openapi.yaml");
        Assert.Equal(200, response.Status);
        var body = await response.TextAsync();
        Assert.Contains("openapi", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Settings_InvalidPayoutLimit_DoesNotOverwriteSettings(int limit)
    {
        var admin = await NewAdminAccount();
        var client = await admin.CreateClient();
        var offering = await client.CreateOffering(admin.StoreId, new() { AppName = "Validation Test" });
        var plan = await client.CreateOfferingPlan(admin.StoreId, offering.Id, new() { Name = "Plan", Price = 0m, TrialDays = 7 });
        var service = ServerTester.PayTester.GetService<TandoSubscriptionService>();
        await service.SaveSettings(new TandoSettings
        {
            SubscriptionOfferingId = offering.Id, SubscriptionPlanId = plan.Id,
            MaxPayoutAmount = 250m
        });
        var controller = ServerTester.PayTester.GetController<UITandoSettingsController>(admin.UserId, admin.StoreId, admin.IsAdmin);
        var result = await controller.Settings(new TandoSettingsViewModel
        {
            SubscriptionOfferingId = offering.Id, SubscriptionPlanId = plan.Id,
            MaxPayoutAmount = limit
        });
        Assert.IsType<ViewResult>(result);
        Assert.NotEmpty(controller.ModelState[nameof(TandoSettingsViewModel.MaxPayoutAmount)]!.Errors);
        var saved = await service.GetSettings();
        Assert.Equal(offering.Id, saved.SubscriptionOfferingId);
        Assert.Equal(plan.Id, saved.SubscriptionPlanId);
        Assert.Equal(250m, saved.MaxPayoutAmount);
    }

}
