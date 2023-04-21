using System.Xml.Serialization;

namespace Meds.Watchdog.Save
{
    public class PlanetData
    {
        [XmlElement]
        public float MinRadius;

        [XmlElement]
        public float AvgRadius;

        [XmlElement]
        public float MaxRadius;

        [XmlElement]
        public int AreasPerRegion;

        [XmlElement]
        public int AreasPerFace;
    }

    public class GridDatabaseConfig
    {
        [XmlElement]
        public int MaxLod = 3;

        [XmlElement]
        public float GridSize = 32;

        public BoundingBox ToWorld(byte lod, BoundingBox cell) => cell * (GridSize * (1 << lod));

        public BoundingBox FromWorld(byte lod, BoundingBox cell) => cell * (1 / (GridSize * (1 << lod)));
    }
}