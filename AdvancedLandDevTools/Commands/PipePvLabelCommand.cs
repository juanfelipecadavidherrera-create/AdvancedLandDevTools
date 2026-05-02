using System;
using System.Collections.Generic;
using System.Linq;
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

[assembly: CommandClass(typeof(AdvancedLandDevTools.Commands.PipePvLabelCommand))]

namespace AdvancedLandDevTools.Commands
{
    /// <summary>
    /// PIPEPVLABEL — Place station-elevation labels at the START and END of each
    /// pressure pipe drawn in a profile view (no fittings, no appurtenances, no
    /// crossings — only along-the-alignment pressure pipes).
    ///
    /// Workflow:
    ///   1. Click anywhere inside a profile view (native or XREF).
    ///   2. Pick which pressure network to label from the auto-detected list of
    ///      networks that have pipes drawn in this PV.
    ///   3. Choose label + marker styles in the same dialog LLABELGEN uses.
    ///   4. Civil 3D's ADDPROFILEVIEWSTAELEVLBL is queued via LISP — clicks the
    ///      PV once, then feeds station/elevation pairs for each endpoint.
    ///
    /// Endpoint elevation = pipe centerline Z + outer radius (crown of pipe).
    /// Endpoints whose station falls outside the PV's station range are clipped.
    /// </summary>
    public class PipePvLabelCommand
    {
        // ─────────────────────────────────────────────────────────────────────
        //  Resolved profile view info — works for both native and XREF PVs.
        //  Same shape as LLabelGenCommand.PvContext.
        // ─────────────────────────────────────────────────────────────────────
        private sealed class PvContext
        {
            public string    Name              = "";
            public ObjectId  NativeId;                        // Null when XREF
            public string    HostHandle        = "";
            public Point3d   PickPoint;
            public Extents3d ExtentsHostWCS;                   // always valid, in host WCS
            public bool      IsXref;
            public Database? XrefDatabase;                     // non-null when XREF
            public Matrix3d  XrefToHost        = Matrix3d.Identity;
        }

        // ─────────────────────────────────────────────────────────────────────
        [CommandMethod("PIPEPVLABEL", CommandFlags.Modal)]
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
                ed.WriteMessage("\n  Advanced Land Development Tools  |  Pressure Pipe PV Labels");
                ed.WriteMessage("\n  Labels start + end station-elevation of each pressure pipe");
                ed.WriteMessage("\n  drawn in the selected profile view (no fittings/crossings).");
                ed.WriteMessage("\n  Works with native and XREF profile views.");
                ed.WriteMessage("\n═══════════════════════════════════════════════════════════");

                // ── Step 1: pick a profile view ──────────────────────────────
                var peo = new PromptEntityOptions(
                    "\n  Click anywhere inside a profile view: ");
                peo.AllowNone               = false;
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

                // ── Step 2: find pressure pipes drawn in this PV ─────────────
                Dictionary<ObjectId, PressureNetworkGroup> netMap;
                if (!ctx.IsXref)
                    netMap = PipePvLabelEngine.FindPressurePipesInPv(ctx.NativeId, db);
                else
                    netMap = PipePvLabelEngine.FindPressurePipesInPvXref(
                        ctx.XrefDatabase!, ctx.Name);

                int totalPipes = netMap.Values.Sum(g => g.PipeIds.Count);
                ed.WriteMessage(
                    $"\n  Pressure networks drawn in this PV: {netMap.Count}  ({totalPipes} pipe(s) total)");

                if (netMap.Count == 0)
                {
                    ed.WriteMessage(
                        "\n  ⚠ No pressure pipes drawn in this profile view." +
                        "\n  Use ADDNETWORKPARTSTOPROF to draw pressure pipes into the PV first.\n");
                    return;
                }

                // ── Step 3: pick which pressure network to label ─────────────
                var netList = netMap.Values
                                    .OrderBy(g => g.NetworkName)
                                    .ToList();

                ed.WriteMessage("\n\n  Pressure networks available:");
                for (int i = 0; i < netList.Count; i++)
                    ed.WriteMessage(
                        $"\n    [{i + 1}] {netList[i].NetworkName}  ({netList[i].PipeIds.Count} pipe(s))");

                var pio = new PromptIntegerOptions(
                    $"\n  Select network to label [1-{netList.Count}]: ");
                pio.LowerLimit = 1;
                pio.UpperLimit = netList.Count;
                pio.AllowNone  = false;

                var pir = ed.GetInteger(pio);
                if (pir.Status != PromptStatus.OK)
                { ed.WriteMessage("\n  Cancelled.\n"); return; }

                var chosenNet = netList[pir.Value - 1];
                ed.WriteMessage($"\n  Using: {chosenNet.NetworkName}");

                // ── Step 4: compute label points (start + end of each pipe) ──
                List<CrossingLabelPoint> labels;
                if (!ctx.IsXref)
                    labels = PipePvLabelEngine.ComputeEndpointLabels(
                        ctx.NativeId, db, chosenNet.PipeIds);
                else
                    labels = PipePvLabelEngine.ComputeEndpointLabelsInXref(
                        ctx.XrefDatabase!, ctx.XrefToHost, ctx.Name, chosenNet.PipeIds);

                ed.WriteMessage($"\n  Endpoints to label (after clipping): {labels.Count}");

                if (labels.Count == 0)
                {
                    ed.WriteMessage(
                        "\n  ⚠ No endpoints fell inside the profile view's station range.\n");
                    return;
                }

                foreach (var p in labels)
                    ed.WriteMessage(
                        $"\n    Sta {LLabelGenEngine.FormatStation(p.Station)}" +
                        $"  CrownElev {p.Elevation:F3}" +
                        $"  Pipe: {p.PipeName}");

