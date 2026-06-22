using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;
using IngameTerminal = Sandbox.ModAPI.Ingame.IMyTerminalBlock;

namespace Quartermaster
{
    // In-game chat commands: "/qm track | untrack | status". track/untrack mark the grid you're aiming at —
    // but only if YOU own it. The marker is written into a block's Custom Data ON THE GRID, so it's
    // authoritative for every faction member's client (not a per-player local list). A grid is only collected
    // by the passive scanner when it carries this marker (see QmConfig.RequireTrackMarker).
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
                    case "track": Mark(true); break;
                    case "untrack": Mark(false); break;
                    case "status": Status(); break;
                    default:
                        Notify.Chat("Quartermaster: aim at YOUR grid, then /qm track or /qm untrack. (/qm status to check)");
                        break;
                }
            }
            catch (Exception ex) { Plugin.Log("command failed: " + ex.Message); Notify.Chat("Quartermaster: command error (see log)."); }
        }

        private void Mark(bool on)
        {
            IngameTerminal hit;
            var grid = LookedAtGrid(out hit);
            if (grid == null) { Notify.Chat("Quartermaster: aim at a grid (within ~200m) first."); return; }

            long me = MyAPIGateway.Session?.Player?.IdentityId ?? 0;
            var owners = grid.BigOwners;
            if (owners == null || !owners.Contains(me))
            {
                Notify.Chat("Quartermaster: you don't own that grid - only its owner can change its tracking.");
                return;
            }

            var target = hit ?? FirstTerminal(grid);
            if (target == null) { Notify.Chat("Quartermaster: no functional block to mark; name a block " + _cfg.TrackMarker + " by hand."); return; }

            string cd = target.CustomData ?? "";
            bool has = cd.IndexOf(_cfg.TrackMarker, StringComparison.OrdinalIgnoreCase) >= 0;
            if (on)
            {
                if (!has) target.CustomData = (cd.Length == 0 ? "" : cd + "\n") + _cfg.TrackMarker;
                Notify.Chat($"Quartermaster: tracking ON for \"{grid.DisplayName}\" (marker on {target.CustomName}).");
            }
            else
            {
                if (has) target.CustomData = RemoveMarkerLine(cd, _cfg.TrackMarker);
                Notify.Chat($"Quartermaster: tracking OFF for \"{grid.DisplayName}\".");
            }
        }

        private void Status()
        {
            IngameTerminal hit;
            var grid = LookedAtGrid(out hit);
            if (grid == null) { Notify.Chat("Quartermaster: aim at a grid to check its tracking."); return; }
            bool marked = GridHasMarker(grid);
            string note = _cfg.RequireTrackMarker ? "" : " (marker not required - all owned/faction grids are tracked)";
            Notify.Chat($"Quartermaster: \"{grid.DisplayName}\" is {(marked ? "TRACKED" : "NOT tracked")}{note}.");
        }

        // Grid the player is aiming at (camera ray ~200m); also resolves the looked-at terminal block if any.
        private IMyCubeGrid LookedAtGrid(out IngameTerminal hitBlock)
        {
            hitBlock = null;
            var cam = MyAPIGateway.Session?.Camera;
            if (cam == null || MyAPIGateway.Physics == null) return null;
            Vector3D from = cam.Position;
            Vector3D to = from + cam.WorldMatrix.Forward * 200.0;
            IHitInfo hit;
            if (!MyAPIGateway.Physics.CastRay(from, to, out hit) || hit?.HitEntity == null) return null;
            var grid = hit.HitEntity as IMyCubeGrid;
            if (grid == null) return null;
            try
            {
                var bpos = grid.RayCastBlocks(from, to);
                if (bpos.HasValue) hitBlock = grid.GetCubeBlock(bpos.Value)?.FatBlock as IngameTerminal;
            }
            catch { }
            return grid;
        }

        private static IngameTerminal FirstTerminal(IMyCubeGrid grid)
        {
            var blocks = new List<IMySlimBlock>();
            grid.GetBlocks(blocks);
            foreach (var b in blocks) { var t = b.FatBlock as IngameTerminal; if (t != null) return t; }
            return null;
        }

        private bool GridHasMarker(IMyCubeGrid grid)
        {
            var blocks = new List<IMySlimBlock>();
            grid.GetBlocks(blocks);
            foreach (var b in blocks)
            {
                var t = b.FatBlock as IngameTerminal;
                if (t == null) continue;
                if ((t.CustomName != null && t.CustomName.IndexOf(_cfg.TrackMarker, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (t.CustomData != null && t.CustomData.IndexOf(_cfg.TrackMarker, StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;
            }
            return false;
        }

        private static string RemoveMarkerLine(string customData, string marker)
        {
            var kept = new List<string>();
            foreach (var ln in customData.Replace("\r\n", "\n").Split('\n'))
                if (ln.IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0) kept.Add(ln);
            return string.Join("\n", kept).Trim();
        }
    }
}
