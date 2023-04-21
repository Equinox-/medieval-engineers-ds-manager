using System;
using System.Globalization;
using System.Xml;

namespace Meds.Watchdog.Save
{
    public readonly struct ChunkId : IEquatable<ChunkId>
    {
        public readonly byte Lod;
        public readonly int X, Y, Z;

        public ChunkId(byte lod, int x, int y, int z)
        {
            Lod = lod;
            X = x;
            Y = y;
            Z = z;
        }

        public static ChunkId From(XmlElement element) => new ChunkId(
            byte.Parse(element.Attributes["Lod"].InnerText),
            int.Parse(element.Attributes["X"].InnerText),
            int.Parse(element.Attributes["Y"].InnerText),
            int.Parse(element.Attributes["Z"].InnerText)
        );

        public override string ToString() => $"[Lod={Lod},X={X},Y={Y},Z={Z}]";

        public bool Equals(ChunkId other) => Lod == other.Lod && X == other.X && Y == other.Y && Z == other.Z;

        public override bool Equals(object obj) => obj is ChunkId other && Equals(other);

        public override int GetHashCode() => (int)DatabaseHash();

        public uint DatabaseHash()
        {
            uint JenkinsHash(uint x)
            {
                x += (x << 10);
                x ^= (x >> 6);
                x += (x << 3);
                x ^= (x >> 11);
                x += (x << 15);
                return x;
            }

            return JenkinsHash((uint)X) ^ JenkinsHash((uint)Y) ^ JenkinsHash((uint)Z) ^ JenkinsHash(Lod);
        }
    }

    public static class ObjectIds
    {
        public static bool TryParseObjectId(string objectIdString, out ulong objectId)
        {
            if (ulong.TryParse(objectIdString, out objectId))
                return true;
            return objectIdString.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                   && ulong.TryParse(objectIdString.Substring(2), NumberStyles.HexNumber, null, out objectId);
        }

        public static string ToString(ulong id) => $"0x{id:X}";
    }

    public readonly struct EntityId : IEquatable<EntityId>
    {
        public readonly ulong Value;

        public EntityId(ulong value) => Value = value;

        public override string ToString() => ObjectIds.ToString(Value);

        public bool Equals(EntityId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is EntityId other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public static bool TryParse(string id, out EntityId result)
        {
            result = default;
            if (!ObjectIds.TryParseObjectId(id, out var raw))
                return false;
            result = new EntityId(raw);
            return true;
        }

        public static implicit operator EntityId(ulong value) => new EntityId(value);

        public static implicit operator ulong(EntityId value) => value.Value;
    }

    public readonly struct GroupId : IEquatable<GroupId>
    {
        public readonly ulong Value;

        public GroupId(ulong value) => Value = value;

        public override string ToString() => ObjectIds.ToString(Value);

        public bool Equals(GroupId other) => Value == other.Value;

        public override bool Equals(object obj) => obj is GroupId other && Equals(other);

        public override int GetHashCode() => Value.GetHashCode();

        public static bool TryParse(string id, out GroupId result)
        {
            result = default;
            if (!ObjectIds.TryParseObjectId(id, out var raw))
                return false;
            result = new GroupId(raw);
            return true;
        }

        public static implicit operator GroupId(ulong value) => new GroupId(value);

        public static implicit operator ulong(GroupId value) => value.Value;
    }
}