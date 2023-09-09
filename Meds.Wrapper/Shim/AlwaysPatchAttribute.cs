using System;
using Medieval;
using VRage.Utils;

namespace Meds.Wrapper.Shim
{
    public sealed class AlwaysPatchAttribute : Attribute
    {
        public bool Late { get; set; }

        // Of the form "[minInclusive,maxExclusive)"
        public string VersionRange { get; set; }

        public bool CanUse()
        {
            if (VersionRange == null)
                return true;
            var comma = VersionRange.IndexOf(',');
            var min = VersionRange.Substring(1, comma - 1);
            var max = VersionRange.Substring(comma + 1, VersionRange.Length - comma - 2);

            var version = MyMedievalGame.ME_VERSION.GetWithoutRevision();
            if (min.Length > 0)
            {
                var minInclusive = VersionRange[0] == '[';
                var minVersion = Version.Parse(min);
                if (minVersion.CompareTo(version) > (minInclusive ? 0 : -1))
                    return false;
            }

            if (max.Length > 0)
            {
                var maxInclusive = VersionRange[VersionRange.Length - 1] == ']';
                var maxVersion = Version.Parse(max);
                if (maxVersion.CompareTo(version) < (maxInclusive ? 0 : 1))
                    return false;
            }

            return true;
        }
    }
}