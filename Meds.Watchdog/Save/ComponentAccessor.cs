using System;
using System.Xml;

namespace Meds.Watchdog.Save
{
    public class ComponentAccessor
    {
        public readonly EntityAccessor Entity;
        public readonly string Type;
        public readonly XmlNode Node;

        public ComponentAccessor(EntityAccessor entity, XmlNode node)
        {
            Entity = entity;
            Node = node;
            Type = SerializationUtils.TypeForNode(node);
        }

        public bool Is(string type) => Type.Equals(SerializationUtils.SimplifyType(type), StringComparison.OrdinalIgnoreCase);
    }
}