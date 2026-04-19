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

            ed.WriteMessage("\n  Querying MDC Groundwater Level (October 2040) service (Assuming FL83-EF)...");

            // Query MDC MapServer
            double navd88;
            if (!QueryGroundwater(pickedPoint.X, pickedPoint.Y, out navd88, out string errorMsg))
            {
                ed.WriteMessage($"\n  ** Query failed: {errorMsg}");
                ed.WriteMessage("\n  The point may be outside Miami-Dade County,");
                ed.WriteMessage("\n  or the MDC service is temporarily unavailable.\n");
                return;
            }

            double ngvd29 = navd88 + NAVD_TO_NGVD_OFFSET;

            ed.WriteMessage("\n");
            ed.WriteMessage("\n  ╔══════════════════════════════════════════════════╗");
            ed.WriteMessage("\n  ║       GROUNDWATER LEVEL — OCTOBER 2040           ║");
            ed.WriteMessage("\n  ╠══════════════════════════════════════════════════╣");
            ed.WriteMessage($"\n  ║  NAVD 88:   {navd88,8:F2} ft                        ║");
            ed.WriteMessage($"\n  ║  NGVD 29:   {ngvd29,8:F2} ft                        ║");
            ed.WriteMessage("\n  ╠══════════════════════════════════════════════════╣");
            ed.WriteMessage("\n  ║  Dataset: Groundwater Level October 2040        ║");
            ed.WriteMessage("\n  ║  Source:  MDC / USGS Groundwater Model (UMD)    ║");
            ed.WriteMessage("\n  ║  Conversion: NGVD = NAVD + 1.52 ft             ║");
            ed.WriteMessage("\n  ╚══════════════════════════════════════════════════╝\n");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  MDC MapServer identify — synchronous HTTP
        // ═══════════════════════════════════════════════════════════════════
        private static bool QueryGroundwater(double x, double y,
            out double navd88, out string errorMsg)
        {
            navd88 = 0;
            errorMsg = "";

            try
            {
                double buffer = 100.0;
                string mapExtent = $"{x - buffer},{y - buffer}," +
                                   $"{x + buffer},{y + buffer}";

                string queryUrl = $"{MDC_GROUNDWATER_URL}" +
                    $"?geometry={x},{y}" +
                    $"&geometryType=esriGeometryPoint" +
                    $"&sr=2236" +
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

    }
}
