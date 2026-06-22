using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Quartermaster
{
    // Local config, loaded from %APPDATA%\Quartermaster\config.json (created with defaults on first run).
    // Nothing secret is baked into the public plugin: the endpoint URL + auth are the operator's own values.
    public class QmConfig
    {
        // ── Sinks ──────────────────────────────────────────────────────────────────
        // Each scan builds one SCHEMA.md envelope and hands it to every enabled sink (both may run).
        // This plugin is BYO: it only extracts and moves the data. Bring your own backend to use it.

        // Offline sink: write each batch to OfflinePath (then pipe it wherever. Your own uploader, git, S3…).
        public bool Offline = true;
        // Empty => %APPDATA%\Quartermaster\offline\quartermaster-batch.json
        public string OfflinePath = "";

        // Online sink: POST each batch to your endpoint. No-op while EndpointUrl is empty.
        public bool Online = true;
        public string EndpointUrl = "";
        // Refuse to POST to a non-https endpoint (the token would travel in cleartext) unless overridden.
        public bool AllowInsecureEndpoint = false;

        // Print a chat-box line on every automatic sync (manual syncs always show a HUD pop-up).
        public bool ChatOnSync = false;

        // ── Online auth ────────────────────────────────────────────────────────────
        // "none" | "bearer" | "oauth2_cc"
        public string AuthMode = "none";
        public string Token = "";            // bearer: static token
        // oauth2_cc (OAuth2 client-credentials): plugin fetches + caches a bearer token from your IdP,
        // refreshing before expiry and on a 401. Works with any OAuth2-protected backend / API gateway.
        public string TokenUrl = "";         // e.g. https://idp.example.com/oauth/token
        public string ClientId = "";
        public string ClientSecret = "";
        public string OAuthScope = "";       // optional, space-delimited

        // Secrets above are encrypted at rest (Windows DPAPI, current user) — on disk they appear as "DPAPI:...".
        // The plugin works with these decrypted in-memory copies, which are never serialized to the file.
        [JsonIgnore] public string TokenPlain = "";
        [JsonIgnore] public string ClientSecretPlain = "";

        // Identifies the world/server in fused data. Set per deployment.
        public string ServerId = "default";

        // Seconds between passive scans.
        public double ScanIntervalSeconds = 60.0;

        // Per-grid opt-in: only grids carrying the marker (in a block's name or Custom Data) are tracked, so a
        // grid is collected ONLY when its owner has explicitly opted it in. Setting a block name / Custom Data
        // requires build rights, so it's owner-gated by the game. Use the in-game "/qm track" command (aim at
        // your grid) to set it, or add the marker to a block by hand. Set false to track all owned/faction grids.
        public bool RequireTrackMarker = true;
        public string TrackMarker = "[QM:track]";

        // Manual-scan hotkey (the periodic scan runs regardless; this is just a force-now key).
        // Default Ctrl+Shift+End — End isn't a movement key, so it won't also steer the character.
        // HotkeyKey is any VRage.Input.MyKeys name (e.g. End, Home, OemBackslash, F8).
        public string HotkeyKey = "End";
        public bool HotkeyCtrl = true;
        public bool HotkeyShift = true;

        // Menu hotkey: opens the in-game Quartermaster config menu (destination URL, online/offline sinks,
        // sync rate, link status, and the Steam "Link account" onboarding). Default Ctrl+Shift+Home.
        public string LinkHotkeyKey = "Home";
        public bool LinkHotkeyCtrl = true;
        public bool LinkHotkeyShift = true;
        // Operator onboarding URL, e.g. https://host/quartermaster/auth/steam/login
        public string OnboardUrl = "";

        // "ownOnly" | "faction" | "factionAndAllies".
        public string Scope = "faction";

        // Feature toggles — a disabled section is OMITTED from the payload (not zeroed).
        public bool IncludeInventory = true;
        public bool IncludeTelemetry = true;
        public bool IncludeArmament = true;

        // Optional user-supplied classification tables (override/extend the embedded defaults).
        public string ClassTablePath = "";      // subtypeId -> class name
        public string WeaponTablePath = "";     // subtypeId -> weapon category

        // Diagnostic: dump every distinct block subtype seen on scanned grids to %APPDATA%\Quartermaster\census\
        // (subtypes.json + weapons.suggested.json) so you can populate weapons.json/classes.json for a modded server.
        public bool SubtypeCensus = false;

        // Optional last-resort class strategy: a regex over the grid name. Capture the class in a group
        // named 'class', e.g. "^\\[(?<class>[A-Z]{2,4})\\]" to read "[CRU] FCN Equinox" -> "CRU". Empty = off.
        public string GridNameClassRegex = "";

        [JsonIgnore] public string LoadError;

        public static string Dir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Quartermaster");

        private static string Path_ => Path.Combine(Dir, "config.json");

        public static QmConfig Load()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                if (!File.Exists(Path_))
                {
                    var fresh = new QmConfig();
                    File.WriteAllText(Path_, JsonConvert.SerializeObject(fresh, Formatting.Indented));
                    return fresh;
                }
                var cfg = JsonConvert.DeserializeObject<QmConfig>(File.ReadAllText(Path_)) ?? new QmConfig();
                cfg.ResolveSecrets();   // decrypt on-disk secrets into the *Plain fields, normalize disk to DPAPI
                // Re-canonicalize: rewrite the file so newly-added fields appear (with defaults) for the user
                // to edit, while preserving any values they've already set. Avoids stale-config surprises.
                // (Secrets are written encrypted; a hand-pasted plaintext token is upgraded to DPAPI here.)
                try { File.WriteAllText(Path_, JsonConvert.SerializeObject(cfg, Formatting.Indented)); } catch { }
                return cfg;
            }
            catch (Exception ex)
            {
                return new QmConfig { LoadError = ex.Message };
            }
        }

        public string ResolveOfflinePath()
        {
            return string.IsNullOrWhiteSpace(OfflinePath)
                ? Path.Combine(Dir, "offline", "quartermaster-batch.json")
                : OfflinePath;
        }

        // Persist current values (used by in-game onboarding to store the issued token). Secrets encrypted.
        public void Save()
        {
            try
            {
                Token = Protect(TokenPlain);
                ClientSecret = Protect(ClientSecretPlain);
                Directory.CreateDirectory(Dir);
                File.WriteAllText(Path_, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch (Exception ex) { LoadError = ex.Message; }
        }

        // Decrypt on-disk secrets into the in-memory *Plain copies, and normalize the on-disk fields to DPAPI
        // (so a plaintext value a user pasted in by hand gets encrypted on the next load).
        private void ResolveSecrets()
        {
            TokenPlain = Unprotect(Token);                 Token = Protect(TokenPlain);
            ClientSecretPlain = Unprotect(ClientSecret);   ClientSecret = Protect(ClientSecretPlain);
        }

        private const string DpapiPrefix = "DPAPI:";

        private static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                var enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
                return DpapiPrefix + Convert.ToBase64String(enc);
            }
            catch { return plain; }   // DPAPI unavailable (non-Windows test) — best effort, leave as-is
        }

        private static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            if (!stored.StartsWith(DpapiPrefix)) return stored;   // legacy/hand-pasted plaintext
            try
            {
                var dec = ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(DpapiPrefix.Length)),
                                                  null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            catch { return ""; }   // can't decrypt (e.g. copied from another machine/user) -> treat as empty
        }
    }
}
