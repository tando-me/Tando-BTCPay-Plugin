using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Tando.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.MassStoreGenerator;

[Route("~/plugins/tando/daraja")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyServerSettings)]
[AutoValidateAntiforgeryToken]
public class UITandoDarajaController(DarajaMobileNumberValidationService darajaValidation) : Controller
{
    [HttpGet("settings")]
    public async Task<IActionResult> Settings()
    {
        return View(await darajaValidation.GetSettings());
    }

    [HttpPost("settings")]
    public async Task<IActionResult> Settings(TandoDarajaSettings model)
    {
        if (!ModelState.IsValid)
            return View(model);

        await darajaValidation.UpdateSettings(model);
        TempData[WellKnownTempData.SuccessMessage] = "Daraja settings saved";
        return RedirectToAction(nameof(Settings));
    }
}
