using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.MassStoreGenerator;

[Route("~/plugins/api/tando/stores/{storeId}")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[IgnoreAntiforgeryToken]
public class TandoMerchantSettingsController(TandoMerchantSettingsService settingsService) : Controller
{
    [HttpGet("mpesa")]
    public async Task<IActionResult> GetMpesaSettings(string storeId)
    {
        var settings = await settingsService.GetMpesaSettings(storeId);
        if (settings is null)
            return NotFound(new { error = "not_configured" });

        return Ok(settings);
    }

    [HttpPut("mpesa")]
    public async Task<IActionResult> SaveMpesaSettings(string storeId, [FromBody] TandoMpesaSettingsRequest request)
    {
        if (request is null)
            return BadRequest(new { error = "request_required" });

        var validationError = settingsService.ValidateMpesaSettings(request);
        if (validationError is not null)
            return BadRequest(new { error = "validation_failed", field = validationError.Field, message = validationError.Message });

        var saved = await settingsService.SaveMpesaSettings(storeId, request);
        if (!saved)
            return NotFound(new { error = "store_not_found" });

        return Ok();
    }

    [HttpGet("split-config")]
    public async Task<IActionResult> GetSplitConfig(string storeId) => Ok(await settingsService.GetSplitConfig(storeId));

    [HttpPut("split-config")]
    public async Task<IActionResult> SaveSplitConfig(string storeId, [FromBody] TandoSplitConfigRequest request)
    {
        if (request is null || request.MpesaPercentage is < 0 or > 100)
            return BadRequest(new { error = "invalid_percentage", detail = "MpesaPercentage must be between 0 and 100." });

        var mpesaSettings = await settingsService.GetMpesaSettings(storeId);
        if (request.MpesaPercentage > 0 && mpesaSettings is null)
        {
            return BadRequest(new
            {
                error = "mpesa_destination_required",
                message = "Set an M-Pesa payout destination before enabling a split above 0%."
            });
        }

        var saved = await settingsService.SaveSplitConfig(storeId, request);
        if (!saved)
            return NotFound(new { error = "store_not_found" });

        return Ok();
    }
}
