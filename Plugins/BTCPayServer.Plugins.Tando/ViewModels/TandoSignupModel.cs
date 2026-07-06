using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Tando.ViewModels;

public class TandoSignupRequest
{
    [Required]
    public string PhoneNumber { get; set; }
}

public class TandoSignupResponse
{
    public string StoreId { get; set; }
    public string PhoneNumber { get; set; }
    public bool AlreadyExisted { get; set; }
}