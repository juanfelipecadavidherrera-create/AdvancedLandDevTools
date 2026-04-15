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
using AcApp    = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AdvancedLandDevTools.Commands.LLabelGenCommand))]

namespace AdvancedLandDevTools.Commands
{
    public class LLabelGenCommand
    {
        // ─────────────────────────────────────────────────────────────────────
        //  Resolved profile view info — works for both native and XREF PVs.
        // ─────────────────────────────────────────────────────────────────────
        private sealed class PvContext
        {
            public string   Name                  = "";
            public ObjectId NativeId;                              // Null when XREF
            public Extents3d ExtentsHostWCS;                       // always valid, in host WCS
            public bool     IsXref;
            public Database? XrefDatabase;                         // non-null when XREF
            public Matrix3d XrefToHost             = Matrix3d.Identity;
        }

        // ─────────────────────────────────────────────────────────────────────
        [CommandMethod("LLABELGEN", CommandFlags.Modal)]
        public void Execute()
        {
            try
            {
                if (!Engine.LicenseManager.EnsureLicensed()) return;

                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                Editor   ed = doc.Editor;
                Database db = doc.Database;

                ed.WriteMessage("\n═══════════════════════════════════════════════════════════");
                ed.WriteMessage("\n  Advanced Land Development Tools  |  Label Generator");
                ed.WriteMessage("\n  Places station-elevation labels at crossing pipe inverts.");
                ed.WriteMessage("\n  Works with native and XREF profile views.");
                ed.WriteMessage("\n═══════════════════════════════════════════════════════════");

                // ── Step 1: Select the profile view (native or XREF) ──────────
                var peo = new PromptEntityOptions(
                    "\n  Click anywhere inside a profile view: ");
                peo.AllowNone              = false;
                peo.AllowObjectOnLockedLayer = true;

                var per = ed.GetEntity(peo);
                if (per.Status != PromptStatus.OK)
                { ed.WriteMessage("\n  Cancelled.\n"); return; }

                var ctx = DetectProfileView(per, db, ed);
                if (ctx == null)
                {
                    ed.WriteMessage(
                        "\n  ❌ No profile view found at that point." +
                        "\n  Click directly on the profile view grid or border.\n");
                    return;
                }
                ed.WriteMessage($"\n  Profile View: '{ctx.Name}'{(ctx.IsXref ? " [XREF]" : "")}");

                // ── Step 2: Find crossing pipe proxies ────────────────────────
                List<CrossingLabelPoint> crossings;
                if (!ctx.IsXref)
                    crossings = LLabelGenEngine.FindCrossingPoints(ctx.NativeId, db);
                else
                    crossings = LLabelGenEngine.FindCrossingPointsInXref(
                        ctx.XrefDatabase!, ctx.XrefToHost, ctx.ExtentsHostWCS);

                ed.WriteMessage($"\n  Crossing pipe proxies found: {crossings.Count}");

                if (crossings.Count == 0)
                {
                    ed.WriteMessage(
                        "\n  ⚠ No crossing pipe parts found in this profile view." +
                        "\n  Make sure pipes are drawn into the profile view" +
                        " (PIPEMAGIC / ADDNETWORKPARTSTOPROF).\n");
                    return;
                }

                foreach (var cp in crossings)
                    ed.WriteMessage(
                        $"\n    Sta {cp.Station:F2}  Elev {cp.Elevation:F3}" +
                        $"  WCS ({cp.DrawingX:F2}, {cp.DrawingY:F2})");

                // ── Step 3: Collect label styles ──────────────────────────────
                var labelStyles  = new List<StyleItem>();
                var markerStyles = new List<StyleItem>();

                using (var tx = db.TransactionManager.StartTransaction())
                {
                    try
                    {
                        var civDoc = CivilApp.CivilDocument.GetCivilDocument(db);

                        try
                        {
                            var seStyles = civDoc.Styles.LabelStyles
                                                .ProfileViewLabelStyles
                                                .StationElevationLabelStyles;
                            CollectLabelStyles(seStyles, labelStyles, tx);
                        }
                        catch (System.Exception ex)
                        {
                            ed.WriteMessage(
                                $"\n  ⚠ Could not read StationElevationLabelStyles: {ex.Message}");
                        }

                        foreach (ObjectId id in civDoc.Styles.MarkerStyles)
                        {
                            try
                            {
                                var ms = tx.GetObject(id, OpenMode.ForRead)
                                         as CivilDB.Styles.MarkerStyle;
                                if (ms != null)
                                    markerStyles.Add(new StyleItem { Name = ms.Name, Id = id });
                            }
                            catch { }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        ed.WriteMessage($"\n  ⚠ Style scan warning: {ex.Message}");
                    }
                    tx.Abort();
                }

                ed.WriteMessage(
                    $"\n  Found: {labelStyles.Count} label style(s), " +
                    $"{markerStyles.Count} marker style(s)");

                if (labelStyles.Count == 0)
                {
                    ed.WriteMessage(
                        "\n  ❌ No Station Elevation Label Styles found in the drawing." +
                        "\n  Settings > Profile View > Label Styles > Station Elevation\n");
                    return;
                }

                markerStyles.Insert(0, new StyleItem { Name = "(None)", Id = ObjectId.Null });

                // ── Step 4: Show dialog ───────────────────────────────────────
                var dlg = new LLabelGenDialog(labelStyles, markerStyles);
                if (AcApp.ShowModalWindow(dlg) != true)
                { ed.WriteMessage("\n  Cancelled.\n"); return; }

                // ── Step 5: Queue label placements ────────────────────────────
                ed.WriteMessage(
                    $"\n  Label style selected. Queuing {crossings.Count} label(s)...");

                int queued = LLabelGenEngine.QueueLabelJobs(
                    ctx.ExtentsHostWCS, crossings,
                    dlg.SelectedLabelStyleId, dlg.SelectedMarkerStyleId,
                    db, doc);

                ed.WriteMessage(
                    $"\n  {queued} label job(s) queued." +
                    "\n  Labels will be placed after this command exits." +
                    "\n═══════════════════════════════════════════════════════════\n");
            }
            catch (System.Exception ex)
            {
                AcApp.DocumentManager.MdiActiveDocument
                    ?.Editor.WriteMessage($"\n[ALDT ERROR] LLABELGEN: {ex.Message}\n");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Find the profile view the user clicked — works for native drawings
        //  AND for drawings where the profile view is inside an XREF.
        //
        //  Strategy:
        //   1. Direct cast (user clicked the PV border entity itself).
        //   2. Scan current DB model space by pick point (RrNetworkCheck pattern).
        //   3. Iterate block references; for each XREF, open its DB, transform
        //      pick point to local space, search that DB for a ProfileView that
        //      contains the point.
        // ─────────────────────────────────────────────────────────────────────
        private static PvContext? DetectProfileView(
            PromptEntityResult per, Database db, Editor ed)
        {
            Point3d pickPt = per.PickedPoint;

            // ── Try 1 & 2: native DB ────────────────────────────────────────
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var ent = tx.GetObject(per.ObjectId, OpenMode.ForRead);
                var pv  = ent as CivilDB.ProfileView
                          ?? FindProfileViewAtPoint(pickPt, tx, db);

                if (pv != null)
                {
                    var ctx = new PvContext
                    {
                        Name   = pv.Name,
                        NativeId = pv.ObjectId,
                        ExtentsHostWCS = GetExtents(pv),
                        IsXref = false
                    };
                    tx.Abort();
                    return ctx;
                }
                tx.Abort();
            }

            // ── Try 3: search XREF databases ────────────────────────────────
            return FindProfileViewInXrefs(pickPt, db, ed);
        }

        private static PvContext? FindProfileViewInXrefs(
            Point3d pickPt, Database db, Editor ed)
        {
            try
            {
                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var ms = tx.GetObject(db.CurrentSpaceId, OpenMode.ForRead)
                             as BlockTableRecord;
                    if (ms == null) { tx.Abort(); return null; }

                    foreach (ObjectId brId in ms)
                    {
                        BlockReference br;
                        try { br = tx.GetObject(brId, OpenMode.ForRead) as BlockReference; }
                        catch { continue; }
                        if (br == null) continue;

                        BlockTableRecord btrDef;
                        try
                        {
                            btrDef = tx.GetObject(
                                br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                        }
                        catch { continue; }
                        if (btrDef == null || !btrDef.IsFromExternalReference) continue;

                        Database xDb;
                        try { xDb = btrDef.GetXrefDatabase(false); }
                        catch { continue; }
                        if (xDb == null) continue;

                        Matrix3d xform    = br.BlockTransform;
                        Matrix3d invXform = xform.Inverse();
                        Point3d  localPt  = pickPt.TransformBy(invXform);

                        var found = SearchDatabaseForProfileView(xDb, localPt);
                        if (found != null)
                        {
                            tx.Abort();
                            // Transform PV extents to host WCS
                            var hostExt = TransformExtents(found.Value.localExt, xform);
                            return new PvContext
                            {
                                Name               = found.Value.name,
                                NativeId           = ObjectId.Null,
                                ExtentsHostWCS     = hostExt,
                                IsXref             = true,
                                XrefDatabase       = xDb,
                                XrefToHost         = xform
                            };
                        }
                    }
                    tx.Abort();
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Opens a separate transaction on xDb, searches model space for a
        /// ProfileView whose grid contains localPt.
        /// Returns (name, localExtents) if found, or null.
        /// </summary>
        private static (string name, Extents3d localExt)? SearchDatabaseForProfileView(
            Database xDb, Point3d localPt)
        {
            try
            {
                using (var xTx = xDb.TransactionManager.StartTransaction())
                {
                    var xMs = xTx.GetObject(xDb.CurrentSpaceId, OpenMode.ForRead)
                              as BlockTableRecord;
                    if (xMs == null) { xTx.Abort(); return null; }

                    foreach (ObjectId xId in xMs)
                    {
                        CivilDB.ProfileView xPv;
                        try { xPv = xTx.GetObject(xId, OpenMode.ForRead) as CivilDB.ProfileView; }
                        catch { continue; }
                        if (xPv == null) continue;

                        double sta = 0, elev = 0;
                        try
                        {
                            if (xPv.FindStationAndElevationAtXY(
                                    localPt.X, localPt.Y, ref sta, ref elev))
                            {
                                string    name = xPv.Name;
                                Extents3d ext  = GetExtents(xPv);
                                xTx.Abort();
                                return (name, ext);
                            }
                        }
                        catch { }
                    }
                    xTx.Abort();
                }
            }
            catch { }
            return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Scan current DB model space for a PV containing the pick point.
        // ─────────────────────────────────────────────────────────────────────
        private static CivilDB.ProfileView? FindProfileViewAtPoint(
            Point3d pickPt, Transaction tx, Database db)
        {
            var pvClass = RXObject.GetClass(typeof(CivilDB.ProfileView));
            var bt      = tx.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            var ms      = tx.GetObject(
                bt![BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;

            foreach (ObjectId id in ms!)
            {
                if (!id.ObjectClass.IsDerivedFrom(pvClass)) continue;
                try
                {
                    var pv = tx.GetObject(id, OpenMode.ForRead) as CivilDB.ProfileView;
                    if (pv == null) continue;
                    double sta = 0, elev = 0;
                    if (pv.FindStationAndElevationAtXY(pickPt.X, pickPt.Y, ref sta, ref elev))
                        return pv;
                }
                catch { }
            }
            return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────────
        private static Extents3d GetExtents(CivilDB.ProfileView pv)
        {
            try { return ((Entity)pv).GeometricExtents; }
            catch { return new Extents3d(); }
        }

        /// <summary>
        /// Transforms a bounding box through a Matrix3d, building the correct
        /// axis-aligned result by transforming all four corners.
        /// </summary>
        private static Extents3d TransformExtents(Extents3d local, Matrix3d xform)
        {
            var ext = new Extents3d();
            ext.AddPoint(local.MinPoint.TransformBy(xform));
            ext.AddPoint(local.MaxPoint.TransformBy(xform));
            ext.AddPoint(
                new Point3d(local.MinPoint.X, local.MaxPoint.Y, 0).TransformBy(xform));
            ext.AddPoint(
                new Point3d(local.MaxPoint.X, local.MinPoint.Y, 0).TransformBy(xform));
            return ext;
        }

        private static void CollectLabelStyles(
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
