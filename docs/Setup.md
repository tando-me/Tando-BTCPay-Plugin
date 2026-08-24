# Local development setup

How to run BTCPay Server locally with the Tando plugin loaded, on regtest, with a working Lightning setup for test payments.

> **Verified on Linux** (Ubuntu 22.04.5 LTS, .NET SDK 10.0.201). macOS should work with the same commands but is untested. On Windows, use WSL2. The docker test relies on bash scripts. Updates verifying other platforms are welcome.

## Prerequisites

The BTCPay Server source is vendored in this repository under `btcpayserver/`, there are no submodules to initialize.

All paths below are relative to the repository root unless a `cd` says otherwise.

## 1. Start the regtest dependencies

BTCPay's test harness provides a docker compose stack with everything the `Bitcoin` launch profile expects: PostgreSQL (port 39372), NBXplorer (port 32838), a regtest bitcoind, and Lightning nodes (c-lightning and LND) for both a "merchant" and a "customer".

```bash
cd btcpayserver/BTCPayServer.Tests
docker compose up -d dev
cd ../..   # return to the repository root for the next steps
```

**Linux gotcha:** the helper scripts in `BTCPayServer.Tests/` may not be executable after checkout. If you get `Permission denied` running any `docker-*.sh` script, fix it once with:

```bash
chmod +x btcpayserver/BTCPayServer.Tests/*.sh
```

## 2. Build the plugin

From the repository root:

```bash
dotnet build Plugins/BTCPayServer.Plugins.Tando
```

This produces `Plugins/BTCPayServer.Plugins.Tando/bin/Debug/net10.0/BTCPayServer.Plugins.Tando.dll`, which is what BTCPay will load in the next step.

## 3. Point BTCPay at the plugin (`appsettings.dev.json`)

BTCPay Server loads development plugins from the paths listed in the `DEBUG_PLUGINS` key of `btcpayserver/BTCPayServer/appsettings.dev.json`. Generate that file either way:

**Option A — run ConfigBuilder** (targets net8.0, so roll it forward to your installed SDK):

```bash
dotnet build ConfigBuilder
cd ConfigBuilder/bin/Debug/net8.0
dotnet --roll-forward LatestMajor ConfigBuilder.dll
cd -
```

ConfigBuilder resolves the plugin's absolute path and writes `btcpayserver/BTCPayServer/appsettings.dev.json`. Note it must be run from its output directory as shown — it locates the `Plugins/` folder and the output file with relative paths.

**Option B — write the file yourself** (from the repository root; `$PWD` expands to your absolute repo path, which is exactly what ConfigBuilder produces):

```bash
cat > btcpayserver/BTCPayServer/appsettings.dev.json <<EOF
{"DEBUG_PLUGINS":"$PWD/Plugins/BTCPayServer.Plugins.Tando/bin/Debug/net10.0/BTCPayServer.Plugins.Tando.dll;"}
EOF
```

Either way, verify the file contains the absolute path to the plugin DLL you built in step 2.

## 4. Run BTCPay Server with the plugin loaded

```bash
cd btcpayserver/BTCPayServer
dotnet run --launch-profile Bitcoin
```

**This must be run from `btcpayserver/BTCPayServer`** (or pass `--project btcpayserver/BTCPayServer`).

`dotnet run` from anywhere else fails with "Couldn't find a project to run."

The server comes up at <http://localhost:14142>. On your first visit, register an account, the `Bitcoin` profile allows admin registration, so the first registered user becomes the server admin.

### Verify the plugin loaded

- The **Tando** item appears in the sidebar navigation once you have a store selected.
- The plugin is listed under **Server Settings → Plugins**.

## 5. Make a test Lightning payment


One-time: open channels between the dockerized Lightning nodes (regtest blocks are mined automatically by the script):


```bash
cd btcpayserver/BTCPayServer.Tests
./docker-lightning-channel-setup.sh
```


Then:


1. In the dashboard, connect your store's Lightning wallet (the `Bitcoin` profile pre-wires a local c-lightning node at `type=clightning;server=tcp://127.0.0.1:30993/`).
2. Create an invoice from the dashboard and open its checkout page.
3. Pay it from the dockerized "customer" node:


```bash
./docker-customer-lightning-cli.sh pay <BOLT11 invoice from the checkout page>
```


The invoice should flip to settled in the dashboard.


To tear the channels down later: `./docker-lightning-channel-teardown.sh`.
