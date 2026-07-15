using System;
using System.Text;

namespace BTCPayServer.Plugins.Tando.Services;

/// <summary>
/// Populated from environment variables (or appsettings.json).
/// Set: Splice__Email, Splice__Password, Splice__BaseUrl, Splice__UseSandbox
/// </summary>
public class SpliceSettings
{
    public string Email { get; set; }
    public string Password { get; set; }
    public string BaseUrl { get; set; } = "https://api.splice.africa";
    public bool UseSandbox { get; set; } = true;

    public bool IsConfigured() =>
        !string.IsNullOrWhiteSpace(Email) &&
        !string.IsNullOrWhiteSpace(Password);

    public string BasicAuthHeader() =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Email}:{Password}"));
}
