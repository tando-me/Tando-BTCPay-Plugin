using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Tando.ViewModels;

public class TandoConnectLightningRequest
{
    [Required]
    public string ConnectionString { get; set; }
}