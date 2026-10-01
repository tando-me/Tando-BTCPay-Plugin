using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Tando.Services;

public interface IPhoneVerifier
{
    string Name { get; }
    Task<bool> IsConfigured();
    Task<PhoneVerificationOutcome> Verify(PhoneVerificationRequest request, CancellationToken cancellationToken = default);
}

public record PhoneVerificationRequest(string Msisdn, string IdType, string IdNumber);

public enum PhoneVerificationOutcome{ Skipped, Verified, Mismatch, Unavailable, NotConfigured, IdNumberRequired, InvalidIdType }