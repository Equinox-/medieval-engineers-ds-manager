using System.Xml;

namespace Meds.Watchdog.Save
{
    public class BlockAccessor
    {
        public readonly EntityAccessor Grid;
        public readonly ulong BlockId;
        public readonly XmlNode BlockData;
        public readonly EntityAccessor Entity;

        public BlockAccessor(EntityAccessor grid, ulong blockId, XmlNode blockData, EntityAccessor entity)
        {
            Grid = grid;
            BlockId = blockId;
            BlockData = blockData;
            Entity = entity;
        }
    }
}