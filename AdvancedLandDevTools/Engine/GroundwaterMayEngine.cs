using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;

namespace AdvancedLandDevTools.Engine
{
    /// <summary>
    /// Queries the MDC "Groundwater Level Average May" FeatureServer
    /// (contour polylines) and interpolates the groundwater elevation
    /// at a picked point from the two nearest contour lines.
    /// </summary>
    public static class GroundwaterMayEngine
    {
        // ── MDC Average May GW contour FeatureServer ─────────────────
        private const string MDC_GW_MAY_URL =
            "https://services.arcgis.com/8Pc9XBTAsYuxx9Ny/arcgis/rest/services/" +
            "GWLevelAvgMay_gdb/FeatureServer/0/query";

        // NAVD88 → NGVD29 offset for Miami-Dade County
        private const double NAVD_TO_NGVD_OFFSET = 1.52;

        private static readonly HttpClient _httpClient;

        static GroundwaterMayEngine()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "AdvancedLandDevTools/1.0 (Civil3D Plugin)");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        // ═══════════════════════════════════════════════════════════════
        //  Public API
        // ═══════════════════════════════════════════════════════════════

        public static void LookupAtPoint(Document doc, Point3d pickedPoint)
        {
            Editor ed = doc.Editor;

            ed.WriteMessage($"\n  Picked point (Drawing coords): " +
                            $"X={pickedPoint.X:F3}, Y={pickedPoint.Y:F3}");

            // Convert drawing coords to WGS84
            if (!GroundwaterCoords.ConvertToLatLon(doc, pickedPoint, out double lat, out double lon))
            {
                ed.WriteMessage("\n  ** Could not convert coordinates to Lat/Lon.");
                ed.WriteMessage("\n  Make sure your drawing has a valid coordinate system assigned.");
                ed.WriteMessage("\n  (Settings > Drawing Settings > Zone tab)\n");
                return;
            }

            ed.WriteMessage($"\n  Converted to WGS84: Lat={lat:F6}, Lon={lon:F6}");
            ed.WriteMessage("\n  Querying MDC Groundwater Level (Average May) service...");

            // Query nearest contours and interpolate
            if (!QueryAndInterpolate(lat, lon, out double navd88,
                    out double lowerElev, out double upperElev,
                    out double lowerDist, out double upperDist,
                    out string errorMsg))
            {
                ed.WriteMessage($"\n  ** Query failed: {errorMsg}");
                ed.WriteMessage("\n  The point may be outside Miami-Dade County,");
                ed.WriteMessage("\n  or the MDC service is temporarily unavailable.\n");
                return;
            }

            double ngvd29 = navd88 + NAVD_TO_NGVD_OFFSET;

            ed.WriteMessage("\n");
            ed.WriteMessage("\n  ╔══════════════════════════════════════════════════╗");
            ed.WriteMessage("\n  ║       GROUNDWATER LEVEL — AVG MAY               ║");
            ed.WriteMessage("\n  ╠══════════════════════════════════════════════════╣");
            ed.WriteMessage($"\n  ║  NAVD 88:   {navd88,8:F2} ft                        ║");
            ed.WriteMessage($"\n  ║  NGVD 29:   {ngvd29,8:F2} ft                        ║");
            ed.WriteMessage("\n  ╠══════════════════════════════════════════════════╣");
            ed.WriteMessage($"\n  ║  Lower contour: {lowerElev,6:F2} ft  ({lowerDist:F0} ft away)    ║");
            ed.WriteMessage($"\n  ║  Upper contour: {upperElev,6:F2} ft  ({upperDist:F0} ft away)    ║");
            ed.WriteMessage("\n  ╠══════════════════════════════════════════════════╣");
            ed.WriteMessage("\n  ║  Dataset: GW Level Avg May (contour interp.)    ║");
            ed.WriteMessage("\n  ║  Source:  MDC / USGS Groundwater Model          ║");
            ed.WriteMessage("\n  ║  Conversion: NGVD = NAVD + 1.52 ft             ║");
            ed.WriteMessage("\n  ╚══════════════════════════════════════════════════╝\n");
        }

        // ═══════════════════════════════════════════════════════════════
        //  Query contours and interpolate elevation
        // ═══════════════════════════════════════════════════════════════

