# Quartermaster
**NOTICE OF PROPRIETARY SYSTEMS.**
You are accessing QUARTERMASTER™, a sanctioned fleet-logistics telemetry apparatus,
brought to you by the Formidan Mandate.

## What is Quartermaster
A Space Engineers plugin, loaded via **Pulsar**, that pipes **tagged data packets out of block Custom Data**
to a backend you run (or a local file). It is a generic, **format-agnostic pipe**: it reads any block whose
Custom Data begins with the marker **`[QM:<tag>]`**, wraps each packet in a small standard envelope, and ships
it. **It never interprets the payload, and never senses anything itself.**

The access guardrail is mechanical: the plugin reads Custom Data **only on grids you can vanilla-access right now**.
Your own grids, or shared faction grids that you're controlling, standing at on foot, or within a broadcasting antenna's range.
So it can only ever forward data a vanilla script, mod, or player could have written on a grid whose terminal
you could open yourself.
Data contract: **[SCHEMA.md](SCHEMA.md)**. Trust model: **[SECURITY.md](SECURITY.md)**.

It is **bring-your-own-backend**: the plugin only *extracts and ships*. There is **no Quartermaster server
operated by the author.** It sends only to the endpoint you configure, or to a local file.

Skinned as a **Formidan Mandate** proprietary corp module. The skin is cosmetic; the tool is "Quartermaster".

---

## What it does

### Pipe (the whole job)
- **Reads tagged packets:** any terminal block whose Custom Data begins with **`[QM:<tag>]`** is a packet. The
  plugin forwards the payload **verbatim** under its tag. It does not parse or understand it. What a packet
  *means* is up to whatever wrote it and whatever consumes it.
- **A small envelope** adds who/where collected (`observer`, `world`) and the source grid / block / faction,
  then ships the batch. Consumers dedup by the grid's replicated **EntityId**. See [SCHEMA.md](SCHEMA.md).

### Feeding it: write a `[QM:<tag>]` packet
Anything that can write block Custom Data can feed Quartermaster. A Programmable Block script, a server mod, or
you by hand. The **Quartermaster Companion** (a separate, ready-made vanilla PB script) publishes a
fleet-logistics packet under tag `qm.fleet.v1`. Inventory, production, power/gas, weapons, as a working
example. It's **optional, not required**: the plugin reads any `[QM:...]` packet, whatever the source.

### In-game commands
- `/qm sync`: force a sync now · `/qm status`: last-sync mode / age / grid count / result ·
  `/qm link`: open settings + Steam onboarding · `/qm help`.

### Ship it: Two options
- **Offline**: write each batch to a local JSON file you can pipe anywhere by whatever mechanism you please.
- **Online**: HTTPS POST to your endpoint. Auth modes: `none`, `bearer`, or OAuth2 client-credentials
  (`oauth2_cc`). Non-HTTPS endpoints are refused by default (the token would be cleartext).

### In-game UI
- **Config menu** (default **Ctrl+Shift+Home**): set the destination URL, the **auth mode** (none / bearer /
  OAuth2 client-credentials) and its fields — a bearer **token**, or the OAuth2 **token URL + client ID +
  secret + scope** — toggle online/offline, set the sync interval, see your live **link status**, link your
  account, and **Wipe auth** to reset. No config-file editing required for any auth mode.
- **Manual sync** (default **Ctrl+Shift+End**) with a HUD confirmation pop-up.
- Optional **chat message on every automatic sync**.

### Account linking (optional, for token-authenticated backends)
- **Sign in through Steam** from inside the game (Steam overlay). The backend you point at can issue a
  per-member token **bound to your verified SteamID** and deliver it straight into the plugin. The token is stored **DPAPI-encrypted** (per Windows user), and the secret is a one-time code exchange. See [SECURITY.md](SECURITY.md).

---

## Install (Pulsar)
**Requirements:** Windows + Space Engineers on Pulsar's **Legacy** (.NET Framework) runtime. Token encryption at
rest uses **Windows DPAPI**; on non-Windows runtimes the token still works but is **not encrypted on disk**, so
prefer Windows for token-authenticated backends. Offline / no-auth use has no such caveat.

1. In Pulsar, add **Quartermaster** from the plugin list and enable it; restart SE.
2. Open the config menu (**Ctrl+Shift+Home**), set your **Destination URL** (or leave online off and use
   the offline file), and **Save**.
3. If your backend uses token auth, click **Link account (Steam)**.

Config also lives at `%APPDATA%\Quartermaster\config.json` if you prefer editing by hand.

## Build locally
Requires a Space Engineers install. Point `SeBin64` at your `...\SpaceEngineers\Bin64` (a local
`Directory.Build.props` or an env var), then `dotnet build -c Release` (or run `deploy.sh`). Newtonsoft.Json is
a NuGet `<PackageReference>`. The MSBuild / `deploy.sh` build defines `LOCAL_BUILD`, which embeds it into the
single output DLL (loaded at runtime via an `AssemblyResolve` shim) so a manual Local install is self-contained;
Pulsar's from-source build (PluginHub) ignores that and restores Newtonsoft from NuGet normally. See
`docs/plugins/BUILDING.md`.

## The backend is yours
Quartermaster ships **no backend**. It defines a data contract ([SCHEMA.md](SCHEMA.md)); you build, or
borrow, a server that ingests it. A FastAPI + Postgres/TimescaleDB + Grafana stack is one proven way, but
anything that accepts the documented JSON works.

## License
MIT. See [LICENSE](LICENSE).
