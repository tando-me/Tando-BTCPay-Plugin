using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace BTCPayServer.Plugins.Tando.Services;

public class PhoneVerificationService
{
    public const string ModeConfigKey = "Tando:PhoneVerification:Mode";
    public static readonly string[] IdTypes = ["01", "02", "05"]; // 01 = National ID (default), 02 = Military ID, 05 = Passport

    public PhoneVerificationService(IEnumerable<IPhoneVerifier> verifiers, IConfiguration configuration, 
        IHostEnvironment environment)
    {
        var mode = configuration[ModeConfigKey];
        Active = string.IsNullOrWhiteSpace(mode)
            ? verifiers.LastOrDefault(v => v is not MockPhoneVerifier)
            : verifiers.LastOrDefault(v => string.Equals(v.Name, mode, StringComparison.OrdinalIgnoreCase))
              ?? throw new InvalidOperationException($"Tando phone verification mode '{mode}' is not registered.");

        if (Active is MockPhoneVerifier && !environment.IsDevelopment())
            throw new InvalidOperationException("Tando mock phone verification is only allowed in Development.");
    }

    public IPhoneVerifier? Active { get; }

    public async Task<PhoneVerificationOutcome> Verify(string msisdn, string? idType, string? idNumber, CancellationToken cancellationToken = default)
    {
        if (Active is null)
            return PhoneVerificationOutcome.Skipped;

        if (string.IsNullOrWhiteSpace(idNumber))
            return PhoneVerificationOutcome.IdNumberRequired;

        idType = string.IsNullOrWhiteSpace(idType) ? IdTypes[0] : idType.Trim();
        if (!IdTypes.Contains(idType))
            return PhoneVerificationOutcome.InvalidIdType;

        return await Active.Verify(new PhoneVerificationRequest(msisdn, idType, idNumber.Trim()), cancellationToken);
    }
}
