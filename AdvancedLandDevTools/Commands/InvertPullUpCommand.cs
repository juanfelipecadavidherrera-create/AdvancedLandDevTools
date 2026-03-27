using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AdvancedLandDevTools.Engine;
using AdvancedLandDevTools.UI;
using CivilDB  = Autodesk.Civil.DatabaseServices;
using CivilApp = Autodesk.Civil.ApplicationServices;
using AcApp   = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AdvancedLandDevTools.Commands.InvertPullUpCommand))]

namespace AdvancedLandDevTools.Commands
{
    public class InvertPullUpCommand
    {
        [CommandMethod("INVERTPULLUP")]
        public void InvertPullUp()
        {
            try
            {
            if (!Engine.LicenseManager.EnsureLicensed()) return;
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            Editor   ed = doc.Editor;
            Database db = doc.Database;

            ed.WriteMessage("\n=== INVERT PULL UP ===");

            // ── Step 1: Collect label & marker styles for dialog ───────────────
            var labelStyles  = new List<StyleItem>();
            var markerStyles = new List<StyleItem>();

            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                try
                {
                    CivilApp.CivilDocument civDoc =
                        CivilApp.CivilDocument.GetCivilDocument(db);

                    var soStyles = civDoc.Styles.LabelStyles
                                         .AlignmentLabelStyles
                                         .StationOffsetLabelStyles;
                    CollectLabelStyles(soStyles, labelStyles, tx);

                    foreach (ObjectId id in civDoc.Styles.MarkerStyles)
                    {
                        try
                        {
                            var ms = tx.GetObject(id, OpenMode.ForRead)
                                     as Autodesk.Civil.DatabaseServices.Styles.MarkerStyle;
                            if (ms != null)
                                markerStyles.Add(new StyleItem { Name = ms.Name, Id = id });
                        }
                        catch { }
                    }
                }
                catch (System.Exception ex)
                {
                    ed.WriteMessage($"\n⚠ Style scan warning: {ex.Message}");
                }
                tx.Abort();
            }

            ed.WriteMessage($"\n  Found: {labelStyles.Count} label style(s), " +
                            $"{markerStyles.Count} marker style(s)");

            markerStyles.Insert(0, new StyleItem { Name = "(None)", Id = ObjectId.Null });

            // ── Step 2: Show dialog ────────────────────────────────────────────
            ObjectId chosenLabelStyleId = ObjectId.Null;

            var dlg = new InvertPullUpDialog(labelStyles, markerStyles);
            bool? dlgResult = AcApp.ShowModalWindow(dlg);
            if (dlgResult != true)
            {
                ed.WriteMessage("\n⚠ Cancelled.");
                return;
            }

            chosenLabelStyleId = dlg.SelectedLabelStyleId;

            // ── Step 3: Select pipe (host or XREF) ───────────────────────────
            var peo = new PromptEntityOptions(
                "\nSelect a pipe (gravity or pressure, host or XREF): ");
            peo.AllowObjectOnLockedLayer = true;

            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\n⚠ No pipe selected. Command cancelled.");
                return;
            }

            // ── Step 4: Resolve pipe geometry ─────────────────────────────────
            InvertPullUpResult result = null;

            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                try
                {
                    var ent = tx.GetObject(per.ObjectId, OpenMode.ForRead);

                    if (ent is CivilDB.Pipe || ent is CivilDB.PressurePipe)
                    {
                        result = InvertPullUpEngine.Calculate(
                            per.ObjectId, per.PickedPoint, tx);
                    }
                    else if (ent is BlockReference br)
                    {
                        result = ResolvePipeFromXref(br, per.PickedPoint, tx, ed);
                    }
                    else
                    {
                        ed.WriteMessage("\n❌ Selected object is not a pipe or XREF.");
                    }
                }
                catch (System.Exception ex)
                {
                    ed.WriteMessage($"\n❌ Error resolving pipe: {ex.Message}");
                }
                tx.Abort();
            }

            if (result == null || !result.Success)
            {
                string errMsg = result?.ErrorMessage ?? "Could not resolve pipe data.";
                ed.WriteMessage($"\n❌ Error: {errMsg}");
                return;
            }

            // ── Step 5: Report pipe inverts ───────────────────────────────────
            ed.WriteMessage($"\n  Pipe      : {result.PipeName} ({result.PipeKind})");
            ed.WriteMessage($"\n  Start inv : {result.StartInvert:F3}'  at ({result.PipeStartWCS.X:F1}, {result.PipeStartWCS.Y:F1})");
            ed.WriteMessage($"\n  End inv   : {result.EndInvert:F3}'  at ({result.PipeEndWCS.X:F1}, {result.PipeEndWCS.Y:F1})");
            ed.WriteMessage($"\n  Clicked   : ({per.PickedPoint.X:F1}, {per.PickedPoint.Y:F1})  →  Interp inv: {result.InvertAtPoint:F3}'");

            // ── Step 6: Queue the label placement ─────────────────────────────
            // Registers a two-level CommandEnded chain:
            //   Level-1  → fires _AeccAddAlignOffLbl fully interactively
            //              (user selects alignment, clicks pipe to place label)
            //   Level-2  → finds new label, interpolates invert from its position,
            //              applies chosen style, injects invert as text override
            string msg = InvertPullUpEngine.QueueLabelCommand(db, result, chosenLabelStyleId);

