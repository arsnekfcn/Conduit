using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Conduit
{
    // In-game Steam onboarding. Opens the operator's /auth/steam/login in the Steam overlay browser (already
    // signed into the user's Steam account), runs a one-shot localhost loopback listener, and when the backend
    // redirects the verified token back to it, writes the token into config — no copy/paste, no external browser.
    //
    // Threading: Begin() runs on the MAIN game thread (called from the hotkey handler) and opens the URL there
    // because the overlay/GUI call is not thread-safe. Only the blocking accept-loop runs on a background thread.
    //
    // Security: the listener binds 127.0.0.1 only; it ignores any hit whose `state` doesn't match the random
    // nonce we generated; it keeps waiting (doesn't consume the one-shot) on a bad hit; and it times out.
    public static class Onboard
    {
        private static volatile TcpListener _current;   // the active attempt; a repeat press supersedes it

        public static void Begin(ConduitConfig cfg)
        {
            if (string.IsNullOrWhiteSpace(cfg.OnboardUrl))
            { Notify.Hud("Conduit: set the Onboard URL first (auth mode = bearer), then Link"); return; }
            if (!cfg.OnboardUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !cfg.AllowInsecureEndpoint)
            { Notify.Hud("Conduit: Onboard URL must be https (or set AllowInsecureEndpoint)"); return; }

            TcpListener listener;
            string state, url;
            int port;
            try
            {
                state = NewState();
                listener = new TcpListener(IPAddress.Loopback, 0);   // 127.0.0.1, OS-assigned ephemeral port
                listener.Start();
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
                string sep = cfg.OnboardUrl.Contains("?") ? "&" : "?";
                url = $"{cfg.OnboardUrl}{sep}state={state}&cb={port}";
            }
            catch (Exception ex) { Plugin.Log("onboard: failed to start loopback listener: " + ex.Message); return; }

            if (_current != null) Plugin.Log("onboard: restarting (previous attempt superseded)");
            _current = listener;   // any in-flight Wait() will see it's no longer current and exit
            Plugin.Log($"onboard: opening Steam sign-in (loopback 127.0.0.1:{port})");
            OpenUrl(url);   // MAIN THREAD — overlay/GUI is not thread-safe
            new Thread(() => Wait(listener, state, cfg)) { IsBackground = true, Name = "qm-onboard" }.Start();
        }

        // Background: wait for the backend to redirect the token to our loopback.
        private static void Wait(TcpListener listener, string state, ConduitConfig cfg)
        {
            try
            {
                var deadline = DateTime.UtcNow.AddMinutes(3);
                while (DateTime.UtcNow < deadline)
                {
                    if (_current != listener) return;   // a newer press superseded us — exit quietly
                    try { if (!listener.Pending()) { Thread.Sleep(200); continue; } }
                    catch { return; /* listener faulted/closed (e.g. a newer attempt superseded it) — stop waiting quietly */ }
                    using (var client = listener.AcceptTcpClient())
                    {
                        client.ReceiveTimeout = 4000;
                        var stream = client.GetStream();
                        string reqLine = ReadRequestLine(stream);
                        string code = QueryParam(reqLine, "code");
                        string gotState = QueryParam(reqLine, "state");
                        bool ok = !string.IsNullOrEmpty(code) && gotState == state;
                        Respond(stream, ok);
                        if (ok)
                        {
                            string token = ClaimToken(cfg, code);   // exchange the one-time code -> token over HTTPS
                            if (!string.IsNullOrEmpty(token))
                            {
                                cfg.TokenPlain = token;   // Save() encrypts it to disk (DPAPI)
                                cfg.AuthMode = "bearer";
                                cfg.Online = true;
                                cfg.Save();
                                Plugin.Log("onboard: account linked - token stored (applies on the next scan).");
                            }
                            else Plugin.Log("onboard: code exchange failed (token not stored)");
                            return;
                        }
                        Plugin.Log("onboard: ignored a loopback hit (bad state / no code); still waiting");
                    }
                }
                Plugin.Log("onboard: timed out waiting for Steam sign-in");
            }
            catch (Exception ex) { Plugin.Log("onboard failed: " + ex.Message); }
            finally { try { listener.Stop(); } catch { /* already stopped/faulted — nothing to do */ } if (_current == listener) _current = null; }
        }

        // Exchange the one-time onboarding code for the actual token via a direct HTTPS POST to the backend.
        // The token never appears in a browser URL/history — only this opaque single-use code does.
        private static string ClaimToken(ConduitConfig cfg, string code)
        {
            try
            {
                string claimUrl = cfg.OnboardUrl.Replace("/auth/steam/login", "/auth/steam/claim");
                claimUrl += (claimUrl.Contains("?") ? "&" : "?") + "code=" + Uri.EscapeDataString(code);
                using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) })
                using (var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, claimUrl))
                {
                    var resp = http.SendAsync(req).GetAwaiter().GetResult();
                    string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (!resp.IsSuccessStatusCode) { Plugin.Log("onboard: claim HTTP " + (int)resp.StatusCode); return null; }
                    return (string)Newtonsoft.Json.Linq.JObject.Parse(body)["token"];
                }
            }
            catch (Exception ex) { Plugin.Log("onboard: claim failed: " + ex.Message); return null; }
        }

        private static string NewState()
        {
            var b = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            var sb = new StringBuilder(32);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        // Read just the HTTP request line: "GET /?token=...&state=... HTTP/1.1"
        private static string ReadRequestLine(NetworkStream s)
        {
            var sb = new StringBuilder();
            int c, n = 0;
            while ((c = s.ReadByte()) >= 0 && c != '\n' && n++ < 8192)
                if (c != '\r') sb.Append((char)c);
            return sb.ToString();
        }

        private static string QueryParam(string requestLine, string key)
        {
            try
            {
                int q = requestLine.IndexOf('?');
                if (q < 0) return null;
                int sp = requestLine.IndexOf(' ', q);
                string qs = sp > q ? requestLine.Substring(q + 1, sp - q - 1) : requestLine.Substring(q + 1);
                foreach (var pair in qs.Split('&'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    if (pair.Substring(0, eq) == key) return Uri.UnescapeDataString(pair.Substring(eq + 1));
                }
            }
            catch { /* malformed request line — treat as "param not present" */ }
            return null;
        }

        private static void Respond(NetworkStream s, bool ok)
        {
            string body = ok
                ? "<h2>Conduit linked.</h2><p>You can close this and return to the game.</p>"
                : "<h2>Link failed.</h2><p>Mismatched request - start onboarding again from the game.</p>";
            string resp = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nConnection: close\r\n"
                + "Content-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body;
            var bytes = Encoding.UTF8.GetBytes(resp);
            s.Write(bytes, 0, bytes.Length);
            s.Flush();
        }

        // Open the URL in the Steam OVERLAY browser (renders over the game, already signed into the user's
        // Steam account) via Steamworks SteamFriends.ActivateGameOverlayToWebPage. Falls back to the system
        // browser only if the overlay is unavailable/disabled.
        private static void OpenUrl(string url)
        {
            try
            {
                var friends = FindType("Steamworks.SteamFriends");
                var utils = FindType("Steamworks.SteamUtils");
                if (friends != null)
                {
                    bool overlayOn = true;
                    var isOn = utils?.GetMethod("IsOverlayEnabled", BindingFlags.Public | BindingFlags.Static);
                    try { if (isOn != null) overlayOn = (bool)isOn.Invoke(null, null); } catch { /* IsOverlayEnabled probe failed; leave overlayOn=true, try the overlay, and fall back to the browser below if it throws */ }

                    if (overlayOn)
                    {
                        var m = friends.GetMethod("ActivateGameOverlayToWebPage", BindingFlags.Public | BindingFlags.Static);
                        if (m != null)
                        {
                            var ps = m.GetParameters();
                            object[] args = ps.Length >= 2
                                ? new object[] { url, Enum.ToObject(ps[1].ParameterType, 0) }   // mode = Default
                                : new object[] { url };
                            m.Invoke(null, args);
                            Plugin.Log("onboard: opened in the Steam overlay browser");
                            return;
                        }
                    }
                    else
                    {
                        Plugin.Log("onboard: Steam overlay is DISABLED — enable it (Steam > Settings > In Game) " +
                                   "for the in-game flow; opening the system browser instead");
                    }
                }
            }
            catch (Exception ex) { Plugin.Log("onboard: overlay open failed, falling back: " + ex.Message); }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
                Plugin.Log("onboard: opened sign-in in the system browser");
            }
            catch (Exception ex) { Plugin.Log("onboard: browser open failed: " + ex.Message); }
        }

        // Resolve a type by full name across all loaded assemblies (Steamworks.NET's assembly name varies).
        private static Type FindType(string fullName)
        {
            var t = Type.GetType(fullName);
            if (t != null) return t;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = a.GetType(fullName); if (t != null) return t; } catch { /* some assemblies throw on GetType (dynamic/reflection-only) — skip and try the next */ }
            }
            return null;
        }
    }
}
