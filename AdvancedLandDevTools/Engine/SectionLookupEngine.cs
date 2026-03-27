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

            if (!ConvertToLatLon(doc, pickedPoint, out double lat, out double lon))
            {
                return new SectionLookupResult
                {
                    Success = false,
                    ErrorMessage = "Could not convert coordinates to Lat/Lon. " +
                                   "Make sure your drawing has a valid coordinate system assigned."
                };
            }

            ed.WriteMessage($"\n  Converted to WGS84: Lat={lat:F6}, Lon={lon:F6}");
            ed.WriteMessage("\n  Querying SFWMD PLSS service...");

            return QueryPLSS(lat, lon);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  SFWMD PLSS REST API query
        // ═════════════════════════════════════════════════════════════════════
        private static SectionLookupResult QueryPLSS(double lat, double lon)
        {
            try
            {
                string geometry = Uri.EscapeDataString($"{{\"x\":{lon},\"y\":{lat}}}");

                string queryUrl = SFWMD_PLSS_URL +
                    $"?where=TRtype%3D1" +
                    $"&geometry={geometry}" +
                    $"&geometryType=esriGeometryPoint" +
                    $"&inSR=4326" +
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

        // ═════════════════════════════════════════════════════════════════════
        //  Coordinate transformation (same pattern as other engines)
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
