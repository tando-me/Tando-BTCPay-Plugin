using System;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Tando.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.MassStoreGenerator;

[Route("~/plugins/tando/daraja")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyServerSettings)]
[AutoValidateAntiforgeryToken]
public class UITandoDarajaController(IServiceProvider serviceProvider) : Controller
{
    private DarajaPhoneVerifier? Daraja => serviceProvider.GetService<DarajaPhoneVerifier>();

    [HttpGet("settings")]
    public async Task<IActionResult> Settings()
    {
        if (Daraja is not { } daraja)
            return NotFound();
        return View(await daraja.GetSettings());
    }

    [HttpPost("settings")]
    public async Task<IActionResult> Settings(DarajaSettings model)
    {
        if (Daraja is not { } daraja)
            return NotFound();

        await daraja.UpdateSettings(model);
        TempData[WellKnownTempData.SuccessMessage] = "Daraja settings saved";
        return RedirectToAction(nameof(Settings));
    }
}