            ed.WriteMessage($"\n  {msg}");
            ed.WriteMessage("\n=== DONE — Place the label to inject the invert elevation. ===\n");
            }
            catch (System.Exception ex)
            {
                var d = AcApp.DocumentManager.MdiActiveDocument;
                d?.Editor.WriteMessage($"\n[ALDT ERROR] INVERTPULLUP: {ex.Message}\n");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Resolve a pipe from inside an XREF BlockReference.
        //  Finds the pipe whose 2D axis is nearest to the clicked point,
        //  transforms its geometry to WCS, and delegates to CalculateFromGeometry.
        // ─────────────────────────────────────────────────────────────────────
        private InvertPullUpResult ResolvePipeFromXref(
            BlockReference br,
            Point3d        clickedPointWCS,
            Transaction    tx,
            Editor         ed)
        {
            var btr = (BlockTableRecord)tx.GetObject(
                br.BlockTableRecord, OpenMode.ForRead);

            if (!btr.IsFromExternalReference && !btr.IsFromOverlayReference)
            {
                return new InvertPullUpResult
                {
                    ErrorMessage = "Selected block is not an XREF. " +
                                   "Please select a gravity or pressure pipe."
                };
            }

            Matrix3d xformToWcs   = br.BlockTransform;
            Matrix3d xformToBlock = xformToWcs.Inverse();
            Point3d  pickInBlock  = clickedPointWCS.TransformBy(xformToBlock);

            double  bestDist     = double.MaxValue;
            Point3d bestStart    = Point3d.Origin, bestEnd = Point3d.Origin;
            double  bestStartInv = 0, bestEndInv = 0;
            string  bestName = "", bestKind = "";
            bool    found    = false;

            foreach (ObjectId entId in btr)
            {
                DBObject obj;
                try { obj = tx.GetObject(entId, OpenMode.ForRead); }
                catch { continue; }

                Point3d pStart, pEnd;
                double  radius;
                string  name, kind;

                if (obj is CivilDB.Pipe gp)
                {
                    pStart = gp.StartPoint;
                    pEnd   = gp.EndPoint;
                    radius = gp.InnerDiameterOrWidth / 2.0;
                    name   = gp.Name;
                    kind   = "Gravity (XREF)";
                }
                else if (obj is CivilDB.PressurePipe pp)
                {
                    pStart = pp.StartPoint;
                    pEnd   = pp.EndPoint;
                    radius = pp.InnerDiameter / 2.0;
                    name   = pp.Name;
                    kind   = "Pressure (XREF)";
                }
                else continue;

                double dist = DistToSegment2D(pickInBlock, pStart, pEnd);
                if (dist < bestDist)
                {
                    bestDist     = dist;
                    bestStart    = pStart;
                    bestEnd      = pEnd;
                    bestStartInv = pStart.Z - radius;
                    bestEndInv   = pEnd.Z   - radius;
                    bestName     = name;
                    bestKind     = kind;
                    found        = true;
                }
            }

            if (!found)
            {
                return new InvertPullUpResult { ErrorMessage = "No pipes found inside the XREF." };
            }

            ed.WriteMessage($"\n  XREF pipe found: {bestName} (dist={bestDist:F2})");

            Point3d wcsStart   = bestStart.TransformBy(xformToWcs);
            Point3d wcsEnd     = bestEnd.TransformBy(xformToWcs);
            double  wcsStartInv = wcsStart.Z - (bestStart.Z - bestStartInv);
            double  wcsEndInv   = wcsEnd.Z   - (bestEnd.Z   - bestEndInv);

            return InvertPullUpEngine.CalculateFromGeometry(
                wcsStart, wcsEnd,
                wcsStartInv, wcsEndInv,
                bestName, bestKind,
                clickedPointWCS);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  2D distance from point to line segment
        // ─────────────────────────────────────────────────────────────────────
        private static double DistToSegment2D(Point3d pt, Point3d segA, Point3d segB)
        {
            double ax = segA.X, ay = segA.Y;
            double bx = segB.X, by = segB.Y;
            double px = pt.X,   py = pt.Y;

            double dx = bx - ax, dy = by - ay;
            double lenSq = dx * dx + dy * dy;

            if (lenSq < 1e-10)
                return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));

            double t = ((px - ax) * dx + (py - ay) * dy) / lenSq;
            t = Math.Max(0.0, Math.Min(1.0, t));

            double projX = ax + t * dx;
            double projY = ay + t * dy;

            return Math.Sqrt((px - projX) * (px - projX) + (py - projY) * (py - projY));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Collect all label styles from a Civil 3D style collection
        // ─────────────────────────────────────────────────────────────────────
        private void CollectLabelStyles(
            CivilDB.Styles.LabelStyleCollection collection,
            List<StyleItem> list,
            Transaction tx)
        {
            try
            {
                foreach (ObjectId id in collection)
                {
                    try
                    {
                        var st = tx.GetObject(id, OpenMode.ForRead)
                                 as CivilDB.Styles.LabelStyle;
                        if (st != null)
                            list.Add(new StyleItem { Name = st.Name, Id = id });
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
