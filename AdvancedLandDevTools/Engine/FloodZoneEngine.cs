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
    public static class FloodZoneEngine
    {
        // ── Configuration ───────────────────────────────────────────────────
        private const string FEMA_NFHL_IDENTIFY_URL =
            "https://hazards.fema.gov/arcgis/rest/services/public/NFHL/MapServer/identify";

        private const string FEMA_NFHL_QUERY_URL =
            "https://hazards.fema.gov/arcgis/rest/services/public/NFHL/MapServer/28/query";

        private static readonly HttpClient _httpClient;

        static FloodZoneEngine()
        {
            var handler = new HttpClientHandler();
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "AdvancedLandDevTools/1.0 (Civil3D Plugin)");
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Public API — Single-point lookup (command-line output only)
        // ═════════════════════════════════════════════════════════════════════
        public static FloodZoneResult LookupSinglePoint(Document doc, Point3d pickedPoint)
        {
            Editor ed = doc.Editor;

            ed.WriteMessage($"\n  Picked point (Drawing coords): X={pickedPoint.X:F3}, Y={pickedPoint.Y:F3}");

            // 1) Convert to WGS84
            if (!ConvertToLatLon(doc, pickedPoint, out double lat, out double lon))
            {
                return new FloodZoneResult
                {
                    Success = false,
                    ErrorMessage = "Could not convert coordinates to Lat/Lon. " +
                                   "Make sure your drawing has a valid coordinate system assigned. " +
                                   "(Settings > Drawing Settings > Zone tab)"
                };
            }

            ed.WriteMessage($"\n  Converted to WGS84: Lat={lat:F6}, Lon={lon:F6}");
            ed.WriteMessage("\n  Querying FEMA National Flood Hazard Layer...");

            // 2) Query FEMA (synchronous — safe on AutoCAD UI thread)
            FloodZoneResult result = QueryFemaIdentify(lat, lon);
            return result;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Coordinate transformation
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

            if (lat < 25.0 || lat > 26.5 || lon < -81.0 || lon > -79.5)
            {
                AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
                    "\n  WARNING: Converted coordinates appear outside Miami-Dade County bounds.");
            }

            return true;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  FEMA NFHL REST API — fully synchronous (no async deadlock risk)
        // ═════════════════════════════════════════════════════════════════════
        private static FloodZoneResult QueryFemaIdentify(double lat, double lon)
        {
            try
            {
                double buffer = 0.001;
                string mapExtent = $"{lon - buffer},{lat - buffer},{lon + buffer},{lat + buffer}";

                string queryUrl = $"{FEMA_NFHL_IDENTIFY_URL}" +
                    $"?geometry={lon},{lat}" +
                    $"&geometryType=esriGeometryPoint" +
                    $"&sr=4326" +
                    $"&layers=all:28" +
                    $"&tolerance=0" +
                    $"&mapExtent={mapExtent}" +
                    $"&imageDisplay=800,600,96" +
                    $"&returnGeometry=false" +
                    $"&f=json";

                // Synchronous HTTP — runs on a thread-pool thread to avoid UI deadlock
                string jsonResponse = System.Threading.Tasks.Task.Run(() =>
                {
                    var response = _httpClient.GetAsync(queryUrl).Result;
                    response.EnsureSuccessStatusCode();
                    return response.Content.ReadAsStringAsync().Result;
                }).Result;

                JObject json = JObject.Parse(jsonResponse);

                JArray? results = json["results"] as JArray;
                if (results == null || results.Count == 0)
                    return QueryFemaDirect(lat, lon);

                JObject? attributes = (results[0] as JObject)?["attributes"] as JObject;
                if (attributes == null)
                    return new FloodZoneResult
                    {
                        Success = false,
                        ErrorMessage = "No attributes returned from FEMA service."
                    };

                return ParseAttributes(attributes);
            }
            catch
            {
                // Identify endpoint failed (403, timeout, etc.) — try direct query
                return QueryFemaDirect(lat, lon);
            }
        }

        private static FloodZoneResult QueryFemaDirect(double lat, double lon)
        {
            try
            {
                string queryUrl = FEMA_NFHL_QUERY_URL +
                    $"?geometry={lon},{lat}" +
                    $"&geometryType=esriGeometryPoint" +
                    $"&inSR=4326" +
                    $"&spatialRel=esriSpatialRelIntersects" +
                    $"&outFields=FLD_AR_ID,FLD_ZONE,FLOODWAY,SFHA_TF,STATIC_BFE," +
                        $"V_DATUM,DEPTH,LEN_UNIT,VELOCITY,VEL_UNIT," +
                        $"AR_REVERT,BFE_REVERT,DEP_REVERT,ZONE_SUBTY,SOURCE_CIT" +
                    $"&returnGeometry=false" +
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
                    return new FloodZoneResult
                    {
                        Success = false,
                        ErrorMessage = "No flood zone data found at this location. " +
                                       "The point may be outside FEMA mapped areas."
                    };

                JObject? attributes = (features[0] as JObject)?["attributes"] as JObject;
                if (attributes == null)
                    return new FloodZoneResult
                    {
                        Success = false,
                        ErrorMessage = "No attributes in query result."
                    };

                return ParseAttributes(attributes);
            }
            catch (Exception ex)
            {
                return new FloodZoneResult
                {
                    Success = false,
                    ErrorMessage = $"Direct query failed: {ex.Message}"
                };
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Parse FEMA attributes into FloodZoneResult
        // ═════════════════════════════════════════════════════════════════════
        private static FloodZoneResult ParseAttributes(JObject attributes)
        {
            string floodZone = Attr(attributes, "FLD_ZONE");
            string sfhaTf    = Attr(attributes, "SFHA_TF");
            string staticBfe = Attr(attributes, "STATIC_BFE");
            string vDatum    = Attr(attributes, "V_DATUM");
            string floodway  = Attr(attributes, "FLOODWAY");
            string depth     = Attr(attributes, "DEPTH");
            string zoneSubty = Attr(attributes, "ZONE_SUBTY");
            string fldArId   = Attr(attributes, "FLD_AR_ID");
            string lenUnit   = Attr(attributes, "LEN_UNIT");
            string sourceCit = Attr(attributes, "SOURCE_CIT");

            string bfeDisplay = "N/A";
            if (!IsNull(staticBfe) && staticBfe != "-9999" && staticBfe != "-9999.0")
            {
                string unit = !IsNull(lenUnit) ? lenUnit : "ft (NAVD88)";
                bfeDisplay = $"{staticBfe} {unit}";
            }

            string depthDisplay = "N/A";
            if (!IsNull(depth) && depth != "-9999" && depth != "-9999.0")
                depthDisplay = $"{depth} ft";

            string sfhaDisplay = "N/A";
            if (!IsNull(sfhaTf))
                sfhaDisplay = sfhaTf.Equals("T", StringComparison.OrdinalIgnoreCase)
                    ? "Yes (Special Flood Hazard Area)" : "No";

            return new FloodZoneResult
            {
                Success            = true,
                FloodZone          = !IsNull(floodZone) ? floodZone : "N/A",
                ZoneSubtype        = !IsNull(zoneSubty) ? zoneSubty : "N/A",
                IsSFHA             = sfhaDisplay,
                BaseFloodElevation = bfeDisplay,
                VerticalDatum      = !IsNull(vDatum) ? vDatum : "NAVD88",
                Floodway           = !IsNull(floodway) ? floodway : "N/A",
                Depth              = depthDisplay,
                FirmPanel          = !IsNull(sourceCit) ? sourceCit : fldArId ?? "N/A"
            };
        }

        private static string Attr(JObject obj, string key)
        {
            JToken? token = obj[key];
            if (token == null || token.Type == JTokenType.Null)
                return "Null";
            return token.ToString();
        }

        private static bool IsNull(string? val)
            => string.IsNullOrEmpty(val) || val == "Null";
    }
}
