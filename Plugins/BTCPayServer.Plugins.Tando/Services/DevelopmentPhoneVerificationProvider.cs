using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.Tando.Services;

/// <summary>Explicit synthetic fixtures for local HTTP testing; never real identity verification.</summary>
public sealed class DevelopmentPhoneVerificationProvider : PhoneVerificationProvider
{
    public DevelopmentPhoneVerificationProvider(IHostEnvironment environment)
    {
        if (!environment.IsDevelopment())
            throw new System.InvalidOperationException("Mock phone verification is allowed only in Development.");
    }

    public override string Mode => "Mock";
    public override bool IsMock => true;

    public override Task<PhoneVerificationResult> ValidateMobileNumber(string msisdn, string idType, string idNumber)
    {
        // Unknown inputs fail closed. Only this documented mock phone numbers can pass.
        var result = (idType, idNumber) switch
        {
            ("01", "mock-verified") => new PhoneVerificationResult(true, false, "Synthetic identity accepted."),
            ("01", "mock-unavailable") => new PhoneVerificationResult(false, true, "Simulated provider outage."),
            ("01", "mock-unconfigured") => new PhoneVerificationResult(false, true, "Simulated missing configuration.", false),
            _ => new PhoneVerificationResult(false, false, "Synthetic identity mismatch.")
        };
        return Task.FromResult(result);
    }
}
