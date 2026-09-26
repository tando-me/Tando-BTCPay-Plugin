using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Tando.Services;

/// <summary>External identity verification boundary. Unavailable verification must block signup.</summary>
public abstract class PhoneVerificationProvider
{
    public virtual string Mode => "Daraja";
    public virtual bool IsMock => false;

    public abstract Task<PhoneVerificationResult> ValidateMobileNumber(string msisdn, string idType, string idNumber);
}

public record PhoneVerificationResult(bool Matches, bool ServiceError, string Detail, bool Configured = true);
