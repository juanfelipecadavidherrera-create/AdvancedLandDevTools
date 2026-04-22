// Advanced Land Development Tools
// Copyright © Juan Felipe Cadavid — All Rights Reserved
// Unauthorized copying or redistribution is prohibited.

using System;
using System.Collections.Generic;
using AdvancedLandDevTools.Models;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivilDB = Autodesk.Civil.DatabaseServices;

namespace AdvancedLandDevTools.Engine
{
    public enum LinkedEntityKind { Unknown, Pipe, Structure, Alignment, Profile, Surface, Generic }

    public class PropertyOption
    {
        public string Name  { get; set; } = "";
        public string Value { get; set; } = "";
    }

    public static class TableDrawerEngine
    {
        private const string DefaultLayer = "ALDT-TABLE";
        private const short  DefaultColor = 3; // green

        // ─────────────────────────────────────────────────────────────────────
        //  Property extraction  (strongly-typed Civil 3D casts)
        //
        //  CRITICAL: Civil 3D managed objects are wrappers around native C++.
        //  Both `dynamic` dispatch and `PropertyInfo.GetValue()` cause native
        //  access violations (uncatchable) on these objects.  The ONLY safe
        //  approach is strongly-typed `is`-casts — the same pattern used in
        //  PressCountEngine, PipeAlignmentIntersector, RrNetworkCheckCommand.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Inspects <paramref name="id"/> and returns its entity kind plus all
        /// extractable property name/value pairs for cell linking.
        /// Uses strongly-typed <c>is</c>-casts to Civil 3D types — never
        /// <c>dynamic</c> or reflection — to avoid native crash risk.
        /// </summary>
        public static (LinkedEntityKind kind, List<PropertyOption> options)
            ExtractProperties(ObjectId id, Database db)
        {
            var opts = new List<PropertyOption>();
            var kind = LinkedEntityKind.Unknown;

            using var tr = db.TransactionManager.StartTransaction();
            try
            {
                var obj = tr.GetObject(id, OpenMode.ForRead);

                // ── Gravity pipe ──────────────────────────────────────────────
                if (obj is CivilDB.Pipe gp)
                {
                    kind = LinkedEntityKind.Pipe;
                    Try(() => opts.Add(P("Diameter",    gp.InnerDiameterOrWidth.ToString("F3"))));
                    Try(() => opts.Add(P("Length",      gp.Length2D.ToString("F3"))));
                    Try(() => opts.Add(P("StartInvert", gp.StartPoint.Z.ToString("F3"))));
                    Try(() => opts.Add(P("EndInvert",   gp.EndPoint.Z.ToString("F3"))));
                    Try(() => opts.Add(P("Slope",       gp.Slope.ToString("F4"))));
                    Try(() => opts.Add(P("Network",     gp.NetworkName)));
                    Try(() => { if (!gp.StartStructureId.IsNull) { var s = (CivilDB.Structure)tr.GetObject(gp.StartStructureId, OpenMode.ForRead); opts.Add(P("UpMH", s.Name)); } });
                    Try(() => { if (!gp.EndStructureId.IsNull)   { var s = (CivilDB.Structure)tr.GetObject(gp.EndStructureId,   OpenMode.ForRead); opts.Add(P("DnMH", s.Name)); } });
                }
                // ── Pressure pipe ─────────────────────────────────────────────
                else if (obj is CivilDB.PressurePipe pp)
                {
                    kind = LinkedEntityKind.Pipe;
                    Try(() => opts.Add(P("StartInvert", pp.StartPoint.Z.ToString("F3"))));
                    Try(() => opts.Add(P("EndInvert",   pp.EndPoint.Z.ToString("F3"))));
                }
                // ── Structure ─────────────────────────────────────────────────
                else if (obj is CivilDB.Structure st)
                {
                    kind = LinkedEntityKind.Structure;
                    Try(() => opts.Add(P("Name",          st.Name)));
                    Try(() => opts.Add(P("RimElevation",  st.RimElevation.ToString("F3"))));
                    Try(() => opts.Add(P("SumpElevation", st.SumpElevation.ToString("F3"))));
                    Try(() => opts.Add(P("InnerLength",   st.InnerLength.ToString("F3"))));
                    Try(() => opts.Add(P("Network",       st.NetworkName)));
                }
                // ── Alignment ─────────────────────────────────────────────────
                else if (obj is CivilDB.Alignment al)
                {
                    kind = LinkedEntityKind.Alignment;
                    Try(() => opts.Add(P("Name",         al.Name)));
                    Try(() => opts.Add(P("Length",        al.Length.ToString("F3"))));
                    Try(() => opts.Add(P("StartStation",  al.StartingStation.ToString("F3"))));
                    Try(() => opts.Add(P("EndStation",    al.EndingStation.ToString("F3"))));
                    Try(() => opts.Add(P("Description",   al.Description)));
                }
                // ── Profile ───────────────────────────────────────────────────
                else if (obj is CivilDB.Profile pr)
                {
                    kind = LinkedEntityKind.Profile;
                    Try(() => opts.Add(P("Name",        pr.Name)));
                    Try(() => opts.Add(P("Length",       pr.Length.ToString("F3"))));
                    Try(() => opts.Add(P("Description",  pr.Description)));
                }
                // ── Surface ───────────────────────────────────────────────────
                else if (obj is CivilDB.TinSurface ts)
                {
                    kind = LinkedEntityKind.Surface;
                    Try(() => opts.Add(P("Name",        ts.Name)));
                    Try(() => opts.Add(P("Description",  ts.Description)));
                }
                else if (obj is CivilDB.Surface sf)
                {
                    kind = LinkedEntityKind.Surface;
                    Try(() => opts.Add(P("Name",        sf.Name)));
                    Try(() => opts.Add(P("Description",  sf.Description)));
                }
                else
                {
                    kind = LinkedEntityKind.Generic;
                }

                // ── Generic AutoCAD properties (always appended) ──────────────
                if (obj is Entity ent)
                {
                    opts.Add(P("Layer",    ent.Layer));
                    opts.Add(P("Linetype", ent.Linetype));
                    opts.Add(P("Handle",   ent.Handle.ToString()));
                    if (obj is Curve curve)
                    {
                        try
                        {
                            opts.Add(P("Length",
                                curve.GetDistanceAtParameter(curve.EndParam).ToString("F3")));
                        }
                        catch { }
                    }
                    if (obj is DBText dbt)
                        opts.Add(P("Text", dbt.TextString));
                    if (obj is MText mt)
                        opts.Add(P("Text", mt.Contents));
                }

                // Remove empty-value entries
                opts.RemoveAll(o => string.IsNullOrWhiteSpace(o.Value));
            }
            catch { }
            finally { tr.Commit(); }

            return (kind, opts);
        }

