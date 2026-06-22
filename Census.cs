using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using VRage.Game.ModAPI;
using IngameGun = Sandbox.ModAPI.Ingame.IMyUserControllableGun;
using IngameTurret = Sandbox.ModAPI.Ingame.IMyLargeTurretBase;
using IngameLauncher = Sandbox.ModAPI.Ingame.IMySmallMissileLauncher;
using IngameBeacon = Sandbox.ModAPI.Ingame.IMyBeacon;

namespace Quartermaster
{
    // Diagnostic: when enabled (config.SubtypeCensus), record every distinct fat-block subtype seen on
    // scanned grids, with hints, so the operator can populate weapons.json / classes.json for a modded
    // server (where weapons + ship-cores are mod blocks with no vanilla interfaces). Writes:
    //   census\subtypes.json           - full annotated census, weapon-likely first
    //   census\weapons.suggested.json  - { subtype: guessedCategory } for likely weapons (rename to weapons.json)
    public class Census
    {
        private static readonly string[] WeaponWords =
        {
            "Turret","Gun","Cannon","Launcher","Weapon","PDC","Rail","Gauss","Artillery","Autocannon",
            "Missile","Torpedo","Rocket","Laser","Beam","Flak","Gatling","Mortar","Coil","Bofors"
        };

        private class Entry
        {
            public string TypeId;
            public string Subtype;
            public int Count;
            [JsonIgnore] public HashSet<long> Grids = new HashSet<long>();
            public int GridCount;
            public bool HasInventory;
            public string VanillaCategory;       // Turret | Launcher | FixedGun | null
            public bool ConveyorSorterBase;      // WeaponCore is commonly built on a conveyor-sorter base
            public bool NameWeaponish;
            public bool Coreish;
            // Real-weapon signal: a vanilla gun, or a conveyor-sorter-based block WITH an inventory (how
            // WeaponCore guns present). A name-only match (e.g. structural "Beam" blocks) is NOT enough.
            [JsonIgnore] public bool Weaponish => VanillaCategory != null || (ConveyorSorterBase && HasInventory);
        }

        private readonly Dictionary<string, Entry> _map = new Dictionary<string, Entry>();

        public void Record(IMyCubeBlock fat, string subtype, long gridId)
        {
            string typeId = fat.BlockDefinition.TypeIdString;
            string key = typeId + "|" + subtype;
            Entry e;
            if (!_map.TryGetValue(key, out e))
            {
                e = new Entry
                {
                    TypeId = typeId,
                    Subtype = subtype,
                    VanillaCategory = VanillaCat(fat),
                    ConveyorSorterBase = typeId.IndexOf("ConveyorSorter", StringComparison.OrdinalIgnoreCase) >= 0,
                    NameWeaponish = LooksWeaponish(subtype),
                    Coreish = (subtype != null && subtype.IndexOf("Core", StringComparison.OrdinalIgnoreCase) >= 0) || fat is IngameBeacon,
                };
                _map[key] = e;
            }
            e.Count++;
            e.HasInventory |= fat.HasInventory;
            e.Grids.Add(gridId);
        }

        private static string VanillaCat(IMyCubeBlock fat)
        {
            if (fat is IngameTurret) return "Turret";
            if (fat is IngameLauncher) return "Launcher";
            if (fat is IngameGun) return "FixedGun";
            return null;
        }

        private static bool LooksWeaponish(string subtype)
        {
            if (string.IsNullOrEmpty(subtype)) return false;
            foreach (var w in WeaponWords)
                if (subtype.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // WeaponCore sensors (lidar/radar/camera) ride the same conveyor-sorter base but aren't weapons.
        private static readonly string[] SensorWords = { "Lidar", "Radar", "Sensor", "Camera", "Scanner" };
        private static bool LooksSensor(string subtype)
        {
            if (string.IsNullOrEmpty(subtype)) return false;
            foreach (var w in SensorWords)
                if (subtype.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        public void Write()
        {
            try
            {
                string dir = Path.Combine(QmConfig.Dir, "census");
                Directory.CreateDirectory(dir);

                var entries = new List<Entry>(_map.Values);
                foreach (var e in entries) e.GridCount = e.Grids.Count;
                entries.Sort((a, b) =>
                {
                    if (a.Weaponish != b.Weaponish) return a.Weaponish ? -1 : 1;   // weapon-likely first
                    return b.Count.CompareTo(a.Count);
                });

                var doc = new
                {
                    generatedUtc = DateTime.UtcNow.ToString("o"),
                    note = "Subtype census. Copy weapon subtypes into weapons.json and the ship-core subtype into classes.json.",
                    totalSubtypes = entries.Count,
                    entries,
                };
                File.WriteAllText(Path.Combine(dir, "subtypes.json"),
                    JsonConvert.SerializeObject(doc, Formatting.Indented));

                var suggested = new Dictionary<string, string>();
                foreach (var e in entries)
                    if (e.Weaponish && !LooksSensor(e.Subtype)) suggested[e.Subtype] = e.VanillaCategory ?? "WeaponCore";
                File.WriteAllText(Path.Combine(dir, "weapons.suggested.json"),
                    JsonConvert.SerializeObject(suggested, Formatting.Indented));

                Plugin.Log($"census: {entries.Count} subtypes -> {Path.Combine(dir, "subtypes.json")}");
            }
            catch (Exception ex) { Plugin.Log("census write failed: " + ex.Message); }
        }
    }
}
