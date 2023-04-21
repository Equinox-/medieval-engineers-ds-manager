using System.Xml;

namespace Meds.Watchdog.Save
{
    public readonly struct ChunkObjectData
    {
        public readonly byte Lod;
        public readonly BoundingBox CellBounds;

        private ChunkObjectData(byte lod, BoundingBox cellBounds)
        {
            Lod = lod;
            CellBounds = cellBounds;
        }

        public static bool TryParse(XmlNode node, out ChunkObjectData chunkData)
        {
            chunkData = default;
            var boundsData = node["Bounds"];
            if (boundsData == null)
                return false;
            var lodText = node["Lod"]?.InnerText;
            if (string.IsNullOrEmpty(lodText) || !byte.TryParse(lodText, out var lod) || lod == 255)
                return false;
            try
            {
                var bounds = boundsData.DeserializeAs<BoundingBox>();
                chunkData = new ChunkObjectData(lod, bounds);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public BoundingBox WorldBounds(GridDatabaseConfig db) => db.ToWorld(Lod, CellBounds);

        public override string ToString() => $"[Lod={Lod}, Bounds={CellBounds}]";
    }
}