                // ── Step 5: collect styles, show dialog (same as LLABELGEN) ──
                var labelStyles  = new List<StyleItem>();
                var markerStyles = new List<StyleItem>();

                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    try
                    {
                        var civDoc = CivilApp.CivilDocument.GetCivilDocument(db);

                        var pvElevStyles = civDoc.Styles.LabelStyles
                                                 .ProfileViewLabelStyles
                                                 .StationElevationLabelStyles;
                        foreach (ObjectId id in pvElevStyles)
                        {
                            try
                            {
                                var st = tx.GetObject(id, OpenMode.ForRead) as CivilDB.Styles.LabelStyle;
                                if (st != null)
                                    labelStyles.Add(new StyleItem { Name = st.Name, Id = id });
                            }
                            catch { }
                        }

                        foreach (ObjectId id in civDoc.Styles.MarkerStyles)
                        {
                            try
                            {
                                var ms = tx.GetObject(id, OpenMode.ForRead) as CivilDB.Styles.MarkerStyle;
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

                markerStyles.Insert(0, new StyleItem { Name = "(None)", Id = ObjectId.Null });

                var dlg = new LLabelGenDialog(labelStyles, markerStyles);
                bool? dlgResult = AcApp.ShowModalWindow(dlg);
                if (dlgResult != true)
                {
                    ed.WriteMessage("\n  Cancelled by user.\n");
                    return;
                }

                ObjectId chosenLabelStyleId  = dlg.SelectedLabelStyleId;
                ObjectId chosenMarkerStyleId = dlg.SelectedMarkerStyleId;

                // ── Step 6: drag offset (same heuristic as LLABELGEN) ────────
                var    pvExt = ctx.ExtentsHostWCS;
                double pvW   = pvExt.MaxPoint.X - pvExt.MinPoint.X;
                double pvH   = pvExt.MaxPoint.Y - pvExt.MinPoint.Y;

                Vector3d dragOffset;
                if (pvW > 1.0 && pvH > 1.0)
                {
                    dragOffset = new Vector3d(pvW * 0.025, pvH * 0.10, 0);
                }
                else
                {
                    double xSpan = labels.Count > 1
                        ? labels.Max(c => c.DrawingX) - labels.Min(c => c.DrawingX)
                        : Math.Abs(labels[0].DrawingX) * 0.05;
                    double ySpan = labels.Count > 1
                        ? labels.Max(c => c.DrawingY) - labels.Min(c => c.DrawingY)
                        : Math.Abs(labels[0].DrawingY) * 0.05;
                    xSpan = Math.Max(xSpan, 5.0);
                    ySpan = Math.Max(ySpan, 2.0);
                    dragOffset = new Vector3d(xSpan * 0.30, ySpan * 0.60, 0);
                }
                ed.WriteMessage(
                    $"\n  DIAG drag: pvW={pvW:F1} pvH={pvH:F1}  offset=({dragOffset.X:F3},{dragOffset.Y:F3})");

                // ── Step 7: queue native ADDPROFILEVIEWSTAELEVLBL via LISP ──
                ed.WriteMessage($"\n\n  Queuing {labels.Count} native label(s)...");

                LLabelGenEngine.QueueLabelCommand(
                    labels, ctx.HostHandle, ctx.PickPoint, doc, ctx.IsXref,
                    chosenLabelStyleId, chosenMarkerStyleId, dragOffset);

                ed.WriteMessage($"\n  ✓ Finished — {labels.Count} label(s) placed.");
                ed.WriteMessage(
                    "\n═══════════════════════════════════════════════════════════\n");
            }
            catch (System.Exception ex)
            {
                AcApp.DocumentManager.MdiActiveDocument
                    ?.Editor.WriteMessage($"\n[ALDT ERROR] PIPEPVLABEL: {ex.Message}\n");
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  Profile-view detection — duplicated from LLabelGenCommand because
        //  those helpers are private there.  Keep this in lockstep with that
        //  file when XREF behaviour changes.
        // ════════════════════════════════════════════════════════════════════
        private static PvContext? DetectProfileView(
            PromptEntityResult per, Database db, Editor ed)
        {
            Point3d pickPt = per.PickedPoint;

            // Native DB first
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var ent = tx.GetObject(per.ObjectId, OpenMode.ForRead);
                var pv  = ent as CivilDB.ProfileView
                          ?? FindProfileViewAtPoint(pickPt, tx, db);

                if (pv != null)
                {
                    var ext = GetExtents(pv);
                    var ctx = new PvContext
                    {
                        Name           = pv.Name,
                        NativeId       = pv.ObjectId,
                        HostHandle     = pv.Handle.ToString(),
                        PickPoint      = pickPt,
                        ExtentsHostWCS = ext,
                        IsXref         = false
                    };
                    tx.Abort();
                    return ctx;
                }
                tx.Abort();
            }

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
                            var hostExt = TransformExtents(found.Value.localExt, xform);
                            string brHandle = br.Handle.ToString();
                            tx.Abort();
                            return new PvContext
                            {
                                Name           = found.Value.name,
                                NativeId       = ObjectId.Null,
                                HostHandle     = brHandle,
                                PickPoint      = pickPt,
                                ExtentsHostWCS = hostExt,
                                IsXref         = true,
                                XrefDatabase   = xDb,
                                XrefToHost     = xform
                            };
                        }
                    }
                    tx.Abort();
                }
            }
            catch { }

            return null;
        }

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

        private static Extents3d GetExtents(CivilDB.ProfileView pv)
        {
            try { return ((Entity)pv).GeometricExtents; }
            catch { return new Extents3d(); }
        }

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
    }
}
