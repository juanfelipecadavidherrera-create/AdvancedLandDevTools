using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace AdvancedLandDevTools.Engine
{
    // ─────────────────────────────────────────────────────────────────────
    //  Data models
    // ─────────────────────────────────────────────────────────────────────

    public enum SegmentType
    {
        Normal = 0,
        /// <summary>Type F barrier curb &amp; gutter — 2 ft horizontal.
        /// Profile: 1.5 ft gutter pan (2% down), 0.5 ft vertical curb face (6 in rise), 0.5 ft flat top.</summary>
        TypeF = 1,
        /// <summary>Type D mountable/rolled curb — 0.5 ft horizontal.
        /// Profile: sloped face rising 4 in (0.33 ft) over 0.5 ft.</summary>
        TypeD = 2
    }

    public class SectionSegment
    {
        [JsonProperty("horizontalDistance")]
        public double HorizontalDistance { get; set; }

        [JsonProperty("slopePercent")]
        public double SlopePercent { get; set; }

        [JsonProperty("type")]
        public SegmentType Type { get; set; } = SegmentType.Normal;
    }

    public class SectionProfile
    {
        [JsonProperty("name")]
        public string Name { get; set; } = "Untitled";

        [JsonProperty("centerlineHeight")]
        public double CenterlineHeight { get; set; } = 15.0;

        [JsonProperty("leftSegments")]
        public List<SectionSegment> LeftSegments { get; set; } = new();

        [JsonProperty("rightSegments")]
        public List<SectionSegment> RightSegments { get; set; } = new();

        [JsonProperty("createdUtc")]
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        [JsonProperty("modifiedUtc")]
        public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>Computed geometry for a section profile (local coords, origin at centerline base).</summary>
    public class SectionGeometry
    {
        /// <summary>Points from centerline top outward left. First point = (0, CenterlineHeight).</summary>
        public List<(double X, double Y)> LeftPoints { get; set; } = new();

        /// <summary>Points from centerline top outward right. First point = (0, CenterlineHeight).</summary>
        public List<(double X, double Y)> RightPoints { get; set; } = new();

        public double CenterlineBaseY => 0;
        public double CenterlineTopY { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Engine
    // ─────────────────────────────────────────────────────────────────────

    public static class SectionDrawerEngine
    {
        private static readonly string StoreDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AdvancedLandDevTools", "Sections");

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };

        // ── Geometry computation ──────────────────────────────────────

        public static SectionGeometry ComputePoints(SectionProfile profile)
        {
            var geo = new SectionGeometry { CenterlineTopY = profile.CenterlineHeight };
            double topY = profile.CenterlineHeight;

            // Right side: dx positive
            geo.RightPoints.Add((0, topY));
            double rx = 0, ry = topY;
            foreach (var seg in profile.RightSegments)
            {
                var subPts = GetSubPoints(seg, +1);
                foreach (var (sdx, sdy) in subPts)
                {
                    rx += sdx;
                    ry += sdy;
                    geo.RightPoints.Add((rx, ry));
                }
            }

            // Left side: dx negative
            geo.LeftPoints.Add((0, topY));
            double lx = 0, ly = topY;
            foreach (var seg in profile.LeftSegments)
            {
                var subPts = GetSubPoints(seg, -1);
                foreach (var (sdx, sdy) in subPts)
                {
                    lx += sdx;
                    ly += sdy;
                    geo.LeftPoints.Add((lx, ly));
                }
            }

            return geo;
        }

        /// <summary>Returns sub-point deltas for a segment. dirSign = +1 for right, -1 for left.</summary>
        private static List<(double dx, double dy)> GetSubPoints(SectionSegment seg, int dirSign)
        {
            switch (seg.Type)
            {
                case SegmentType.TypeF:
                    // Type F Curb & Gutter — 2 ft total horizontal
                    // 1) 1.5 ft gutter pan sloping down 2%
                    // 2) Vertical curb face: 0.5 ft rise (6 in), ~0 horizontal
                    // 3) 0.5 ft flat curb top
                    return new List<(double, double)>
                    {
                        (dirSign * 1.5,  -0.03),    // gutter pan (2% down toward curb)
                        (dirSign * 0.0,   0.50),     // vertical curb face (6 in up)
                        (dirSign * 0.5,   0.00)      // flat curb top
                    };

                case SegmentType.TypeD:
                    // Type D Mountable Curb — 0.5 ft total horizontal
                    // Rolled slope rising 4 in (0.33 ft) over 0.5 ft
                    return new List<(double, double)>
                    {
                        (dirSign * 0.5, 0.33)
                    };

                default: // Normal
                    double dx = dirSign * seg.HorizontalDistance;
                    double dy = seg.HorizontalDistance * (seg.SlopePercent / 100.0);
                    return new List<(double, double)> { (dx, dy) };
            }
        }

        /// <summary>Number of sub-points emitted by a segment (for preview label indexing).</summary>
        public static int SubPointCount(SectionSegment seg) => seg.Type switch
        {
            SegmentType.TypeF => 3,
            SegmentType.TypeD => 1,
            _ => 1
        };

        // ── Persistence ──────────────────────────────────────────────

        public static void Save(SectionProfile profile)
        {
            Directory.CreateDirectory(StoreDir);
            profile.ModifiedUtc = DateTime.UtcNow;
            string path = GetFilePath(profile.Name);
            File.WriteAllText(path, JsonConvert.SerializeObject(profile, JsonSettings));
        }

        public static SectionProfile? Load(string name)
        {
            string path = GetFilePath(name);
            if (!File.Exists(path)) return null;
            return JsonConvert.DeserializeObject<SectionProfile>(
                File.ReadAllText(path), JsonSettings);
        }

        public static List<string> ListSections()
        {
            var names = new List<string>();
            if (!Directory.Exists(StoreDir)) return names;
            foreach (string file in Directory.GetFiles(StoreDir, "*.json"))
            {
                try
                {
                    var prof = JsonConvert.DeserializeObject<SectionProfile>(
                        File.ReadAllText(file), JsonSettings);
                    if (prof != null) names.Add(prof.Name);
                }
                catch { }
            }
            return names;
        }

        public static void Delete(string name)
        {
            string path = GetFilePath(name);
            if (File.Exists(path)) File.Delete(path);
        }

        private static string GetFilePath(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            string safe = name;
            foreach (char c in invalid) safe = safe.Replace(c, '_');
            return Path.Combine(StoreDir, safe.Trim() + ".json");
        }
    }
}