        private static bool QueryAndInterpolate(
            double lat, double lon,
            out double navd88,
            out double lowerElev, out double upperElev,
            out double lowerDist, out double upperDist,
            out string errorMsg)
        {
            navd88 = 0;
            lowerElev = upperElev = 0;
            lowerDist = upperDist = 0;
            errorMsg = "";

            try
            {
                // Query contours within a search radius (start at 2 km, expand if needed)
                List<ContourHit> hits = null!;
                int[] radiiMeters = { 2000, 5000, 10000 };

                foreach (int radius in radiiMeters)
                {
                    hits = QueryContours(lat, lon, radius, out errorMsg);
                    if (hits != null && hits.Count >= 1) break;
                }

                if (hits == null || hits.Count == 0)
                {
                    errorMsg = "No groundwater contour data found near this location.";
                    return false;
                }

                // Sort by distance to find the two closest distinct-elevation contours
                hits.Sort((a, b) => a.Distance.CompareTo(b.Distance));

                // If only one contour found, use it directly
                if (hits.Count == 1 || Math.Abs(hits[0].Elevation - hits[1].Elevation) < 0.001)
                {
                    navd88 = hits[0].Elevation;
                    lowerElev = upperElev = hits[0].Elevation;
                    lowerDist = upperDist = hits[0].Distance;
                    return true;
                }

                // Find two closest contours with different elevations
                var lower = hits[0];
                ContourHit? upper = null;
                for (int i = 1; i < hits.Count; i++)
                {
                    if (Math.Abs(hits[i].Elevation - lower.Elevation) > 0.001)
                    {
                        upper = hits[i];
                        break;
                    }
                }

                if (upper == null)
                {
                    navd88 = lower.Elevation;
                    lowerElev = upperElev = lower.Elevation;
                    lowerDist = upperDist = lower.Distance;
                    return true;
                }

                // Ensure lower/upper are correct
                if (lower.Elevation > upper.Elevation)
                {
                    var tmp = lower;
                    lower = upper;
                    upper = tmp;
                }

                lowerElev = lower.Elevation;
                upperElev = upper.Elevation;
                lowerDist = lower.Distance;
                upperDist = upper.Distance;

                // Inverse distance weighted interpolation
                double totalDist = lower.Distance + upper.Distance;
                if (totalDist < 0.01)
                {
                    navd88 = (lower.Elevation + upper.Elevation) / 2.0;
                }
                else
                {
                    // Closer contour gets more weight
                    double wLower = 1.0 / Math.Max(lower.Distance, 0.1);
                    double wUpper = 1.0 / Math.Max(upper.Distance, 0.1);
                    navd88 = (lower.Elevation * wLower + upper.Elevation * wUpper)
                             / (wLower + wUpper);
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMsg = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  FeatureServer spatial query
        // ═══════════════════════════════════════════════════════════════

        private static List<ContourHit> QueryContours(
            double lat, double lon, int radiusMeters, out string errorMsg)
        {
            errorMsg = "";
            var results = new List<ContourHit>();

            try
            {
                string queryUrl = $"{MDC_GW_MAY_URL}" +
                    $"?geometry={lon.ToString(CultureInfo.InvariantCulture)}" +
                    $",{lat.ToString(CultureInfo.InvariantCulture)}" +
                    $"&geometryType=esriGeometryPoint" +
                    $"&spatialRel=esriSpatialRelIntersects" +
                    $"&distance={radiusMeters}" +
                    $"&units=esriSRUnit_Meter" +
                    $"&inSR=4326" +
                    $"&outFields=Elevation" +
                    $"&returnGeometry=true" +
                    $"&outSR=4326" +
                    $"&f=json";

                string jsonResponse = System.Threading.Tasks.Task.Run(() =>
                {
                    var response = _httpClient.GetAsync(queryUrl).Result;
                    response.EnsureSuccessStatusCode();
                    return response.Content.ReadAsStringAsync().Result;
                }).Result;

                JObject json = JObject.Parse(jsonResponse);
                JArray? features = json["features"] as JArray;

                if (features == null || features.Count == 0)
                {
                    errorMsg = "No contour features in search radius.";
                    return results;
                }

                foreach (JObject feature in features)
                {
                    var attrs = feature["attributes"] as JObject;
                    if (attrs == null) continue;

                    double? elev = (double?)attrs["Elevation"];
                    if (elev == null) continue;

                    // Calculate distance from point to nearest vertex on contour
                    double dist = ComputeDistanceToContour(feature, lon, lat);

                    results.Add(new ContourHit
                    {
                        Elevation = elev.Value,
                        Distance = dist
                    });
                }

                return results;
            }
            catch (Exception ex)
            {
                errorMsg = ex.InnerException?.Message ?? ex.Message;
                return results;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  Distance from point to polyline contour (WGS84 → approx ft)
        // ═══════════════════════════════════════════════════════════════

        private static double ComputeDistanceToContour(JObject feature, double ptLon, double ptLat)
        {
            // Approximate conversion from degrees to feet at Miami latitude (~25.7°N)
            const double DEG_LAT_TO_FT = 364173.0;  // 1° lat ≈ 364,173 ft
            const double DEG_LON_TO_FT = 328084.0;  // 1° lon ≈ 328,084 ft at 25.7°N

            double minDist = double.MaxValue;

            var geometry = feature["geometry"] as JObject;
            if (geometry == null) return minDist;

            var paths = geometry["paths"] as JArray;
            if (paths == null) return minDist;

            foreach (JArray path in paths)
            {
                for (int i = 0; i < path.Count - 1; i++)
                {
                    var p1 = path[i] as JArray;
                    var p2 = path[i + 1] as JArray;
                    if (p1 == null || p2 == null || p1.Count < 2 || p2.Count < 2) continue;

                    double x1 = ((double)p1[0] - ptLon) * DEG_LON_TO_FT;
                    double y1 = ((double)p1[1] - ptLat) * DEG_LAT_TO_FT;
                    double x2 = ((double)p2[0] - ptLon) * DEG_LON_TO_FT;
                    double y2 = ((double)p2[1] - ptLat) * DEG_LAT_TO_FT;

                    double dist = PointToSegmentDist(0, 0, x1, y1, x2, y2);
                    if (dist < minDist) minDist = dist;
                }
            }

            return minDist;
        }

        /// <summary>
        /// Shortest distance from point (px,py) to line segment (ax,ay)-(bx,by).
        /// </summary>
        private static double PointToSegmentDist(
            double px, double py,
            double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay;
            double lenSq = dx * dx + dy * dy;

            if (lenSq < 1e-12)
            {
                // Degenerate segment
                double ddx = px - ax, ddy = py - ay;
                return Math.Sqrt(ddx * ddx + ddy * ddy);
            }

            double t = ((px - ax) * dx + (py - ay) * dy) / lenSq;
            t = Math.Max(0, Math.Min(1, t));

            double closestX = ax + t * dx;
            double closestY = ay + t * dy;
            double distX = px - closestX;
            double distY = py - closestY;
            return Math.Sqrt(distX * distX + distY * distY);
        }

        private class ContourHit
        {
            public double Elevation;
            public double Distance; // in feet
        }
    }
}
