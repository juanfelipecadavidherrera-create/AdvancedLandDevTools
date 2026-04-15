using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivilDB = Autodesk.Civil.DatabaseServices;

namespace AdvancedLandDevTools.Engine
{
    // ─────────────────────────────────────────────────────────────────────────
    //  One crossing point to label inside a profile view.
    // ─────────────────────────────────────────────────────────────────────────
    public class CrossingLabelPoint
    {
        public double Station   { get; set; }
        public double Elevation { get; set; }
        /// <summary>Label insertion X in HOST drawing WCS.</summary>
        public double DrawingX  { get; set; }
        /// <summary>Label insertion Y in HOST drawing WCS (invert elevation of the crossing pipe).</summary>
        public double DrawingY  { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  One label-placement job to execute after the owning command exits.
    // ─────────────────────────────────────────────────────────────────────────
    internal class LabelGenJob
    {
        public string        Command    { get; set; } = "";
        public Database      Db         { get; set; } = null!;
        public HashSet<long> PreHandles { get; set; } = new();
        public double        DragDx     { get; set; }
        public double        DragDy     { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  LLabelGenEngine
    //
    //  Scans for crossing pipe proxy entities inside a profile view,
    //  queues ADDPROFILEVIEWSTAELEVLBL commands for each crossing,
    //  and applies a diagonal drag offset after each label is placed.
    //
    //  Supports both native profile views (current DB) and XREFed profile views
    //  (proxy entities live in the XREF database, DrawingX/Y are in host WCS).
    // ═══════════════════════════════════════════════════════════════════════════
    public static class LLabelGenEngine
    {
        private const string DXF_NETWORK_PART  = "AECC_GRAPH_PROFILE_NETWORK_PART";
        private const string DXF_PRESSURE_PART = "AECC_GRAPH_PROFILE_PRESSURE_PART";

        private static readonly Queue<LabelGenJob> _pendingJobs       = new();
        private static bool                        _handlerRegistered = false;

        // ─────────────────────────────────────────────────────────────────────
        //  NATIVE: profile view is in the current (host) database.
        //  Scans host model space for AECC proxy entities inside the PV bounds.
        // ─────────────────────────────────────────────────────────────────────
        public static List<CrossingLabelPoint> FindCrossingPoints(
            ObjectId pvId, Database db)
        {
            var points = new List<CrossingLabelPoint>();

            using (var tx = db.TransactionManager.StartTransaction())
            {
                var pv = tx.GetObject(pvId, OpenMode.ForRead) as CivilDB.ProfileView;
                if (pv == null) { tx.Abort(); return points; }

                Extents3d pvExt;
                try { pvExt = ((Entity)pv).GeometricExtents; }
                catch { tx.Abort(); return points; }

                ScanModelSpaceForProxies(pv, pvExt, tx, db, points,
                    entityToHostWCS: Matrix3d.Identity);

                tx.Abort();
            }
            return points;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  XREF: profile view is inside an XREF block reference.
        //
        //  Opens the XREF database directly, finds the ProfileView whose
        //  (host-WCS) extents match pvExtentsHostWCS, then scans that XREF DB
        //  for AECC proxy entities.  DrawingX/Y are transformed to host WCS.
        // ─────────────────────────────────────────────────────────────────────
        public static List<CrossingLabelPoint> FindCrossingPointsInXref(
            Database xrefDb, Matrix3d xrefToHost, Extents3d pvExtentsHostWCS)
        {
            var points = new List<CrossingLabelPoint>();

            try
            {
                using (var xTx = xrefDb.TransactionManager.StartTransaction())
                {
                    // ── Find the ProfileView in the XREF DB ──────────────────
                    // Its local extents, when transformed to host WCS, should
                    // match pvExtentsHostWCS.
                    Matrix3d hostToXref = xrefToHost.Inverse();
                    var xMs = xTx.GetObject(xrefDb.CurrentSpaceId, OpenMode.ForRead)
                              as BlockTableRecord;
                    if (xMs == null) { xTx.Abort(); return points; }

                    CivilDB.ProfileView? targetPv = null;
                    Extents3d pvExtLocal           = default;

                    foreach (ObjectId xId in xMs)
                    {
                        CivilDB.ProfileView xPv;
                        try { xPv = xTx.GetObject(xId, OpenMode.ForRead) as CivilDB.ProfileView; }
                        catch { continue; }
                        if (xPv == null) continue;

                        // Check if this PV's host-WCS centre overlaps pvExtentsHostWCS
                        Extents3d localExt;
                        try { localExt = ((Entity)xPv).GeometricExtents; }
                        catch { continue; }

                        // Transform XREF-local centre → host WCS and test overlap
                        double lCx = (localExt.MinPoint.X + localExt.MaxPoint.X) / 2.0;
                        double lCy = (localExt.MinPoint.Y + localExt.MaxPoint.Y) / 2.0;
                        var centreHost = new Point3d(lCx, lCy, 0).TransformBy(xrefToHost);

                        if (centreHost.X < pvExtentsHostWCS.MinPoint.X ||
                            centreHost.X > pvExtentsHostWCS.MaxPoint.X) continue;
                        if (centreHost.Y < pvExtentsHostWCS.MinPoint.Y ||
                            centreHost.Y > pvExtentsHostWCS.MaxPoint.Y) continue;

                        targetPv   = xPv;
                        pvExtLocal = localExt;
                        break;
                    }

                    if (targetPv == null) { xTx.Abort(); return points; }

                    // ── Scan XREF model space for AECC proxy entities ────────
                    ScanModelSpaceForProxies(targetPv, pvExtLocal, xTx, xrefDb, points,
                        entityToHostWCS: xrefToHost);

                    xTx.Abort();
                }
            }
            catch { }

            return points;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Core proxy scanner.  Works on any database (host or XREF).
        //
        //  pv            — the ProfileView in the same DB as the entities
        //  pvExtLocal    — PV GeometricExtents in that DB's local space
        //  entityToHostWCS — transform from local space to host WCS
        //                    (Matrix3d.Identity for native, brTransform for XREF)
        // ─────────────────────────────────────────────────────────────────────
        private static void ScanModelSpaceForProxies(
            CivilDB.ProfileView pv,
            Extents3d           pvExtLocal,
            Transaction         tx,
            Database            db,
            List<CrossingLabelPoint> points,
            Matrix3d            entityToHostWCS)
        {
            var btr = tx.GetObject(db.CurrentSpaceId, OpenMode.ForRead)
                      as BlockTableRecord;
            if (btr == null) return;

            foreach (ObjectId id in btr)
            {
                try
                {
                    string dxf = id.ObjectClass.DxfName;
                    if (dxf != DXF_NETWORK_PART && dxf != DXF_PRESSURE_PART) continue;

                    var ent = tx.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    Extents3d ext;
                    try { ext = ent.GeometricExtents; }
                    catch { continue; }

                    // Centre of the proxy ellipse in local (DB) space
                    double cx = (ext.MinPoint.X + ext.MaxPoint.X) / 2.0;
                    double cy = (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0;

                    // Must be inside the profile view bounds (local space)
                    if (cx < pvExtLocal.MinPoint.X || cx > pvExtLocal.MaxPoint.X) continue;
                    if (cy < pvExtLocal.MinPoint.Y || cy > pvExtLocal.MaxPoint.Y) continue;

                    // Bottom of ellipse = invert point (local space)
                    double invertY = ext.MinPoint.Y;

                    // Station + elevation from the ProfileView (uses local coords)
                    double sta = 0, elev = 0;
                    if (!pv.FindStationAndElevationAtXY(cx, invertY, ref sta, ref elev))
                        continue;

                    // Transform label insertion point to HOST WCS
                    var hostPt = new Point3d(cx, invertY, 0).TransformBy(entityToHostWCS);

                    points.Add(new CrossingLabelPoint
                    {
                        Station   = sta,
                        Elevation = elev,
                        DrawingX  = hostPt.X,
                        DrawingY  = hostPt.Y
                    });
                }
                catch { }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Queue label placement jobs.
        //
        //  pvExtentsHostWCS is used to click the profile view in ADDPROFILEVIEWSTAELEVLBL.
        //  We click the centre of the PV grid — this works for both native and
        //  XREF profile views because the WCS coordinates are the same regardless.
        //  This replaces the old (handent "handle") approach which does NOT work
        //  inside SendStringToExecute (AutoCAD does not evaluate LISP there).
        // ─────────────────────────────────────────────────────────────────────
        public static int QueueLabelJobs(
            Extents3d                pvExtentsHostWCS,
            List<CrossingLabelPoint> crossings,
            ObjectId                 labelStyleId,
            ObjectId                 markerStyleId,
            Database                 db,
            Document                 doc)
        {
            if (crossings.Count == 0) return 0;

            // Safety reset
            _pendingJobs.Clear();
            if (_handlerRegistered)
            {
                try { doc.CommandEnded -= OnCommandEnded; } catch { }
                _handlerRegistered = false;
            }

            // Click point for selecting the profile view:
            // Use the centre of the PV — safe area, always within the grid.
            double pvCx = (pvExtentsHostWCS.MinPoint.X + pvExtentsHostWCS.MaxPoint.X) / 2.0;
            double pvCy = (pvExtentsHostWCS.MinPoint.Y + pvExtentsHostWCS.MaxPoint.Y) / 2.0;
            string pvClick = $"{pvCx:F4},{pvCy:F4}";

            foreach (var cp in crossings)
            {
                // ADDPROFILEVIEWSTAELEVLBL prompts:
                //   "Select a profile view:"           ← supply WCS click inside PV
                //   "Specify station and elevation:"   ← supply label position
                //   (loop continues — empty Enter exits)
                string coords = $"{cp.DrawingX:F4},{cp.DrawingY:F4}";
                string cmd    = $"ADDPROFILEVIEWSTAELEVLBL {pvClick}\n{coords}\n \n";

                _pendingJobs.Enqueue(new LabelGenJob
                {
                    Command    = cmd,
                    Db         = db,
                    PreHandles = new HashSet<long>(),   // filled in ProcessNextJob
                    DragDx     = 3.0,
                    DragDy     = 3.0
                });
            }

            if (_pendingJobs.Count > 0 && !_handlerRegistered)
            {
                doc.CommandEnded += OnCommandEnded;
                _handlerRegistered = true;
            }

            return crossings.Count;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Level-1: fires once after LLABELGEN exits — kicks off the job queue.
        // ─────────────────────────────────────────────────────────────────────
        private static void OnCommandEnded(object sender, CommandEventArgs e)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            doc.CommandEnded -= OnCommandEnded;
            _handlerRegistered = false;

            ProcessNextJob(doc);
        }

        private static void ProcessNextJob(Document doc)
        {
            if (_pendingJobs.Count == 0) return;

            var job = _pendingJobs.Dequeue();

            // Snapshot right before firing — ensures each job detects only its own label.
            job.PreHandles = SnapshotModelHandles(job.Db);

            // Level-2 handler: self-removing named reference (not anonymous lambda)
            // so it doesn't accumulate across successive jobs.
            CommandEventHandler? levelTwo = null;
            levelTwo = (s, ev) =>
            {
                doc.CommandEnded -= levelTwo;
                ApplyDragOffset(doc, job);
                ProcessNextJob(doc);
            };
            doc.CommandEnded += levelTwo;

            try
            {
                doc.SendStringToExecute(job.Command, true, false, false);
            }
            catch (Exception ex)
            {
                doc.Editor.WriteMessage($"\n  ⚠ LLabelGen send error: {ex.Message}");
                doc.CommandEnded -= levelTwo;
                ProcessNextJob(doc);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Find newly created labels and displace them (+DragDx, +DragDy).
        // ─────────────────────────────────────────────────────────────────────
        private static void ApplyDragOffset(Document doc, LabelGenJob job)
        {
            try
            {
                using (var tx = job.Db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tx.GetObject(job.Db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tx.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    int moved = 0;
                    foreach (ObjectId id in ms)
                    {
                        if (job.PreHandles.Contains(id.Handle.Value)) continue;

                        try
                        {
                            var ent = tx.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null) continue;

                            string dxfName = id.ObjectClass.DxfName;
                            bool isLabel   = (dxfName.Contains("AECC") && dxfName.Contains("LABEL"))
                                          || ent is CivilDB.Label;
                            if (!isLabel) continue;

                            var entW = tx.GetObject(id, OpenMode.ForWrite) as Entity;
                            entW?.TransformBy(Matrix3d.Displacement(
                                new Vector3d(job.DragDx, job.DragDy, 0)));
                            moved++;
                        }
                        catch { }
                    }

                    tx.Commit();

                    if (moved > 0)
                        doc.Editor.WriteMessage(
                            $"\n  ✓ Label placed and offset (+{job.DragDx:F0}, +{job.DragDy:F0}).");
                    else
                        doc.Editor.WriteMessage("\n  ⚠ Label placed but drag offset not applied " +
                            "(new entity not identified as a label).");
                }
            }
            catch (Exception ex)
            {
                doc.Editor.WriteMessage($"\n  ⚠ Drag offset error: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Snapshot all entity handles in model space for diff-based detection.
        // ─────────────────────────────────────────────────────────────────────
        private static HashSet<long> SnapshotModelHandles(Database db)
        {
            var handles = new HashSet<long>();
            try
            {
                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tx.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                        handles.Add(id.Handle.Value);
                    tx.Abort();
                }
            }
            catch { }
            return handles;
        }
    }
}
