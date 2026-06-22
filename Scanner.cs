using System;
using System.Collections.Generic;
using Sandbox.ModAPI;                       // MyAPIGateway, IMyGasTank, IMyBatteryBlock, IMyReactor
using VRage.Game.ModAPI;                    // IMyCubeGrid, IMySlimBlock, IMyCubeBlock, IMyFaction
using VRage.ModAPI;                         // IMyEntity (mod)
using IngameItem = VRage.Game.ModAPI.Ingame.MyInventoryItem;
using IngameItemType = VRage.Game.ModAPI.Ingame.MyItemType;
using IngameGun = Sandbox.ModAPI.Ingame.IMyUserControllableGun;
using IngameTurret = Sandbox.ModAPI.Ingame.IMyLargeTurretBase;
using IngameLauncher = Sandbox.ModAPI.Ingame.IMySmallMissileLauncher;
using IngameTerminal = Sandbox.ModAPI.Ingame.IMyTerminalBlock;
using IngameAssembler = Sandbox.ModAPI.Ingame.IMyAssembler;
using IngameRefinery = Sandbox.ModAPI.Ingame.IMyRefinery;
using IngameProduction = Sandbox.ModAPI.Ingame.IMyProductionBlock;

namespace Quartermaster
{
    // Passive scan: enumerate grids in streaming range, filter to own/faction per config.Scope, and read
    // inventory + telemetry + armament + classification off each. Everything here is the public ModAPI
    // read surface (verified replicated to clients for in-range grids); no terminal interaction needed.
    public static class Scanner
    {
        public static Envelope Scan(QmConfig cfg, Classifier classifier, Census census = null)
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

            var entities = new HashSet<IMyEntity>();
            MyAPIGateway.Entities.GetEntities(entities, e => e is IMyCubeGrid);

            var slims = new List<IMySlimBlock>();

            foreach (var e in entities)
            {
                var grid = e as IMyCubeGrid;
                if (grid == null) continue;

                long ownerId; string relation;
                if (!Evaluate(grid, me, myFac, cfg.Scope, out ownerId, out relation)) continue;
                if (grid.Physics == null) continue;

                slims.Clear();
                grid.GetBlocks(slims);
                // Per-grid opt-in: skip unless the owner has marked the grid (marker in a block name/CustomData).
                if (cfg.RequireTrackMarker && !HasTrackMarker(slims, cfg.TrackMarker)) continue;
                env.Grids.Add(BuildGrid(grid, ownerId, relation, myFac, session, cfg, classifier, slims, census));
            }

            return env;
        }

