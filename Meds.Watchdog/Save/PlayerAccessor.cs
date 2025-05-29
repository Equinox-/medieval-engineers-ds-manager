using System.Xml;

namespace Meds.Watchdog.Save
{
    public class PlayerAccessor
    {
        public readonly SteamId Id;
        public readonly XmlElement Entity;
        public readonly EntityAccessor EntityAccessor;
        public long? IdentityId;

        public string DisplayName => EntityAccessor.Component("ModelComponent")?.Node?["DisplayName"]?.InnerText;

        public PlayerAccessor(XmlNode serializedPlayerStorage)
        {
            Id = ulong.TryParse(serializedPlayerStorage["PlayerId"]?.InnerText ?? "0", out var id) ? id : 0;
            IdentityId = long.TryParse(serializedPlayerStorage["IdentityId"]?.InnerText ?? "", out var idd) ? (long?)idd : null;
            Entity = serializedPlayerStorage["Entity"];
            EntityAccessor = new EntityAccessor(Entity, null);
        }
    }
}