        /// <summary>
        /// Returns the current value of a single named property for the given entity.
        /// Returns empty string if not found.
        /// </summary>
        public static string GetPropertyValue(ObjectId id, string propName, Database db)
        {
            var (_, opts) = ExtractProperties(id, db);
            return opts.Find(o => o.Name == propName)?.Value ?? "";
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Draw table to model space
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Draws <paramref name="td"/> into model space at <paramref name="insertPt"/>
        /// (top-left corner) and returns the handle registry for auto-update.
        /// </summary>
        public static DrawnTable DrawTable(
            Document doc, TableDefinition td, Point3d insertPt,
            string layer = DefaultLayer)
        {
            var result = new DrawnTable { TableId = td.Id };

            using var tr = doc.Database.TransactionManager.StartTransaction();
            var bt  = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
            var ms  = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            var lt  = (LayerTable)tr.GetObject(doc.Database.LayerTableId, OpenMode.ForRead);
            var tst = (TextStyleTable)tr.GetObject(doc.Database.TextStyleTableId, OpenMode.ForRead);

            EnsureLayer(tr, lt, layer, DefaultColor);

            // ── Resolve text style ────────────────────────────────────────────
            ObjectId textStyleId = doc.Database.Textstyle; // current TEXTSTYLE default
            string wantedStyle = td.TextStyleName ?? "Standard";
            if (tst.Has(wantedStyle))
                textStyleId = tst[wantedStyle];

            double cellTextHeight = td.TextHeight > 0 ? td.TextHeight : 2.5;

            double x0 = insertPt.X;
            double y0 = insertPt.Y;   // top edge

            // Column left-edge X positions
            var colX = new double[td.Cols + 1];
            colX[0] = x0;
            for (int c = 0; c < td.Cols; c++)
                colX[c + 1] = colX[c] + SafeDim(td.ColWidths, c, 20.0);

            // Row top-edge Y positions (descending — AutoCAD Y grows up)
            var rowY = new double[td.Rows + 1];
            rowY[0] = y0;
            for (int r = 0; r < td.Rows; r++)
                rowY[r + 1] = rowY[r] - SafeDim(td.RowHeights, r, 8.0);

            // ── Outer border ─────────────────────────────────────────────────
            AddLine(tr, ms, lt, layer, DefaultColor,
                new Point3d(colX[0],        rowY[0],        0),
                new Point3d(colX[td.Cols],  rowY[0],        0), result);
            AddLine(tr, ms, lt, layer, DefaultColor,
                new Point3d(colX[0],        rowY[td.Rows],  0),
                new Point3d(colX[td.Cols],  rowY[td.Rows],  0), result);
            AddLine(tr, ms, lt, layer, DefaultColor,
                new Point3d(colX[0],        rowY[0],        0),
                new Point3d(colX[0],        rowY[td.Rows],  0), result);
            AddLine(tr, ms, lt, layer, DefaultColor,
                new Point3d(colX[td.Cols],  rowY[0],        0),
                new Point3d(colX[td.Cols],  rowY[td.Rows],  0), result);

            // ── Interior horizontal lines ─────────────────────────────────────
            for (int r = 1; r < td.Rows; r++)
                AddLine(tr, ms, lt, layer, DefaultColor,
                    new Point3d(colX[0],       rowY[r], 0),
                    new Point3d(colX[td.Cols], rowY[r], 0), result);

            // ── Interior vertical lines ───────────────────────────────────────
            for (int c = 1; c < td.Cols; c++)
                AddLine(tr, ms, lt, layer, DefaultColor,
                    new Point3d(colX[c], rowY[0],       0),
                    new Point3d(colX[c], rowY[td.Rows], 0), result);

            // ── Cell text (MText) ─────────────────────────────────────────────
            const short TextColor = 7; // white

            for (int r = 0; r < td.Rows; r++)
            {
                for (int c = 0; c < td.Cols; c++)
                {
                    var cell = td[r, c];
                    if (cell == null || cell.IsMergedSlave) continue;

                    string txt = cell.DisplayValue;
                    if (string.IsNullOrWhiteSpace(txt)) continue;

                    int spanR = Math.Max(1, Math.Min(cell.RowSpan, td.Rows - r));
                    int spanC = Math.Max(1, Math.Min(cell.ColSpan, td.Cols - c));
                    double cx = (colX[c] + colX[c + spanC]) / 2.0;
                    double cy = (rowY[r] + rowY[r + spanR]) / 2.0;

                    var mt = new MText
                    {
                        Location    = new Point3d(cx, cy, 0),
                        Contents    = txt,
                        TextHeight  = cellTextHeight,
                        TextStyleId = textStyleId,
                        Attachment  = AttachmentPoint.MiddleCenter,
                        LayerId     = lt[layer],
                        ColorIndex  = TextColor
                    };
                    ms.AppendEntity(mt);
                    tr.AddNewlyCreatedDBObject(mt, true);
                    result.CellMTextHandles[$"{r},{c}"] = mt.Handle.ToString();
                }
            }

            tr.Commit();
            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Refresh a single cell's MText in place
        // ─────────────────────────────────────────────────────────────────────

        public static void RefreshCellMText(
            Document doc, DrawnTable drawn, TableDefinition td,
            int row, int col, string newValue)
        {
            string key = $"{row},{col}";
            if (!drawn.CellMTextHandles.TryGetValue(key, out string? handleStr)) return;

            using var tr = doc.Database.TransactionManager.StartTransaction();
            try
            {
                var handle = new Handle(Convert.ToInt64(handleStr, 16));
                if (!doc.Database.TryGetObjectId(handle, out ObjectId id) || id.IsErased)
                {
                    tr.Commit();
                    return;
                }
                var mt = (MText)tr.GetObject(id, OpenMode.ForWrite);
                mt.Contents = newValue;
                tr.Commit();
            }
            catch { tr.Abort(); }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Private helpers
        // ─────────────────────────────────────────────────────────────────────

        private static void EnsureLayer(
            Transaction tr, LayerTable lt, string name, short colorIdx)
        {
            if (lt.Has(name)) return;
            lt.UpgradeOpen();
            var lr = new LayerTableRecord
            {
                Name  = name,
                Color = Color.FromColorIndex(ColorMethod.ByAci, colorIdx)
            };
            lt.Add(lr);
            tr.AddNewlyCreatedDBObject(lr, true);
        }

        private static void AddLine(
            Transaction tr, BlockTableRecord ms, LayerTable lt,
            string layer, short colorIdx,
            Point3d p1, Point3d p2, DrawnTable result)
        {
            var line = new Line(p1, p2)
            {
                LayerId    = lt[layer],
                ColorIndex = colorIdx
            };
            ms.AppendEntity(line);
            tr.AddNewlyCreatedDBObject(line, true);
            result.LineHandles.Add(line.Handle.ToString());
        }

        private static double SafeDim(List<double> list, int idx, double fallback)
            => idx < list.Count && list[idx] > 0 ? list[idx] : fallback;

        private static PropertyOption P(string name, string value)
            => new PropertyOption { Name = name, Value = value };

        /// <summary>
        /// Execute an action, swallowing any exception.
        /// Used to wrap individual property reads on Civil 3D objects so
        /// one failed property does not prevent reading the others.
        /// </summary>
        private static void Try(Action action)
        {
            try { action(); }
            catch { }
        }
    }
}
