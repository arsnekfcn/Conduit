# Quartermaster Data Contract — v1.0 (DRAFT)

One batch = the observations from one observer (one player's client), captured at one moment, then delivered to a
file (offline) and/or `POST`ed to an endpoint (online); see **Transport & auth**.

**Fusion / faction de-dup (what a backend MUST do):** the dedup key is `(world.serverId, grid.entityId)`.
`entityId` is identical for every observer, so when N faction members stream the same grid the backend keeps
exactly **one** record per grid via **newest `capturedAtUtc`.
Because `capturedAtUtc` is the client's wall-clock, a backend SHOULD also stamp its own **server receive time**
as the authoritative freshness/age signal, and clamp implausibly-future client timestamps so a skewed clock
can't permanently win the dedup.

`Content-Type: application/json`. Amounts are JSON numbers; units noted per field. `entityId` is a stable
64-bit grid id (preserved across sessions) and is the fusion key. All `*IdentityId`/`factionId` are SE
internal 64-bit ids; `steamId` is the 64-bit SteamID.

## Envelope

```json
{
  "schemaVersion": "1.0",
  "capturedAtUtc": "2026-06-19T12:34:56Z",
  "observer": {
    "identityId": 144115188075855873,
    "steamId": 76561198000000000,
    "displayName": "exampleName"
  },
  "world": {
    "serverId": "exampleServer",
    "sessionName": "exampleSession",
    "syncDistanceMeters": 3000
  },
  "grids": [ /* Grid objects */ ]
}
```

## Grid

```json
{
  "entityId": 107362248192,
  "groupId": 107362248192,
  "name": "exampleGrid",
  "gridSize": "Large",
  "isStatic": true,
  "blockCount": 4210,
  "owner": {
    "identityId": 144115188075855873,
    "factionId": 129000000000000001,
    "factionTag": "PBC",
    "relationToObserver": "Self"
  },
  "health": { "percent": 0.972, "damagedBlocks": 12, "destroyedBlocks": 0 },
  "classification": {
    "class": "Cruiser",
    "source": "subtype",
    "coreSubtype": "ShipCore_Cruiser"
  },
  "telemetry": {
    "hydrogen": { "filledRatio": 0.80, "capacityLiters": 4000000, "tankCount": 4 },
    "oxygen":   { "filledRatio": 0.55, "capacityLiters": 1000000, "tankCount": 2 },
    "batteries": { "storedMWh": 12.5, "maxMWh": 20.0, "count": 6 },
    "reactors":  { "count": 2, "fuelKg": 340.0, "fuelSubtype": "Uranium" }
  },
  "armament": [
    { "category": "Turret",      "subtype": "LargeBlockBatteryTurret", "count": 4 },
    { "category": "WeaponCore",  "subtype": "FRailgunMk2",          "count": 2 },
    { "category": "unclassified","subtype": "SomeModGun",              "count": 1 }
  ],
  "ammo": [
    { "subtype": "NATO_25x184mm",   "amount": 1200 },
    { "subtype": "RailgunSlug", "amount": 48 }
  ],
  "inventory": [
    { "category": "Ore",       "typeId": "MyObjectBuilder_Ore",                 "subtype": "Iron",      "amount": 50000.0 },
    { "category": "Ingot",     "typeId": "MyObjectBuilder_Ingot",              "subtype": "Iron",      "amount": 18200.0 },
    { "category": "Component", "typeId": "MyObjectBuilder_Component",          "subtype": "SteelPlate","amount": 3400 },
    { "category": "Ammo",      "typeId": "MyObjectBuilder_AmmoMagazine",       "subtype": "NATO_25x184mm", "amount": 1200 }
  ],
  "production": {
    "assemblers": 8,
    "assemblersActive": 3,
    "refineries": 4,
    "refineriesActive": 4,
    "otherProductionBlocks": 1
  }
}
```

## Field notes

- **`entityId`**: server-assigned, replicated 64-bit id — identical for every observer. The fusion/dedup key
  is `(world.serverId, entityId)` (EntityId is unique only within one world), newest `capturedAtUtc` wins.
- **`groupId`**: smallest EntityId across the grid's mechanical group (rotor/piston subgrids). All subgrids
  of one ship share it, so consumers can roll a multi-grid ship into a single logical unit. Connectors are
  not mechanical, so two docked ships keep distinct groupIds. For a single-grid ship, `groupId == entityId`.
- **`relationToObserver`**: `Self | FactionShare | Faction | Allied`. Scanner emits only grids matching the
  configured `scope`.
- **`classification.source`**: `subtype | customdata | gridname | unknown`. If `unknown`, `class` is null but
  `coreSubtype` (if a core block is present) is still reported so the backend can classify later.
- **`armament.category`**: `Turret | FixedGun | Launcher | WeaponCore | unclassified`. Unknown weapon-ish
  blocks are reported with their raw `subtype` and `unclassified` — never dropped.
- **`inventory`**: aggregated **per grid** (summed across all the grid's inventories) in v1.0 — answers
  "how much X at this facility." `amount` units: Ore/Ingot = kg; Component/Ammo/Tool = item count.
- **`ammo`**: convenience roll-up of ammo magazines across the grid (also present in `inventory`).
- **`production.*Active`**: count of those blocks currently producing (`IsProducing`); `total − active = idle`,
  for refinery/assembler utilization.
- Any section the observer couldn't read (e.g. feature toggled off, or no rights) is **omitted**, not zeroed —
  so consumers can tell "0" from "unknown."

## Optional/forward-compatible
- Consumers MUST ignore unknown fields (additive schema evolution).
- `schemaVersion` bumps minor for additive, major for breaking.
- Future: per-block inventory breakdown, production queue contents, neutral/enemy "contacts".

## Transport & auth

The plugin is **BYO**: it only extracts and ships the envelope. What stores/uses it is yours to build. Each
scan it can write the batch to a local file (**offline** pipe it anywhere: your own uploader, git, S3 CLI…)
and/or `POST` it to your endpoint (**online**); both can run at once. The body is identical either way. Only
the online sink uses auth headers:

- `none`: no auth header.
- `bearer`: `Authorization: Bearer <token>` (static token).
- `oauth2_cc`: OAuth2 **client-credentials**: the plugin `POST`s `grant_type=client_credentials` (+
  `client_id`/`client_secret`/optional `scope`) to your `tokenUrl`, caches the returned token until just before
  `expires_in`, sends it as `Authorization: Bearer <token>`, and refreshes on expiry or a `401`. Pure
  machine-to-machine; works with any OAuth2-protected backend / API gateway (Auth0, Keycloak, Azure AD, …).

**Hardening a backend (recommended for faction-share):** issue **per-member** credentials, not one shared
secret (revoke one without affecting others); split **ingest** vs **read** scope (a leaked push credential
then can't read faction intel); and **bind** each credential to one `observer.steamId`, rejecting batches whose
`observer.steamId` doesn't match — so a leaked credential can't impersonate another member. `steamId` is the
practical binding key because it's verifiable out-of-band (Steam OpenID "Sign in through Steam". No publisher
key needed) while the SteamID inside the body alone is self-asserted and must not be trusted as authentication.
