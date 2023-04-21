using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Xml;

namespace Meds.Watchdog.Save
{
    public sealed class ChunkAccessor
    {
        public readonly XmlElement SerializedChunk;
        public readonly ChunkId Id;

        public IEnumerable<EntityId> Entities => SerializedChunk.ChildNodes.OfType<XmlElement>()
            .Where(x => x.LocalName == "Entity")
            .SelectMany(x => EntityId.TryParse(x.InnerText, out var id) ? new[] { id } : Enumerable.Empty<EntityId>());

        public IEnumerable<GroupId> Groups => SerializedChunk.ChildNodes.OfType<XmlElement>()
            .Where(x => x.LocalName == "Closure")
            .SelectMany(x => x.ChildNodes.OfType<XmlElement>())
            .Where(x => x.LocalName == "Group")
            .SelectMany(x => GroupId.TryParse(x.InnerText, out var id) ? new[] { id } : Enumerable.Empty<GroupId>());

        public ChunkAccessor(XmlElement serializedChunk)
        {
            SerializedChunk = serializedChunk;
            Id = ChunkId.From(SerializedChunk["Id"]);
        }
    }
}