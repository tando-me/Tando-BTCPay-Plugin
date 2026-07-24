using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Tando;
using BTCPayServer.Plugins.Tando.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace BTCPayServer.Plugins.MassStoreGenerator;

[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UITandoSettingsController(TandoSubscriptionService subscriptionService, IStringLocalizer stringLocalizer) : Controller
{
    private IStringLocalizer StringLocalizer { get; } = stringLocalizer;

    [HttpGet("~/server/services/tando")]
    public async Task<IActionResult> Settings()
    {
        return View(await subscriptionService.GetSettings());
    }

    [HttpPost("~/server/services/tando")]
    public async Task<IActionResult> Settings(TandoSettings model)
    {
        model.SubscriptionOfferingId = string.IsNullOrWhiteSpace(model.SubscriptionOfferingId) ? null : model.SubscriptionOfferingId.Trim();
        await subscriptionService.SaveSettings(model);
        TempData[WellKnownTempData.SuccessMessage] = StringLocalizer["Tando settings updated"].Value;
        return RedirectToAction(nameof(Settings));
    }
}
