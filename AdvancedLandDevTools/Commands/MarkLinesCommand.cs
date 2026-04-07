using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AdvancedLandDevTools.UI;
using CivilDB  = Autodesk.Civil.DatabaseServices;
using CivilApp = Autodesk.Civil.ApplicationServices;
using AcApp    = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AdvancedLandDevTools.Commands
{
    public class MarkLinesCommand
    {
        /// <summary>Crossing record: station + host-drawing layer name.</summary>
        private class CrossingInfo
        {
            public double Station;
            public string LayerName = "";
        }

        [CommandMethod("MARKLINES", CommandFlags.Modal)]
        public void MarkLines()
        {
            try
            {
            if (!Engine.LicenseManager.EnsureLicensed()) return;
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            Editor   ed = doc.Editor;
            Database db = doc.Database;

            ed.WriteMessage("\n");
            ed.WriteMessage("═══════════════════════════════════════════════════════════\n");
            ed.WriteMessage("  Advanced Land Development Tools  |  Mark Lines           \n");
            ed.WriteMessage("═══════════════════════════════════════════════════════════\n");

            // ── Step 1: Show layer picker dialog ──────────────────────────
            var dlg = new MarkLinesDialog(db);
            bool? dlgResult = AcApp.ShowModalWindow(dlg);
            if (dlgResult != true)
            {
                ed.WriteMessage("\n  Command cancelled.\n");
                return;
            }

            var selectedLayers = new HashSet<string>(
                dlg.SelectedLayerNames, StringComparer.OrdinalIgnoreCase);

            ed.WriteMessage($"\n  Layers selected: {selectedLayers.Count}");

            // ── Step 2: Select one or more profile views (Enter to finish) ──
            var pvIds = new List<ObjectId>();
            while (true)
            {
                string prompt = pvIds.Count == 0
                    ? "\n  Select a profile view (Enter when done): "
                    : $"\n  Select another profile view or Enter to finish [{pvIds.Count} selected]: ";

                var peo = new PromptEntityOptions(prompt);
                peo.AllowNone = true;
                peo.AllowObjectOnLockedLayer = true;

                var per = ed.GetEntity(peo);
                if (per.Status == PromptStatus.None || per.Status == PromptStatus.Cancel)
                    break;
                if (per.Status != PromptStatus.OK)
                    break;

                // Resolve to a profile view
                using (var txCheck = db.TransactionManager.StartTransaction())
                {
                    var ent = txCheck.GetObject(per.ObjectId, OpenMode.ForRead);
                    var pv  = ent as CivilDB.ProfileView
                              ?? FindProfileViewAtPoint(per.PickedPoint, txCheck, db);

                    if (pv != null && !pvIds.Contains(pv.ObjectId))
                    {
                        pvIds.Add(pv.ObjectId);
                        ed.WriteMessage($"\n  Added: '{pv.Name}'");
                    }
                    else if (pv == null)
                        ed.WriteMessage("\n  Not a profile view — try again.");
                    else
                        ed.WriteMessage("\n  Already selected.");

                    txCheck.Abort();
                }
            }

            if (pvIds.Count == 0)
            {
                ed.WriteMessage("\n  No profile views selected — cancelled.\n");
                return;
            }

            ed.WriteMessage($"\n  Processing {pvIds.Count} profile view(s)...");

            // ── Step 3: Process all selected profile views ────────────────
            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                try
                {
                    var bt      = tx.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                    var ms      = tx.GetObject(bt![BlockTableRecord.ModelSpace], OpenMode.ForRead)
                                  as BlockTableRecord;
                    var msWrite = tx.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite)
                                  as BlockTableRecord;
                    var ltRead  = tx.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;

                    // Cache crossings + alignment samples keyed by AlignmentId
                    // so multiple profile views on the same alignment only scan entities once.
                    var crossingsCache  = new Dictionary<ObjectId, List<CrossingInfo>>();
                    var alignSampleCache = new Dictionary<ObjectId, List<Point2d>>();

                    int totalDrawn = 0;

                    foreach (ObjectId pvId in pvIds)
                    {
                        var pv = tx.GetObject(pvId, OpenMode.ForRead) as CivilDB.ProfileView;
                        if (pv == null) continue;

                        if (pv.AlignmentId.IsNull)
                        {
                            ed.WriteMessage($"\n  '{pv.Name}': no alignment — skipped.");
                            continue;
                        }

                        var alignment = tx.GetObject(pv.AlignmentId, OpenMode.ForRead)
                                        as CivilDB.Alignment;
                        if (alignment == null)
                        {
                            ed.WriteMessage($"\n  '{pv.Name}': cannot open alignment — skipped.");
                            continue;
                        }

                        // ── Sample alignment (cached per alignment) ───────
                        if (!alignSampleCache.TryGetValue(pv.AlignmentId, out var alignPts))
                        {
                            alignPts = SampleAlignment(alignment);
                            alignSampleCache[pv.AlignmentId] = alignPts;
                        }

                        // ── Find crossings (cached per alignment) ─────────
                        if (!crossingsCache.TryGetValue(pv.AlignmentId, out var crossings))
                        {
                            crossings = new List<CrossingInfo>();

                            // Host drawing entities
                            foreach (ObjectId id in ms!)
                            {
                                try
                                {
                                    var obj = tx.GetObject(id, OpenMode.ForRead) as Entity;
                                    if (obj == null) continue;
                                    if (!selectedLayers.Contains(obj.Layer)) continue;
                                    CollectCrossings(obj, alignment, alignPts,
                                                     obj.Layer, crossings);
                                }
                                catch { }
                            }

                            // XREF entities
                            foreach (ObjectId id in ms)
                            {
                                try
                                {
                                    if (tx.GetObject(id, OpenMode.ForRead) is not BlockReference br)
                                        continue;
                                    var btr = tx.GetObject(br.BlockTableRecord, OpenMode.ForRead)
                                              as BlockTableRecord;
                                    if (btr == null) continue;
                                    if (!btr.IsFromExternalReference && !btr.IsFromOverlayReference)
                                        continue;

                                    Matrix3d xform    = br.BlockTransform;
                                    string   xrefName = btr.Name;

                                    foreach (ObjectId entId in btr)
                                    {
                                        try
                                        {
                                            var obj = tx.GetObject(entId, OpenMode.ForRead) as Entity;
                                            if (obj == null) continue;
                                            string hostLayer = xrefName + "|" + obj.Layer;
                                            if (!selectedLayers.Contains(hostLayer) &&
                                                !selectedLayers.Contains(obj.Layer)) continue;
                                            string useLayer = selectedLayers.Contains(hostLayer)
                                                              ? hostLayer : obj.Layer;
                                            CollectCrossingsXref(obj, alignment, alignPts,
                                                                  xform, useLayer, crossings);
                                        }
                                        catch { }
                                    }
                                }
                                catch { }
                            }

                            crossingsCache[pv.AlignmentId] = crossings;
                            ed.WriteMessage($"\n  '{alignment.Name}': {crossings.Count} crossing(s) found.");
                        }

                        // ── Draw vertical lines in this profile view ──────
                        double pvElevMin = pv.ElevationMin;
                        double pvElevMax = pv.ElevationMax;
                        int drawn = 0;

                        foreach (var cx in crossings)
                        {
                            if (cx.Station < pv.StationStart - 0.5 ||
                                cx.Station > pv.StationEnd   + 0.5) continue;

                            double xBot = 0, yBot = 0, xTop = 0, yTop = 0;
                            if (!pv.FindXYAtStationAndElevation(cx.Station, pvElevMin, ref xBot, ref yBot)) continue;
                            if (!pv.FindXYAtStationAndElevation(cx.Station, pvElevMax, ref xTop, ref yTop)) continue;

                            var vLine = new Line(
                                new Point3d(xBot, yBot, 0),
                                new Point3d(xTop, yTop, 0));

                            if (ltRead!.Has(cx.LayerName))
                                vLine.LayerId = ltRead[cx.LayerName];

                            msWrite!.AppendEntity(vLine);
                            tx.AddNewlyCreatedDBObject(vLine, true);
                            drawn++;
                        }

                        ed.WriteMessage($"\n  '{pv.Name}': {drawn} line(s) drawn.");
                        totalDrawn += drawn;
                    }

                    tx.Commit();
                    ed.WriteMessage($"\n  Total lines drawn: {totalDrawn}");
                    ed.WriteMessage("\n  Mark Lines complete.");
                    ed.WriteMessage("\n═══════════════════════════════════════════════════════════\n");
                }
                catch (System.Exception ex)
                {
                    ed.WriteMessage($"\n  Error: {ex.Message}\n");
                    tx.Abort();
                }
            }
            }
            catch (System.Exception ex)
            {
                var d = AcApp.DocumentManager.MdiActiveDocument;
                d?.Editor.WriteMessage($"\n[ALDT ERROR] MARKLINES: {ex.Message}\n");
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Sample the alignment at 1-ft intervals to build a list of 2D
        //  points that represents its plan geometry.  Used for accurate
        //  line-to-alignment intersection.
        // ═══════════════════════════════════════════════════════════════════
        private static List<Point2d> SampleAlignment(CivilDB.Alignment alignment)
        {
            var pts = new List<Point2d>();
            double start = alignment.StartingStation;
            double end   = alignment.EndingStation;
            double step  = 1.0; // 1-ft sampling

            for (double sta = start; sta <= end; sta += step)
            {
                try
                {
                    double x = 0, y = 0;
                    alignment.PointLocation(sta, 0.0, ref x, ref y);
                    pts.Add(new Point2d(x, y));
                }
                catch { }
            }

            // Always include the last station
            try
            {
                double xe = 0, ye = 0;
                alignment.PointLocation(end, 0.0, ref xe, ref ye);
                if (pts.Count == 0 || pts[^1].GetDistanceTo(new Point2d(xe, ye)) > 0.01)
                    pts.Add(new Point2d(xe, ye));
            }
            catch { }

            return pts;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Find where a 2D segment intersects the alignment polyline.
        //  Returns crossing stations via the alignment's StationOffset.
        // ═══════════════════════════════════════════════════════════════════
        private static void FindSegmentAlignmentIntersections(
            Point2d segA, Point2d segB,
            CivilDB.Alignment alignment,
            List<Point2d> alignPts,
            string layerName,
            List<CrossingInfo> crossings)
        {
            for (int i = 0; i < alignPts.Count - 1; i++)
            {
                Point2d alA = alignPts[i];
                Point2d alB = alignPts[i + 1];

                if (Intersect2D(segA, segB, alA, alB, out Point2d intPt))
                {
                    try
                    {
                        double sta = 0, off = 0;
                        alignment.StationOffset(intPt.X, intPt.Y, ref sta, ref off);

                        crossings.Add(new CrossingInfo
                        {
                            Station   = sta,
                            LayerName = layerName
                        });
                    }
                    catch { }
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  2D segment–segment intersection.
        //  Returns true if the segments actually cross (not just co-linear).
        // ═══════════════════════════════════════════════════════════════════
        private static bool Intersect2D(
            Point2d a1, Point2d a2, Point2d b1, Point2d b2, out Point2d result)
        {
            result = Point2d.Origin;

            double d1x = a2.X - a1.X, d1y = a2.Y - a1.Y;
            double d2x = b2.X - b1.X, d2y = b2.Y - b1.Y;

            double cross = d1x * d2y - d1y * d2x;
            if (Math.Abs(cross) < 1e-10) return false; // parallel

            double dx = b1.X - a1.X, dy = b1.Y - a1.Y;
            double t = (dx * d2y - dy * d2x) / cross;
            double u = (dx * d1y - dy * d1x) / cross;

            if (t < 0 || t > 1 || u < 0 || u > 1) return false;

            result = new Point2d(a1.X + t * d1x, a1.Y + t * d1y);
            return true;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Collect crossing stations from a host-drawing entity.
        // ═══════════════════════════════════════════════════════════════════
        private static void CollectCrossings(
            Entity entity, CivilDB.Alignment alignment,
            List<Point2d> alignPts, string layerName,
            List<CrossingInfo> crossings)
        {
            if (entity is Line line)
            {
                var a = new Point2d(line.StartPoint.X, line.StartPoint.Y);
                var b = new Point2d(line.EndPoint.X,   line.EndPoint.Y);
                FindSegmentAlignmentIntersections(a, b, alignment, alignPts,
                                                  layerName, crossings);
            }
            else if (entity is Polyline pl)
            {
                for (int i = 0; i < pl.NumberOfVertices - 1; i++)
                {
                    var a = new Point2d(pl.GetPoint2dAt(i).X,     pl.GetPoint2dAt(i).Y);
                    var b = new Point2d(pl.GetPoint2dAt(i + 1).X, pl.GetPoint2dAt(i + 1).Y);
                    FindSegmentAlignmentIntersections(a, b, alignment, alignPts,
                                                      layerName, crossings);
                }
                if (pl.Closed && pl.NumberOfVertices > 2)
                {
                    var a = new Point2d(pl.GetPoint2dAt(pl.NumberOfVertices - 1).X,
                                        pl.GetPoint2dAt(pl.NumberOfVertices - 1).Y);
                    var b = new Point2d(pl.GetPoint2dAt(0).X, pl.GetPoint2dAt(0).Y);
                    FindSegmentAlignmentIntersections(a, b, alignment, alignPts,
                                                      layerName, crossings);
                }
            }
            else if (entity is Polyline3d pl3d)
            {
                var tx  = entity.Database.TransactionManager.TopTransaction;
                var pts = new List<Point2d>();
                foreach (ObjectId vId in pl3d)
                {
                    try
                    {
                        var v = tx.GetObject(vId, OpenMode.ForRead) as PolylineVertex3d;
                        if (v != null) pts.Add(new Point2d(v.Position.X, v.Position.Y));
                    }
                    catch { }
                }
                for (int i = 0; i < pts.Count - 1; i++)
                    FindSegmentAlignmentIntersections(pts[i], pts[i + 1], alignment,
                                                      alignPts, layerName, crossings);
            }
            else if (entity is Polyline2d pl2d)
            {
                var tx  = entity.Database.TransactionManager.TopTransaction;
                var pts = new List<Point2d>();
                foreach (ObjectId vId in pl2d)
                {
                    try
                    {
                        var v = tx.GetObject(vId, OpenMode.ForRead) as Vertex2d;
                        if (v != null) pts.Add(new Point2d(v.Position.X, v.Position.Y));
                    }
                    catch { }
                }
                for (int i = 0; i < pts.Count - 1; i++)
                    FindSegmentAlignmentIntersections(pts[i], pts[i + 1], alignment,
                                                      alignPts, layerName, crossings);
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Collect crossings from an XREF entity (transform to WCS).
        // ═══════════════════════════════════════════════════════════════════
        private static void CollectCrossingsXref(
            Entity entity, CivilDB.Alignment alignment,
            List<Point2d> alignPts, Matrix3d xform,
            string layerName, List<CrossingInfo> crossings)
        {
            if (entity is Line line)
            {
                var sp = line.StartPoint.TransformBy(xform);
                var ep = line.EndPoint.TransformBy(xform);
                FindSegmentAlignmentIntersections(
                    new Point2d(sp.X, sp.Y), new Point2d(ep.X, ep.Y),
                    alignment, alignPts, layerName, crossings);
            }
            else if (entity is Polyline pl)
            {
                for (int i = 0; i < pl.NumberOfVertices - 1; i++)
                {
                    var sp = pl.GetPoint3dAt(i).TransformBy(xform);
                    var ep = pl.GetPoint3dAt(i + 1).TransformBy(xform);
                    FindSegmentAlignmentIntersections(
                        new Point2d(sp.X, sp.Y), new Point2d(ep.X, ep.Y),
                        alignment, alignPts, layerName, crossings);
                }
                if (pl.Closed && pl.NumberOfVertices > 2)
                {
                    var sp = pl.GetPoint3dAt(pl.NumberOfVertices - 1).TransformBy(xform);
                    var ep = pl.GetPoint3dAt(0).TransformBy(xform);
                    FindSegmentAlignmentIntersections(
                        new Point2d(sp.X, sp.Y), new Point2d(ep.X, ep.Y),
                        alignment, alignPts, layerName, crossings);
                }
            }
            else if (entity is Polyline3d pl3d)
            {
                var tx  = entity.Database.TransactionManager.TopTransaction;
                var pts = new List<Point2d>();
                foreach (ObjectId vId in pl3d)
                {
                    try
                    {
                        var v = tx.GetObject(vId, OpenMode.ForRead) as PolylineVertex3d;
                        if (v != null)
                        {
                            var wp = v.Position.TransformBy(xform);
                            pts.Add(new Point2d(wp.X, wp.Y));
                        }
                    }
                    catch { }
                }
                for (int i = 0; i < pts.Count - 1; i++)
                    FindSegmentAlignmentIntersections(pts[i], pts[i + 1], alignment,
                                                      alignPts, layerName, crossings);
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Find the profile view containing a WCS point.
        // ═══════════════════════════════════════════════════════════════════
        private static CivilDB.ProfileView? FindProfileViewAtPoint(
            Point3d pickPoint, Transaction tx, Database db)
        {
            RXClass pvClass = RXObject.GetClass(typeof(CivilDB.ProfileView));

            var bt = tx.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            var ms = tx.GetObject(
                     bt![BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;

            foreach (ObjectId id in ms!)
            {
                if (!id.ObjectClass.IsDerivedFrom(pvClass)) continue;
                try
                {
                    var pv = tx.GetObject(id, OpenMode.ForRead) as CivilDB.ProfileView;
                    if (pv == null) continue;

                    double sta = 0, elev = 0;
                    if (pv.FindStationAndElevationAtXY(
                            pickPoint.X, pickPoint.Y, ref sta, ref elev))
                        return pv;
                }
                catch { }
            }
            return null;
        }
    }
}
