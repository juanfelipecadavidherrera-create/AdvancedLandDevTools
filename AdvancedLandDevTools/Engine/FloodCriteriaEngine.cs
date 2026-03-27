using System;
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
        //  Public API — Single-point lookup
        // ═════════════════════════════════════════════════════════════════════
        public static FloodCriteriaResult LookupPoint(Document doc, Point3d pickedPoint)
        {
            Editor ed = doc.Editor;

            ed.WriteMessage($"\n  Picked point (Drawing coords): X={pickedPoint.X:F3}, Y={pickedPoint.Y:F3}");

            // 1) Convert to WGS84
            if (!ConvertToLatLon(doc, pickedPoint, out double lat, out double lon))
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

            // 2) Query MDC — find nearest flood criteria contour
            return QueryFloodCriteria(lat, lon);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  MDC Flood Criteria query — returns nearest contour with distance
        // ═════════════════════════════════════════════════════════════════════
        private static FloodCriteriaResult QueryFloodCriteria(double lat, double lon)
        {
            try
            {
                // Query all features with geometry, then find nearest on the server
                // Use a generous envelope around the point to catch nearby contours
                double buffer = 0.02;  // ~2km buffer
                string envelope = $"{lon - buffer},{lat - buffer},{lon + buffer},{lat + buffer}";

                string queryUrl = MDC_FLOOD_CRITERIA_URL +
                    $"?where=1=1" +
                    $"&geometry={envelope}" +
                    $"&geometryType=esriGeometryEnvelope" +
                    $"&inSR=4326" +
                    $"&spatialRel=esriSpatialRelIntersects" +
                    $"&outFields=ELEV" +
                    $"&returnGeometry=true" +
                    $"&returnDistances=true" +
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
                    // Try wider search
                    return QueryFloodCriteriaWide(lat, lon);
                }

                // Find the nearest feature by computing distance to each line
                double minDist = double.MaxValue;
                double nearestElev = 0;

                foreach (var feature in features)
                {
                    var attrs = feature["attributes"] as JObject;
                    var geom = feature["geometry"] as JObject;
                    if (attrs == null || geom == null) continue;

                    double elev = attrs["ELEV"]?.Value<double>() ?? 0;

                    // Compute distance from point to polyline paths
                    double dist = ComputeDistanceToPolyline(lon, lat, geom);

                    if (dist < minDist)
                    {
                        minDist = dist;
                        nearestElev = elev;
                    }
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
                    Distance  = $"{distFeet:F0} ft (approx)"
                };
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

        private static FloodCriteriaResult QueryFloodCriteriaWide(double lat, double lon)
        {
            try
            {
                // Wider search — get all 13 features
                string queryUrl = MDC_FLOOD_CRITERIA_URL +
                    $"?where=1=1" +
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
                    return new FloodCriteriaResult
                    {
                        Success = false,
                        ErrorMessage = "No flood criteria data returned from MDC service."
                    };
                }

                double minDist = double.MaxValue;
                double nearestElev = 0;

                foreach (var feature in features)
                {
                    var attrs = feature["attributes"] as JObject;
                    var geom = feature["geometry"] as JObject;
                    if (attrs == null || geom == null) continue;

                    double elev = attrs["ELEV"]?.Value<double>() ?? 0;
                    double dist = ComputeDistanceToPolyline(lon, lat, geom);

                    if (dist < minDist)
                    {
                        minDist = dist;
                        nearestElev = elev;
                    }
                }

                if (minDist == double.MaxValue)
                {
                    return new FloodCriteriaResult
                    {
                        Success = false,
                        ErrorMessage = "Could not compute distance to flood criteria contours."
                    };
                }

                double distFeet = minDist * 364567.0;

                return new FloodCriteriaResult
                {
                    Success   = true,
                    Elevation = $"{nearestElev:F1} ft NGVD",
                    Distance  = $"{distFeet:F0} ft (approx)"
                };
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
        //  Distance from point to ArcGIS polyline geometry
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

        // ═════════════════════════════════════════════════════════════════════
        //  Coordinate transformation (reuses same logic as FloodZoneEngine)
        // ═════════════════════════════════════════════════════════════════════
        private static bool ConvertToLatLon(Document doc, Point3d drawingPoint,
                                            out double lat, out double lon)
        {
            lat = 0; lon = 0;

            try
            {
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var civilDoc = Autodesk.Civil.ApplicationServices.CivilDocument
                        .GetCivilDocument(doc.Database);

                    string drawingCS = civilDoc.Settings.DrawingSettings
                        .UnitZoneSettings.CoordinateSystemCode;

                    if (string.IsNullOrEmpty(drawingCS))
                    {
                        doc.Editor.WriteMessage(
                            "\n  WARNING: No coordinate system assigned to drawing.");
                        doc.Editor.WriteMessage(
                            "\n  Falling back to approximate State Plane FL East conversion.");
                        tr.Commit();
                        return ConvertStatePlaneApprox(
                            drawingPoint.X, drawingPoint.Y, out lat, out lon);
                    }

                    doc.Editor.WriteMessage($"\n  Drawing coordinate system: {drawingCS}");

                    bool ok = TransformViaGeolocation(drawingCS, drawingPoint, out lat, out lon);
                    tr.Commit();

                    if (ok) return true;

                    doc.Editor.WriteMessage(
                        "\n  Geolocation API unavailable. Using approximate conversion.");
                    return ConvertStatePlaneApprox(
                        drawingPoint.X, drawingPoint.Y, out lat, out lon);
                }
            }
            catch (Exception ex)
            {
                doc.Editor.WriteMessage($"\n  Transform exception: {ex.Message}");
                doc.Editor.WriteMessage("\n  Falling back to approximate conversion.");
                return ConvertStatePlaneApprox(
                    drawingPoint.X, drawingPoint.Y, out lat, out lon);
            }
        }

        private static bool TransformViaGeolocation(string drawingCS, Point3d pt,
                                                     out double lat, out double lon)
        {
            lat = 0; lon = 0;
            try
            {
                var asm = System.Reflection.Assembly.Load("Autodesk.Geolocation");
                if (asm == null) return false;

                var csType = asm.GetType("Autodesk.Geolocation.CoordinateSystem");
                var txType = asm.GetType("Autodesk.Geolocation.CoordinateSystemTransformer");
                if (csType == null || txType == null) return false;

                object sourceCRS = System.Activator.CreateInstance(csType, drawingCS)!;
                object targetCRS = System.Activator.CreateInstance(csType, "LL84")!;
                object transformer = System.Activator.CreateInstance(txType, sourceCRS, targetCRS)!;

                var transformMethod = txType.GetMethod("Transform",
                    new[] { typeof(Point3d) });
                if (transformMethod == null) return false;

                object result = transformMethod.Invoke(transformer, new object[] { pt })!;
                Point3d tgt = (Point3d)result;
                lon = tgt.X;
                lat = tgt.Y;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ConvertStatePlaneApprox(
            double eastingFt, double northingFt, out double lat, out double lon)
        {
            double eastingM  = eastingFt  * 0.3048006096;
            double northingM = northingFt * 0.3048006096;

            double falseEastingM   = 200000.0;
            double centralMeridian = -81.0;
            double latOrigin       = 24.333333333;

            double dE = eastingM - falseEastingM;
            double dN = northingM;

            double metersPerDegreeLat = 110920.0;
            double metersPerDegreeLon = 99960.0;

            lat = latOrigin + (dN / metersPerDegreeLat);
            lon = centralMeridian + (dE / metersPerDegreeLon);

            return true;
        }
    }
}
