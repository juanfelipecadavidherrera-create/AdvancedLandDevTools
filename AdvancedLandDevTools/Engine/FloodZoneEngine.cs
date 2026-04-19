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
            ed.WriteMessage("\n  Querying FEMA National Flood Hazard Layer (Assuming FL83-EF)...");

            // 2) Query FEMA (synchronous — safe on AutoCAD UI thread)
            FloodZoneResult result = QueryFemaIdentify(pickedPoint.X, pickedPoint.Y);
            return result;
        }

        // removed coord conversion

        // ═════════════════════════════════════════════════════════════════════
        //  FEMA NFHL REST API — fully synchronous (no async deadlock risk)
        // ═════════════════════════════════════════════════════════════════════
        private static FloodZoneResult QueryFemaIdentify(double x, double y)
        {
            try
            {
                double buffer = 100.0; // 100 feet
                string mapExtent = $"{x - buffer},{y - buffer},{x + buffer},{y + buffer}";

                string queryUrl = $"{FEMA_NFHL_IDENTIFY_URL}" +
                    $"?geometry={x},{y}" +
                    $"&geometryType=esriGeometryPoint" +
                    $"&sr=2236" +
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
                    return QueryFemaDirect(x, y);

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
                return QueryFemaDirect(x, y);
            }
        }

        private static FloodZoneResult QueryFemaDirect(double x, double y)
        {
            try
            {
                string queryUrl = FEMA_NFHL_QUERY_URL +
                    $"?geometry={x},{y}" +
                    $"&geometryType=esriGeometryPoint" +
                    $"&inSR=2236" +
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
