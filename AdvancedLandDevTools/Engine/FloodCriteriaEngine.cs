using System;
using System.Collections.Generic;
using System.Net.Http;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;
using AdvancedLandDevTools.Models;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AdvancedLandDevTools.Engine
{
    public static class FloodCriteriaEngine
    {
        private const string MDC_FLOOD_CRITERIA_URL =
            "https://services1.arcgis.com/B4MnusZHL3vmqU3t/arcgis/rest/services/" +
            "County_Flood_Criteria_2022/FeatureServer/0/query";

        private const double CLIP_RADIUS = 500.0;  // drawing units (feet)

        private static readonly HttpClient _httpClient;

        static FloodCriteriaEngine()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "AdvancedLandDevTools/1.0 (Civil3D Plugin)");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Public API — Single-point lookup with contour geometry
        // ═════════════════════════════════════════════════════════════════════
        public static FloodCriteriaResult LookupPoint(Document doc, Point3d pickedPoint)
        {
            Editor ed = doc.Editor;

            ed.WriteMessage($"\n  Picked point (Drawing coords): X={pickedPoint.X:F3}, Y={pickedPoint.Y:F3}");

            // 1) Convert picked point to WGS84
            if (!GroundwaterCoords.ConvertToLatLon(doc, pickedPoint, out double lat, out double lon))
            {
                return new FloodCriteriaResult
                {
                    Success = false,
                    ErrorMessage = "Could not convert coordinates to Lat/Lon. " +
                                   "Make sure your drawing has a valid coordinate system assigned."
                };
            }

            ed.WriteMessage($"\n  Converted to WGS84: Lat={lat:F6}, Lon={lon:F6}");
            ed.WriteMessage("\n  Querying Miami-Dade County Flood Criteria 2022...");

            // 2) Get drawing CS for reverse conversion of returned geometry
            string drawingCS = GroundwaterCoords.GetDrawingCoordinateSystem(doc);

            // 3) Query MDC — find nearest contour + collect geometry for drawing
            return QueryFloodCriteria(lat, lon, pickedPoint, drawingCS);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  MDC Flood Criteria query — returns nearest contour + clipped lines
        // ═════════════════════════════════════════════════════════════════════
        private static FloodCriteriaResult QueryFloodCriteria(
            double lat, double lon, Point3d pickedPoint, string drawingCS)
        {
            try
            {
                // Generous envelope to catch nearby contours
                double buffer = 0.02;  // ~2 km
                string envelope = $"{lon - buffer},{lat - buffer},{lon + buffer},{lat + buffer}";

                string queryUrl = MDC_FLOOD_CRITERIA_URL +
                    $"?where=1=1" +
                    $"&geometry={envelope}" +
                    $"&geometryType=esriGeometryEnvelope" +
                    $"&inSR=4326" +
                    $"&outSR=4326" +
                    $"&spatialRel=esriSpatialRelIntersects" +
                    $"&outFields=ELEV" +
                    $"&returnGeometry=true" +
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
                    // Try wider search — get all 13 features
                    return QueryFloodCriteriaWide(lat, lon, pickedPoint, drawingCS);
                }

                return BuildResult(features, lat, lon, pickedPoint, drawingCS);
            }
            catch (Exception ex)
            {
                return new FloodCriteriaResult
                {
                    Success = false,
                    ErrorMessage = $"Query failed: {ex.InnerException?.Message ?? ex.Message}"
                };
            }
        }

        private static FloodCriteriaResult QueryFloodCriteriaWide(
            double lat, double lon, Point3d pickedPoint, string drawingCS)
        {
            try
            {
                string queryUrl = MDC_FLOOD_CRITERIA_URL +
                    $"?where=1=1" +
                    $"&outFields=ELEV" +
                    $"&outSR=4326" +
                    $"&returnGeometry=true" +
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
                    return new FloodCriteriaResult
                    {
                        Success = false,
                        ErrorMessage = "No flood criteria data returned from MDC service."
                    };
                }

                return BuildResult(features, lat, lon, pickedPoint, drawingCS);
            }
            catch (Exception ex)
            {
                return new FloodCriteriaResult
                {
                    Success = false,
                    ErrorMessage = $"Wide query failed: {ex.InnerException?.Message ?? ex.Message}"
                };
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Build result from features — nearest elevation + clipped contours
        // ═════════════════════════════════════════════════════════════════════
        private static FloodCriteriaResult BuildResult(
            JArray features, double lat, double lon,
            Point3d pickedPoint, string drawingCS)
        {
            double minDist = double.MaxValue;
            double nearestElev = 0;
            var contours = new List<ContourLineData>();
            var center = new Point2d(pickedPoint.X, pickedPoint.Y);

            foreach (var feature in features)
            {
                var attrs = feature["attributes"] as JObject;
                var geom  = feature["geometry"]   as JObject;
                if (attrs == null || geom == null) continue;

                double elev = attrs["ELEV"]?.Value<double>() ?? 0;

                // Distance in WGS84 degrees for nearest-contour determination
                double dist = ComputeDistanceToPolyline(lon, lat, geom);
                if (dist < minDist)
                {
                    minDist = dist;
                    nearestElev = elev;
                }

                // Convert geometry to drawing coords and clip to 500 ft radius
                var clipped = ConvertAndClipPaths(geom, elev, center, drawingCS);
                contours.AddRange(clipped);
            }

            if (minDist == double.MaxValue)
            {
                return new FloodCriteriaResult
                {
                    Success = false,
                    ErrorMessage = "Could not compute distance to flood criteria contours."
                };
            }

            // Convert degrees distance to approximate feet
            double distFeet = minDist * 364567.0;  // rough deg-to-ft at lat ~25.8

            return new FloodCriteriaResult
            {
                Success   = true,
                Elevation = $"{nearestElev:F1} ft NGVD",
                Distance  = $"{distFeet:F0} ft (approx)",
                Contours  = contours
            };
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Convert ArcGIS polyline paths (WGS84) → drawing coords, clip to radius
        // ═════════════════════════════════════════════════════════════════════
        private static List<ContourLineData> ConvertAndClipPaths(
            JObject geometry, double elev, Point2d center, string drawingCS)
        {
            var result = new List<ContourLineData>();
            var paths = geometry["paths"] as JArray;
            if (paths == null) return result;

            foreach (JArray path in paths)
            {
                // Convert all vertices to drawing coordinates
                var drawingPts = new List<Point2d>(path.Count);
                foreach (var coord in path)
                {
                    double wgsX = coord[0]!.Value<double>();  // lon
                    double wgsY = coord[1]!.Value<double>();  // lat

                    if (GroundwaterCoords.ConvertFromLatLon(
                            drawingCS, wgsY, wgsX, out double dx, out double dy))
                    {
                        drawingPts.Add(new Point2d(dx, dy));
                    }
                }

                if (drawingPts.Count < 2) continue;

                // Clip to 500 ft radius
                var clippedRuns = ClipPathToRadius(drawingPts, center, CLIP_RADIUS);
                foreach (var run in clippedRuns)
                {
                    if (run.Count < 2) continue;
                    result.Add(new ContourLineData
                    {
                        Elevation = elev,
                        Points    = run
                    });
                }
            }

            return result;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Clip a polyline path to a circle (center, radius)
        //  Returns one or more "runs" of vertices inside the circle.
        // ═════════════════════════════════════════════════════════════════════
        private static List<List<Point2d>> ClipPathToRadius(
            List<Point2d> path, Point2d center, double radius)
        {
            var runs = new List<List<Point2d>>();
            var current = new List<Point2d>();

            for (int i = 0; i < path.Count - 1; i++)
            {
                var p1 = path[i];
                var p2 = path[i + 1];
                bool in1 = p1.GetDistanceTo(center) <= radius;
                bool in2 = p2.GetDistanceTo(center) <= radius;

                if (in1 && in2)
                {
                    // Both inside — add both (first only if starting a new run)
                    if (current.Count == 0) current.Add(p1);
                    current.Add(p2);
                }
                else if (in1 && !in2)
                {
                    // Exiting the circle
                    if (current.Count == 0) current.Add(p1);
                    var clip = LineCircleIntersect(p1, p2, center, radius);
                    if (clip.HasValue) current.Add(clip.Value);
                    // End this run
                    if (current.Count >= 2) runs.Add(current);
                    current = new List<Point2d>();
                }
                else if (!in1 && in2)
                {
                    // Entering the circle
                    var clip = LineCircleIntersect(p1, p2, center, radius);
                    if (clip.HasValue) current.Add(clip.Value);
                    current.Add(p2);
                }
                else
                {
                    // Both outside — segment might still cross the circle
                    var clips = LineCircleTwoIntersects(p1, p2, center, radius);
                    if (clips != null)
                    {
                        if (current.Count >= 2) runs.Add(current);
                        current = new List<Point2d>();
                        runs.Add(new List<Point2d> { clips.Value.Item1, clips.Value.Item2 });
                    }
                }
            }

            if (current.Count >= 2) runs.Add(current);
            return runs;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Line–circle intersection helpers
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Finds the single intersection point where a segment crosses the circle
        /// boundary (one endpoint inside, one outside).
        /// </summary>
        private static Point2d? LineCircleIntersect(
            Point2d p1, Point2d p2, Point2d c, double r)
        {
            double dx = p2.X - p1.X, dy = p2.Y - p1.Y;
            double fx = p1.X - c.X,  fy = p1.Y - c.Y;

            double a = dx * dx + dy * dy;
            double b = 2 * (fx * dx + fy * dy);
            double cc = fx * fx + fy * fy - r * r;

            double disc = b * b - 4 * a * cc;
            if (disc < 0) return null;

            double sqrtDisc = Math.Sqrt(disc);
            double t1 = (-b - sqrtDisc) / (2 * a);
            double t2 = (-b + sqrtDisc) / (2 * a);

            // Pick the t value between 0 and 1
            double t = (t1 >= 0 && t1 <= 1) ? t1 : t2;
            if (t < 0 || t > 1) return null;

            return new Point2d(p1.X + t * dx, p1.Y + t * dy);
        }

        /// <summary>
        /// Finds two intersection points when a segment passes through the circle
        /// (both endpoints outside). Returns null if segment doesn't cross.
        /// </summary>
        private static (Point2d, Point2d)? LineCircleTwoIntersects(
            Point2d p1, Point2d p2, Point2d c, double r)
        {
            double dx = p2.X - p1.X, dy = p2.Y - p1.Y;
            double fx = p1.X - c.X,  fy = p1.Y - c.Y;

            double a = dx * dx + dy * dy;
            double b = 2 * (fx * dx + fy * dy);
            double cc = fx * fx + fy * fy - r * r;

            double disc = b * b - 4 * a * cc;
            if (disc < 0) return null;

            double sqrtDisc = Math.Sqrt(disc);
            double t1 = (-b - sqrtDisc) / (2 * a);
            double t2 = (-b + sqrtDisc) / (2 * a);

            if (t1 > t2) { double tmp = t1; t1 = t2; t2 = tmp; }
            if (t1 < 0 || t1 > 1 || t2 < 0 || t2 > 1) return null;

            return (
                new Point2d(p1.X + t1 * dx, p1.Y + t1 * dy),
                new Point2d(p1.X + t2 * dx, p1.Y + t2 * dy)
            );
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Distance from point to ArcGIS polyline geometry (in WGS84 degrees)
        // ═════════════════════════════════════════════════════════════════════
        private static double ComputeDistanceToPolyline(double px, double py, JObject geometry)
        {
            double minDist = double.MaxValue;

            var paths = geometry["paths"] as JArray;
            if (paths == null) return minDist;

            foreach (JArray path in paths)
            {
                for (int i = 0; i < path.Count - 1; i++)
                {
                    double ax = path[i]![0]!.Value<double>();
                    double ay = path[i]![1]!.Value<double>();
                    double bx = path[i + 1]![0]!.Value<double>();
                    double by = path[i + 1]![1]!.Value<double>();

                    double dist = PointToSegmentDistance(px, py, ax, ay, bx, by);
                    if (dist < minDist)
                        minDist = dist;
                }
            }

            return minDist;
        }

        private static double PointToSegmentDistance(
            double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax;
            double dy = by - ay;
            double lenSq = dx * dx + dy * dy;

            if (lenSq < 1e-20)
                return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));

            double t = ((px - ax) * dx + (py - ay) * dy) / lenSq;
            t = Math.Max(0, Math.Min(1, t));

            double projX = ax + t * dx;
            double projY = ay + t * dy;

            return Math.Sqrt((px - projX) * (px - projX) + (py - projY) * (py - projY));
        }
    }
}
