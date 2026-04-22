// Advanced Land Development Tools
// Copyright © Juan Felipe Cadavid — All Rights Reserved
// Unauthorized copying or redistribution is prohibited.

using System;
using System.Net.Http;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Newtonsoft.Json.Linq;

namespace AdvancedLandDevTools.Engine
{
    /// <summary>Summary returned to the command after drawing.</summary>
    public record CoralAsBuiltSummary(
        bool    Success,
        string? ErrorMessage,
        int     GravityCount,
        int     ForceCount,
        int     ManholeCount,
        int     LateralCount);

    /// <summary>
    /// Queries the Coral Gables Sewer GIS (ArcGIS FeatureServer) and draws
    /// the results into the active model-space drawing.
    ///
    /// Service:   Coral_Gables_Sewer_WebApp_Update_Feb_2023_Rev_2
    /// SR:        FL State Plane East / NAD83 — WKID 2236 (drawing must be in same SR).
    /// Layers:    7 Gravity Mains · 8 Force Mains · 3 Manholes · 21 Laterals
    /// </summary>
    public static class CoralAsBuiltEngine
    {
        // ── Service constants ──────────────────────────────────────────────────
        private const string BASE =
            "https://services6.arcgis.com/CHdT8mfx1lPMw07R/arcgis/rest/services/" +
            "Coral_Gables_Sewer_WebApp_Update_Feb_2023_Rev_2/FeatureServer";

        private const int L_GRAVITY  = 7;
        private const int L_FORCE    = 8;
        private const int L_MANHOLES = 3;
        private const int L_LATERALS = 21;

        // ── AutoCAD layer names ────────────────────────────────────────────────
        private const string LYR_GRAVITY  = "ALDT-CG-GRAVITY-MAIN";
        private const string LYR_FORCE    = "ALDT-CG-FORCE-MAIN";
        private const string LYR_MANHOLE  = "ALDT-CG-MANHOLE";
        private const string LYR_LATERAL  = "ALDT-CG-LATERAL";

        // ── Color indices (explicit on entity, override layer color) ───────────
        private const short C_GRAVITY  = 4;   // cyan
        private const short C_FORCE    = 1;   // red
        private const short C_MANHOLE  = 2;   // yellow
        private const short C_LATERAL  = 6;   // magenta

        private static readonly HttpClient Http;

