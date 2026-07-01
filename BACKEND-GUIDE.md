# Building a Conduit backend

Conduit is **bring-your-own-backend**. The plugin only reads tagged `[CDT:<tag>]` Custom Data off
grids you can vanilla-access, wraps each packet in a small envelope, and **POSTs it to a URL you
configure**. There is no author-hosted server; your data goes only where you point it.

This doc is the wire contract plus a minimal receiver you can copy-paste. If you just want to *see*
what Conduit produces without standing anything up, skip to [No backend needed](#no-backend-needed).

The envelope format itself is specified in [SCHEMA.md](SCHEMA.md) — this doc covers the HTTP/auth
contract around it.

---

## 1. The ingest endpoint (the only thing you must implement)

Conduit sends, per sync:

| | |
|---|---|
| **Method** | `POST` |
| **URL** | whatever you set as *Destination URL* in the config menu |
| **Content-Type** | `application/json` |
| **Body** | one envelope (see [SCHEMA.md](SCHEMA.md)) — `schemaVersion`, `observer`, `world`, `packets[]` |
| **Auth header** | none, or `Authorization: Bearer <token>` (see [Auth](#3-auth-options)) |
| **Timeout** | the plugin waits 20s |

Example body (trimmed):

```json
{
  "schemaVersion": "2.0",
  "capturedAtUtc": "2026-06-28T05:41:13.9Z",
  "observer": { "identityId": 144115188075856777, "steamId": 7656119..., "displayName": "arsnek" },
  "world":    { "serverId": "my-server", "sessionName": "My World", "syncDistanceMeters": 10000 },
  "packets": [
    {
      "tag": "qm.fleet.v1",
      "source":  { "entityId": 1234567, "gridName": "Butler HQ", "blockName": "QM", "factionTag": "FCN" },
      "payload": { "grids": [ /* whatever the in-game script wrote after the [CDT:qm.fleet.v1] marker */ ] }
    }
  ]
}
```

**Response codes the plugin understands:**

| You return | Plugin behavior |
|---|---|
| `200` | success — shown as "OK" in status |
| `401` / `403` | "auth rejected / re-auth" (for `oauth2_cc` it refreshes the token and retries once) |
| any other code | surfaced as `HTTP <code>` |
| connection failure | "network error" |

So the minimum viable backend is: **accept the POST, return `200`.** Everything else is your choice
(store it, fan it out to a dashboard, drop it on the floor).

---

## 2. Minimal receiver (FastAPI)

About 20 lines. Any HTTP framework works the same way.

```python
# pip install fastapi uvicorn
# uvicorn main:app --host 0.0.0.0 --port 8000
from fastapi import FastAPI, Request, Header, HTTPException

app = FastAPI()
TOKEN = "change-me"   # match this to the plugin's bearer token; delete the check for auth = none

@app.post("/v1/ingest")
async def ingest(request: Request, authorization: str | None = Header(None)):
    if authorization != f"Bearer {TOKEN}":          # remove for auth = none
        raise HTTPException(status_code=401, detail="bad token")
    env = await request.json()
    for p in env.get("packets", []):
        src = p.get("source", {})
        print(f'{p["tag"]} from {src.get("gridName")!r} -> {p["payload"]}')
    return {"ok": True, "packets": len(env.get("packets", []))}
```

Then in the Conduit config menu set **Destination URL** to `https://<your-host>/v1/ingest`. (Conduit
requires `https://` unless you tick `AllowInsecureEndpoint` in `config.json`. See
[HTTPS](#https-is-required), so during local testing put this behind a TLS reverse proxy, or use
the local-file sink below.)

### Keying / dedup

Successive syncs re-send the current state. A consumer that wants "latest state per grid" should
**upsert keyed by `(world.serverId, source.entityId)`**. `serverId` distinguishes worlds,
`entityId` is the stable per-grid id. `capturedAtUtc` lets you keep history or discard stale writes.

---

## 3. Auth options

Set in the config menu under **Auth mode**. The plugin's side of each is fixed; your backend just has
to honor it.

### `none`
No `Authorization` header. Fine for a private/firewalled box. Your endpoint accepts anything that POSTs.

### `bearer`
Plugin sends `Authorization: Bearer <token>`. You decide how tokens are minted and checked. A static
shared secret, a row in a table, a JWT you validate, whatever. The token is entered in-menu (or set via
[Steam onboarding](#4-steam-onboarding-optional)) and stored encrypted at rest (DPAPI).

### `oauth2_cc` (OAuth2 client-credentials)
For backends fronted by an IdP / API gateway. Before posting, the plugin fetches a token from your
**Token URL**:

```
POST <TokenUrl>
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials&client_id=<id>&client_secret=<secret>&scope=<optional>
```

Respond with JSON:

```json
{ "access_token": "…", "token_type": "Bearer", "expires_in": 3600 }
```

The plugin caches the token until ~60s before `expires_in`, attaches it as `Authorization: Bearer …`
on the ingest POST, and on a `401` refreshes once and retries. (`expires_in` defaults to 3600 if you
omit it.) Minimal issuer:

```python
from fastapi import Form, HTTPException

@app.post("/v1/oauth/token")
async def token(grant_type: str = Form(...), client_id: str = Form(...),
                client_secret: str = Form(...), scope: str = Form("")):
    if (client_id, client_secret) != ("conduit", "s3cret"):
        raise HTTPException(status_code=401, detail="bad client")
    return {"access_token": "issued-token", "token_type": "Bearer", "expires_in": 3600}
```

---

## 4. Steam onboarding (optional)

A convenience for `bearer` mode: instead of pasting a token, the player clicks **Link account (Steam)**
and signs in through the Steam overlay, and your backend mints a token bound to their verified SteamID.
Entirely optional, if you don't implement it, operators just hand out bearer tokens directly.

The flow (the plugin drives the loopback half; you implement two endpoints):

1. Plugin opens, in the Steam overlay browser:
   `GET <OnboardUrl>?state=<nonce>&cb=<loopbackPort>`
   where `OnboardUrl` is your **Onboard URL** (e.g. `https://host/auth/steam/login`).
2. **Your `/auth/steam/login`** runs Steam OpenID sign-in, then redirects the browser back to the
   plugin's loopback, echoing the nonce and handing over a **single-use code** (not the token):
   `302 → http://127.0.0.1:<cb>/?code=<one-time-code>&state=<nonce>`
   The plugin ignores any hit whose `state` doesn't match the nonce it generated.
3. Plugin exchanges that code for the real token via a direct HTTPS POST to a sibling endpoint. It
   takes your `OnboardUrl` and swaps `/auth/steam/login` → `/auth/steam/claim`:
   `POST <OnboardUrl with /auth/steam/login → /auth/steam/claim>?code=<one-time-code>`
4. **Your `/auth/steam/claim`** validates the (single-use, short-TTL) code and returns:
   `{ "token": "<bearer token>" }`

The plugin stores that token (DPAPI), flips Auth mode to `bearer`, and uses it on the next sync. The
token never appears in a browser URL or history — only the opaque code does. Bind the minted token to
the SteamID you verified, give it the scope you want (e.g. ingest-only), and expire it on whatever
schedule you like (the plugin surfaces a rejected token as "re-auth").

---

## No backend needed

For a quick look, you don't need a server at all. In the config menu enable
**"Also write an offline batch file"** (the Offline sink). Each sync writes the exact same envelope
JSON to:

```
%APPDATA%\Conduit\offline\conduit-batch.json
```

Open that file to see precisely what would be POSTed. Pipe it wherever you want with your own uploader.

---

## HTTPS is required

Conduit refuses to POST to a non-`https://` Destination URL (and to fetch an `oauth2_cc` token from a
non-HTTPS Token URL), because the bearer token / client secret would otherwise travel in cleartext.
For real use, terminate TLS at a reverse proxy in front of your receiver. For throwaway local testing
only, you can set `"AllowInsecureEndpoint": true` in `config.json` to lift the gate.
