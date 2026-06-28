using System;
using System.Collections.Generic;
using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace Conduit
{
    // OAuth2 client-credentials token cache for the online sink. Fetches a bearer token from the
    // operator's token endpoint (their IdP / API gateway), caches it until shortly before expiry,
    // and refreshes on demand.
    public static class OAuth
    {
        private static readonly object Lock = new object();
        private static string _token;
        private static DateTime _expiresUtc = DateTime.MinValue;

        public static string GetToken(ConduitConfig cfg, HttpClient http, bool forceRefresh = false)
        {
            lock (Lock)
            {
                if (!forceRefresh && _token != null && DateTime.UtcNow < _expiresUtc)
                    return _token;

                // The client secret travels to TokenUrl — require HTTPS (same gate as the online sink) unless
                // the operator explicitly allows an insecure endpoint.
                if (!string.IsNullOrEmpty(cfg.TokenUrl)
                    && !cfg.TokenUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    && !cfg.AllowInsecureEndpoint)
                    throw new Exception("oauth2_cc: TokenUrl is not https (the client secret would be cleartext). "
                                        + "Use https, or set AllowInsecureEndpoint=true to override.");

                var form = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("grant_type", "client_credentials"),
                    new KeyValuePair<string, string>("client_id", cfg.ClientId ?? ""),
                    new KeyValuePair<string, string>("client_secret", cfg.ClientSecretPlain ?? ""),
                };
                if (!string.IsNullOrWhiteSpace(cfg.OAuthScope))
                    form.Add(new KeyValuePair<string, string>("scope", cfg.OAuthScope));

                using (var req = new HttpRequestMessage(HttpMethod.Post, cfg.TokenUrl))
                {
                    req.Content = new FormUrlEncodedContent(form);
                    var resp = http.SendAsync(req).GetAwaiter().GetResult();
                    string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!resp.IsSuccessStatusCode)
                        throw new Exception($"token endpoint {(int)resp.StatusCode}: {Truncate(body)}");

                    var j = JObject.Parse(body);
                    _token = (string)j["access_token"];
                    int ttl = (int?)j["expires_in"] ?? 3600;
                    // Refresh 60s early (floor 30s) so a token never expires mid-post.
                    _expiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(30, ttl - 60));
                    return _token;
                }
            }
        }

        public static void Invalidate()
        {
            lock (Lock) { _token = null; _expiresUtc = DateTime.MinValue; }
        }

        private static string Truncate(string s) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length > 200 ? s.Substring(0, 200) : s);
    }
}
