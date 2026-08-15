using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Tando.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Tando;

[Route("~/plugins/api/tando/stores/{storeId}/invoices/{invoiceId}/split")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[IgnoreAntiforgeryToken]
public class TandoSplitController(TandoSplitService splitService) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Get(string storeId, string invoiceId)
    {
        var record = await splitService.GetSplit(storeId, invoiceId);
        if (record is null)
            return NotFound(new { error = "split_not_recorded" });

        return Ok(record);
    }

    [HttpPost]
    public async Task<IActionResult> Compute(string storeId, string invoiceId)
    {
        var (record, error) = await splitService.ComputeAndRecordSplit(storeId, invoiceId);
        if (error is not null)
            return NotFound(new { error });

        return Ok(record);
    }

    [HttpPut("settle")]
    public async Task<IActionResult> Settle(string storeId, string invoiceId)
    {
        var (success, error) = await splitService.MarkMpesaSettled(storeId, invoiceId);
        if (!success)
            return error == "invoice_not_found" ? NotFound(new { error }) : BadRequest(new { error });

        return Ok();
    }
}
