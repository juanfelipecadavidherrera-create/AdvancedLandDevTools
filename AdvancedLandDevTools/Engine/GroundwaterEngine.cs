using System;
using System.Net.Http;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;

namespace AdvancedLandDevTools.Engine
{
    public static class GroundwaterEngine
    {
        // ── MDC Groundwater MapServer identify endpoint ───────────────────
        private const string MDC_GROUNDWATER_URL =
            "https://gisweb.miamidade.gov/arcgis/rest/services/" +
            "VulnerabilityViewer/MD_Groundwater/MapServer/identify";

        // NAVD88 → NGVD29 offset for Miami-Dade County
        private const double NAVD_TO_NGVD_OFFSET = 1.52;

        private static readonly HttpClient _httpClient;

        static GroundwaterEngine()
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "AdvancedLandDevTools/1.0 (Civil3D Plugin)");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Public API — look up groundwater elevation at a point
        // ═══════════════════════════════════════════════════════════════════
        public static void LookupAtPoint(Document doc, Point3d pickedPoint)
        {
            Editor ed = doc.Editor;

            ed.WriteMessage($"\n  Picked point (Drawing coords): " +
                            $"X={pickedPoint.X:F3}, Y={pickedPoint.Y:F3}");

            // Convert drawing coords to WGS84
            if (!ConvertToLatLon(doc, pickedPoint, out double lat, out double lon))
            {
                ed.WriteMessage("\n  ** Could not convert coordinates to Lat/Lon.");
                ed.WriteMessage("\n  Make sure your drawing has a valid coordinate system assigned.");
                ed.WriteMessage("\n  (Settings > Drawing Settings > Zone tab)\n");
                return;
            }

            ed.WriteMessage($"\n  Converted to WGS84: Lat={lat:F6}, Lon={lon:F6}");
            ed.WriteMessage("\n  Querying MDC Groundwater Level service...");

            // Query MDC MapServer
            double navd88;
            if (!QueryGroundwater(lat, lon, out navd88, out string errorMsg))
            {
                ed.WriteMessage($"\n  ** Query failed: {errorMsg}");
                ed.WriteMessage("\n  The point may be outside Miami-Dade County,");
                ed.WriteMessage("\n  or the MDC service is temporarily unavailable.\n");
                return;
            }

            double ngvd29 = navd88 + NAVD_TO_NGVD_OFFSET;

            ed.WriteMessage("\n");
            ed.WriteMessage("\n  ╔══════════════════════════════════════════════════╗");
            ed.WriteMessage("\n  ║       GROUNDWATER LEVEL LOOKUP RESULTS          ║");
            ed.WriteMessage("\n  ╠══════════════════════════════════════════════════╣");
            ed.WriteMessage($"\n  ║  NAVD 88:   {navd88,8:F2} ft                        ║");
            ed.WriteMessage($"\n  ║  NGVD 29:   {ngvd29,8:F2} ft                        ║");
            ed.WriteMessage("\n  ╠══════════════════════════════════════════════════╣");
            ed.WriteMessage("\n  ║  Dataset: Groundwater Level May 2040            ║");
            ed.WriteMessage("\n  ║  Source:  MDC / USGS Groundwater Model (UMD)    ║");
            ed.WriteMessage("\n  ║  Conversion: NGVD = NAVD + 1.52 ft             ║");
            ed.WriteMessage("\n  ╚══════════════════════════════════════════════════╝\n");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  MDC MapServer identify — synchronous HTTP
        // ═══════════════════════════════════════════════════════════════════
        private static bool QueryGroundwater(double lat, double lon,
            out double navd88, out string errorMsg)
        {
            navd88 = 0;
            errorMsg = "";

            try
            {
                double buffer = 0.001;
                string mapExtent = $"{lon - buffer},{lat - buffer}," +
                                   $"{lon + buffer},{lat + buffer}";

                string queryUrl = $"{MDC_GROUNDWATER_URL}" +
                    $"?geometry={lon},{lat}" +
                    $"&geometryType=esriGeometryPoint" +
                    $"&sr=4326" +
                    $"&layers=all:0" +
                    $"&tolerance=1" +
                    $"&mapExtent={mapExtent}" +
                    $"&imageDisplay=128,128,96" +
                    $"&returnGeometry=false" +
                    $"&f=json";

                string jsonResponse = System.Threading.Tasks.Task.Run(() =>
                {
                    var response = _httpClient.GetAsync(queryUrl).Result;
                    response.EnsureSuccessStatusCode();
                    return response.Content.ReadAsStringAsync().Result;
                }).Result;

                JObject json = JObject.Parse(jsonResponse);
                JArray? results = json["results"] as JArray;

                if (results == null || results.Count == 0)
                {
                    errorMsg = "No groundwater data found at this location.";
                    return false;
                }

                JObject? attributes = (results[0] as JObject)?["attributes"] as JObject;
                if (attributes == null)
                {
                    errorMsg = "No attributes returned from MDC service.";
                    return false;
                }

                string? pixelValue = attributes["Stretch.Pixel Value"]?.ToString()
                                  ?? attributes["Pixel Value"]?.ToString();

                if (string.IsNullOrEmpty(pixelValue) || pixelValue == "NoData")
                {
                    errorMsg = "No groundwater data at this location (NoData pixel).";
                    return false;
                }

                if (!double.TryParse(pixelValue,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out navd88))
                {
                    errorMsg = $"Could not parse pixel value: '{pixelValue}'";
                    return false;
                }

                return true;
            }
            catch (System.Exception ex)
            {
                errorMsg = ex.InnerException?.Message ?? ex.Message;
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Coordinate transformation (same pattern as FloodZoneEngine)
        // ═══════════════════════════════════════════════════════════════════
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
            catch (System.Exception ex)
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
