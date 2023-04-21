using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Xml;

namespace Meds.Watchdog.Save
{
    public class GroupAccessor
    {
        public readonly ChunkObjectData? ChunkData;
        public readonly XmlElement Group;
        public readonly ImmutableHashSet<EntityId> TopLevelEntities;

        public string Type => SerializationUtils.TypeForNode(Group);

        public GroupId Id => new GroupId(ulong.Parse(Group.Attributes!["Id"]!.InnerText));

        public GroupAccessor(XmlNode serializedGroup)
        {
            Group = serializedGroup["Group"];
            TopLevelEntities = serializedGroup["TopLevelEntities"]?
                .OfType<XmlNode>()
                .Where(x => x.Name == "Entity")
                .Select(x => ulong.TryParse(x.InnerText, out var id) ? id : 0)
                .Where(x => x != 0)
                .Select(x => new EntityId(x))
                .ToImmutableHashSet() ?? ImmutableHashSet<EntityId>.Empty;

            if (ChunkObjectData.TryParse(serializedGroup, out var chunkData))
                ChunkData = chunkData;
            else
                ChunkData = null;
        }
    }
}