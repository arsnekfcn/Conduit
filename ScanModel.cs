using System.Collections.Generic;

namespace Quartermaster
{
    // Schema v2.0: a generic, format-AGNOSTIC envelope. The plugin reads tagged Custom Data packets
    // ("[QM:<tag>]\n<payload>") off grids you can vanilla-access and forwards them verbatim. It does not
    // interpret the payload: any script-collectable data is piped out cleanly, and consumers do whatever
    // they want with it.
    public class Envelope
    {
        public string SchemaVersion = "2.0";
        public string CapturedAtUtc;            // ISO-8601 UTC
        public Observer Observer;
        public World World;
        public List<Packet> Packets = new List<Packet>();
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

    public class Packet
    {
        public string Tag;            // the <tag> from "[QM:<tag>]" namespaces the data
        public PacketSource Source;
        public object Payload;        // a parsed JSON token when the payload is valid JSON, else the raw string
    }

    public class PacketSource
    {
        public long EntityId;         // the grid carrying the packet block
        public string GridName;
        public string BlockName;
        public string FactionTag;     // source grid's faction (own/faction only, by the plugin's access gate)
    }
}
