using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivilApp = Autodesk.Civil.ApplicationServices;
using CivilDB  = Autodesk.Civil.DatabaseServices;

namespace AdvancedLandDevTools.Engine
{
    public class PipeMagicResult
    {
        public int ProfileViewsProcessed { get; set; }
        public int GravityNetworksAdded  { get; set; }
        public int PressurePipesAdded    { get; set; }
        public List<string> Log { get; } = new();
        public void AddSuccess(string m) => Log.Add($"  ✓  {m}");
        public void AddInfo   (string m) => Log.Add($"  ℹ  {m}");
        public void AddFailure(string m) => Log.Add($"  ✗  {m}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  One command string to run after PIPEMAGIC exits.
    //  handent references are embedded directly in the command string so
    //  no implied selection or screen coordinates are needed.
    // ─────────────────────────────────────────────────────────────────────────
    internal class ProjectionJob
    {
        public string   Command  { get; set; } = "";
        public string   PvHandle { get; set; } = "";
        public Database Db       { get; set; } = null!;
    }

    public static class PipeMagicEngine
    {
        // Static queue of jobs to run after the current command exits
        private static readonly Queue<ProjectionJob> _pendingJobs = new();
        private static bool _handlerRegistered = false;

        // ─────────────────────────────────────────────────────────────────────
        public static PipeMagicResult Run(IList<ObjectId> profileViewIds)
        {
            var result = new PipeMagicResult();

            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) { result.AddFailure("No active document."); return result; }
            Database db = doc.Database;

            // ── Collect gravity network ids + pressure pipe ids ───────────────
            var gravityNetIds   = new List<ObjectId>();
            var pressurePipeIds = new List<ObjectId>();

            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                try
                {
                    var civDoc = CivilApp.CivilDocument.GetCivilDocument(db);
                    foreach (ObjectId nid in civDoc.GetPipeNetworkIds())
                        gravityNetIds.Add(nid);

                    var bt = tx.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                    var ms = tx.GetObject(
                             bt![BlockTableRecord.ModelSpace], OpenMode.ForRead)
                             as BlockTableRecord;
                    foreach (ObjectId id in ms!)
                    {
                        try
                        {
                            if (tx.GetObject(id, OpenMode.ForRead) is CivilDB.PressurePipe)
                                pressurePipeIds.Add(id);
                        }
                        catch { }
                    }
                }
                catch (Exception ex) { result.AddInfo($"Scan warning: {ex.Message}"); }
                tx.Abort();
            }

            result.AddInfo($"Found {gravityNetIds.Count} gravity network(s), " +
                           $"{pressurePipeIds.Count} pressure pipe(s) in drawing.");

            // ── Process each profile view — detect crossings, queue jobs ──────
            foreach (ObjectId pvId in profileViewIds)
            {
                try
                {
                    int gBefore = result.GravityNetworksAdded;
                    int pBefore = result.PressurePipesAdded;

                    QueueProjectionJobs(pvId, gravityNetIds, pressurePipeIds, db, result);

                    if (result.GravityNetworksAdded > gBefore ||
                        result.PressurePipesAdded   > pBefore)
                        result.ProfileViewsProcessed++;
                }
                catch (Exception ex)
                {
                    result.AddFailure($"Profile view {pvId.Handle}: {ex.Message}");
                }
            }

            // ── Register one-shot CommandEnded handler to run queued jobs ─────
            // The handler fires AFTER the PIPEMAGIC command exits, so implied
            // selection survives long enough for the add-parts commands to pick it up.
            if (_pendingJobs.Count > 0 && !_handlerRegistered)
            {
                doc.CommandEnded += OnCommandEnded;
                _handlerRegistered = true;
                result.AddInfo(
                    $"Queued {_pendingJobs.Count} projection job(s). " +
                    "They will execute immediately after this command exits.");
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Detect crossings and push ProjectionJob entries onto the queue
        // ─────────────────────────────────────────────────────────────────────
        private static void QueueProjectionJobs(
            ObjectId        pvId,
            List<ObjectId>  gravityNetIds,
            List<ObjectId>  pressurePipeIds,
            Database        db,
            PipeMagicResult result)
        {
            var crossingGravityPipeIds  = new List<ObjectId>();
            var crossingPressurePipeIds = new List<ObjectId>();
            string pvName   = "";
            string pvHandle = pvId.Handle.ToString();

            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                var pv = tx.GetObject(pvId, OpenMode.ForRead) as CivilDB.ProfileView;
                if (pv == null)
                {
                    result.AddFailure($"Object {pvId.Handle} is not a ProfileView.");
                    tx.Abort(); return;
                }

                pvName = pv.Name;
                result.AddInfo($"── Profile View: '{pvName}' ──");

                if (pv.AlignmentId.IsNull)
                {
                    result.AddFailure($"  '{pvName}' has no alignment.");
                    tx.Abort(); return;
                }

                var al = tx.GetObject(pv.AlignmentId, OpenMode.ForRead) as CivilDB.Alignment;
                if (al == null)
                {
                    result.AddFailure("  Cannot open alignment."); tx.Abort(); return;
                }

                result.AddInfo($"  Alignment: '{al.Name}' " +
                               $"[{al.StartingStation:F0} – {al.EndingStation:F0}]");

                // Gravity: check each pipe in every network individually
                foreach (ObjectId nid in gravityNetIds)
                {
                    try
                    {
                        var net = tx.GetObject(nid, OpenMode.ForRead) as CivilDB.Network;
                        if (net == null) continue;
                        foreach (ObjectId pid in net.GetPipeIds())
                        {
                            try
                            {
                                var pipe = tx.GetObject(pid, OpenMode.ForRead) as CivilDB.Pipe;
                                if (pipe != null &&
                                    PipeCrossesAlignment(pipe.StartPoint, pipe.EndPoint, al))
                                {
                                    crossingGravityPipeIds.Add(pid);
                                    result.AddSuccess(
                                        $"  Gravity pipe '{pipe.Name}' crosses — queued.");
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                // Pressure
                foreach (ObjectId pid in pressurePipeIds)
                {
                    try
                    {
                        var pipe = tx.GetObject(pid, OpenMode.ForRead) as CivilDB.PressurePipe;
                        if (pipe != null &&
                            PipeCrossesAlignment(pipe.StartPoint, pipe.EndPoint, al))
                        {
                            crossingPressurePipeIds.Add(pid);
                            result.AddSuccess(
                                $"  Pressure pipe '{pipe.Name}' crosses — queued.");
                        }
                    }
                    catch { }
                }

                tx.Abort();
            }

            if (crossingGravityPipeIds.Count == 0 && crossingPressurePipeIds.Count == 0)
            {
                result.AddInfo($"  No crossing pipes found for '{pvName}'."); return;
            }

            // Build pipe handle list for handent selection
            // AutoCAD command strings accept:  (handent "A1B2")
            // which resolves a hex handle directly to an entity — no screen pick needed.
            // Sequence the command inserts pipes one-by-one via PICKFIRST then picks the PV.

            string BuildHandent(ObjectId id) => $"(handent \"{id.Handle}\")";
            string pvHandent = BuildHandent(pvId);

            // Command flow:
            //   ADDNETWORKPARTSTOPROF   ← start
            //   S                       ← "Selected parts only" keyword
            //   (handent "pipeHandle")  ← select the crossing pipe
            //   (enter)                 ← end selection
            //   (handent "pvHandle")    ← pick profile view
            //   (enter)                 ← confirm

            // Enqueue gravity jobs — one pipe per job
            foreach (ObjectId pid in crossingGravityPipeIds)
            {
                string cmd =
                    $"ADDNETWORKPARTSTOPROF " +
                    $"S " +                      // "Selected parts only"
                    $"{BuildHandent(pid)}\n" +   // pick crossing pipe
                    $"\n" +                       // end selection
                    $"{pvHandent}\n";             // pick profile view

                _pendingJobs.Enqueue(new ProjectionJob
                {
                    Command  = cmd,
                    PvHandle = pvHandle,
                    Db       = db
                });
                result.GravityNetworksAdded++;
            }

            // Enqueue pressure jobs
            foreach (ObjectId pid in crossingPressurePipeIds)
            {
                string cmd =
                    $"ADDPRESSUREPARTSTOPROF " +
                    $"S " +                      // "Selected parts only"
                    $"{BuildHandent(pid)}\n" +
                    $"\n" +
                    $"{pvHandent}\n";

                _pendingJobs.Enqueue(new ProjectionJob
                {
                    Command  = cmd,
                    PvHandle = pvHandle,
                    Db       = db
                });
                result.PressurePipesAdded++;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  CommandEnded handler — fires after PIPEMAGIC exits.
        //  At this point implied selection survives long enough for the
        //  Civil 3D command to consume it before control returns here.
        // ─────────────────────────────────────────────────────────────────────
        private static void OnCommandEnded(object sender, CommandEventArgs e)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            doc.CommandEnded -= OnCommandEnded;
            _handlerRegistered = false;

            // Drain the queue — each job is a fully self-contained command string
            // using (handent "XX") handles for both pipes and profile view.
            // No implied selection needed. No screen coordinates needed.
            // SendStringToExecute runs each command one at a time in sequence.
            while (_pendingJobs.Count > 0)
            {
                var job = _pendingJobs.Dequeue();
                try
                {
                    doc.SendStringToExecute(job.Command, true, false, false);
                }
                catch (Exception ex)
                {
                    doc.Editor.WriteMessage(
                        $"\n⚠ PipeMagic deferred job error: {ex.Message}");
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        private static bool PipeCrossesAlignment(
            Point3d startPt, Point3d endPt, CivilDB.Alignment al)
        {
            try
            {
                double sta1 = 0, off1 = 0, sta2 = 0, off2 = 0;
                al.StationOffset(startPt.X, startPt.Y, ref sta1, ref off1);
                al.StationOffset(endPt.X,   endPt.Y,   ref sta2, ref off2);

                bool inRange =
                    (sta1 >= al.StartingStation - 1.0 && sta1 <= al.EndingStation + 1.0) ||
                    (sta2 >= al.StartingStation - 1.0 && sta2 <= al.EndingStation + 1.0);
                if (!inRange) return false;

                if (Math.Abs(off1) < 0.01 || Math.Abs(off2) < 0.01) return true;
                return (off1 * off2) < 0;
            }
            catch { return false; }
        }
    }
}
