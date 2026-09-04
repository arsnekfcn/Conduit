# Conduit
**NOTICE OF PROPRIETARY SYSTEMS.**
You are accessing CONDUIT™, a sanctioned data-conduit apparatus,
brought to you by the Formidan Mandate.

## What is Conduit
A Space Engineers plugin, loaded via **Pulsar**, that pipes **tagged data packets out of block Custom Data**
to a backend you run (or a local file). It is a generic, **format-agnostic pipe**: it reads any block whose
Custom Data begins with the marker **`[CDT:<tag>]`**, wraps each packet in a small standard envelope, and ships
it. **It never interprets the payload, and never senses anything itself.**

The access guardrail is mechanical: the plugin reads Custom Data **only on grids you can vanilla-access right now**.
Your own or shared faction grids that you're controlling (in a seat, including anything docked to it by connector), that have their terminal open, or that are within a broadcasting antenna's range (with your own antenna online). Standing next to a grid doesn't count. A grid carrying a packet that fails this gate is named in the log with the reason (`out of reach, not forwarded: <grid>: ...`), once per five minutes, and a manual sync says how many were skipped.
So it can only ever forward data a vanilla script, mod, or player could have written on a grid whose terminal
you could open yourself.
Data contract: **[SCHEMA.md](SCHEMA.md)**. Trust model: **[SECURITY.md](SECURITY.md)**.

It is **bring-your-own-backend**: the plugin only *extracts and ships*. There is **no Conduit server
operated by the author.** It sends only to the endpoint you configure, or to a local file.

Skinned as a **Formidan Mandate** proprietary corp module. The skin is cosmetic; the tool is "Conduit".

---

## What it does

### Pipe (the whole job)
- **Reads tagged packets:** any terminal block whose Custom Data begins with **`[CDT:<tag>]`** is a packet. The
  plugin forwards the payload **verbatim** under its tag. It does not parse or understand it. What a packet
  *means* is up to whatever wrote it and whatever consumes it.
- **A small envelope** adds who/where collected (`observer`, `world`) and the source grid / block / faction,
  then ships the batch. Plugin dedups by the grid's replicated **EntityId**. Consumers will want to dedup by a less
  ephemeral value if used in a multiplayer setting, set by script. See [SCHEMA.md](SCHEMA.md).

### Feeding it: write a `[CDT:<tag>]` packet
Anything that can write block Custom Data can feed Conduit. A Programmable Block script, a server mod, or
you by hand. The [**Conduit Example**](https://github.com/arsnekfcn/conduit-example) (a separate, ready-made
vanilla PB script) writes a `[CDT:conduit.example.v1]` packet of a grid's inventory totals — a worked template
you can copy and adapt. It's **optional, not required**: the plugin reads any `[CDT:...]` packet, whatever the
source.

### In-game commands
- `/conduit sync`: force a sync now · `/conduit status`: last-sync mode / age / grid count / result ·
  `/conduit link`: open settings + Steam onboarding · `/conduit help`.

### Ship it: Two options
- **Offline**: write each batch to a local JSON file you can pipe anywhere by whatever mechanism you please.
- **Online**: HTTPS POST to your endpoint. Auth modes: `none`, `bearer`, or OAuth2 client-credentials
  (`oauth2_cc`). Non-HTTPS endpoints are refused by default (the token would be cleartext).

### In-game UI
- **Config menu** (default **Ctrl+Shift+Home**): set the destination URL, the **auth mode** (none / bearer /
  OAuth2 client-credentials) and its fields, a bearer **token**, or the OAuth2 **token URL + client ID +
  secret + scope**, toggle online/offline, set the sync interval, see your live **link status**, link your
  account, and **Wipe auth** to reset. No config-file editing required for any auth mode.
- **Manual sync** (default **Ctrl+Shift+End**) with a HUD confirmation pop-up.
- Optional **chat message on every automatic sync**.

### Account linking (optional, for token-authenticated backends)
- **Steam login** — sign in through the Steam overlay from inside the game. The backend you point at can
  issue a per-member token **bound to your verified SteamID** and deliver it straight into the plugin.
- **Web login** — the backend-agnostic sibling: opens *your* onboarding URL in the system browser, where you
  authenticate the player however you like (Discord, GitHub, SSO, …) and mint a token server-side. The plugin
  holds no client id or secret — it just receives the token. See the [backend guide](BACKEND-GUIDE.md#5-web-login-onboarding-optional).
- Either way the token is stored **DPAPI-encrypted** (per Windows user), delivered as a one-time code
  exchange (never in a browser URL). See [SECURITY.md](SECURITY.md).

---

## Install (Pulsar)
**Requirements:** Space Engineers on Pulsar's **Legacy** (.NET Framework) or **Interim** (.NET 10) runtime.
Token encryption at rest uses **Windows DPAPI** on both; on Linux (Interim) the token still works but is
**not encrypted on disk**, so prefer Windows for token-authenticated backends. Offline / no-auth use has no
such caveat.

1. In Pulsar, add **Conduit** from the plugin list and enable it; restart SE.
2. Open the config menu (**Ctrl+Shift+Home**), set your **Destination URL** (or leave online off and use
   the offline file), and **Save**.
3. If your backend uses token auth, click **Steam login** (or **Web login** for a non-Steam backend).

Config also lives at `%APPDATA%\Conduit\config.json` if you prefer editing by hand.

## Build locally
Requires a Space Engineers install. Point `SeBin64` at your `...\SpaceEngineers\Bin64` (a local
`Directory.Build.props` or an env var), then `dotnet build -c Release` (or run `deploy.sh`). Newtonsoft.Json is a
NuGet `<PackageReference>`; `deploy.sh` copies `Newtonsoft.Json.dll` as a separate file next to `Conduit.dll` so
a manual Local install has its dependency present (no embedding). Pulsar's from-source build (PluginHub) provides
Newtonsoft.Json from its own bundled libraries, so the plugin manifest deliberately does not list it.

## The backend is yours
Conduit ships **no backend**. It defines a data contract ([SCHEMA.md](SCHEMA.md)); you build, or
borrow, a server that ingests it. A FastAPI + Postgres/TimescaleDB + Grafana stack is one proven way, but
anything that accepts the documented JSON works.

## License
MIT. See [LICENSE](LICENSE).
