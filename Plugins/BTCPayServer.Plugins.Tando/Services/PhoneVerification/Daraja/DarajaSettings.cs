using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Tando.Services;

public class DarajaSettings
{
    [Display(Name = "Consumer key")]
    public string? ConsumerKey { get; set; }

    [Display(Name = "Consumer secret")]
    public string? ConsumerSecret { get; set; }

    [Display(Name = "Short code")]
    public string? ShortCode { get; set; }

    [Display(Name = "Use sandbox")]
    public bool UseSandbox { get; set; } = true;

    public bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(ConsumerKey) &&
        !string.IsNullOrWhiteSpace(ConsumerSecret) &&
        !string.IsNullOrWhiteSpace(ShortCode);

    public string GetBaseUrl() => UseSandbox ? "https://sandbox.safaricom.co.ke" : "https://api.safaricom.co.ke";
}