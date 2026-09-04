using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using VRage.Input;
using VRage.Plugins;
using VRage.Utils;

namespace Conduit
{
    // Conduit plugin entry point (client-side, Pulsar/Legacy net48). Periodically reads [CDT:<tag>] Custom
    // Data packets off own/faction grids you can vanilla-access and ships them to a backend (or a local
    // file). The Custom Data read runs on the main/update thread; serialization + network go to a bg thread.
    public class Plugin : IPlugin
    {
        public const string Id = "conduit";

        public static Plugin Instance;     // for the config menu to reach scan/config-apply

        private ConduitConfig _cfg;
        private int _frame;
        private int _intervalFrames = 240;          // recomputed from config once loaded (~60 fps)
        private int _liveFrames = 6;                // live-push scan cadence (frames)
        private string _lastFingerprint;            // last-pushed payload fingerprint (live push)
        private DateTime _lastLivePush = DateTime.MinValue;
        private readonly Dictionary<string, DateTime> _skipLogged = new Dictionary<string, DateTime>();
        private static readonly TimeSpan SkipLogEvery = TimeSpan.FromMinutes(5);
        private int _hkCooldown;
        private MyKeys _hkKey = MyKeys.End;
        private int _linkCooldown;
        private MyKeys _linkKey = MyKeys.Home;
        private Commands _commands;
        private bool _chatHooked;
        private volatile bool _sending;

        public void Init(object gameInstance)
        {
            Log("Init: loading");
            // Newtonsoft.Json: sibling DLL for a Local install (deploy.sh copies it), else Pulsar's bundled copy.
#if NETFRAMEWORK
            // net48 only: modern .NET negotiates TLS 1.2+ by default and ServicePointManager is
            // obsolete there (and a no-op for HttpClient).
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12; } catch { /* best-effort TLS bump; if the runtime rejects the OR, its default protocol still negotiates HTTPS */ }
#endif
            try
            {
                Instance = this;
                _cfg = ConduitConfig.Load();
                if (_cfg.LoadError != null) Log("config load error (using defaults): " + _cfg.LoadError);
                _commands = new Commands(_cfg);
                _intervalFrames = Math.Max(30, (int)(_cfg.ScanIntervalSeconds * 60.0));
                _liveFrames = Math.Max(1, _cfg.LiveScanFrames);
                if (!Enum.TryParse(_cfg.HotkeyKey, true, out _hkKey)) _hkKey = MyKeys.End;
                if (!Enum.TryParse(_cfg.LinkHotkeyKey, true, out _linkKey)) _linkKey = MyKeys.Home;
                string online = (_cfg.Online && !string.IsNullOrWhiteSpace(_cfg.EndpointUrl))
                    ? $"online[{_cfg.AuthMode}]={_cfg.EndpointUrl}" : "online=off";
                string offline = _cfg.Offline ? $"offline={_cfg.ResolveOfflinePath()}" : "offline=off";
                Log($"ready: scope={_cfg.Scope} interval={_cfg.ScanIntervalSeconds}s {offline} {online}");
            }
            catch (Exception ex) { Log("Init FAILED: " + ex); }
        }

        public void Update()
        {
            if (_cfg == null || MyAPIGateway.Session == null) return;
            if (!_chatHooked && _commands != null && MyAPIGateway.Utilities != null)
            {
                try { MyAPIGateway.Utilities.MessageEntered += _commands.OnMessage; _chatHooked = true; } catch { /* retried every Update until it binds (_chatHooked); a transient null during load is expected */ }
            }
            try { TryHotkey(); } catch { /* per-tick input poll: a transient input/GUI-state hiccup must not throw out of Update and stall the sim */ }
            try { TryLinkHotkey(); } catch { /* same: the link-hotkey poll must never throw into the sim loop */ }
            if (_cfg.LivePush)
            {
                if (++_frame < _liveFrames) return;
                _frame = 0;
                try { LiveTick(); } catch (Exception ex) { Log("live scan failed: " + ex.Message); }
            }
            else
            {
                if (++_frame < _intervalFrames) return;
                _frame = 0;
                try { ScanAndSend(false); } catch (Exception ex) { Log("scan failed: " + ex.Message); }
            }
        }

        // Live push: scan on the fast cadence, POST only when the reachable payloads changed since the last
        // push. Skips while a send is in flight (the change re-detects next tick), so a spammy PB naturally
        // rate-limits to the round-trip time and the newest state always wins.
        private void LiveTick()
        {
            if (_sending) return;
            if ((DateTime.UtcNow - _lastLivePush).TotalSeconds < Math.Max(0.0, _cfg.LiveMinIntervalSeconds)) return;
            var env = Scanner.Scan(_cfg);
            LogSkipped(env);
            int count = env.Packets.Count;
            if (count == 0) { _lastFingerprint = null; return; }   // nothing in reach; re-appearance re-pushes
            if (env.Fingerprint == _lastFingerprint) return;       // unchanged
            _lastFingerprint = env.Fingerprint;
            _lastLivePush = DateTime.UtcNow;
            Dispatch(env, count, false);
        }

        // Grids carrying a packet that the reach gate refused, once per grid per five minutes: enough to
        // answer "why is DX6 REF LEFT not updating" from the log without flooding it at 10 Hz.
        private void LogSkipped(Envelope env)
        {
            if (env.Skipped.Count == 0) return;
            var now = DateTime.UtcNow;
            foreach (var line in env.Skipped)
            {
                DateTime last;
                if (_skipLogged.TryGetValue(line, out last) && now - last < SkipLogEvery) continue;
                _skipLogged[line] = now;
                Log("out of reach, not forwarded: " + line);
            }
        }