        static CoralAsBuiltEngine()
        {
            Http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            Http.DefaultRequestHeaders.Add("User-Agent", "AdvancedLandDevTools/1.0 (Civil3D Plugin)");
            Http.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Public API
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Fetches sewer as-built data from the Coral Gables GIS within
        /// <paramref name="radius"/> feet of <paramref name="center"/> and
        /// draws all features into <paramref name="doc"/> model space.
        /// </summary>
        public static CoralAsBuiltSummary FetchAndDraw(
            Document doc, Point3d center, double radius = 1000.0)
        {
            var ed = doc.Editor;
            ed.WriteMessage($"\n  Querying Coral Gables Sewer GIS — {radius:F0} ft radius...");

            try
            {
                // ── HTTP queries (four layers) ─────────────────────────────────
                var gravity  = QueryLayer(L_GRAVITY,  center, radius,
                    "ASSET_ID,DIAMETER,MATERIAL_DESC,AB_DATE,CONDITION,STATUS,UP_MH,DN_MH");
                var force    = QueryLayer(L_FORCE,    center, radius,
                    "ASSET_ID,DIAMETER,MATERIAL_DESC,AB_DATE,CONDITION,STATUS");
                var manholes = QueryLayer(L_MANHOLES, center, radius,
                    "ASSET_NUMBER,ASSET_TYPE,RIM_ELEV,INV_ELEV,CONDITION,STATUS");
                var laterals = QueryLayer(L_LATERALS, center, radius,
                    "ASSET_ID,DIAMETER,MATERIAL_DESC,STATUS");

                int gCount = 0, fCount = 0, mCount = 0, lCount = 0;

                // ── Draw into model space ──────────────────────────────────────
                using var tr = doc.Database.TransactionManager.StartTransaction();

                var bt = (BlockTable)tr.GetObject(
                    doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var lt = (LayerTable)tr.GetObject(
                    doc.Database.LayerTableId, OpenMode.ForWrite);

                EnsureLayer(tr, lt, LYR_GRAVITY, C_GRAVITY);
                EnsureLayer(tr, lt, LYR_FORCE,   C_FORCE);
                EnsureLayer(tr, lt, LYR_MANHOLE, C_MANHOLE);
                EnsureLayer(tr, lt, LYR_LATERAL, C_LATERAL);

                var allIds = new ObjectIdCollection();

                // Gravity mains — cyan polylines
                if (gravity != null)
                    foreach (JToken feat in gravity)
                    {
                        var attrs = feat["attributes"] as JObject;
                        var paths = feat["geometry"]?["paths"] as JArray;
                        if (paths == null) continue;
                        foreach (JToken path in paths)
                        {
                            var id = DrawPolyline(tr, ms, lt, LYR_GRAVITY, C_GRAVITY, path as JArray);
                            if (id != ObjectId.Null)
                            {
                                allIds.Add(id);
                                gCount++;
                                var mid = GetPathMidpoint(path as JArray);
                                if (mid.HasValue)
                                {
                                    string lbl = BuildPipeLabel(attrs, "GRAVITY");
                                    var lid = DrawLabel(tr, ms, lt, LYR_GRAVITY, C_GRAVITY, mid.Value, lbl);
                                    if (lid != ObjectId.Null) allIds.Add(lid);
                                }
                            }
                        }
                    }

                // Force mains — red polylines
                if (force != null)
                    foreach (JToken feat in force)
                    {
                        var attrs = feat["attributes"] as JObject;
                        var paths = feat["geometry"]?["paths"] as JArray;
                        if (paths == null) continue;
                        foreach (JToken path in paths)
                        {
                            var id = DrawPolyline(tr, ms, lt, LYR_FORCE, C_FORCE, path as JArray);
                            if (id != ObjectId.Null)
                            {
                                allIds.Add(id);
                                fCount++;
                                var mid = GetPathMidpoint(path as JArray);
                                if (mid.HasValue)
                                {
                                    string lbl = BuildPipeLabel(attrs, "FORCE");
                                    var lid = DrawLabel(tr, ms, lt, LYR_FORCE, C_FORCE, mid.Value, lbl);
                                    if (lid != ObjectId.Null) allIds.Add(lid);
                                }
                            }
                        }
                    }

                // Laterals — magenta polylines
                if (laterals != null)
                    foreach (JToken feat in laterals)
                    {
                        var attrs = feat["attributes"] as JObject;
                        var paths = feat["geometry"]?["paths"] as JArray;
                        if (paths == null) continue;
                        foreach (JToken path in paths)
                        {
                            var id = DrawPolyline(tr, ms, lt, LYR_LATERAL, C_LATERAL, path as JArray);
                            if (id != ObjectId.Null)
                            {
                                allIds.Add(id);
                                lCount++;
                                var mid = GetPathMidpoint(path as JArray);
                                if (mid.HasValue)
                                {
                                    string lbl = BuildPipeLabel(attrs, "");
                                    var lid = DrawLabel(tr, ms, lt, LYR_LATERAL, C_LATERAL, mid.Value, lbl);
                                    if (lid != ObjectId.Null) allIds.Add(lid);
                                }
                            }
                        }
                    }

                // Manholes — yellow circles (2 ft radius)
                if (manholes != null)
                    foreach (JToken feat in manholes)
                    {
                        var geom = feat["geometry"] as JObject;
                        if (geom?["x"] == null || geom["y"] == null) continue;
                        double mx = geom["x"]!.Value<double>();
                        double my = geom["y"]!.Value<double>();
                        var id = DrawCircle(tr, ms, lt, LYR_MANHOLE, C_MANHOLE, mx, my, 2.0);
                        if (id != ObjectId.Null)
                        {
                            allIds.Add(id);
                            mCount++;
                            var mattrs = feat["attributes"] as JObject;
                            string assetNum = mattrs?["ASSET_NUMBER"]?.ToString() ?? "";
                            string rimStr   = mattrs?["RIM_ELEV"]?.ToString() ?? "";
                            string invStr   = mattrs?["INV_ELEV"]?.ToString() ?? "";
                            double rim = double.TryParse(rimStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double r) ? r : double.NaN;
                            double inv = double.TryParse(invStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double i) ? i : double.NaN;

                            if (!string.IsNullOrEmpty(assetNum))
                            {
                                var l1 = DrawLabel(tr, ms, lt, LYR_MANHOLE, C_MANHOLE, new Point3d(mx, my + 3.5, 0), $"MH:{assetNum}");
                                if (l1 != ObjectId.Null) allIds.Add(l1);
                            }
                            if (!double.IsNaN(rim))
                            {
                                var l2 = DrawLabel(tr, ms, lt, LYR_MANHOLE, C_MANHOLE, new Point3d(mx, my + 1.75, 0), $"RIM:{rim:F2}");
                                if (l2 != ObjectId.Null) allIds.Add(l2);
                            }
                            if (!double.IsNaN(inv))
                            {
                                var l3 = DrawLabel(tr, ms, lt, LYR_MANHOLE, C_MANHOLE, new Point3d(mx, my, 0), $"INV:{inv:F2}");
                                if (l3 != ObjectId.Null) allIds.Add(l3);
                            }
                        }
                    }

                // Group all drawn entities under one named group.
                // Do NOT use 'using var' — the transaction owns the object after
                // AddNewlyCreatedDBObject; disposing the wrapper before Commit()
                // can silently break the group membership.
                if (allIds.Count > 0)
                {
                    var groupDict = (DBDictionary)tr.GetObject(
                        doc.Database.GroupDictionaryId, OpenMode.ForWrite);
                    string gName = "ALDT_CGASBUILT_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var group = new Group(gName, true);   // transaction takes ownership below
                    groupDict.SetAt(gName, group);
                    tr.AddNewlyCreatedDBObject(group, true);
                    foreach (ObjectId id in allIds)
                        group.Append(id);
                }

                tr.Commit();
                return new CoralAsBuiltSummary(true, null, gCount, fCount, mCount, lCount);
            }
            catch (Exception ex)
            {
                return new CoralAsBuiltSummary(false, ex.Message, 0, 0, 0, 0);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Private helpers — HTTP
        // ═════════════════════════════════════════════════════════════════════

        private static JArray? QueryLayer(
            int layerId, Point3d center, double radius, string outFields)
        {
            string url =
                $"{BASE}/{layerId}/query" +
                $"?geometry={center.X:F3},{center.Y:F3}" +
                $"&geometryType=esriGeometryPoint" +
                $"&inSR=2236" +
                $"&spatialRel=esriSpatialRelIntersects" +
                $"&distance={radius:F0}" +
                $"&units=esriSRUnit_Foot" +
                $"&outSR=2236" +
                $"&returnGeometry=true" +
                $"&outFields={outFields}" +
                $"&f=json";

            string json = System.Threading.Tasks.Task.Run(async () =>
            {
                var resp = await Http.GetAsync(url);
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync();
            }).Result;

            var obj = JObject.Parse(json);

            // ArcGIS can return HTTP 200 with an error body — handle it.
            if (obj["error"] != null)
                return null;

            return obj["features"] as JArray;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Private helpers — AutoCAD drawing
        // ═════════════════════════════════════════════════════════════════════

        private static void EnsureLayer(
            Transaction tr, LayerTable lt, string name, short colorIdx)
        {
            if (lt.Has(name)) return;
            var lr = new LayerTableRecord
            {
                Name  = name,
                Color = Color.FromColorIndex(ColorMethod.ByAci, colorIdx)
            };
            lt.Add(lr);
            tr.AddNewlyCreatedDBObject(lr, true);
        }

        private static ObjectId DrawPolyline(
            Transaction tr, BlockTableRecord ms, LayerTable lt,
            string layerName, short colorIdx, JArray? path)
        {
            if (path == null || path.Count < 2) return ObjectId.Null;

            var pl = new Polyline();
            int vtx = 0;
            foreach (JToken pt in path)
            {
                if (pt is not JArray coords || coords.Count < 2) continue;
                pl.AddVertexAt(vtx++,
                    new Point2d(coords[0].Value<double>(), coords[1].Value<double>()),
                    0, 0, 0);
            }
            if (vtx < 2) { pl.Dispose(); return ObjectId.Null; }

            pl.LayerId    = lt[layerName];
            pl.ColorIndex = colorIdx;
            ms.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
            return pl.ObjectId;
        }

        private static ObjectId DrawCircle(
            Transaction tr, BlockTableRecord ms, LayerTable lt,
            string layerName, short colorIdx,
            double x, double y, double radius)
        {
            var circle = new Circle(new Point3d(x, y, 0), Vector3d.ZAxis, radius);
            circle.LayerId    = lt[layerName];
            circle.ColorIndex = colorIdx;
            ms.AppendEntity(circle);
            tr.AddNewlyCreatedDBObject(circle, true);
            return circle.ObjectId;
        }

        /// <summary>
        /// Returns the coordinate of the middle vertex of the path array.
        /// Returns null if fewer than 1 point.
        /// </summary>
        private static Point3d? GetPathMidpoint(JArray? path)
        {
            if (path == null || path.Count < 1) return null;
            int midIdx = path.Count / 2;
            var pt = path[midIdx] as JArray;
            if (pt == null || pt.Count < 2) return null;
            return new Point3d(pt[0].Value<double>(), pt[1].Value<double>(), 0);
        }

        /// <summary>
        /// Builds a pipe label string: {DIAMETER}" {MATERIAL_DESC} {suffix}, trimmed.
        /// Returns empty string if attrs is null.
        /// </summary>
        private static string BuildPipeLabel(JObject? attrs, string suffix)
        {
            if (attrs == null) return string.Empty;
            string diameter = attrs["DIAMETER"]?.ToString() ?? "";
            string material = attrs["MATERIAL_DESC"]?.ToString() ?? "";

            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(diameter))
                parts.Add($"{diameter}\"");
            if (!string.IsNullOrWhiteSpace(material))
                parts.Add(material);
            if (!string.IsNullOrWhiteSpace(suffix))
                parts.Add(suffix);

            return string.Join(" ", parts).Trim();
        }

        /// <summary>
        /// Creates a centred DBText entity, appends it to model space, and returns its ObjectId.
        /// Returns ObjectId.Null if text is null or whitespace.
        /// </summary>
        private static ObjectId DrawLabel(
            Transaction tr, BlockTableRecord ms, LayerTable lt,
            string layerName, short colorIdx,
            Point3d pos, string text, double height = 2.0)
        {
            if (string.IsNullOrWhiteSpace(text)) return ObjectId.Null;

            var dbt = new DBText
            {
                Position       = pos,
                TextString     = text,
                Height         = height,
                LayerId        = lt[layerName],
                ColorIndex     = colorIdx,
                HorizontalMode = TextHorizontalMode.TextCenter,
            };
            // AlignmentPoint must be set after HorizontalMode for centred text.
            dbt.AlignmentPoint = pos;

            ms.AppendEntity(dbt);
            tr.AddNewlyCreatedDBObject(dbt, true);
            return dbt.ObjectId;
        }
    }
}
