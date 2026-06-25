using System;

namespace Quartermaster
{
    // In-game chat commands: "/qm sync | status | link | help".
    public class Commands
    {
        private readonly QmConfig _cfg;

        public Commands(QmConfig cfg) { _cfg = cfg; }

        public void OnMessage(string messageText, ref bool sendToOthers)
        {
            if (string.IsNullOrWhiteSpace(messageText)) return;
            string t = messageText.Trim();
            if (!t.StartsWith("/qm", StringComparison.OrdinalIgnoreCase)) return;
            sendToOthers = false;   // don't broadcast the command to chat

            string[] parts = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string cmd = parts.Length > 1 ? parts[1].ToLowerInvariant() : "help";
            try
            {
                switch (cmd)
                {
                    case "sync": Sync(); break;
                    case "status": Notify.Chat(StatusLine()); break;
                    case "link": Plugin.Instance?.OpenMenu(); break;
                    default:
                        Notify.Chat("Quartermaster: /qm sync (force a sync) | status | link (settings/onboarding) | help");
                        break;
                }
            }
            catch (Exception ex) { Plugin.Log("command failed: " + ex.Message); Notify.Chat("Quartermaster: command error (see log)."); }
        }

        private void Sync()
        {
            var p = Plugin.Instance;
            if (p == null) { Notify.Chat("Quartermaster: not ready."); return; }
            Notify.Chat("Quartermaster: syncing...");
            p.ManualSync();
        }

        private string StatusLine()
        {
            string mode = (_cfg.Online && !string.IsNullOrWhiteSpace(_cfg.EndpointUrl)) ? "online" : "offline";
            string age = SyncStatus.LastSyncTicksUtc == 0
                ? "never"
                : (int)Math.Max(0, (DateTime.UtcNow.Ticks - SyncStatus.LastSyncTicksUtc) / TimeSpan.TicksPerSecond) + "s ago";
            return $"Quartermaster [{mode}]: last sync {age}, {SyncStatus.LastGridCount} grid(s), {CodeText(SyncStatus.LastOnlineCode)}";
        }

        private static string CodeText(int code)
        {
            if (code == 200) return "OK";
            if (code == 0) return "offline/none";
            if (code == 401 || code == 403) return "auth rejected";
            if (code < 0) return "network error";
            return "HTTP " + code;
        }
    }
}
