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
    public static class SectionLookupEngine
    {
        private const string SFWMD_PLSS_URL =
            "https://geoweb.sfwmd.gov/agsext2/rest/services/" +
            "LandSurveyAndControl/PLSS/FeatureServer/2/query";

        private static readonly HttpClient _httpClient;

        static SectionLookupEngine()
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
        //  Public API — Single-point TTRRSS lookup
        // ═════════════════════════════════════════════════════════════════════
        public static SectionLookupResult LookupPoint(Document doc, Point3d pickedPoint)
        {
            Editor ed = doc.Editor;
            ed.WriteMessage($"\n  Picked point (Drawing coords): X={pickedPoint.X:F3}, Y={pickedPoint.Y:F3}");
            ed.WriteMessage("\n  Querying SFWMD PLSS service (Assuming FL83-EF)...");

            return QueryPLSS(pickedPoint.X, pickedPoint.Y);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  SFWMD PLSS REST API query
        // ═════════════════════════════════════════════════════════════════════
        private static SectionLookupResult QueryPLSS(double x, double y)
        {
            try
            {
                string geometry = Uri.EscapeDataString($"{{\"x\":{x},\"y\":{y}}}");

                string queryUrl = SFWMD_PLSS_URL +
                    $"?where=TRtype%3D1" +
                    $"&geometry={geometry}" +
                    $"&geometryType=esriGeometryPoint" +
                    $"&inSR=2236" +
                    $"&spatialRel=esriSpatialRelIntersects" +
                    $"&outFields=SSTTRR,SECNO,TWP,RGE" +
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
                {
                    return new SectionLookupResult
                    {
                        Success = false,
                        ErrorMessage = "No PLSS data found at this location. " +
                                       "The point may be outside the surveyed area."
                    };
                }

                JObject? attrs = (features[0] as JObject)?["attributes"] as JObject;
                if (attrs == null)
                {
                    return new SectionLookupResult
                    {
                        Success = false,
                        ErrorMessage = "No attributes returned from PLSS service."
                    };
                }

                string ssttrr = attrs["SSTTRR"]?.ToString() ?? "N/A";
                int secNo     = attrs["SECNO"]?.Value<int>() ?? 0;
                int twp       = (int)(attrs["TWP"]?.Value<double>() ?? 0);
                int rge       = (int)(attrs["RGE"]?.Value<double>() ?? 0);

                return new SectionLookupResult
                {
                    Success  = true,
                    SSTTRR   = ssttrr,
                    Section  = secNo,
                    Township = twp,
                    Range    = rge
                };
            }
            catch (Exception ex)
            {
                return new SectionLookupResult
                {
                    Success = false,
                    ErrorMessage = $"Query failed: {ex.InnerException?.Message ?? ex.Message}"
                };
            }
        }

        // removed coord conversion
    }
}
