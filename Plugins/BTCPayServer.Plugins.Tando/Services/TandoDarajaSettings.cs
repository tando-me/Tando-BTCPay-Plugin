using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Tando.Services;

public class TandoDarajaSettings
{
    [Display(Name = "Consumer Key")]
    public string ConsumerKey { get; set; }

    [Display(Name = "Consumer Secret")]
    public string ConsumerSecret { get; set; }

    [Display(Name = "Organization Short Code")]
    public string ShortCode { get; set; }

    [Display(Name = "Use sandbox environment")]
    public bool UseSandbox { get; set; } = true;

    public bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(ConsumerKey) &&
        !string.IsNullOrWhiteSpace(ConsumerSecret) &&
        !string.IsNullOrWhiteSpace(ShortCode);
}
