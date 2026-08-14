using System;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Tando.Helper;
using BTCPayServer.Plugins.Tando.ViewModels;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using static BTCPayServer.Services.Stores.StoreRepository;

namespace BTCPayServer.Plugins.MassStoreGenerator;

[Route("~/plugins/api/tando/stores/{storeId}/employees")]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Greenfield)]
[IgnoreAntiforgeryToken]
public class TandoEmployeesController(StoreRepository storeRepository, UserManager<ApplicationUser> userManager) : Controller
{
    [HttpGet]
    public async Task<IActionResult> List(string storeId)
    {
        var users = await storeRepository.GetStoreUsers(storeId, new[] { StoreRoleId.Employee });
        return Ok(users.Select(u => new
        {
            userId = u.Id,
            email = u.Email,
            phoneNumber = u.UserBlob?.Name
        }));
    }

    [HttpPost]
    public async Task<IActionResult> Invite(string storeId, [FromBody] TandoInviteEmployeeRequest request)
    {
        var normalizedPhone = KenyanPhoneNumber.Normalize(request?.PhoneNumber);
        if (normalizedPhone is null)
            return BadRequest(new { error = "invalid_phone_number", detail = $"Expected a Kenyan MSISDN, e.g. {KenyanPhoneNumber.ExampleFormat}." });

        var syntheticEmail = $"{normalizedPhone}@bitcoin.co.ke";
        var user = await userManager.FindByEmailAsync(syntheticEmail);
        var alreadyExisted = user is not null;
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName = syntheticEmail,
                Email = syntheticEmail,
                RequiresEmailConfirmation = false,
                RequiresApproval = false,
                Approved = true,
                Created = DateTimeOffset.UtcNow
            };
            var blob = user.GetBlob() ?? new UserBlob();
            blob.Name = normalizedPhone;
            user.SetBlob(blob);

            var identityResult = await userManager.CreateAsync(user);
            if (!identityResult.Succeeded)
                return BadRequest(new { error = "employee_creation_failed", detail = identityResult.Errors.Select(e => e.Description) });
        }

        var result = await storeRepository.AddOrUpdateStoreUser(storeId, user.Id, StoreRoleId.Employee);
        if (result is not AddOrUpdateStoreUserResult.Success)
        {
            return result switch
            {
                AddOrUpdateStoreUserResult.DuplicateRole => Ok(new TandoEmployeeResponse { UserId = user.Id, PhoneNumber = normalizedPhone, AlreadyExisted = true }),
                _ => StatusCode(500, new { error = "employee_role_assignment_failed" })
            };
        }
        return Ok(new TandoEmployeeResponse { UserId = user.Id, PhoneNumber = normalizedPhone, AlreadyExisted = alreadyExisted });
    }

    [HttpDelete("{userId}")]
    public async Task<IActionResult> Revoke(string storeId, string userId)
    {
        var removed = await storeRepository.RemoveStoreUser(storeId, userId);
        if (!removed)
            return BadRequest(new { error = "cannot_remove_last_owner_or_user_not_found" });

        return Ok();
    }
}

