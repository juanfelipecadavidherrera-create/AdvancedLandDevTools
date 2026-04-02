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
                        // Close region at the last road point (do NOT include the block's
                        // first sub-point — that would push the hatch into the curb area)
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
                    // Type F Curb & Gutter — 2 ft total horizontal (surveyed geometry)
                    // Surface profile (3 sub-points → SubPointCount = 3):
                    // 1) Gutter pan: 1.5 ft horiz, -0.0546 ft vertical (gutter flows toward curb)
                    // 2) Curb face + nose fillet: 0.1667 ft horiz, +0.5118 ft rise
                    // 3) Flat curb top: 0.3333 ft horiz
                    return new List<(double, double)>
                    {
                        (dirSign * 1.5000, -0.0546),   // gutter pan end at curb face
                        (dirSign * 0.1667,  0.5118),   // curb face + nose fillet
                        (dirSign * 0.3333,  0.0000)    // flat curb top
                    };

                case SegmentType.TypeD:
                    // Type D Mountable/Rolled Curb — 0.6666 ft wide, 0.5 ft rise (surveyed)
                    // Surface profile (3 sub-points → SubPointCount = 3):
                    // 1) Straight slope up curb face: 0.1351 ft horiz, +0.3738 ft rise
                    // 2) Arc rollover to flat top: 0.1617 ft horiz, +0.1262 ft rise
                    // 3) Flat top to back: 0.3698 ft horiz
                    return new List<(double, double)>
                    {
                        (dirSign * 0.1351,  0.3738),  // curb face slope
                        (dirSign * 0.1617,  0.1262),  // arc rollover (simplified)
                        (dirSign * 0.3698,  0.0000)   // flat top
                    };

                case SegmentType.ValleyGutter:
                    // Valley Gutter — 2 ft wide, V-shaped depression (surveyed)
                    // Road surface on each side; valley floor 0.1211 ft below road surface.
                    return new List<(double, double)>
                    {
                        (dirSign * 1.0, -0.1211),    // down to valley floor
                        (dirSign * 1.0,  0.1211)     // back up to road surface
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
                {
                    // Type F Curb & Gutter — real surveyed geometry.
                    // Coordinate origin (sx, sy) = road surface at gutter front edge.
                    // Offsets derived from AutoCAD LIST data (normalized to block corner).
                    //
                    // Toe fillet arc : start (1.2811, 0.5352)→end (1.5000, 0.7832), bulge +0.3782
                    // Nose fillet arc: start (1.5000, 1.1284)→end (1.6667, 1.2950), bulge -0.4142
                    //
                    // Bulge is multiplied by dir so arcs mirror correctly on the left side.

                    double toeX0 = sx + dir * 1.2811, toeY0 = sy - 0.3026;
                    double toeX1 = sx + dir * 1.5000, toeY1 = sy - 0.0546;

                    double noseX0 = sx + dir * 1.5000, noseY0 = sy + 0.2906;
                    double noseX1 = sx + dir * 1.6667, noseY1 = sy + 0.4572;

                    outline = new List<(double, double)>();
                    outline.Add((sx,                   sy));            // front top (road surface)
                    outline.Add((sx + dir * 1.0,       sy - 0.3378));  // gutter low point
                    outline.Add((toeX0,                toeY0));         // toe fillet start

                    foreach (var p in TessellateArc(toeX0, toeY0, toeX1, toeY1,  0.3782 * dir))
                        outline.Add(p);                                  // toe fillet arc

                    outline.Add((noseX0,               noseY0));        // up curb face (nose fillet start)

                    foreach (var p in TessellateArc(noseX0, noseY0, noseX1, noseY1, -0.4142 * dir))
                        outline.Add(p);                                  // nose fillet arc

                    outline.Add((sx + dir * 2.0000,    sy + 0.4572));  // curb top back corner
                    outline.Add((sx + dir * 2.0000,    sy - 0.8378));  // back bottom
                    outline.Add((sx,                   sy - 0.8378));  // front bottom
                    outline.Add((sx,                   sy));            // close
                    break;
                }

                case SegmentType.TypeD:
                {
                    // Type D Rolled/Mountable Curb — real surveyed geometry.
                    // (sx, sy) = road surface at curb front face (road side).
                    // Block: 0.6666 ft wide, 1.0 ft below road + 0.5 ft above road.
                    //
                    // Rollover arc: from curb nose (0.1351, +0.3738) to flat top (0.2968, +0.5)
                    //   bulge = -0.3442 * dir  (CW for right side, CCW mirrored for left)

                    double arcX0 = sx + dir * 0.1351, arcY0 = sy + 0.3738;
                    double arcX1 = sx + dir * 0.2968, arcY1 = sy + 0.5000;

                    outline = new List<(double, double)>();
                    outline.Add((sx,                    sy));           // road surface (front face base)
                    outline.Add((arcX0,                 arcY0));        // curb nose (top of sloped face)

                    foreach (var p in TessellateArc(arcX0, arcY0, arcX1, arcY1, -0.3442 * dir))
                        outline.Add(p);                                  // rollover arc to flat top

                    outline.Add((sx + dir * 0.6666,    sy + 0.5000)); // back top
                    outline.Add((sx + dir * 0.6666,    sy - 1.0000)); // back bottom (1 ft below road)
                    outline.Add((sx,                    sy - 1.0000)); // front bottom
                    outline.Add((sx,                    sy));           // close
                    break;
                }

                case SegmentType.ValleyGutter:
                {
                    // Valley Gutter — real surveyed geometry.
                    // (sx, sy) = road surface at left edge of gutter.
                    // Width: 2.0128 ft total. Road surface 0.8930 ft above slab base.
                    // Valley floor 0.1211 ft below road surface at x=1.0128.
                    // Two edge fillets: radius 0.0556 ft, bulge -0.4460.
                    //
                    // Left fillet : (0.0128, -0.0552) → (0.0743,  0.000), bulge -0.4460 * dir
                    // Right fillet: (1.9513,  0.000) → (2.0128, -0.0552), bulge -0.4460 * dir

                    double lfX0 = sx + dir * 0.0128, lfY0 = sy - 0.0552;
                    double lfX1 = sx + dir * 0.0743, lfY1 = sy;

                    double rfX0 = sx + dir * 1.9513, rfY0 = sy;
                    double rfX1 = sx + dir * 2.0128, rfY1 = sy - 0.0552;

                    outline = new List<(double, double)>();
                    outline.Add((sx,                    sy));           // left edge road surface
                    outline.Add((lfX0,                  lfY0));         // left fillet start

                    foreach (var p in TessellateArc(lfX0, lfY0, lfX1, lfY1, -0.4460 * dir))
                        outline.Add(p);                                  // left fillet arc

                    outline.Add((sx + dir * 1.0128,    sy - 0.1211)); // valley floor
                    outline.Add((rfX0,                  rfY0));         // right road surface level

                    foreach (var p in TessellateArc(rfX0, rfY0, rfX1, rfY1, -0.4460 * dir))
                        outline.Add(p);                                  // right fillet arc

                    outline.Add((sx + dir * 2.0128,    sy - 0.8930)); // right bottom
                    outline.Add((sx,                    sy - 0.8930)); // left bottom
                    outline.Add((sx,                    sy));           // close
                    break;
                }
            }

            if (outline != null)
                geo.BlockOutlines.Add(outline);
        }

        /// <summary>
        /// Tessellates a polyline arc segment (defined by start, end, and AutoCAD bulge value)
        /// into a list of points excluding the start but including the end.
        /// For mirrored (left-side) arcs pass bulge * dir so curvature flips correctly.
        /// </summary>
        private static List<(double X, double Y)> TessellateArc(
            double x1, double y1, double x2, double y2, double bulge, int segments = 6)
        {
            var pts = new List<(double, double)>();
            double dx = x2 - x1, dy = y2 - y1;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 1e-9) { pts.Add((x2, y2)); return pts; }

            double alpha = 4.0 * Math.Atan(Math.Abs(bulge));
            double r = d / (2.0 * Math.Sin(alpha / 2.0));
            double distToCenter = Math.Sqrt(Math.Max(0.0, r * r - (d / 2.0) * (d / 2.0)));

            // Perpendicular unit vector to chord
            double px = -dy / d, py = dx / d;

            // Positive bulge → CCW arc → center is to the left of travel direction
            double s = bulge > 0 ? 1.0 : -1.0;
            double cx = (x1 + x2) / 2.0 + s * px * distToCenter;
            double cy = (y1 + y2) / 2.0 + s * py * distToCenter;

            double startAngle = Math.Atan2(y1 - cy, x1 - cx);
            double sweep = alpha * (bulge > 0 ? 1.0 : -1.0);

            for (int i = 1; i <= segments; i++)
            {
                double angle = startAngle + sweep * i / segments;
                pts.Add((cx + r * Math.Cos(angle), cy + r * Math.Sin(angle)));
            }
            return pts;
        }

        /// <summary>Number of sub-points emitted by a segment (for preview label indexing).</summary>
        public static int SubPointCount(SectionSegment seg) => seg.Type switch
        {
            SegmentType.TypeF => 3,
            SegmentType.TypeD => 3,
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
