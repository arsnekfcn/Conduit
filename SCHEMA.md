# Conduit Data Contract

The plugin is a **generic, format-agnostic pipe**. It reads tagged data packets out of block **Custom Data**
and ships them, to a local file (offline) and/or `POST`ed to your endpoint (online), wrapped in a small
standard envelope. **It never interprets the payload.** What a packet *means* is defined by whatever script /
server mod / hand wrote it, and parsed by whatever consumer you point it at.

## The marker

Any terminal block whose Custom Data begins with **`[CDT:<tag>]`** is a packet. The `<tag>` namespaces the
data; everything after the first newline is the payload:

```
[CDT:qm.fleet.v1]
{ "grids": [ ... ] }
```

`<tag>` is free-form (`qm.fleet.v1`, `mybase.whatever`). Payload can be anything. JSON,
CSV, key=value, plain text. The plugin parses it as JSON when it is valid JSON (so it rides the wire as
structured data), otherwise forwards it as a string.

## Envelope (what the plugin emits)

```json
{
  "schemaVersion": "2.0",
  "capturedAtUtc": "2026-06-24T12:34:56Z",
  "observer": { "identityId": 1441…, "steamId": 7656…, "displayName": "name" },
  "world":    { "serverId": "myServer", "sessionName": "…", "syncDistanceMeters": 3000 },
  "packets": [
    {
      "tag": "qm.fleet.v1",
      "source": { "entityId": 1073…, "gridName": "Butler HQ", "blockName": "QM Hub", "factionTag": "PBC" },
      "payload": { /* opaque, exactly what the writing script put after the marker */ }
    }
  ]
}
```

- **`observer` / `world`**: who/where the packets were collected, applied to every packet in the batch.
- **`source`**: the grid + block the packet was read from, and that grid's faction (the plugin's only added
  context; useful when the payload itself doesn't carry ownership, e.g. a Programmable Block can't read faction).
- **`payload`**: verbatim. Consumers dispatch on `tag` and parse accordingly.

## Example payload: the `qm.fleet.v1` companion format

The companion script (a separate vanilla PB example) writes this under tag `qm.fleet.v1`, one example of a
payload; your own scripts can define any other format under any other tag.

```json
{ "grids": [ {
  "entityId": 1073…, "name": "Equinox", "gridSize": "Large", "isStatic": false,
  "inventory":  [ { "category": "Ingot", "subtype": "Iron", "amount": 18200 },
                  { "category": "Ammo",  "subtype": "NATO_25x184mm", "amount": 1200 } ],
  "production": { "refineries": 4, "refineriesActive": 4, "assemblers": 8, "assemblersActive": 3 },
  "power":      { "batteryStoredMWh": 12.5, "batteryMaxMWh": 20.0, "reactors": 2 },
  "gas":        { "hydrogenRatio": 0.80, "oxygenRatio": 0.55 },
  "weapons":    { "turrets": 4, "fixedGuns": 2, "launchers": 1 }
} ] }
```

A consumer that understands `qm.fleet.v1` keys grids by **`(world.serverId, entityId)`** (the cross-observer
dedup key, newest `capturedAtUtc` wins, and it SHOULD also stamp its own receive time as the authoritative
freshness signal, clamping implausibly-future client clocks). `amount` units: Ore/Ingot = kg; Component/Ammo =
count. The reference backend maps this payload into inventory / production / telemetry / armament rows and
attaches the source grid's faction.

## Forward-compatible
- Consumers MUST ignore unknown fields and unknown tags.
- `schemaVersion` bumps minor for additive envelope changes, major for breaking ones.
- Multiple packets per batch (different tags, or the same tag from several reachable grids) are normal.

## Transport & auth

BYO: the plugin only extracts and ships the envelope; what stores/uses it is yours. Each sync it can write the
batch to a local file (**offline**: pipe it anywhere: your own uploader, git, S3 CLI…) and/or `POST` to your
endpoint (**online**); both can run at once, body identical. Only the online sink uses auth headers:

- `none`: no auth header.
- `bearer`: `Authorization: Bearer <token>`.
- `oauth2_cc`: OAuth2 client-credentials, the plugin fetches/caches/refreshes a token from your `tokenUrl` and
  sends it as a bearer. Pure machine-to-machine (Auth0, Keycloak, Azure AD, …).

**Hardening a backend (recommended for faction-share):** issue **per-member** credentials, not one shared
secret; split **ingest** vs **read** scope; and **bind** each credential to one `observer.steamId`, rejecting
batches whose `observer.steamId` doesn't match, so a leaked credential can't impersonate another member.
`steamId` is verifiable out-of-band via Steam OpenID ("Sign in through Steam"); the
SteamID inside the body alone is self-asserted and must not be trusted as authentication.
