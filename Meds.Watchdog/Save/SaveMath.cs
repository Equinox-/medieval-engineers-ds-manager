using System.Numerics;
using System.Xml.Serialization;

namespace Meds.Watchdog.Save
{
    public struct SerializableVector3
    {
        [XmlIgnore]
        public Vector3 Value;

        [XmlAttribute("x")]
        public float X
        {
            get => Value.X;
            set => Value.X = value;
        }

        [XmlAttribute("y")]
        public float Y
        {
            get => Value.Y;
            set => Value.Y = value;
        }

        [XmlAttribute("z")]
        public float Z
        {
            get => Value.Z;
            set => Value.Z = value;
        }

        [XmlElement("X")]
        public float SerX
        {
            get => Value.X;
            set => Value.X = value;
        }

        [XmlElement("Y")]
        public float SerY
        {
            get => Value.Y;
            set => Value.Y = value;
        }

        [XmlElement("Z")]
        public float SerZ
        {
            get => Value.Z;
            set => Value.Z = value;
        }

        public static implicit operator Vector3(SerializableVector3 vec) => vec.Value;
        public static implicit operator SerializableVector3(Vector3 vec) => new SerializableVector3 { Value = vec };
    }

    public struct BoundingBox
    {
        [XmlElement("Min", typeof(SerializableVector3))]
        public Vector3 Min;

        [XmlElement("Max", typeof(SerializableVector3))]
        public Vector3 Max;

        public Vector3 Center => (Min + Max) / 2;

        public void Include(BoundingBox other)
        {
            Min = Vector3.Min(Min, other.Min);
            Max = Vector3.Max(Max, other.Max);
        }

        public void Include(Vector3 other)
        {
            Min = Vector3.Min(Min, other);
            Max = Vector3.Max(Max, other);
        }

        public override string ToString() => $"[{Min} - {Max}]";

        public static BoundingBox operator *(BoundingBox lhs, float rhs) => new BoundingBox
        {
            Min = (rhs < 0 ? lhs.Max : lhs.Min) * rhs,
            Max = (rhs < 0 ? lhs.Min : lhs.Max) * rhs
        };
    }

    public struct BoundingSphere
    {
        [XmlElement("Center", typeof(SerializableVector3))]
        public Vector3 Center;

        [XmlElement("Radius")]
        public float Radius;

        public static BoundingSphere From(BoundingBox other) => new BoundingSphere
        {
            Center = other.Center,
            Radius = (other.Max - other.Min).Length() / 2,
        };
    }

    public struct PositionAndOrientation
    {
        [XmlElement("Position", typeof(SerializableVector3))]
        public Vector3 Position;

        [XmlElement("Forward", typeof(SerializableVector3))]
        public Vector3 Forward;

        [XmlElement("Up", typeof(SerializableVector3))]
        public Vector3 Up;
    }
}