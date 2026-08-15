using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Tando.Services;
using BTCPayServer.Plugins.Tando.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Tando;

[Route("~/plugins/api/tando/stores/{storeId}/products")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[IgnoreAntiforgeryToken]
public class TandoProductsController(TandoProductProvisioningService productProvisioningService) : Controller
{
    [HttpGet]
    public async Task<IActionResult> List(string storeId)
    {
        var items = await productProvisioningService.ListProducts(storeId);
        return Ok(items.Select(ToResponse));
    }

    [HttpPost]
    public async Task<IActionResult> Create(string storeId, [FromBody] TandoCreateProductRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
            return BadRequest(new { error = "name_required" });
        if (request.Price < 0)
            return BadRequest(new { error = "invalid_price" });

        var (hasPos, hasCart) = await productProvisioningService.GetProvisioningStatus(storeId);
        if (!hasPos && !hasCart)
            return NotFound(new { error = "store_not_provisioned", message = "This store has no POS/Cart apps yet - complete onboarding first." });

        var item = await productProvisioningService.AddProduct(storeId, request.Name.Trim(), request.Price, request.ImageUrl);
        return Ok(ToResponse(item));
    }

    [HttpDelete("{itemId}")]
    public async Task<IActionResult> Delete(string storeId, string itemId)
    {
        var removed = await productProvisioningService.RemoveProduct(storeId, itemId);
        if (!removed)
            return NotFound(new { error = "product_not_found" });

        return Ok();
    }

    private static TandoProductResponse ToResponse(Client.Models.AppItem item) => new()
    {
        Id = item.Id,
        Name = item.Title,
        Price = item.Price,
        ImageUrl = item.Image
    };
}