        // True if any terminal block on the grid carries the opt-in marker in its name or Custom Data.
        private static bool HasTrackMarker(List<IMySlimBlock> slims, string marker)
        {
            if (string.IsNullOrEmpty(marker)) return true;
            foreach (var b in slims)
            {
                var t = b.FatBlock as IngameTerminal;
                if (t == null) continue;
                if ((t.CustomName != null && t.CustomName.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (t.CustomData != null && t.CustomData.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0))
                    return true;
            }
            return false;
        }

        // Decide scope membership. included=true => grid sent to backend.
        private static bool Evaluate(IMyCubeGrid grid, long me, IMyFaction myFac, string scope,
                                     out long ownerId, out string relation)
        {
            ownerId = 0; relation = null;
            var owners = grid.BigOwners;
            if (owners == null || owners.Count == 0) return false;

            foreach (var o in owners) { if (o != 0) { ownerId = o; break; } }   // first real owner (for context)
            if (ownerId == 0) return false;                                     // all zero => unowned

            foreach (var o in owners)
                if (o != 0 && o == me) { ownerId = o; relation = "Self"; return true; }

            if (scope != "ownOnly" && myFac != null)
                foreach (var o in owners)
                    if (o != 0 && myFac.IsMember(o)) { ownerId = o; relation = "Faction"; return true; }

            relation = null;
            return false;
        }

        private static Grid BuildGrid(IMyCubeGrid grid, long ownerId, string relation, IMyFaction myFac,
            IMySession session, QmConfig cfg, Classifier classifier, List<IMySlimBlock> slims, Census census)
        {
            var ownerFac = session.Factions?.TryGetPlayerFaction(ownerId);

            var g = new Grid
            {
                EntityId = grid.EntityId,
                GroupId = ComputeGroupId(grid),
                Name = grid.DisplayName,
                GridSize = grid.GridSizeEnum.ToString(),
                IsStatic = grid.IsStatic,
                BlockCount = slims.Count,
                Owner = new Owner
                {
                    IdentityId = ownerId,
                    FactionId = ownerFac?.FactionId,
                    FactionTag = ownerFac?.Tag,
                    RelationToObserver = relation,
                },
            };

            // Health (always cheap; integrity is replicated).
            double integ = 0, maxInteg = 0; int damaged = 0, destroyed = 0;
            // Telemetry accumulators
            int h2Tanks = 0, o2Tanks = 0, batt = 0, reactors = 0;
            double h2Fill = 0, h2Cap = 0, o2Fill = 0, o2Cap = 0, battStored = 0, battMax = 0;
            var reactorFuel = new Dictionary<string, double>();   // fuel ingot subtype -> kg
            // Production (totals + active/producing counts; idle = total - active)
            int assemblers = 0, assemblersActive = 0, refineries = 0, refineriesActive = 0, otherProd = 0;
            // Aggregations
            var inv = new Dictionary<string, InventoryItem>();
            var arms = new Dictionary<string, Armament>();
            string coreSubtype = null, coreClass = null, coreCustomData = null;

            var items = new List<IngameItem>();
            foreach (var slim in slims)
            {
                integ += slim.Integrity; maxInteg += slim.MaxIntegrity;
                if (slim.IsDestroyed) destroyed++;
                else if (slim.Integrity < slim.MaxIntegrity) damaged++;

                var fat = slim.FatBlock;
                if (fat == null) continue;
                string subtype = fat.BlockDefinition.SubtypeName;

                census?.Record(fat, subtype, grid.EntityId);

                // Classification: first fat block whose subtype is in the class table is the "core".
                if (coreClass == null && classifier.TryClassForSubtype(subtype, out var cls))
                {
                    coreClass = cls; coreSubtype = subtype;
                }

                if (cfg.IncludeTelemetry)
                {
                    var tank = fat as IMyGasTank;
                    if (tank != null)
                    {
                        if (IsHydrogen(subtype)) { h2Tanks++; h2Cap += tank.Capacity; h2Fill += tank.FilledRatio * tank.Capacity; }
                        else { o2Tanks++; o2Cap += tank.Capacity; o2Fill += tank.FilledRatio * tank.Capacity; }
                    }
                    var battery = fat as IMyBatteryBlock;
                    if (battery != null) { batt++; battStored += battery.CurrentStoredPower; battMax += battery.MaxStoredPower; }
                    var reactor = fat as IMyReactor;
                    if (reactor != null) { reactors++; ReadReactorFuel(fat, reactorFuel); }
                }

                if (cfg.IncludeArmament)
                {
                    string cat = WeaponCategory(fat, subtype, classifier);
                    if (cat != null)
                    {
                        string key = cat + "|" + subtype;
                        Armament a;
                        if (!arms.TryGetValue(key, out a)) { a = new Armament { Category = cat, Subtype = subtype }; arms[key] = a; }
                        a.Count++;
                    }
                }

                if (fat is IngameAssembler) { assemblers++; if ((fat as IngameProduction)?.IsProducing == true) assemblersActive++; }
                else if (fat is IngameRefinery) { refineries++; if ((fat as IngameProduction)?.IsProducing == true) refineriesActive++; }
                else if (fat is IngameProduction) otherProd++;

                if (cfg.IncludeInventory) AccumulateInventory(fat, items, inv);

                // Stash core CustomData for the customdata classification fallback.
                if (coreSubtype == subtype && coreCustomData == null)
                {
                    var term = fat as IngameTerminal;
                    if (term != null) coreCustomData = term.CustomData;
                }
            }

            g.Health = new Health
            {
                Percent = maxInteg > 0 ? integ / maxInteg : 1.0,
                DamagedBlocks = damaged,
                DestroyedBlocks = destroyed,
            };

            g.Classification = ResolveClassification(classifier, grid, coreSubtype, coreClass, coreCustomData);

            if (cfg.IncludeTelemetry)
            {
                g.Telemetry = new Telemetry();
                if (h2Tanks > 0) g.Telemetry.Hydrogen = new GasStock { TankCount = h2Tanks, CapacityLiters = h2Cap, FilledRatio = h2Cap > 0 ? h2Fill / h2Cap : 0 };
                if (o2Tanks > 0) g.Telemetry.Oxygen = new GasStock { TankCount = o2Tanks, CapacityLiters = o2Cap, FilledRatio = o2Cap > 0 ? o2Fill / o2Cap : 0 };
                if (batt > 0) g.Telemetry.Batteries = new BatteryStock { Count = batt, StoredMWh = battStored, MaxMWh = battMax };
                if (reactors > 0)
                {
                    double fuelKg = 0; string domSub = null; double domAmt = -1;
                    foreach (var kv in reactorFuel) { fuelKg += kv.Value; if (kv.Value > domAmt) { domAmt = kv.Value; domSub = kv.Key; } }
                    g.Telemetry.Reactors = new ReactorStock { Count = reactors, FuelKg = fuelKg, FuelSubtype = domSub };
                }
            }

            if (cfg.IncludeArmament && arms.Count > 0) g.Armament = new List<Armament>(arms.Values);

            if (cfg.IncludeInventory)
            {
                g.Inventory = new List<InventoryItem>(inv.Values);
                var ammo = new List<AmmoItem>();
                foreach (var it in inv.Values) if (it.Category == "Ammo") ammo.Add(new AmmoItem { Subtype = it.Subtype, Amount = it.Amount });
                if (ammo.Count > 0) g.Ammo = ammo;
            }

            g.Production = new Production
            {
                Assemblers = assemblers, AssemblersActive = assemblersActive,
                Refineries = refineries, RefineriesActive = refineriesActive,
                OtherProductionBlocks = otherProd,
            };
            return g;
        }

        private static Classification ResolveClassification(Classifier classifier, IMyCubeGrid grid,
            string coreSubtype, string coreClass, string coreCustomData)
        {
            // Priority: subtype table -> core CustomData -> grid-name regex -> unknown (raw subtype kept).
            if (coreClass != null)
                return new Classification { Class = coreClass, Source = "subtype", CoreSubtype = coreSubtype };

            string fromData = classifier.ClassFromCustomData(coreCustomData);
            if (fromData != null)
                return new Classification { Class = fromData, Source = "customdata", CoreSubtype = coreSubtype };

            string fromName = classifier.ClassFromGridName(grid.DisplayName);
            if (fromName != null)
                return new Classification { Class = fromName, Source = "gridname", CoreSubtype = coreSubtype };

            return new Classification { Class = null, Source = "unknown", CoreSubtype = coreSubtype };
        }

        // Logical-ship id: the smallest EntityId across the grid's mechanical group (rotor/piston subgrids).
        // Deterministic, so every subgrid reports the same value -> the backend can roll them into one ship.
        // Connectors are NOT mechanical, so two docked ships stay separate.
        private static readonly List<IMyCubeGrid> _groupTmp = new List<IMyCubeGrid>();
        private static long ComputeGroupId(IMyCubeGrid grid)
        {
            long min = grid.EntityId;
            try
            {
                var group = grid.GetGridGroup(GridLinkTypeEnum.Mechanical);
                if (group != null)
                {
                    _groupTmp.Clear();
                    group.GetGrids(_groupTmp);
                    foreach (var gg in _groupTmp) if (gg != null && gg.EntityId < min) min = gg.EntityId;
                }
            }
            catch { /* group API hiccup -> fall back to own EntityId */ }
            return min;
        }

        private static void AccumulateInventory(IMyCubeBlock fat, List<IngameItem> items, Dictionary<string, InventoryItem> inv)
        {
            if (!fat.HasInventory) return;
            for (int i = 0; i < fat.InventoryCount; i++)
            {
                var inventory = fat.GetInventory(i);
                if (inventory == null) continue;
                items.Clear();
                inventory.GetItems(items);
                foreach (var it in items)
                {
                    string typeId = it.Type.TypeId;
                    string subtype = it.Type.SubtypeId;
                    string key = typeId + "/" + subtype;
                    InventoryItem agg;
                    if (!inv.TryGetValue(key, out agg))
                    {
                        agg = new InventoryItem { Category = Categorize(typeId), TypeId = typeId, Subtype = subtype };
                        inv[key] = agg;
                    }
                    agg.Amount += (double)it.Amount;
                }
            }
        }

        // Sum the reactor's fuel ingots by subtype (Uranium on vanilla; FusionFuel etc. on modded servers).
        private static void ReadReactorFuel(IMyCubeBlock reactor, Dictionary<string, double> into)
        {
            if (!reactor.HasInventory) return;
            var tmp = new List<IngameItem>();
            for (int i = 0; i < reactor.InventoryCount; i++)
            {
                var inventory = reactor.GetInventory(i);
                if (inventory == null) continue;
                tmp.Clear();
                inventory.GetItems(tmp);
                foreach (var it in tmp)
                {
                    if (it.Type.TypeId.IndexOf("Ingot", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string sub = it.Type.SubtypeId;
                    double amt; into.TryGetValue(sub, out amt);
                    into[sub] = amt + (double)it.Amount;
                }
            }
        }

        // Vanilla weapons by interface; modded/WeaponCore by the subtype table.
        private static string WeaponCategory(IMyCubeBlock fat, string subtype, Classifier classifier)
        {
            string fromTable = classifier.WeaponCategoryForSubtype(subtype);
            if (fromTable != null) return fromTable;
            if (fat is IngameTurret) return "Turret";
            if (fat is IngameLauncher) return "Launcher";
            if (fat is IngameGun) return "FixedGun";
            return null;
        }

        private static bool IsHydrogen(string subtype) =>
            subtype != null && subtype.IndexOf("Hydrogen", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string Categorize(string typeId)
        {
            if (typeId == null) return "Other";
            if (typeId.IndexOf("Ore", StringComparison.OrdinalIgnoreCase) >= 0) return "Ore";
            if (typeId.IndexOf("Ingot", StringComparison.OrdinalIgnoreCase) >= 0) return "Ingot";
            if (typeId.IndexOf("Component", StringComparison.OrdinalIgnoreCase) >= 0) return "Component";
            if (typeId.IndexOf("AmmoMagazine", StringComparison.OrdinalIgnoreCase) >= 0) return "Ammo";
            if (typeId.IndexOf("PhysicalGunObject", StringComparison.OrdinalIgnoreCase) >= 0) return "Tool";
            return "Other";
        }
    }
}