        // Build the payload on the MAIN thread, then hand it off.
        // manual=true shows a HUD pop-up on completion; auto syncs optionally announce in chat.
        private void ScanAndSend(bool manual)
        {
            if (_sending) { if (manual) Notify.Hud("Conduit: a sync is already running"); return; }
            var env = Scanner.Scan(_cfg);
            LogSkipped(env);
            int count = env.Packets.Count;
            if (count == 0)
            {
                if (manual) Notify.Hud(env.Skipped.Count > 0
                    ? "Conduit: no packets in reach; " + env.Skipped.Count + " grid(s) out of reach (see log)"
                    : "Conduit: no [CDT:...] packets in reach to sync");
                return;
            }
            Dispatch(env, count, manual, env.Skipped.Count);
        }

        // Serialize + POST on a bg thread. Sets _sending for the duration (one in-flight send at a time).
        private void Dispatch(Envelope env, int count, bool manual, int skipped = 0)
        {
            _sending = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                int code = 0;
                try { code = Sender.Send(_cfg, env); }
                catch (Exception ex) { Log("send failed: " + ex.Message); code = -1; }
                finally { _sending = false; }
                if (manual) Notify.Hud(SyncMsg("Manual sync", count, code)
                                        + (skipped > 0 ? $" ({skipped} grid(s) out of reach, see log)" : ""), 4000);
                else if (_cfg.ChatOnSync) Notify.Chat(SyncMsg("Synced", count, code));
            });
        }

        private static string SyncMsg(string prefix, int count, int code)
        {
            if (code == 200) return $"{prefix}: {count} packet(s) OK";
            if (code == 0) return $"{prefix}: {count} packet(s) written offline";
            if (code < 0) return $"{prefix}: {count} packet(s) - network error";
            return $"{prefix}: server returned {code}";
        }

        // Called from the config menu (main thread).
        public void ManualSync() { try { ScanAndSend(true); } catch (Exception ex) { Log("manual sync failed: " + ex.Message); } }
        public void OnConfigChanged()
        {
            _intervalFrames = Math.Max(30, (int)(_cfg.ScanIntervalSeconds * 60.0));
            _liveFrames = Math.Max(1, _cfg.LiveScanFrames);
            _lastFingerprint = null;   // re-push once under the new settings
            _frame = 0;
        }

        // Configurable hotkey (default Ctrl+Shift+End): force an immediate scan. The periodic scan runs
        // regardless, so this is just a force-now aid. Exact modifier match so it won't fire on other combos.
        private void TryHotkey()
        {
            if (_hkCooldown > 0) { _hkCooldown--; return; }
            if (_hkKey == MyKeys.None) return;
            try { if (MyAPIGateway.Gui != null && MyAPIGateway.Gui.ChatEntryVisible) return; } catch { /* Gui can be null mid-load; treat as "chat not open" and continue */ }
            var input = MyAPIGateway.Input;
            if (input == null) return;
            if (_cfg.HotkeyCtrl != input.IsAnyCtrlKeyPressed()) return;
            if (_cfg.HotkeyShift != input.IsAnyShiftKeyPressed()) return;
            if (input.IsNewKeyPressed(_hkKey))
            {
                _hkCooldown = 60;
                Log("hotkey: manual scan");
                try { ScanAndSend(true); } catch (Exception ex) { Log("manual scan failed: " + ex.Message); }
            }
        }

        // "Link account" hotkey (default Ctrl+Shift+Home): kick off in-game Steam onboarding.
        private void TryLinkHotkey()
        {
            if (_linkCooldown > 0) { _linkCooldown--; return; }
            if (_linkKey == MyKeys.None) return;
            try { if (MyAPIGateway.Gui != null && MyAPIGateway.Gui.ChatEntryVisible) return; } catch { /* Gui can be null mid-load; treat as "chat not open" and continue */ }
            var input = MyAPIGateway.Input;
            if (input == null) return;
            if (_cfg.LinkHotkeyCtrl != input.IsAnyCtrlKeyPressed()) return;
            if (_cfg.LinkHotkeyShift != input.IsAnyShiftKeyPressed()) return;
            if (input.IsNewKeyPressed(_linkKey))
            {
                _linkCooldown = 120;
                Log("hotkey: open menu");
                OpenMenu();
            }
        }

        // Open the config / link menu (from the hotkey or the /conduit link chat command). Main thread only.
        public void OpenMenu()
        {
            try { MyGuiSandbox.AddScreen(new ConfigScreen(_cfg)); } catch (Exception ex) { Log("menu open failed: " + ex.Message); }
        }

        // Pulsar calls this (by reflection) for the plugin's Settings button and the Ctrl+Shift+/ config list.
        public void OpenConfigDialog() => OpenMenu();

        public void Dispose()
        {
            try { if (_chatHooked && MyAPIGateway.Utilities != null) MyAPIGateway.Utilities.MessageEntered -= _commands.OnMessage; } catch { /* teardown best-effort: if the utilities are already gone there is nothing to detach */ }
            Log("Dispose");
        }

        public static void Log(string msg) => MyLog.Default?.WriteLineAndConsole("[" + Id + "] " + msg);
    }
}
