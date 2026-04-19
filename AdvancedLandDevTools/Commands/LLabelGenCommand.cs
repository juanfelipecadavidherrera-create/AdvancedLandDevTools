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

                // ── Step 1: Select the profile view ──────────────────────────
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

                // ── Step 2: Find ALL crossings (no exclusion yet) ────────────
                List<CrossingLabelPoint> crossings;
                if (!ctx.IsXref)
                    crossings = LLabelGenEngine.FindCrossingPoints(ctx.NativeId, db);
                else
                    crossings = LLabelGenEngine.FindCrossingPointsInXref(
                        ctx.XrefDatabase!, ctx.XrefToHost, ctx.Name);

                ed.WriteMessage($"\n  Total crossings detected: {crossings.Count}");

                if (crossings.Count == 0)
                {
                    ed.WriteMessage(
                        "\n  ⚠ No crossing pipe parts found in this profile view." +
                        "\n  Make sure pipes are drawn into the profile view" +
                        " (PIPEMAGIC / ADDNETWORKPARTSTOPROF).\n");
                    return;
                }

                // ── Step 3: List networks found, prompt which to exclude ─────
                var networkMap = new Dictionary<ObjectId, string>();
                foreach (var cp in crossings)
                {
                    if (!cp.NetworkId.IsNull && !networkMap.ContainsKey(cp.NetworkId))
                    {
                        string label = string.IsNullOrEmpty(cp.NetworkName)
                            ? "(unknown network)"
                            : cp.NetworkName;
                        int cnt = crossings.Count(c => c.NetworkId == cp.NetworkId);
                        networkMap[cp.NetworkId] = $"{label}  ({cnt} crossing(s))";
                    }
                }

                ObjectId excludedNetId = ObjectId.Null;

                if (networkMap.Count > 1)
                {
                    ed.WriteMessage("\n\n  Networks with crossings in this profile view:");
                    var netList = networkMap.ToList();
                    for (int i = 0; i < netList.Count; i++)
                        ed.WriteMessage($"\n    [{i + 1}] {netList[i].Value}");
                    ed.WriteMessage($"\n    [0] Don't exclude any");

                    var pio = new PromptIntegerOptions(
                        $"\n  Enter network number to EXCLUDE (0-{netList.Count}): ");
                    pio.LowerLimit = 0;
                    pio.UpperLimit = netList.Count;
                    pio.DefaultValue = 0;
                    pio.AllowNone = true;

                    var pir = ed.GetInteger(pio);
                    if (pir.Status == PromptStatus.Cancel)
                    { ed.WriteMessage("\n  Cancelled.\n"); return; }

                    int choice = (pir.Status == PromptStatus.None) ? 0 : pir.Value;
                    if (choice > 0 && choice <= netList.Count)
                    {
                        excludedNetId = netList[choice - 1].Key;
                        ed.WriteMessage($"\n  Excluding: {netList[choice - 1].Value}");
                    }
                }
                else if (networkMap.Count == 1)
                {
                    ed.WriteMessage($"\n  Only one network found: {networkMap.Values.First()}");
                    ed.WriteMessage("\n  No exclusion needed.");
                }

                // ── Step 4: Filter crossings ─────────────────────────────────
                if (!excludedNetId.IsNull)
                    crossings = crossings
                        .Where(c => c.NetworkId != excludedNetId)
                        .ToList();

                ed.WriteMessage($"\n  Crossings after exclusion: {crossings.Count}");

                if (crossings.Count == 0)
                {
                    ed.WriteMessage("\n  ⚠ No crossings remaining after exclusion.\n");
                    return;
                }

                foreach (var cp in crossings)
                    ed.WriteMessage(
                        $"\n    Sta {LLabelGenEngine.FormatStation(cp.Station)}" +
                        $"  Elev {cp.Elevation:F3}" +
                        $"  Pipe: {cp.PipeName}" +
                        $"  Net: {cp.NetworkName}");

                // ── Step 4.5: Collect styles and show UI ─────────────────────
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

                var dlg = new AdvancedLandDevTools.UI.LLabelGenDialog(labelStyles, markerStyles);
                bool? dlgResult = AcApp.ShowModalWindow(dlg);
                if (dlgResult != true)
                {
                    ed.WriteMessage("\n  Cancelled by user.\n");
                    return;
                }

                ObjectId chosenLabelStyleId = dlg.SelectedLabelStyleId;
                ObjectId chosenMarkerStyleId = dlg.SelectedMarkerStyleId;

                // ── Step 5: Queue native label placement ─────────────────────
                //  LISP (command ... pause ...) fires ADDPROFILEVIEWSTAELEVLBL
                //  after this command exits.  The user clicks the profile view
                //  ONCE (works for XREF), then all crossing coordinates are
                //  fed automatically as point picks → native 3D labels.
                ed.WriteMessage(
                    $"\n\n  Queuing {crossings.Count} native label(s)...");

                // Compute drag offset — ~2.5% of PV width right, ~10% of PV height up.
                // For XREF profile views GeometricExtents is often degenerate (zero),
                // so fall back to the spread of the actual crossing DrawingX/Y values.
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
                    // XREF fallback: derive scale from the spread of crossing coords.
                    double xSpan = crossings.Count > 1
                        ? crossings.Max(c => c.DrawingX) - crossings.Min(c => c.DrawingX)
                        : Math.Abs(crossings[0].DrawingX) * 0.05;
                    double ySpan = crossings.Count > 1
                        ? crossings.Max(c => c.DrawingY) - crossings.Min(c => c.DrawingY)
                        : Math.Abs(crossings[0].DrawingY) * 0.05;
                    xSpan = Math.Max(xSpan, 5.0);
                    ySpan = Math.Max(ySpan, 2.0);
                    dragOffset = new Vector3d(xSpan * 0.30, ySpan * 0.60, 0);
                }
                ed.WriteMessage($"\n  DIAG drag: pvW={pvW:F1} pvH={pvH:F1}  offset=({dragOffset.X:F3},{dragOffset.Y:F3})");

                LLabelGenEngine.QueueLabelCommand(
                    crossings, ctx.HostHandle, ctx.PickPoint, doc, ctx.IsXref,
                    chosenLabelStyleId, chosenMarkerStyleId, dragOffset);

                ed.WriteMessage(
                    $"\n  ✓ Finished — {crossings.Count} label(s) placed.");
                ed.WriteMessage(
                    "\n═══════════════════════════════════════════════════════════\n");
            }
            catch (System.Exception ex)
            {
                AcApp.DocumentManager.MdiActiveDocument
                    ?.Editor.WriteMessage($"\n[ALDT ERROR] LLABELGEN: {ex.Message}\n");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Detect profile view — native or XREF.
        // ─────────────────────────────────────────────────────────────────────
        private static PvContext? DetectProfileView(
            PromptEntityResult per, Database db, Editor ed)
        {
            Point3d pickPt = per.PickedPoint;

            // Try native DB first
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

            // Search XREF databases
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

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────────
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
