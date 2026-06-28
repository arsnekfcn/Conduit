# Security & trust

Conduit is open source. Read the code and verify these claims yourself. This document covers where
your data and credentials go, what the plugin can and can't do, and the question that matters most:
**can the plugin author, or another player, see your data? No.**

## TL;DR

- **There is no Conduit backend operated by the author.** The plugin sends your data **only** to the
  endpoint *you* configure (`EndpointUrl`), and/or writes it to a local file. There is no code path that
  routes it anywhere else. Not to the author, not to anyone. That's a property of the open source you can read.
- Your **auth token is stored encrypted** (Windows DPAPI, per user) and is sent **only** to your endpoint,
  **only over HTTPS** (a non-HTTPS endpoint is refused unless you explicitly set `AllowInsecureEndpoint`).
- The data leaves your machine **out of band**.. A direct HTTPS POST to your backend. It never travels
  through Space Engineers' multiplayer replication, so **other players (and server-side mods) cannot read
  it.** TLS protects it in transit.
- **Only what you can vanilla-access.** A grid's data is collected only when a block on it carries a
  `[CDT:<tag>]` packet in its Custom Data **and** you can access that grid in-game right now. Controlling it,
  physically at it, or within a broadcasting antenna's range, limited to your own / faction grids. The plugin
  never scans grid contents itself; it only forwards a packet that a vanilla script, mod, or player wrote on a
  grid whose terminal you could open yourself. (A companion PB script is one way to produce that packet; any
  source works. The plugin doesn't care what wrote it.)
- **Steam is used as an enrollment identity provider only.** "Sign in through Steam" verifies your SteamID
  *once* so your backend can issue a token bound to it. Per-request auth afterward is your own bearer token,
  not Steam.

## The reach gate: What "vanilla-access" means

A grid's `[CDT:...]` packets are read only when **two independent gates both pass**: (a) the grid is **own or
shared same-faction**. Enemy/neutral/**allied**/unowned are all excluded (cross-faction allies deliberately so,
since you can't open an allied faction's terminals in vanilla), **and** (b) you can **reach** it right now one
of the three vanilla ways: controlling the construct, on foot within control-panel range (~20 m), or within the
range of a **broadcasting antenna on that grid**.

One deliberate note on the antenna path: it checks the grid's own broadcasting-antenna radius against your
position; it does **not** additionally require your character's suit antenna to be relaying. This matches how
players actually use antenna range. The gate is otherwise conservative: non-broadcasting remote grids and allies
are excluded entirely, and no position is ever transmitted.

## What the plugin sends, and where

Each sync collects the `[CDT:<tag>]` packets from the own/faction grids you can vanilla-access (see
[SCHEMA.md](SCHEMA.md) for the envelope), wraps them in one batch, and hands it to whichever sinks you
enabled:

- **Offline sink** → a local file (`%APPDATA%\Conduit\offline\…`), which you can do anything with.
- **Online sink** → an HTTPS POST to `EndpointUrl`, with the auth header for your chosen mode.

The plugin's only outbound network calls are: (1) your configured `EndpointUrl`, (2) during account linking,
your backend's onboarding endpoints, and (3) `steamcommunity.com` for the Steam sign-in itself. Nothing else.

## Account linking (Steam onboarding)

If your backend uses token auth, you can link from inside the game:

- **Steam OpenID** ("Sign in through Steam") runs in the Steam overlay. You're already signed into Steam,
  so it's a one-click consent. It proves your **SteamID** to your backend; no Steam password ever reaches
  the plugin or your backend.
- Your backend issues a token and returns it to the plugin via a **one-time, single-use code** delivered to
  a localhost loopback so the **token never appears in a browser URL or history**. A random `state` nonce
  ties the response to the request.
- The token is written **DPAPI-encrypted**; on disk it's `DPAPI:…`, readable only by your Windows account
  on that PC.

Steam is a *legacy OpenID 2.0* provider. The plugin/backend mitigate the known weaknesses (single-use
response nonce, return-URL validation), but two honest caveats remain, inherent to any "sign in with X":
- It proves you own a **SteamID**, not that you own Space Engineers (full app-ownership checks need the
  publisher's Steam Web API key, which third parties don't have). So a backend should **allow-list** the
  SteamIDs it will issue credentials to. That list is the real authorization gate.
- If your **Steam account** is compromised, someone could enroll as you. Steam Guard 2FA, a push-only token
  scope, and revocation keep the blast radius small.

## Can the plugin author access your data? No.

There is no author-operated server and no hidden endpoint. The plugin posts to the URL you set and nowhere
else. The author cannot see your data or your token. This is verifiable in the source (`Sender.cs`,
`OAuth.cs`, `Onboard.cs` are the only files that touch the network).

## For backend operators

The plugin is a push-only collector; your server holds the data and decides who can read it. The reference
backend implements all of the following. **Enable them** (some are opt-in), and don't rely on a shared
default token:
- Issue **per-member** credentials, not one shared secret (revoke one without affecting others).
- Split **ingest** vs **read** scope, a leaked push token then can't read your data.
- **Bind** each credential to one `observer.steamId` and reject mismatches (a leaked token can't impersonate
  another member). Turn on bound-ingest enforcement so unbound tokens are rejected.
- Rate-limit ingest and onboarding; keep the onboarding allow-list tight.
- Expire tokens. Not required, but good security practice would have users re-authenticating once every couple weeks.

See the **Transport & auth** and de-dup sections of [SCHEMA.md](SCHEMA.md) for the contract a backend must implement.

## Reporting

Found a vulnerability? Open an issue (omit sensitive detail) or contact the maintainer listed in
[CONTRIBUTORS.md](CONTRIBUTORS.md).
