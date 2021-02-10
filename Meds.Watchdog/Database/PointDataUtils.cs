using System.Globalization;
using System.Text;

namespace Meds.Watchdog.Database
{
    /// <summary>
    /// Copied from https://github.com/influxdata/influxdb-client-csharp/blob/master/Client/Writes/PointData.cs#L478-L549
    /// </summary>
    public static class PointDataUtils
    {
        public static void EscapeValue(StringBuilder sb, string value)
        {
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\"':
                        sb.Append("\\\"");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
        }

        public static void EscapeKey(StringBuilder sb, string key)
        {
            foreach (var c in key)
            {
                switch (c)
                {
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    case ' ':
                        sb.Append("\\ ");
                        break;
                    case ',':
                        sb.Append("\\,");
                        break;
                    case '=':
                        sb.Append("\\=");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
        }

        public static void WriteTag(StringBuilder sb, string key, string value, bool safeKey)
        {
            if (string.IsNullOrEmpty(value))
                return;
            sb.Append(",");
            if (safeKey)
                sb.Append(key);
            else
                EscapeKey(sb, key);
            sb.Append("=");
            EscapeKey(sb, value);
        }
    }
}