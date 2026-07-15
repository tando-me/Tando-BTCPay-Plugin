using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Tando.ViewModels;

public class TandoSignupRequest
{
    [Required]
    public string PhoneNumber { get; set; }

    [Required]
    public string IdNumber { get; set; }

    /// <summary>01 = National ID (default), 02 = Military ID, 05 = Passport</summary>
    public string IdType { get; set; } = "01";
}

public class TandoSubscribeRequest
{
    [Required]
    public string PhoneNumber { get; set; }

    [Required]
    public string IdNumber { get; set; }

    /// <summary>01 = National ID (default), 02 = Military ID, 05 = Passport</summary>
    public string IdType { get; set; } = "01";

    public string PlanId { get; set; }
}

public class TandoSignupResponse
{
    public string StoreId { get; set; }
    public string PhoneNumber { get; set; }
    public bool AlreadyExisted { get; set; }
    public string? PosAppId { get; set; }
    public string? CartAppId { get; set; }
    public bool PhoneNumberVerified { get; set; }
}