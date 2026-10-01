using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Tando.Services;

public abstract class PhoneVerificationProvider
{
    public abstract string Mode { get; }
    public virtual bool IsMock => false;
    public virtual bool Enabled => true;
    public virtual Task<bool> IsConfigured() => Task.FromResult(true);

    public abstract Task<PhoneVerificationResult> ValidateMobileNumber(string msisdn, string idType, string idNumber);
}

public record PhoneVerificationResult(bool Matches, bool ServiceError, string Detail, bool Configured = true);

public sealed class NoPhoneVerificationProvider : PhoneVerificationProvider
{
    public override string Mode => "None";
    public override bool Enabled => false;
    public override Task<bool> IsConfigured() => Task.FromResult(false);

    public override Task<PhoneVerificationResult> ValidateMobileNumber(string msisdn, string idType, string idNumber) =>
        Task.FromResult(new PhoneVerificationResult(false, false, "Phone verification is disabled.", Configured: false));
}