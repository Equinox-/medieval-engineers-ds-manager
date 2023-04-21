using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Xml;
using System.Xml.Serialization;

namespace Meds.Watchdog.Save
{
    public static class SerializationUtils
    {
        private static readonly ConcurrentDictionary<(Type, string), XmlSerializer> DeserializerCache =
            new ConcurrentDictionary<(Type, string), XmlSerializer>();

        public static T DeserializeAs<T>(this XmlNode node)
        {
            var deserializer = DeserializerCache.GetOrAdd((typeof(T), node.Name),
                key => new XmlSerializer(key.Item1, new XmlRootAttribute(key.Item2)));
            return (T)deserializer.Deserialize(new XmlNodeReader(node));
        }

        public static XmlElement CreateElement(this XmlDocument doc, XmlElement parent, string name, string innerText = null)
        {
            var element = doc.CreateElement(name);
            parent.AppendChild(element);
            if (innerText != null)
                element.InnerText = innerText;
            return element;
        }

        public static XmlElement AttributesFrom(this XmlElement target, Vector3 vec)
        {
            target.SetAttribute("x", vec.X.ToString(CultureInfo.InvariantCulture));
            target.SetAttribute("y", vec.X.ToString(CultureInfo.InvariantCulture));
            target.SetAttribute("z", vec.X.ToString(CultureInfo.InvariantCulture));
            return target;
        }

        private const string StripPrefix = "MyObjectBuilder_";
        private const string StripSuffix = "Component";
        public const string UnknownType = "Unknown";

        public static string SimplifyType(string type)
        {
            if (type.StartsWith(StripPrefix, StringComparison.OrdinalIgnoreCase))
                type = type.Substring(StripPrefix.Length);
            if (type.EndsWith(StripSuffix, StringComparison.OrdinalIgnoreCase))
                type = type.Substring(0, type.Length - StripSuffix.Length);
            return type;
        }

        public static string TypeForNode(XmlNode node) => SimplifyType(node.Attributes?["xsi:type"]?.Value ?? UnknownType);
    }
}