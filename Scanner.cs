using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;
using IngameTerminal = Sandbox.ModAPI.Ingame.IMyTerminalBlock;
using IngameAntenna = Sandbox.ModAPI.Ingame.IMyRadioAntenna;

namespace Quartermaster
{
    // Generic tagged-CustomData pipe. Reads any block whose Custom Data begins with "[QM:<tag>]", but ONLY
    // on grids you can vanilla-access right now: own/shared faction, and either controlling it, on foot at it, or
    // within a broadcasting antenna's range. It forwards each packet verbatim and never interprets the
    // payload. it can only ever read what a vanilla script / server mod / hand could put on a grid whose terminal you could open yourself.
    public static class Scanner
    {
        private const string Marker = "[QM:";

        public static Envelope Scan(QmConfig cfg)
        {
            var env = new Envelope { CapturedAtUtc = DateTime.UtcNow.ToString("o") };
            var session = MyAPIGateway.Session;
            if (session == null) return env;

            long me = session.Player?.IdentityId ?? 0;
            IMyFaction myFac = (me != 0) ? session.Factions?.TryGetPlayerFaction(me) : null;

            env.Observer = new Observer
            {
                IdentityId = me,
                SteamId = (long?)session.Player?.SteamUserId,
                DisplayName = session.Player?.DisplayName,
            };
            env.World = new World
            {
                ServerId = cfg.ServerId,
                SessionName = session.Name,
                SyncDistanceMeters = session.SessionSettings?.SyncDistance,
            };

            Vector3D playerPos = session.Player?.GetPosition() ?? Vector3D.Zero;
            IMyCubeGrid controlled = ResolveControlledGrid(session);

            var entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities, e => e is IMyCubeGrid);
            var slims = new List<IMySlimBlock>();

            foreach (var e in entities)
            {
                var grid = e as IMyCubeGrid;
                if (grid == null || grid.Physics == null) continue;

                long ownerId; string relation;
                if (!Evaluate(grid, me, myFac, cfg.Scope, out ownerId, out relation)) continue;   // own/faction only

                slims.Clear();
                grid.GetBlocks(slims);
                if (!CanAccess(grid, playerPos, controlled, slims)) continue;       // vanilla reach gate

                string facTag = session.Factions?.TryGetPlayerFaction(ownerId)?.Tag;
                foreach (var b in slims)
                {
                    var t = b.FatBlock as IngameTerminal;
                    if (t == null) continue;
                    // Vanilla terminal access. Respects SHARE MODE. A factionmate's block set to share "None"
                    // isn't openable by you in-game, so don't read it: Evaluate() passed the grid at owner/faction
                    // level, but share is per-block. Owner / faction-shared / unowned pass; no-share, enemy, and
                    // neutral all fail.
                    if (!t.HasPlayerAccess(me, MyRelationsBetweenPlayerAndBlock.NoOwnership)) continue;
                    var cd = t.CustomData;
                    if (string.IsNullOrEmpty(cd) || !cd.StartsWith(Marker, StringComparison.Ordinal)) continue;
                    var pkt = ParsePacket(cd, grid, t, facTag);
                    if (pkt != null) env.Packets.Add(pkt);
                }
            }

            return env;
        }

        // "[QM:<tag>]\n<payload>" -> Packet. Payload is parsed JSON when valid, else the raw string.
        private static Packet ParsePacket(string cd, IMyCubeGrid grid, IngameTerminal block, string facTag)
        {
            int close = cd.IndexOf(']');
            if (close < Marker.Length) return null;
            string tag = cd.Substring(Marker.Length, close - Marker.Length).Trim();
            if (tag.Length == 0) return null;

            int nl = cd.IndexOf('\n', close);
            string payloadText = nl >= 0 ? cd.Substring(nl + 1).Trim() : "";
            object payload = payloadText;                       // default: raw string
            if (payloadText.Length > 0)
            {
                try { payload = JToken.Parse(payloadText); }    // structured when it's JSON
                catch { payload = payloadText; }
            }

            return new Packet
            {
                Tag = tag,
                Payload = payload,
                Source = new PacketSource
                {
                    EntityId = grid.EntityId,
                    GridName = grid.DisplayName,
                    BlockName = block.CustomName,
                    FactionTag = facTag,
                },
            };
        }

        // The grid the player is currently controlling (cockpit / flight seat / remote control); null on foot.
        private static IMyCubeGrid ResolveControlledGrid(IMySession session)
        {
            var ent = session.Player?.Controller?.ControlledEntity?.Entity;
            return (ent as IMyCubeBlock)?.CubeGrid;
        }

        // Vanilla reach. The three ways the game lets you open a grid's terminal:
        //   (1) you're controlling the construct, 
        //   (2) you're physically at it (control-panel access), or
        //   (3) you're within range of a broadcasting antenna ON that grid (suit-antenna access).
        // Reach model / deliberate design choices (the gate is kept conservative. It excludes more than it must):
        //   - Ownership is already restricted to own/faction by Evaluate(), so an enemy/neutral antenna can never
        //     be used here; (3) only ever applies to a grid you could open anyway.
        //   - (3) tests the grid's own broadcasting-antenna radius against your position. It does NOT additionally
        //     require your character's suit antenna to be relaying.
        //   - Non-broadcasting remote grids and cross-faction allies are excluded entirely.
        //   - Distance is computed locally only.
        private const double ControlPanelRangeM = 20.0;
        private static readonly List<IMyCubeGrid> _grp = new List<IMyCubeGrid>();
        private static bool CanAccess(IMyCubeGrid grid, Vector3D playerPos, IMyCubeGrid controlled, List<IMySlimBlock> slims)
        {
            if (SameMechGroup(grid, controlled)) return true;
            if (grid.WorldAABB.Distance(playerPos) <= ControlPanelRangeM) return true;   // on foot at the grid
            foreach (var b in slims)
            {
                var ant = b.FatBlock as IngameAntenna;
                if (ant == null || !ant.Enabled || !ant.EnableBroadcasting) continue;
                double r = ant.Radius;
                if (Vector3D.DistanceSquared(playerPos, grid.GetPosition()) <= r * r) return true;
            }
            return false;
        }

        private static bool SameMechGroup(IMyCubeGrid a, IMyCubeGrid b)
        {
            if (a == null || b == null) return false;
            if (a.EntityId == b.EntityId) return true;
            try
            {
                var g = b.GetGridGroup(GridLinkTypeEnum.Mechanical);
                if (g != null)
                {
                    _grp.Clear();
                    g.GetGrids(_grp);
                    foreach (var x in _grp) if (x != null && x.EntityId == a.EntityId) return true;
                }
            }
            catch { }
            return false;
        }

        // own/faction membership (enemy / neutral / allied / unowned excluded).
        private static bool Evaluate(IMyCubeGrid grid, long me, IMyFaction myFac, string scope,
                                     out long ownerId, out string relation)
        {
            ownerId = 0; relation = null;
            var owners = grid.BigOwners;
            if (owners == null || owners.Count == 0) return false;

            foreach (var o in owners) { if (o != 0) { ownerId = o; break; } }
            if (ownerId == 0) return false;

            foreach (var o in owners)
                if (o != 0 && o == me) { ownerId = o; relation = "Self"; return true; }

            if (scope != "ownOnly" && myFac != null)
                foreach (var o in owners)
                    if (o != 0 && myFac.IsMember(o)) { ownerId = o; relation = "Faction"; return true; }

            relation = null;
            return false;
        }
    }
}
