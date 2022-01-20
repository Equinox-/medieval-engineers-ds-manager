using System.Text;

namespace Meds.Metrics
{
    public static class ZeroGcStrings
    {
        private const int IntegerStringsOffset = 128;
        private static readonly string[] IntegerStrings;

        static ZeroGcStrings()
        {
            IntegerStrings = new string[256];
            for (var i = 0; i < IntegerStrings.Length; i++)
                IntegerStrings[i] = (i - IntegerStringsOffset).ToString();
        }

        public static string ToString(int val)
        {
            var i = val + IntegerStringsOffset;
            return i >= 0 && i < IntegerStrings.Length ? IntegerStrings[i] : val.ToString();
        }
    }
}