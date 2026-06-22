using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Quartermaster
{
    // Turns a scanned Envelope into the SCHEMA.md JSON and hands it to every enabled sink:
    //   Offline  -> write the batch to a local file (pipe it wherever; BYO uploader)
    //   Online   -> POST the batch to the operator's endpoint with the configured auth
    // Both can run in the same scan. Called on a background thread with an already-built Envelope
    // (the actual game-state read happens on the main thread in Scanner.Scan).
    public static class Sender
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,   // omit unread sections (the "unknown vs zero" rule)
            Formatting = Formatting.Indented,
        };

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

        public static string Serialize(Envelope env) => JsonConvert.SerializeObject(env, Settings);

        // Returns the online result: 200 ok | HTTP code | 0 = offline-only/no online | -1 = network error.
        public static int Send(QmConfig cfg, Envelope env)
        {
            string json = Serialize(env);
            int n = env.Grids.Count;

            bool wroteAny = false;
            if (cfg.Offline) { WriteOffline(cfg, json, n); wroteAny = true; }

            int code = 0;
            if (cfg.Online && !string.IsNullOrWhiteSpace(cfg.EndpointUrl))
            {
                bool https = cfg.EndpointUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                if (!https && !cfg.AllowInsecureEndpoint)
                    Plugin.Log("online sink BLOCKED: EndpointUrl is not https (token would be cleartext). " +
                               "Use https, or set AllowInsecureEndpoint=true to override.");
                else { code = Post(cfg, json, n); wroteAny = true; }
            }

            if (!wroteAny)
                Plugin.Log("no sink active: set Offline=true and/or Online=true (with a valid EndpointUrl)");

            SyncStatus.Record(code, n);
            return code;
        }

        private static void WriteOffline(QmConfig cfg, string json, int gridCount)
        {
            try
            {
                string path = cfg.ResolveOfflinePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, json);
                Plugin.Log($"offline: wrote {gridCount} grid(s) -> {path}");
            }
            catch (Exception ex) { Plugin.Log("offline write failed: " + ex.Message); }
        }

        private static int Post(QmConfig cfg, string json, int gridCount)
        {
            try
            {
                HttpStatusCode code = SendOnce(cfg, json, forceRefresh: false);
                // OAuth: a 401 usually means a stale/expired token. Refresh once and retry.
                if (code == HttpStatusCode.Unauthorized &&
                    string.Equals(cfg.AuthMode, "oauth2_cc", StringComparison.OrdinalIgnoreCase))
                {
                    OAuth.Invalidate();
                    code = SendOnce(cfg, json, forceRefresh: true);
                }
                Plugin.Log($"post: {gridCount} grid(s) -> {(int)code} {code}");
                return (int)code;
            }
            catch (Exception ex) { Plugin.Log("post failed: " + ex.Message); return -1; }
        }

        private static HttpStatusCode SendOnce(QmConfig cfg, string json, bool forceRefresh)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, cfg.EndpointUrl))
            {
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                ApplyAuth(cfg, req, forceRefresh);
                var resp = Http.SendAsync(req).GetAwaiter().GetResult();
                return resp.StatusCode;
            }
        }

        private static void ApplyAuth(QmConfig cfg, HttpRequestMessage req, bool forceRefresh)
        {
            switch ((cfg.AuthMode ?? "none").ToLowerInvariant())
            {
                case "bearer":
                    if (!string.IsNullOrEmpty(cfg.TokenPlain))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.TokenPlain);
                    break;
                case "oauth2_cc":
                    string tok = OAuth.GetToken(cfg, Http, forceRefresh);
                    if (!string.IsNullOrEmpty(tok))
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tok);
                    break;
            }
        }
    }
}
