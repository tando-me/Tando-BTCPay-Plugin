# BTCPayServer.Plugins.Tando

Plugin project for the **Kenyan Merchant BTCPay PoS plugin** — mobile Point of Sale accepting both M-Pesa (via PSP) and Bitcoin (Lightning), two independent rails with no forced conversion.

**The spec lives in the repository root [README](../../README.md)** — design principles, two-rail settlement model, LSP/PSP architecture, and the open pre-implementation items. Read it before touching this project.

**Current status (2026-07-22):** early implementation. The onboarding/store-creation slice is merged (PR #1, `ft/onboarding`); the remaining phases and the spec's open design decisions (node stack, LSP protocol, recovery, refunds, pricing, …) are still ahead.
