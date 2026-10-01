using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Tando.Services;

public sealed class MockPhoneVerifier : IPhoneVerifier
{
    public string Name => "Mock";

    public Task<bool> IsConfigured() => Task.FromResult(true);

    public Task<PhoneVerificationOutcome> Verify(PhoneVerificationRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(request.IdNumber switch
        {
            "mock-verified" when request.IdType == "01" => PhoneVerificationOutcome.Verified,
            "mock-unavailable" => PhoneVerificationOutcome.Unavailable,
            "mock-unconfigured" => PhoneVerificationOutcome.NotConfigured,
            _ => PhoneVerificationOutcome.Mismatch
        });
}