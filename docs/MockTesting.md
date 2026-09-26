# Development signup verification without Daraja

This provider simulates phone/ID verification only. BTCPay still authenticates the
request and performs its real subscription/store work. Successful fixtures can
create records in your local database.

Merge this property into the existing `Tando` object in
`btcpayserver/BTCPayServer/appsettings.dev.json`, preserving `DEBUG_PLUGINS`:

```json
"Tando": {
  "PhoneVerification": { "Mode": "Mock" }
}
```

Build from the repository root and restart BTCPay from its project directory:

```bash
dotnet build Plugins/BTCPayServer.Plugins.Tando
cd btcpayserver/BTCPayServer
dotnet run --launch-profile Bitcoin
```

In Postman, set the base URL to `http://localhost:14142`. Create a BTCPay API
key with unscoped Modify store settings permission and send it as
`Authorization: token YOUR_API_KEY`.

Use any valid Kenyan MSISDN and ID type `01`; the outcome depends only on `idNumber`:

```json
{
  "phoneNumber": "0712345678",
  "idNumber": "mock-verified",
  "idType": "01"
}
```

Change only `idNumber` to run negative cases:

| ID number | Result |
|---|---|
| `mock-verified` | Synthetic match; continues to subscription flow |
| `mock-unavailable` | 503 `phone_validation_unavailable` |
| `mock-unconfigured` | 503 `kyc_not_configured` |
| anything else | 400 `phone_id_mismatch` |

Test `POST /plugins/api/tando/signup`. The negative cases stop before
subscription or store creation. For a successful store, configure a local active
subscription offering and designated plan with a positive trial period in Tando
settings. A 503 `subscription_not_configured` means verification passed but
subscription setup is incomplete.

Unknown ID numbers or a non-01 idType mismatch. The mock provider is rejected
outside the Development environment, and production never falls back to it.


## Scope

Signup preserves the existing automatic trial subscription and store creation flow.
There is no plan-selection or signup/subscribe step. This mock covers verification
only; M-Pesa payout adapters, callbacks, retries, idempotency and reconciliation
are separate follow-up work. Merchant payout settings are stored separately and
are not verified by this signup check. Real Daraja contract and live behavior must
be validated with approved credentials before claiming end-to-end verification.
