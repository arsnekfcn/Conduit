# Quartermaster
**NOTICE OF PROPRIETARY SYSTEMS.**
You are accessing QUARTERMASTER™, a sanctioned fleet-logistics telemetry apparatus,
brought to you by the Formidan Mandate.

## What is Quartermaster
A Space Engineers plugin, loaded via **Pulsar**, that turns your client into a passive
**logistics collector** for a faction. It scans the grids you and your faction own that are in
streaming range. Inventories, production (refineries/assemblers, active vs idle), ship class/hull,
health, hydrogen/oxygen/battery, reactor fuel, weapons, ammo, and world position. It then **pushes** that
data to a backend **you run**.

It is **bring-your-own-backend**: the plugin only *extracts and ships* the data. What stores and
visualizes it is yours to build. The data contract is documented in **[SCHEMA.md](SCHEMA.md)**. There
is **no Quartermaster server operated by the author**; the plugin sends data only to the endpoint you
configure (or to a local file). See **[SECURITY.md](SECURITY.md)**.

Skinned as a **Formidan Mandate** proprietary corp module. The skin is cosmetic; the tool is "Quartermaster".

---

## What it does

### Collect (passive, automatic)
- Periodically scans own/faction grids in streaming range into a structured snapshot: per-grid inventory
  (ore / ingot / component / ammo), production capacity + utilization, ship class/hull, health,
  hydrogen / oxygen / battery, reactor fuel, weapons, ammo magazines, and world position.
- The de-dup key is the grid's replicated **EntityId**, so when several faction members report the same
  grid the records fuse to one (newest snapshot wins). See [SCHEMA.md](SCHEMA.md).

### Ship it: Two options
- **Offline**: write each batch to a local JSON file you can pipe anywhere.
- **Online**: HTTPS POST to your endpoint. Auth modes: `none`, `bearer`, `hmac`, or OAuth2
  client-credentials (`oauth2_cc`). Non-HTTPS endpoints are refused by default (the token would be cleartext).

### In-game UI
- **Config menu** (default **Ctrl+Shift+Home**): set the destination URL, toggle online/offline, set the
  sync interval, see your live **link status**, and link your account. No config-file editing required.
- **Manual sync** (default **Ctrl+Shift+End**) with a HUD confirmation pop-up.
- Optional **chat message on every automatic sync**.

### Account linking (optional — for token-authenticated backends)
- **Sign in through Steam** from inside the game (Steam overlay). The backend you point at can issue a
  per-member token **bound to your verified SteamID** and deliver it straight into the plugin. The token is stored **DPAPI-encrypted** (per Windows user), and the secret is a one-time code exchange. See [SECURITY.md](SECURITY.md).

---

## Install (Pulsar)
1. In Pulsar, add **Quartermaster** from the plugin list and enable it; restart SE.
2. Open the config menu (**Ctrl+Shift+Home**), set your **Destination URL** (or leave online off and use
   the offline file), and **Save**.
3. If your backend uses token auth, click **Link account (Steam)**.

Config also lives at `%APPDATA%\Quartermaster\config.json` if you prefer editing by hand.

## Build locally
Requires a Space Engineers install. Point `SeBin64` at your `...\SpaceEngineers\Bin64` (a local
`Directory.Build.props` or an env var), then `dotnet build -c Release`. Newtonsoft.Json is vendored in
`libs/` and embedded into the single output DLL (loaded at runtime via an `AssemblyResolve` shim).

## The backend is yours
Quartermaster ships **no backend**. It defines a data contract ([SCHEMA.md](SCHEMA.md)); you build — or
borrow — a server that ingests it. A FastAPI + Postgres/TimescaleDB + Grafana stack is one proven way, but
anything that accepts the documented JSON works.

## License
MIT. See [LICENSE](LICENSE).
