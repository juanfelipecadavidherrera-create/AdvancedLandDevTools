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
        /// Surface ends at curb top (elevated). Block outline shows full concrete.</summary>
        TypeF = 1,
        /// <summary>Type D mountable/rolled curb — 0.5 ft horizontal.
        /// Surface ends at curb top (elevated). Block outline shows full concrete.</summary>
        TypeD = 2,
        /// <summary>Valley gutter — 2 ft horizontal.
        /// V-shaped depression: slopes down 1 in over 1 ft, then back up 1 in over 1 ft.</summary>
        ValleyGutter = 3
    }

    public class SectionSegment
    {
        [JsonProperty("horizontalDistance")]
        public double HorizontalDistance { get; set; }

        [JsonProperty("slopePercent")]
        public double SlopePercent { get; set; }

        [JsonProperty("type")]
        public SegmentType Type { get; set; } = SegmentType.Normal;

        /// <summary>When true, road structural layers (asphalt/base/subgrade) are drawn under this segment.</summary>
        [JsonProperty("isRoad")]
        public bool IsRoad { get; set; }
    }

    public class SectionProfile
    {
        [JsonProperty("name")]
        public string Name { get; set; } = "Untitled";

        [JsonProperty("centerlineHeight")]
        public double CenterlineHeight { get; set; } = 25.0;

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

        /// <summary>Closed polyline outlines for curb/gutter block shapes.</summary>
        public List<List<(double X, double Y)>> BlockOutlines { get; set; } = new();

        /// <summary>Segment boundary info: position + type (for divider lines — skip blocks).</summary>
        public List<(double X, double Y, SegmentType Type)> SegmentBoundaries { get; set; } = new();

        /// <summary>Contiguous road surface regions for structural layers.</summary>
        public List<List<(double X, double Y)>> RoadRegions { get; set; } = new();

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
                double sx = rx, sy = ry;
                var subPts = GetSubPoints(seg, +1);
                foreach (var (sdx, sdy) in subPts)
                {
                    rx += sdx;
                    ry += sdy;
                    geo.RightPoints.Add((rx, ry));
                }
                geo.SegmentBoundaries.Add((rx, ry, seg.Type));
                AddBlockOutline(geo, seg, sx, sy, +1);
            }

            // Left side: dx negative
            geo.LeftPoints.Add((0, topY));
            double lx = 0, ly = topY;
            foreach (var seg in profile.LeftSegments)
            {
                double sx = lx, sy = ly;
                var subPts = GetSubPoints(seg, -1);
                foreach (var (sdx, sdy) in subPts)
                {
                    lx += sdx;
                    ly += sdy;
                    geo.LeftPoints.Add((lx, ly));
                }
                geo.SegmentBoundaries.Add((lx, ly, seg.Type));
                AddBlockOutline(geo, seg, sx, sy, -1);
            }

            // Build road regions from both sides
            BuildRoadRegions(geo, profile);

            return geo;
        }

        /// <summary>Build contiguous road surface regions for structural layer drawing.</summary>
        private static void BuildRoadRegions(SectionGeometry geo, SectionProfile profile)
        {
            // Build full surface: left reversed + right (skip duplicate origin)
            var fullSurface = new List<(double X, double Y)>();
            var fullIsRoad = new List<bool>();

            // Left side reversed (outermost → center)
            // Each left segment's sub-points map to LeftPoints[1..N]
            int lIdx = geo.LeftPoints.Count - 1;
            for (int s = profile.LeftSegments.Count - 1; s >= 0; s--)
            {
                int count = SubPointCount(profile.LeftSegments[s]);
                for (int k = 0; k < count; k++)
                {
                    if (lIdx >= 0)
                    {
                        fullSurface.Add(geo.LeftPoints[lIdx]);
                        fullIsRoad.Add(profile.LeftSegments[s].IsRoad);
                        lIdx--;
                    }
                }
            }
            // Add center point (origin)
            fullSurface.Add(geo.LeftPoints[0]);
            fullIsRoad.Add(false); // center point itself

            // Right side (center → outermost), skip first (already added)
            int rIdx = 1;
            foreach (var seg in profile.RightSegments)
            {
                int count = SubPointCount(seg);
                for (int k = 0; k < count; k++)
                {
                    if (rIdx < geo.RightPoints.Count)
                    {
                        fullSurface.Add(geo.RightPoints[rIdx]);
                        fullIsRoad.Add(seg.IsRoad);
                        rIdx++;
                    }
                }
            }

            // Extract contiguous road regions
            List<(double X, double Y)>? current = null;
            for (int i = 0; i < fullSurface.Count; i++)
            {
                if (fullIsRoad[i])
                {
                    if (current == null)
                    {
                        current = new List<(double, double)>();
                        // Add previous point as region start if available
                        if (i > 0) current.Add(fullSurface[i - 1]);
                    }
                    current.Add(fullSurface[i]);
                }
                else
                {
                    if (current != null)
                    {
                        // Close region with this non-road point
                        current.Add(fullSurface[i]);
                        geo.RoadRegions.Add(current);
                        current = null;
                    }
                }
            }
            if (current != null && current.Count >= 2)
                geo.RoadRegions.Add(current);
        }

        /// <summary>Returns sub-point deltas for a segment. dirSign = +1 for right, -1 for left.</summary>
        private static List<(double dx, double dy)> GetSubPoints(SectionSegment seg, int dirSign)
        {
            switch (seg.Type)
            {
                case SegmentType.TypeF:
                    // Type F Curb & Gutter — 2 ft total horizontal
                    // Surface ends at curb TOP (elevated +0.5 ft) so next segment starts elevated.
                    // 1) 1.5 ft gutter pan sloping down 2%
                    // 2) Vertical curb face: 0.53 ft rise (net +0.5 above start)
                    // 3) 0.5 ft flat curb top
                    return new List<(double, double)>
                    {
                        (dirSign * 1.50, -0.025),   // gutter pan (2% down)
                        (dirSign * 0.00,  0.525),    // vertical curb face
                        (dirSign * 0.50,  0.00)      // flat curb top
                    };

                case SegmentType.TypeD:
                    // Type D Mountable Curb — 0.5 ft total horizontal
                    // Surface ends at curb TOP (elevated +0.33 ft) so next segment starts elevated.
                    // 1) Front rolled slope: 4 in rise over 0.25 ft
                    // 2) Flat top: 0.25 ft
                    return new List<(double, double)>
                    {
                        (dirSign * 0.25,  0.33),     // front slope up (4 in)
                        (dirSign * 0.25,  0.00)      // flat top
                    };

                case SegmentType.ValleyGutter:
                    // Valley Gutter — 2 ft total horizontal, V-shaped depression
                    return new List<(double, double)>
                    {
                        (dirSign * 1.0, -0.083),     // down to center (~1 in)
                        (dirSign * 1.0,  0.083)      // back up from center
                    };

                default: // Normal
                    double dx = dirSign * seg.HorizontalDistance;
                    double dy = seg.HorizontalDistance * (seg.SlopePercent / 100.0);
                    return new List<(double, double)> { (dx, dy) };
            }
        }

        /// <summary>Adds a closed block outline for special segment types (curbs, valley gutter).</summary>
        private static void AddBlockOutline(SectionGeometry geo, SectionSegment seg,
            double sx, double sy, int dir)
        {
            List<(double X, double Y)>? outline = null;

            switch (seg.Type)
            {
                case SegmentType.TypeF:
                    // Type F: 2 ft wide. Curb top at sy + 0.5 ft.
                    // 12" (1 ft) deep below curb top at back, 6" (0.5 ft) slab at gutter.
                    double fTop = sy + 0.500;
                    outline = new List<(double, double)>
                    {
                        (sx,               sy),                // gutter start (top)
                        (sx + dir * 1.50,  sy - 0.025),       // gutter lip at curb face
                        (sx + dir * 1.50,  fTop),              // top of curb face
                        (sx + dir * 2.00,  fTop),              // curb top end
                        (sx + dir * 2.00,  fTop - 1.00),      // back bottom (12" below top)
                        (sx,               sy - 0.50),         // front bottom (6" slab)
                        (sx,               sy)                 // close
                    };
                    break;

                case SegmentType.TypeD:
                    // Type D: 0.5 ft wide. Curb top at sy + 0.33 ft.
                    // 18" (1.5 ft) deep below curb top.
                    double dTop = sy + 0.33;
                    outline = new List<(double, double)>
                    {
                        (sx,               sy),                // front base (top)
                        (sx + dir * 0.25,  dTop),              // slope peak
                        (sx + dir * 0.50,  dTop),              // flat top end
                        (sx + dir * 0.50,  dTop - 1.50),      // back bottom (18" below top)
                        (sx,               sy - 1.50),         // front bottom (18" below start)
                        (sx,               sy)                 // close
                    };
                    break;

                case SegmentType.ValleyGutter:
                    // Valley Gutter: 2 ft wide, 1.5 ft (18") deep, V on top, flat bottom
                    outline = new List<(double, double)>
                    {
                        (sx,               sy),                // left edge top
                        (sx + dir * 1.00,  sy - 0.083),       // center V bottom
                        (sx + dir * 2.00,  sy),               // right edge top
                        (sx + dir * 2.00,  sy - 1.50),        // right bottom (18" deep)
                        (sx,               sy - 1.50),         // left bottom (18" deep)
                        (sx,               sy)                 // close
                    };
                    break;
            }

            if (outline != null)
                geo.BlockOutlines.Add(outline);
        }

        /// <summary>Number of sub-points emitted by a segment (for preview label indexing).</summary>
        public static int SubPointCount(SectionSegment seg) => seg.Type switch
        {
            SegmentType.TypeF => 3,
            SegmentType.TypeD => 2,
            SegmentType.ValleyGutter => 2,
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
