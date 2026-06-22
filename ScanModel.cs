using System.Collections.Generic;
using Newtonsoft.Json;

namespace Quartermaster
{
    // POCOs mirroring SCHEMA.md v1.0. Serialized with a CamelCase resolver + NullValueHandling.Ignore,
    // so any field left null is OMITTED (the contract's "unknown vs zero" rule: a missing section means
    // "the observer couldn't read it", not "it's empty").

    public class Envelope
    {
        public string SchemaVersion = "1.0";
        public string CapturedAtUtc;            // ISO-8601 UTC
        public Observer Observer;
        public World World;
        public List<Grid> Grids = new List<Grid>();
    }

    public class Observer
    {
        public long IdentityId;
        public long? SteamId;
        public string DisplayName;
    }

    public class World
    {
        public string ServerId;
        public string SessionName;
        public double? SyncDistanceMeters;
    }

    public class Grid
    {
        public long EntityId;                   // universal, server-assigned, same for all observers (dedup key)
        public long GroupId;                    // mechanical-group representative id: subgrids share it (logical ship)
        public string Name;
        public string GridSize;                 // "Large" | "Small"
        public bool IsStatic;
        public int BlockCount;
        public Owner Owner;
        public Position Position;
        public Health Health;
        public Classification Classification;
        public Telemetry Telemetry;
        public List<Armament> Armament;
        public List<AmmoItem> Ammo;
        public List<InventoryItem> Inventory;
        public Production Production;
    }

    public class Owner
    {
        public long IdentityId;
        public long? FactionId;
        public string FactionTag;
        public string RelationToObserver;       // Self | FactionShare | Faction | Allied
    }

    public class Position { public double X, Y, Z; }

    public class Health
    {
        public double Percent;                  // 0..1
        public int DamagedBlocks;
        public int DestroyedBlocks;
    }

    public class Classification
    {
        [JsonProperty("class")] public string Class;   // "class" is a C# keyword
        public string Source;                   // subtype | customdata | gridname | unknown
        public string CoreSubtype;
    }

    public class Telemetry
    {
        public GasStock Hydrogen;
        public GasStock Oxygen;
        public BatteryStock Batteries;
        public ReactorStock Reactors;
    }

    public class GasStock { public double FilledRatio; public double CapacityLiters; public int TankCount; }
    public class BatteryStock { public double StoredMWh; public double MaxMWh; public int Count; }
    // FuelKg = total reactor-fuel ingots (Uranium on vanilla; e.g. FusionFuel on modded servers).
    // FuelSubtype = the dominant fuel ingot subtype, so consumers don't have to guess.
    public class ReactorStock { public int Count; public double FuelKg; public string FuelSubtype; }

    public class Armament
    {
        public string Category;                 // Turret | FixedGun | Launcher | WeaponCore | unclassified
        public string Subtype;
        public int Count;
    }

    public class AmmoItem { public string Subtype; public double Amount; }

    public class InventoryItem
    {
        public string Category;                 // Ore | Ingot | Component | Ammo | Tool | Other
        public string TypeId;
        public string Subtype;
        public double Amount;
    }

    public class Production
    {
        public int Assemblers;
        public int AssemblersActive;        // currently producing (IsProducing) — total minus this = idle
        public int Refineries;
        public int RefineriesActive;
        public int OtherProductionBlocks;
    }
}
