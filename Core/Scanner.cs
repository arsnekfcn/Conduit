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

namespace Conduit
{
    // Generic tagged-CustomData pipe. Reads any block whose Custom Data begins with "[CDT:<tag>]", but ONLY
    // on grids you can vanilla-access right now: own/shared faction, and either controlling it, on foot at it, or
    // within a broadcasting antenna's range. It forwards each packet verbatim and never interprets the
    // payload. it can only ever read what a vanilla script / server mod / hand could put on a grid whose terminal you could open yourself.
    public static class Scanner
    {
        private const string Marker = "[CDT:";

        public static Envelope Scan(ConduitConfig cfg)
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
            IMyCubeGrid terminalGrid = ResolveOpenTerminalGrid();
            // gates the antenna-range reach path below (no relay = no remote read)
            bool relayOnline = LocalRelayOnline(session, controlled);

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
                if (!CanAccess(grid, playerPos, controlled, terminalGrid, relayOnline, slims)) continue;   // vanilla reach gate

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

        // "[CDT:<tag>]\n<payload>" -> Packet. Payload is parsed JSON when valid, else the raw string.
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

        // The grid whose terminal/control-panel is open RIGHT NOW (null if none). This is the precise vanilla
        // "I have this grid's terminal up" signal - not a distance heuristic.
        private static IMyCubeGrid ResolveOpenTerminalGrid()
        {
            try
            {
                if (!Sandbox.Game.Gui.MyGuiScreenTerminal.IsOpen) return null;
                var e = Sandbox.Game.Gui.MyGuiScreenTerminal.InteractedEntity;
                return e as IMyCubeGrid ?? (e as IMyCubeBlock)?.CubeGrid;
            }
            catch { return null; }
        }

        // Vanilla reach, the three real ways you have a grid's terminal: you control its construct (in a chair),
        // you have its terminal open, or you're in range of a live broadcasting antenna on it with your own relay
        // online. NO distance heuristic - merely standing next to a grid doesn't count. Ownership is already
        // limited to own/faction by Evaluate, so this only applies to grids you could open anyway.
        private static readonly List<IMyCubeGrid> _grp = new List<IMyCubeGrid>();
        private static bool CanAccess(IMyCubeGrid grid, Vector3D playerPos, IMyCubeGrid controlled, IMyCubeGrid terminalGrid, bool relayOnline, List<IMySlimBlock> slims)
        {
            if (SameMechGroup(grid, controlled)) return true;     // in a chair (controlling the construct)
            if (SameMechGroup(grid, terminalGrid)) return true;   // its terminal is open
            if (!relayOnline) return false;                       // antenna path needs your own relay online
            foreach (var b in slims)
            {
                var ant = b.FatBlock as IngameAntenna;
                if (!IsLiveBroadcastAntenna(ant)) continue;
                double r = ant.Radius;
                // measure to the antenna block, not the grid pivot (can be tens of metres off on a large grid)
                if (Vector3D.DistanceSquared(playerPos, ant.GetPosition()) <= r * r) return true;
            }
            return false;
        }

        // A live relay = on, broadcasting, functional, and powered (the first two can be true on a dead antenna).
        private static bool IsLiveBroadcastAntenna(IngameAntenna ant)
            => ant != null && ant.Enabled && ant.EnableBroadcasting && ant.IsFunctional && ant.IsWorking;

        // Your own relay must be online too: suit antenna broadcasting, or (when piloting) a live antenna on the
        // ship you control. EnabledBroadcasting is on Sandbox.Game.Entities.IMyControllableEntity.
        private static readonly List<IMySlimBlock> _relayBlocks = new List<IMySlimBlock>();
        private static bool LocalRelayOnline(IMySession session, IMyCubeGrid controlled)
        {
            // prefer the character; fall back to the controlled entity when Character is briefly null (respawn)
            var ctl = (session.Player?.Character as Sandbox.Game.Entities.IMyControllableEntity)
                      ?? (session.Player?.Controller?.ControlledEntity?.Entity as Sandbox.Game.Entities.IMyControllableEntity);
            if (ctl != null && ctl.EnabledBroadcasting) return true;                          // suit antenna on
            if (controlled != null && GridHasLiveBroadcastAntenna(controlled)) return true;   // piloted ship's antenna
            return false;
        }

        private static bool GridHasLiveBroadcastAntenna(IMyCubeGrid grid)
        {
            _relayBlocks.Clear();
            grid.GetBlocks(_relayBlocks);
            foreach (var b in _relayBlocks)
                if (IsLiveBroadcastAntenna(b.FatBlock as IngameAntenna)) return true;
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
            catch { /* GetGridGroup can throw on a partially-streamed grid; treat as "not the controlled construct" */ }
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
