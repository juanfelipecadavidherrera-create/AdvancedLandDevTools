using System;
using System.Net.Http;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;
using AdvancedLandDevTools.Models;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AdvancedLandDevTools.Engine
{
    public static class PropertyAppraisalEngine
    {
        // ── Configuration ───────────────────────────────────────────────────
        private const string MD_LANDINFO_QUERY_URL =
            "https://gisweb.miamidade.gov/arcgis/rest/services/MD_LandInformation/MapServer/26/query";

        private static readonly HttpClient _httpClient;

        static PropertyAppraisalEngine()
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
        //  Public API — Single-point lookup
        // ═════════════════════════════════════════════════════════════════════
        public static PropertyAppraisalResult LookupSinglePoint(Document doc, Point3d pickedPoint)
        {
            Editor ed = doc.Editor;
            ed.WriteMessage($"\n  Querying MDC GIS at X={pickedPoint.X:F3}, Y={pickedPoint.Y:F3} (Assuming FL83-EF)...");

            return QueryMdcProperty(pickedPoint.X, pickedPoint.Y);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Public API — Draw Buffer Parcels
        // ═════════════════════════════════════════════════════════════════════
        public static void DrawParcelsInRadius(Document doc, Point3d pickedPoint, double radius)
        {
            try
            {
                string queryUrl = MD_LANDINFO_QUERY_URL +
                    $"?geometry={pickedPoint.X},{pickedPoint.Y}" +
                    $"&geometryType=esriGeometryPoint" +
                    $"&inSR=2236" +
                    $"&spatialRel=esriSpatialRelIntersects" +
                    $"&distance={radius}" +
                    $"&units=esriSRUnit_Foot" +
                    $"&outSR=2236" +
                    $"&returnGeometry=true" +
                    $"&outFields=OBJECTID" +
                    $"&f=json";

                string jsonResponse = System.Threading.Tasks.Task.Run(() =>
                {
                    var response = _httpClient.GetAsync(queryUrl).Result;
                    response.EnsureSuccessStatusCode();
                    return response.Content.ReadAsStringAsync().Result;
                }).Result;

                JObject json = JObject.Parse(jsonResponse);
                JArray? features = json["features"] as JArray;
                if (features == null || features.Count == 0) return;

                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    BlockTable bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    ObjectIdCollection polylineIds = new ObjectIdCollection();

                    foreach (JToken feature in features)
                    {
                        JObject? geom = feature["geometry"] as JObject;
                        if (geom == null) continue;

                        JArray? rings = geom["rings"] as JArray;
                        if (rings == null) continue;

                        foreach (JToken ring in rings)
                        {
                            JArray? points = ring as JArray;
                            if (points == null) continue;

                            using (Polyline poly = new Polyline())
                            {
                                int vertexIdx = 0;
                                foreach (JToken ptToken in points)
                                {
                                    JArray? coords = ptToken as JArray;
                                    if (coords != null && coords.Count >= 2)
                                    {
                                        double x = (double)coords[0];
                                        double y = (double)coords[1];
                                        poly.AddVertexAt(vertexIdx++, new Point2d(x, y), 0, 0, 0);
                                    }
                                }
                                poly.Closed = true;
                                poly.ColorIndex = 3; // Green

                                ObjectId id = ms.AppendEntity(poly);
                                tr.AddNewlyCreatedDBObject(poly, true);
                                polylineIds.Add(id);
                            }
                        }
                    }

                    if (polylineIds.Count > 0)
                    {
                        DBDictionary groupDict = (DBDictionary)tr.GetObject(doc.Database.GroupDictionaryId, OpenMode.ForWrite);
                        string groupName = "ALDT_FOLIO_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        
                        using (Group group = new Group(groupName, true))
                        {
                            ObjectId groupId = groupDict.SetAt(groupName, group);
                            tr.AddNewlyCreatedDBObject(group, true);
                            group.Append(polylineIds);
                        }
                    }

                    tr.Commit();
                    doc.Editor.WriteMessage($"\n  Drew {polylineIds.Count} parcel boundary lines inside {radius}ft radius.");
                }
            }
            catch (Exception ex)
            {
                doc.Editor.WriteMessage($"\n  Failed to draw parcel buffer: {ex.Message}");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  MDC GIS REST API
        // ═════════════════════════════════════════════════════════════════════
        private static PropertyAppraisalResult QueryMdcProperty(double x, double y)
        {
            try
            {
                string queryUrl = MD_LANDINFO_QUERY_URL +
                    $"?geometry={x},{y}" +
                    $"&geometryType=esriGeometryPoint" +
                    $"&inSR=2236" +
                    $"&spatialRel=esriSpatialRelIntersects" +
                    $"&outFields=FOLIO,TRUE_SITE_ADDR,TRUE_SITE_CITY,TRUE_SITE_ZIP_CODE,TRUE_OWNER1" +
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
                    return new PropertyAppraisalResult
                    {
                        Success = false,
                        ErrorMessage = "No property data found at this location."
                    };
                }

                JObject? attributes = (features[0] as JObject)?["attributes"] as JObject;
                if (attributes == null)
                {
                    return new PropertyAppraisalResult
                    {
                        Success = false,
                        ErrorMessage = "No attributes returned."
                    };
                }

                return ParseAttributes(attributes);
            }
            catch (Exception ex)
            {
                return new PropertyAppraisalResult
                {
                    Success = false,
                    ErrorMessage = $"Query failed: {ex.Message}"
                };
            }
        }

        private static PropertyAppraisalResult ParseAttributes(JObject attributes)
        {
            string folio = Attr(attributes, "FOLIO");
            string owner1 = Attr(attributes, "TRUE_OWNER1");
            string siteAddr = Attr(attributes, "TRUE_SITE_ADDR");
            string siteCity = Attr(attributes, "TRUE_SITE_CITY");
            string siteZip = Attr(attributes, "TRUE_SITE_ZIP_CODE");

            // format folio (0101110501010 -> 01-0111-050-1010)
            if (folio.Length == 13 && long.TryParse(folio, out _))
            {
                folio = $"{folio.Substring(0, 2)}-{folio.Substring(2, 4)}-{folio.Substring(6, 3)}-{folio.Substring(9, 4)}";
            }

            return new PropertyAppraisalResult
            {
                Success = true,
                Folio = !IsNull(folio) ? folio : "N/A",
                OwnerName = !IsNull(owner1) ? owner1 : "N/A",
                SiteAddress = !IsNull(siteAddr) ? siteAddr : "N/A",
                SiteCity = !IsNull(siteCity) ? siteCity : "N/A",
                SiteZipCode = !IsNull(siteZip) ? siteZip : "N/A"
            };
        }

        private static string Attr(JObject obj, string key)
        {
            JToken? token = obj[key];
            if (token == null || token.Type == JTokenType.Null)
                return "Null";
            return token.ToString().Trim();
        }

        private static bool IsNull(string? val)
            => string.IsNullOrEmpty(val) || val == "Null";
    }
}
