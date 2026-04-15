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
        /// <summary>WCS X inside the profile view grid.</summary>
        public double DrawingX  { get; set; }
        /// <summary>WCS Y inside the profile view grid.</summary>
        public double DrawingY  { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  One label-placement job to execute after the owning command exits.
    // ─────────────────────────────────────────────────────────────────────────
    internal class LabelGenJob
    {
        public string   Command         { get; set; } = "";
        public Database Db              { get; set; } = null!;
        public HashSet<long> PreHandles { get; set; } = new();
        public double   DragDx          { get; set; }
        public double   DragDy          { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  LLabelGenEngine
    //
    //  Scans for crossing pipe proxy entities inside a profile view,
    //  queues ADDPROFILEVIEWSTAELEVLBL commands for each crossing,
    //  and applies a diagonal drag offset after each label is placed.
    //
    //  Follows the same deferred SendStringToExecute + CommandEnded pattern
    //  used by PipeMagicEngine and InvertPullUpEngine.
    // ═══════════════════════════════════════════════════════════════════════════
    public static class LLabelGenEngine
    {
        private const string DXF_NETWORK_PART  = "AECC_GRAPH_PROFILE_NETWORK_PART";
        private const string DXF_PRESSURE_PART = "AECC_GRAPH_PROFILE_PRESSURE_PART";

        private static readonly Queue<LabelGenJob> _pendingJobs    = new();
        private static bool                        _handlerRegistered = false;

        // ─────────────────────────────────────────────────────────────────────
        //  Scan the profile view for crossing pipe proxy entities and return
        //  the list of crossing points (station, elevation, WCS X/Y).
        // ─────────────────────────────────────────────────────────────────────
        public static List<CrossingLabelPoint> FindCrossingPoints(
            ObjectId pvId, Database db)
        {
            var points = new List<CrossingLabelPoint>();

            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                var pv = tx.GetObject(pvId, OpenMode.ForRead) as CivilDB.ProfileView;
                if (pv == null) { tx.Abort(); return points; }

                Extents3d pvExt;
                try { pvExt = ((Entity)pv).GeometricExtents; }
                catch { tx.Abort(); return points; }

                var btr = tx.GetObject(db.CurrentSpaceId, OpenMode.ForRead)
                          as BlockTableRecord;
                if (btr == null) { tx.Abort(); return points; }

                foreach (ObjectId id in btr)
                {
                    try
                    {
                        string dxf = id.ObjectClass.DxfName;
                        if (dxf != DXF_NETWORK_PART && dxf != DXF_PRESSURE_PART)
                            continue;

                        var ent = tx.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;

                        Extents3d ext;
                        try { ext = ent.GeometricExtents; }
                        catch { continue; }

                        // Center of the proxy ellipse
                        double cx = (ext.MinPoint.X + ext.MaxPoint.X) / 2.0;
                        double cy = (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0;

                        // Must be inside the profile view bounds
                        if (cx < pvExt.MinPoint.X || cx > pvExt.MaxPoint.X) continue;
                        if (cy < pvExt.MinPoint.Y || cy > pvExt.MaxPoint.Y) continue;

                        // Bottom of ellipse = invert point
                        double invertY = ext.MinPoint.Y;

                        // Convert WCS coords → station + elevation
                        double sta = 0, elev = 0;
                        if (!pv.FindStationAndElevationAtXY(cx, invertY, ref sta, ref elev))
                            continue;

                        points.Add(new CrossingLabelPoint
                        {
                            Station   = sta,
                            Elevation = elev,
                            DrawingX  = cx,
                            DrawingY  = invertY
                        });
                    }
                    catch { }
                }

                tx.Abort();
            }

            return points;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Queue label placement jobs for all crossing points.
        //  Each job fires one ADDPROFILEVIEWSTAELEVLBL command, then the
        //  CommandEnded handler applies the +3,+3 drag offset.
        // ─────────────────────────────────────────────────────────────────────
        public static int QueueLabelJobs(
            ObjectId                   pvId,
            List<CrossingLabelPoint>   crossings,
            ObjectId                   labelStyleId,
            ObjectId                   markerStyleId,
            Database                   db,
            Document                   doc)
        {
            if (crossings.Count == 0) return 0;

            // Safety reset
            _pendingJobs.Clear();
            if (_handlerRegistered)
            {
                try { doc.CommandEnded -= OnCommandEnded; } catch { }
                _handlerRegistered = false;
            }

            string pvHandent = $"(handent \"{pvId.Handle}\")";

            foreach (var cp in crossings)
            {
                // Command string:
                //   ADDPROFILEVIEWSTAELEVLBL   ← start the command
                //   (handent "pvHandle")       ← select the profile view
                //   X,Y                        ← coordinates for label placement
                //   (enter)                    ← finish (one label per invocation)
                // NOTE: snapshot is taken in ProcessNextJob right before firing,
                // not here, so each job only detects the entity it created.
                string coords = $"{cp.DrawingX:F6},{cp.DrawingY:F6}";
                string cmd    = $"ADDPROFILEVIEWSTAELEVLBL {pvHandent}\n{coords}\n\n";

                _pendingJobs.Enqueue(new LabelGenJob
                {
                    Command    = cmd,
                    Db         = db,
                    PreHandles = new HashSet<long>(),   // filled in ProcessNextJob
                    DragDx     = 3.0,
                    DragDy     = 3.0
                });
            }

            // Register one-shot CommandEnded handler (fires when LLABELGEN exits)
            if (_pendingJobs.Count > 0 && !_handlerRegistered)
            {
                doc.CommandEnded += OnCommandEnded;
                _handlerRegistered = true;
            }

            return crossings.Count;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Level-1: fires once after LLABELGEN exits.
        //  Kicks off the job queue.
        // ─────────────────────────────────────────────────────────────────────
        private static void OnCommandEnded(object sender, CommandEventArgs e)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            doc.CommandEnded -= OnCommandEnded;
            _handlerRegistered = false;

            ProcessNextJob(doc);
        }

        private static void ProcessNextJob(Document doc)
        {
            if (_pendingJobs.Count == 0) return;

            var job = _pendingJobs.Dequeue();

            // Take snapshot NOW, right before firing the placement command.
            // This guarantees we only detect the entity this specific job creates,
            // not labels placed by earlier jobs in the same batch.
            job.PreHandles = SnapshotLabelHandles(job.Db);

            // Register Level-2 handler using a named reference so it can self-remove.
            // Anonymous lambdas that can't be unsubscribed would accumulate across
            // jobs and fire multiple times per CommandEnded event.
            CommandEventHandler? levelTwo = null;
            levelTwo = (s, ev) =>
            {
                doc.CommandEnded -= levelTwo;   // self-remove before doing any work
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
                doc.Editor.WriteMessage($"\n  ⚠ LLabelGen job error: {ex.Message}");
                doc.CommandEnded -= levelTwo;   // clean up handler on send failure
                ProcessNextJob(doc);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Find newly created labels and move them +dx, +dy.
        // ─────────────────────────────────────────────────────────────────────
        private static void ApplyDragOffset(Document doc, LabelGenJob job)
        {
            try
            {
                Database db = job.Db;
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tx.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    int moved = 0;
                    foreach (ObjectId id in ms)
                    {
                        // Skip entities that existed before the command ran
                        if (job.PreHandles.Contains(id.Handle.Value)) continue;

                        try
                        {
                            var ent = tx.GetObject(id, OpenMode.ForRead) as Entity;
                            if (ent == null) continue;

                            // Check if this is a profile view label type
                            string dxfName = id.ObjectClass.DxfName;
                            if (!dxfName.Contains("AECC") ||
                                !dxfName.Contains("LABEL"))
                            {
                                // Also try casting to CivilDB.Label
                                if (!(ent is CivilDB.Label)) continue;
                            }

                            // Move the label by the drag offset
                            var entW = tx.GetObject(id, OpenMode.ForWrite) as Entity;
                            if (entW != null)
                            {
                                entW.TransformBy(
                                    Matrix3d.Displacement(
                                        new Vector3d(job.DragDx, job.DragDy, 0)));
                                moved++;
                            }
                        }
                        catch { }
                    }

                    tx.Commit();

                    if (moved > 0)
                        doc.Editor.WriteMessage(
                            $"\n  ✓ Label placed and dragged (+{job.DragDx:F0}, +{job.DragDy:F0}).");
                }
            }
            catch (Exception ex)
            {
                doc.Editor.WriteMessage(
                    $"\n  ⚠ Drag offset error: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Snapshot handles of all current entities in model space.
        // ─────────────────────────────────────────────────────────────────────
        private static HashSet<long> SnapshotLabelHandles(Database db)
        {
            var handles = new HashSet<long>();
            try
            {
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tx.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        handles.Add(id.Handle.Value);
                    }
                    tx.Abort();
                }
            }
            catch { }
            return handles;
        }
    }
}
