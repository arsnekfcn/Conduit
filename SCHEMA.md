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

## Example payload: a fleet-logistics format (`qm.fleet.v1`)

One real-world example is a fleet-logistics reporter publishing under tag `qm.fleet.v1`. It's just an example;
your own scripts can define any format under any tag. The bundled
[Conduit Example](https://github.com/arsnekfcn/conduit-example) writes a simpler `conduit.example.v1` packet.

```json
{
  "entityId": 1234567890123456789,   // current engine id, reference/telemetry ONLY, rotates often in MP
  "uid": "1234567890123456789",      // stable, producer-assigned grid handle = the dedup identity
  "name": "Wanderer",
  "gridSize": "Large",               // "Large" | "Small"
  "isStatic": false,
  "blockCount": 100,
  "position": [12345.6, -789.0, 42.0],
  "inventory": [
    { "category": "Ore",  "subtype": "Cobalt",     "amount": 1250000.0 },
    { "category": "Ammo", "subtype": "NATO_25x184mm", "amount": 480.0 }
  ],
  "production": { "assemblers": 6, "assemblersActive": 2, "refineries": 4, "refineriesActive": 3 },
  "power":      { "batteryStoredMWh": 134.5, "batteryMaxMWh": 200.0, "reactors": 2 },
  "gas":        { "hydrogenRatio": 0.82, "ox
  "weapons":    { "turrets": 27, "fixedGuns": 2, "launchers": 12 }
}
```

A consumer that understands qm.fleet.v1 keys grids by (world.serverId, uid) — a stable, producer-assigned grid handle. 
Every observer of the same grid reports the same uid (so it is the cross-observer dedup key), and it also persists across0 
the multiplayer operations that rotate the engine entityId: fixship copy/paste, admin grid restore, Nexus transfer, and others, 
which makes entityId a poor identity. entityId is carried for reference but should NOT be used as the dedup key; when a producer 
omits uid, fall back to str(entityId). Newest capturedAtUtc wins, and the consumer SHOULD also stamp its own receive time 
as the authoritative freshness signal, clamping implausibly-future client clocks. amount units: Ore/Ingot = kg; Component/Ammo = count. 
The reference backend maps this payload into inventory / production / telemetry / armament rows and attaches the source grid's faction.

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
