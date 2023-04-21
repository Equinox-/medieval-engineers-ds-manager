using System;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Meds.Watchdog.Save
{
    public struct PlanetAreas
    {
        public int Face;
        public int MinX;

        public int MinY;

        // Exclusive
        public int MaxX;

        // Exclusive
        public int MaxY;

        public PlanetAreas(int face, int areaX, int areaY)
        {
            Face = face;
            MinX = areaX;
            MinY = areaY;
            MaxX = areaX + 1;
            MaxY = areaY + 1;
        }
    }

    public static class KeenPlanetAreas
    {
        private static readonly string[] KingdomNames = new[]
        {
            // ReSharper disable StringLiteralTypo
            "Fareon",
            "Bar Hadur",
            "Levos",
            "Rintel",
            "Umbril",
            "Darios"
            // ReSharper restore StringLiteralTypo
        };

        private static readonly Regex AreaParseRegex =
            new Regex(@"^[ \t]*(?<kingdom>[A-Za-z]+(?:| Hadur))[, \t]*(?<region>[A-Za-z][0-9]+)(?:|[, \t]*(?<area>[A-Za-z][0-9]+))[ \t]*$");

        /// <summary>
        /// Calculates an area patch from the provided area specifier string.
        /// </summary>
        public static bool TryParseArea(this PlanetData data, string text, out PlanetAreas areas)
        {
            areas = default;
            var match = AreaParseRegex.Match(text);
            if (!match.Success)
                return false;
            areas.Face = -1;
            var kingdom = match.Groups["kingdom"].Value;
            for (var i = 0; i < KingdomNames.Length; i++)
                if (KingdomNames[i].StartsWith(kingdom, StringComparison.OrdinalIgnoreCase))
                {
                    areas.Face = i;
                    break;
                }

            if (areas.Face == -1)
                return false;
            var region = match.Groups["region"].Value;
            if (!UnpackAlphaNumber(region, out var regionX, out var regionY))
                return false;
            var area = match.Groups["area"];
            if (!area.Success)
            {
                // Full region patch
                areas.MinX = data.AreasPerRegion * regionX;
                areas.MinY = data.AreasPerRegion * regionY;
                areas.MaxX = areas.MinX + data.AreasPerRegion;
                areas.MaxY = areas.MinY + data.AreasPerRegion;
                return true;
            }

            if (!UnpackAlphaNumber(area.Value, out var areaX, out var areaY))
                return false;
            areas.MinX = data.AreasPerRegion * regionX + areaX;
            areas.MinY = data.AreasPerRegion * regionY + areaY;
            areas.MaxX = areas.MinX + 1;
            areas.MaxY = areas.MinY + 1;
            return true;
        }

        private static bool UnpackAlphaNumber(string spec, out int x, out int y)
        {
            x = y = default;
            if (spec.Length < 2)
                return false;
            var ch = spec[0];
            if (ch >= 'a' && ch <= 'z')
                x = ch - 'a';
            else if (ch >= 'A' && ch <= 'Z')
                x = ch - 'A';
            else
                return false;
            if (!int.TryParse(spec.Substring(1), out y))
                return false;
            y--;
            return true;
        }

        public static Vector2 GetAreaCoords(this PlanetData data, Vector3 pos, out int direction)
        {
            var uv = KeenCubemap.ProjectToCubemap(pos, out direction);
            uv = (uv + Vector2.One) / 2;
            // ReSharper disable CompareOfFloatsByEqualityOperator
            if (uv.X == 1f)
                uv.X = 0.99999999f;
            if (uv.Y == 1f)
                uv.Y = 0.99999999f;
            // ReSharper restore CompareOfFloatsByEqualityOperator
            uv *= data.AreasPerFace;
            return uv;
        }

        public static void GetAreaName(this PlanetData data, Vector3 pos, out string kingdom, out string region, out string area)
        {
            var uv = data.GetAreaCoords(pos, out var face);
            var x = (int)uv.X;
            var y = (int)uv.Y;
            data.GetAreaName(face, x, y, out kingdom, out region, out area);
        }

        public static void GetAreaName(this PlanetData data, int face, int x, int y, out string kingdom, out string region, out string area)
        {
            var regionX = x / data.AreasPerRegion;
            var regionY = y / data.AreasPerRegion;

            var areaX = x % data.AreasPerRegion;
            var areaY = y % data.AreasPerRegion;

            kingdom = KingdomNames[face];
            region = $"{(char)('A' + regionX)}{regionY + 1}";
            area = $"{(char)('A' + areaX)}{areaY + 1}";
        }

        public static string GetAreaName(this PlanetData data, Vector3 pos)
        {
            var uv = data.GetAreaCoords(pos, out var face);
            var x = (int)uv.X;
            var y = (int)uv.Y;
            return data.GetAreaName(face, x, y);
        }

        public static string GetAreaName(this PlanetData data, int face, int x, int y)
        {
            data.GetAreaName(face, x, y, out var kingdom, out var region, out var area);
            return $"{kingdom}, {region}, {area}";
        }

        private static Vector3 CalculateNormOrigin(this PlanetData data, int x, int y, int face)
        {
            var uv = new Vector2(x, y);
            uv = uv * 2 - new Vector2(data.AreasPerFace);
            uv /= data.AreasPerFace;
            return KeenCubemap.UnProjectFromCubemap(uv, face);
        }

        /// <summary>
        /// Calculates an enclosing bounding box around the given inclusive area patch.
        /// </summary>
        public static BoundingBox CalculateEnclosingBox(this PlanetData data, PlanetAreas areas)
        {
            var box = new BoundingBox
            {
                Min = new Vector3(float.PositiveInfinity),
                Max = new Vector3(float.NegativeInfinity),
            };
            for (var x = areas.MinX; x <= areas.MaxX; x++)
            for (var y = areas.MinY; y <= areas.MaxY; y++)
            {
                var uv = new Vector2(x, y);
                uv = uv * 2 - new Vector2(data.AreasPerFace);
                uv /= data.AreasPerFace;
                var norm = KeenCubemap.UnProjectFromCubemap(uv, areas.Face);
                box.Include(norm * data.MinRadius);
                box.Include(norm * data.MaxRadius);
            }

            return box;
        }
    }
}