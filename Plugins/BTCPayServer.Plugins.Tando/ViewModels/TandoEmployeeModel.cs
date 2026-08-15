using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Tando.ViewModels;

public class TandoInviteEmployeeRequest
{
    [Required]
    public string PhoneNumber { get; set; }
}

public class TandoEmployeeResponse
{
    public string UserId { get; set; }
    public string PhoneNumber { get; set; }
    public bool AlreadyExisted { get; set; }
}
