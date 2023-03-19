using System.IO;
using System.Reflection;

namespace Meds.Dist
{
    public static class DistUtils
    {
        public static string DiscoverConfigFile()
        {
            var assembly = Assembly.GetExecutingAssembly().Location;
            var parent = Path.GetDirectoryName(assembly);
            if (parent == null)
                return null;
            var candidate1 = Path.Combine(parent, "config.xml");
            if (File.Exists(candidate1))
                return candidate1;
            var grandparent = Path.GetDirectoryName(parent);
            if (grandparent == null)
                return null;
            var candidate2 = Path.Combine(grandparent, "config.xml");
            return File.Exists(candidate2) ? candidate2 : null;
        }
    }
}