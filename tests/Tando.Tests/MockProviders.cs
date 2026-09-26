using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Tando.Services;

namespace Tando.Tests;

// Test assembly only: never registered by the production plugin.
public sealed class MockDarajaProvider(PhoneVerificationResult result) : PhoneVerificationProvider
{
    public int Calls { get; private set; }
    public override Task<PhoneVerificationResult> ValidateMobileNumber(string msisdn, string idType, string idNumber)
    {
        Calls++;
        return Task.FromResult(result);
    }
}

