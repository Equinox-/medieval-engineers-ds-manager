using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Xml;

namespace Meds.Watchdog.Save
{
    public sealed class EntityAccessor
    {
        public readonly ChunkObjectData? ChunkData;
        public readonly XmlElement Entity;

        public PositionAndOrientation Position => Entity["PositionAndOrientation"].DeserializeAs<PositionAndOrientation>();

        public PositionAndOrientation? PositionOptional
        {
            get
            {
                try
                {
                    return Position;
                }
                catch
                {
                    return null;
                }
            }
        }

        public string Subtype => Entity.Attributes?["Subtype"]?.Value;

        public EntityId Id => new EntityId(ulong.Parse(Entity["EntityId"]!.InnerText));

        public IEnumerable<ComponentAccessor> Components =>
            Entity["ComponentContainer"]?
                .OfType<XmlNode>()
                .Select(x => new ComponentAccessor(this, x))
            ?? Enumerable.Empty<ComponentAccessor>();

        public ComponentAccessor Component(string type) => Components.FirstOrDefault(x => x.Is(type));

        private ComponentAccessor GridData => Component("MyObjectBuilder_GridDataComponent");

        private ComponentAccessor GridHierarchy => Component("MyObjectBuilder_GridHierarchyComponent");

        public int BlockCount => GridData?.Node["Blocks"]?.ChildNodes.OfType<XmlNode>().Count() ?? 0;

        public IEnumerable<BlockAccessor> Blocks
        {
            get
            {
                var blockDataNodes = GridData?.Node["Blocks"]?.ChildNodes.OfType<XmlNode>();
                if (blockDataNodes == null)
                    yield break;

                var blockEntities = new Dictionary<ulong, EntityAccessor>();
                var blockEntityNodes = Component("MyObjectBuilder_GridHierarchyComponent")?.Node["BlockToEntityMap"]?
                    .ChildNodes.OfType<XmlNode>();
                if (blockEntityNodes != null)
                    foreach (var node in blockEntityNodes)
                    {
                        var idText = node["BlockId"]?.InnerText;
                        if (idText != null && ulong.TryParse(idText, out var id))
                            blockEntities[id] = new EntityAccessor(node["Entity"]);
                    }

                foreach (var node in blockDataNodes)
                {
                    var idText = node["Id"]?.InnerText;
                    if (idText != null && ulong.TryParse(idText, out var id))
                        yield return new BlockAccessor(this, id, node, blockEntities.TryGetValue(id, out var entity) ? entity : null);
                }
            }
        }

        public Vector3 CenterOrPivot(GridDatabaseConfig gridDb) => ChunkData?.WorldBounds(gridDb).Center ?? Position.Position;

        public EntityAccessor(XmlNode serializedEntity)
        {
            Entity = serializedEntity["Entity"];

            if (ChunkObjectData.TryParse(serializedEntity, out var chunkData))
                ChunkData = chunkData;
            else
                ChunkData = null;
        }

        public EntityAccessor(XmlElement entity, ChunkObjectData? chunkData)
        {
            Entity = entity;
            ChunkData = chunkData;
        }
    }
}