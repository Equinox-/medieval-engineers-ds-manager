using System;
using SteamKit2;
using static SteamKit2.SteamApps.PICSProductInfoCallback;

namespace Meds.Watchdog.Steam
{
    public static class PICSProductInfoExtensions
    {
        public static ulong GetManifestId(this PICSProductInfo info, uint depotId, string branch)
        {
            return info.GetSection(EAppInfoSection.Depots)[depotId.ToString()]["manifests"][branch]["gid"].AsUnsignedLong();
        }

        public static uint GetWorkshopDepot(this PICSProductInfo info)
        {
            return info.GetSection(EAppInfoSection.Depots)["workshopdepot"].AsUnsignedInteger();
        }

        private static KeyValue GetSection(this PICSProductInfo info, EAppInfoSection section)
        {
            // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
            switch (section)
            {
                case EAppInfoSection.Depots:
                    return info.KeyValues["depots"];
                default:
                    throw new NotSupportedException(section.ToString("G"));
            }
        }
    }
}