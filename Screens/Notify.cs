using System;
using Sandbox;
using Sandbox.ModAPI;

namespace Conduit
{
    // Main-thread HUD pop-ups + chat messages. ShowNotification/ShowMessage must run on the game thread;
    // sync runs on a background thread, so marshal back via MySandboxGame.Static.Invoke.
    static class Notify
    {
        public static void OnMain(Action a)
        {
            try { var g = MySandboxGame.Static; if (g != null) g.Invoke(a, "Conduit"); }
            catch (Exception ex) { Plugin.Log("notify failed: " + ex.Message); }
        }

        public static void Hud(string text, int ms = 3000)
            => OnMain(() => { try { MyAPIGateway.Utilities?.ShowNotification(text, ms); } catch { /* HUD toast is cosmetic + best-effort; a failed notification must not surface or recurse */ } });

        public static void Chat(string text)
            => OnMain(() => { try { MyAPIGateway.Utilities?.ShowMessage("Conduit", text); } catch { /* same: a failed chat line is cosmetic and must not throw on the main thread */ } });
    }

    // Last-sync result, shown as the "linked?" status in the config menu.
    static class SyncStatus
    {
        public static volatile int LastOnlineCode;   // 200 ok | 401/403 rejected | 0 none/offline | -1 net error
        public static int LastGridCount;
        public static long LastSyncTicksUtc;

        public static void Record(int code, int grids)
        {
            LastOnlineCode = code;
            LastGridCount = grids;
            LastSyncTicksUtc = DateTime.UtcNow.Ticks;
        }
    }
}
