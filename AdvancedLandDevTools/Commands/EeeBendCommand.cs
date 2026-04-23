using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AdvancedLandDevTools.Helpers;
using CivilDB  = Autodesk.Civil.DatabaseServices;
using CivilApp = Autodesk.Civil.ApplicationServices;
using AcApp    = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AdvancedLandDevTools.Commands
{
    /// <summary>
    /// EEEBEND — Inserts a pressure-network pipe duck (bypass) around a crossing pipe.
    ///
    /// Workflow:
    ///   1. Click the pressure pipe in the profile view.
    ///   2. Click near the crossing pipe in the profile — the command auto-detects
    ///      the crossing pipe's invert and crown via PipeAlignmentIntersector.
    ///   3. Choose duck direction (Down/Up).
    ///   4. The command computes 4 bend points and modifies the pressure network.
    ///
    /// Duck geometry (slope 1H : 10V, leg = 11.6 ft):
    ///   Upper-Left  = crossing_sta − 10 ft,   original_elev
    ///   Lower-Left  = crossing_sta − 10 + ΔH, duck_elev
    ///   Lower-Right = crossing_sta + 10 − ΔH, duck_elev
    ///   Upper-Right = crossing_sta + 10 ft,   original_elev
    /// </summary>
    public class EeeBendCommand
    {
        // ── Duck geometry constants ────────────────────────────────────────────
        private const double HorizOffset = 10.0;   // ft from crossing centre to inner bends

        // ────────────────────────────────────────────────────────────────────────
        [CommandMethod("EEEBEND", CommandFlags.Modal)]
        public void EeeBend()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor   ed  = doc.Editor;
            Database db  = doc.Database;

            try
            {
                if (!Engine.LicenseManager.EnsureLicensed()) return;

                ed.WriteMessage("\n═══════════════════════════════════════════════════════════");
                ed.WriteMessage("\n  Advanced Land Development Tools  |  EEE Bend (Duck)");
                ed.WriteMessage("\n  Pressure network pipe runs in profile views only.");
                ed.WriteMessage("\n═══════════════════════════════════════════════════════════");

                // ── Step 1: Select a pressure pipe in the profile ────────────────
                var peo = new PromptEntityOptions(
                    "\n  Click a pressure pipe in the profile view: ");
                peo.AllowObjectOnLockedLayer = true;
                var per = ed.GetEntity(peo);
                if (per.Status != PromptStatus.OK) { ed.WriteMessage("\n  Cancelled.\n"); return; }

                ObjectId pipeId      = ObjectId.Null;
                ObjectId alignId     = ObjectId.Null;
                ObjectId profileViewId = ObjectId.Null;
                Point3d  pickPt      = per.PickedPoint;

                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var ent = tx.GetObject(per.ObjectId, OpenMode.ForRead);
                    if (ent is CivilDB.PressurePipe)
                    {
                        pipeId = per.ObjectId;
                        var pv2 = FindProfileViewAtPoint(pickPt, tx, db);
                        if (pv2 != null) { profileViewId = pv2.ObjectId; alignId = pv2.AlignmentId; }
                    }
                    else
                    {
                        var pv = ent as CivilDB.ProfileView
                               ?? FindProfileViewAtPoint(pickPt, tx, db);
                        if (pv != null)
                        {
                            profileViewId = pv.ObjectId;
                            alignId       = pv.AlignmentId;

                            if (!alignId.IsNull)
                            {
                                var aln2 = tx.GetObject(alignId, OpenMode.ForRead) as CivilDB.Alignment;
                                if (aln2 != null)
                                    pipeId = FindPressurePipeNear(pv, pickPt, tx, db, ed, aln2);
                            }
                        }
                    }
                    tx.Abort();
                }

                if (pipeId.IsNull)
                {
                    ed.WriteMessage(
                        "\n  No pressure pipe found. Click directly on the pressure pipe line in the profile.\n");
                    return;
                }

                // ── Step 2: Click near the crossing pipe ─────────────────────────
                ed.WriteMessage("\n  Click near the crossing pipe in the profile view:");
                var ppoPt = new PromptPointOptions(
                    "\n  Click crossing location: ");
                ppoPt.AllowNone = false;
                var ppr = ed.GetPoint(ppoPt);
                if (ppr.Status != PromptStatus.OK) { ed.WriteMessage("\n  Cancelled.\n"); return; }
                Point3d crossingPickPt = ppr.Value;

                // ── Step 2b: Down or Up duck? ────────────────────────────────────
                var kwdOpts = new PromptKeywordOptions(
                    "\n  Duck direction [Down/Up] <Down>: ");
                kwdOpts.Keywords.Add("Down");
                kwdOpts.Keywords.Add("Up");
                kwdOpts.AllowNone = true;   // Enter = Down
                var kwdRes = ed.GetKeywords(kwdOpts);
                if (kwdRes.Status == PromptStatus.Cancel) { ed.WriteMessage("\n  Cancelled.\n"); return; }
                bool goUp = kwdRes.Status == PromptStatus.OK && kwdRes.StringResult == "Up";

                // ── Step 3: Auto-detect crossing pipe and resolve geometry ───────
                double crossingSta       = 0;
                double crossingInvElev   = 0;
                double crossingCrownElev = 0;
                double pipeElevAtCross   = 0;
                Point3d pipeOrigStart    = Point3d.Origin;
                Point3d pipeOrigEnd      = Point3d.Origin;
                string crossingPipeName  = "";

                double elevLow = 0;
                double elevUL  = 0;
                double elevUR  = 0;
                double pipeRadius = 0;
                double staUL = 0, staLL = 0, staLR = 0, staUR = 0;

                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var pipe = tx.GetObject(pipeId, OpenMode.ForRead) as CivilDB.PressurePipe;
                    if (pipe == null) { tx.Abort(); return; }

                    pipeOrigStart = pipe.StartPoint;
                    pipeOrigEnd   = pipe.EndPoint;
                    pipeRadius    = pipe.OuterDiameter / 2.0;

                    // Find the profile view
                    CivilDB.ProfileView? pv = null;
                    if (!profileViewId.IsNull)
                        pv = tx.GetObject(profileViewId, OpenMode.ForRead) as CivilDB.ProfileView;
                    pv ??= FindProfileViewAtPoint(crossingPickPt, tx, db)
                        ?? FindProfileViewAtPoint(pickPt, tx, db);

                    if (pv == null)
                    {
                        ed.WriteMessage(
                            "\n  Cannot find profile view — click within the profile view boundaries.\n");
                        tx.Abort();
                        return;
                    }

                    profileViewId = pv.ObjectId;
                    if (alignId.IsNull) alignId = pv.AlignmentId;

                    var aln = tx.GetObject(alignId, OpenMode.ForRead) as CivilDB.Alignment;
                    if (aln == null)
                    {
                        ed.WriteMessage("\n  Cannot open alignment.\n");
                        tx.Abort();
                        return;
                    }

                    // Get clicked station from profile view
                    double clickedSta = 0, clickedElev = 0;
                    if (!pv.FindStationAndElevationAtXY(
                            crossingPickPt.X, crossingPickPt.Y,
                            ref clickedSta, ref clickedElev))
                    {
                        ed.WriteMessage(
                            "\n  Crossing click is outside the profile view bounds.\n");
                        tx.Abort();
                        return;
                    }

                    ed.WriteMessage($"\n  Clicked station: {clickedSta:F2}");

                    // ── Auto-find crossing pipe via PipeAlignmentIntersector ──────
                    ObjectId pressureNetId = pipe.NetworkId;
                    PipeAlignmentCrossing? bestCrossing = null;
                    double bestStaDist = double.MaxValue;

                    var bt2 = tx.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                    var ms2 = tx.GetObject(
                             bt2![BlockTableRecord.ModelSpace], OpenMode.ForRead)
                             as BlockTableRecord;

                    foreach (ObjectId id in ms2!)
                    {
                        try
                        {
                            var obj = tx.GetObject(id, OpenMode.ForRead);

                            bool isPipe = false;
                            ObjectId objNetId = ObjectId.Null;

                            if (obj is CivilDB.Pipe gp)
                            {
                                isPipe   = true;
                                objNetId = gp.NetworkId;
                            }
                            else if (obj is CivilDB.PressurePipe pp)
                            {
                                isPipe   = true;
                                objNetId = pp.NetworkId;
                                // Skip pipes in the same pressure network
                                if (!pressureNetId.IsNull && objNetId == pressureNetId)
                                    continue;
                            }

                            if (!isPipe) continue;

                            foreach (var c in PipeAlignmentIntersector.FindCrossings(id, aln, tx))
                            {
                                double dist = Math.Abs(c.Station - clickedSta);
                                if (dist < bestStaDist)
                                {
                                    bestStaDist = dist;
                                    bestCrossing = c;
                                }
                            }
                        }
                        catch { }
                    }

                    if (bestCrossing == null)
                    {
                        ed.WriteMessage(
                            "\n  No crossing pipe found near the clicked location." +
                            "\n  Make sure a gravity or pressure pipe crosses the alignment.\n");
                        tx.Abort();
                        return;
                    }

                    crossingSta       = bestCrossing.Station;
                    crossingInvElev   = bestCrossing.InvertElevation;
                    crossingCrownElev = bestCrossing.CrownElevation;
                    crossingPipeName  = bestCrossing.PipeName;

                    ed.WriteMessage($"\n  Crossing pipe: {crossingPipeName}");
                    ed.WriteMessage($"\n  Crossing → Sta {crossingSta:F2}, Inv {crossingInvElev:F3}, Crown {crossingCrownElev:F3} ft");

                    // Pipe stations via alignment
                    double sta1 = 0, off1 = 0, sta2 = 0, off2 = 0;
                    aln.StationOffset(pipeOrigStart.X, pipeOrigStart.Y, ref sta1, ref off1);
                    aln.StationOffset(pipeOrigEnd.X,   pipeOrigEnd.Y,   ref sta2, ref off2);

                    if (crossingSta < Math.Min(sta1, sta2) - 1.0 ||
                        crossingSta > Math.Max(sta1, sta2) + 1.0)
                        ed.WriteMessage(
                            $"\n  WARNING: Crossing station {crossingSta:F2} is outside pipe range" +
                            $" [{Math.Min(sta1, sta2):F2}–{Math.Max(sta1, sta2):F2}].");

                    // Pressure pipe CL elevation at crossing station
                    double t = Math.Abs(sta2 - sta1) < 0.001
                               ? 0.5
                               : (crossingSta - sta1) / (sta2 - sta1);
                    t = Math.Max(0.0, Math.Min(1.0, t));
                    pipeElevAtCross = pipeOrigStart.Z + t * (pipeOrigEnd.Z - pipeOrigStart.Z);

                    // ── Compute duck elevation ───────────────────────────────────
                    if (goUp)
                    {
                        // UP duck — pressure pipe arcs OVER the crossing.
                        // Clearance = 1.0 ft above crossing crown + full pressure pipe
                        // outer diameter so that the pipe body fully clears.
                        // CL = crossingCrown + 1.0 + pipeOuterDiameter
                        elevLow = crossingCrownElev + 1.0 + pipe.OuterDiameter;
                    }
                    else
                    {
                        // DOWN duck — pressure pipe ducks UNDER the crossing.
                        // Pressure pipe crown (CL + pipeRadius) must clear
                        // crossing invert by 1.0 ft.
                        // CL = crossingInvert - 1.0 - pipeRadius
                        elevLow = crossingInvElev - 1.0 - pipeRadius;
                    }

                    // ── Compute duck stations (45 degree intersection) ───────────
                    staLL = crossingSta - HorizOffset;
                    staLR = crossingSta + HorizOffset;

                    double m_pipe = Math.Abs(sta2 - sta1) < 0.001 ? 0 : (pipeOrigEnd.Z - pipeOrigStart.Z) / (sta2 - sta1);
                    double deltaZ = Math.Abs(elevLow - pipeElevAtCross);
                    double m_pipe_left  = goUp ? -m_pipe : m_pipe;
                    double m_pipe_right = goUp ? m_pipe : -m_pipe;

                    double L = (deltaZ + HorizOffset) / (1.0 + m_pipe_left);
                    double R = (deltaZ + HorizOffset) / (1.0 + m_pipe_right);

                    staUL = crossingSta - L;
                    staUR = crossingSta + R;

                    // Upper bends at original pipe grade
                    elevUL = pipeElevAtCross + m_pipe * (staUL - crossingSta);
                    elevUR = pipeElevAtCross + m_pipe * (staUR - crossingSta);


                    tx.Abort();
                }

                // ── Step 4: Report geometry ──────────────────────────────────────
                string duckDir   = goUp ? "UP (over)" : "DOWN (under)";
                string innerLabel = goUp ? "Top C/L  " : "Bottom C/L";
                double invertClearance = goUp
                    ? (elevLow - pipeRadius) - crossingCrownElev
                    : crossingInvElev - (elevLow + pipeRadius);

                ed.WriteMessage("\n");
                ed.WriteMessage("\n  ╔══════════════════════════════════════════════════════════╗");
                ed.WriteMessage($"\n  ║          EEE BEND — DUCK {duckDir,-6} GEOMETRY              ║");
                ed.WriteMessage("\n  ╠══════════════════════════════════════════════════════════╣");
                ed.WriteMessage($"\n  ║  Crossing pipe        : {crossingPipeName,-30}      ║");
                ed.WriteMessage($"\n  ║  Crossing invert      : {crossingInvElev,8:F3} ft                  ║");
                ed.WriteMessage($"\n  ║  Crossing crown       : {crossingCrownElev,8:F3} ft                  ║");
                ed.WriteMessage($"\n  ║  Pipe C/L at crossing : {pipeElevAtCross,8:F3} ft                  ║");
                ed.WriteMessage($"\n  ║  {innerLabel} (duck)    : {elevLow,8:F3} ft (clr {invertClearance:F2} ft)  ║");
                ed.WriteMessage("\n  ╠══════════════════════════════════════════════════════════╣");
                ed.WriteMessage($"\n  ║  Outer-Left  Sta {staUL,10:F2} | C/L {elevUL,7:F3} ft            ║");
                ed.WriteMessage($"\n  ║  Inner-Left  Sta {staLL,10:F2} | C/L {elevLow,7:F3} ft            ║");
                ed.WriteMessage($"\n  ║  Inner-Right Sta {staLR,10:F2} | C/L {elevLow,7:F3} ft            ║");
                ed.WriteMessage($"\n  ║  Outer-Right Sta {staUR,10:F2} | C/L {elevUR,7:F3} ft            ║");
                ed.WriteMessage("\n  ╠══════════════════════════════════════════════════════════╣");
                ed.WriteMessage($"\n  ║  Inner span ±{HorizOffset:F0} ft  |  Bend angle 45° (1H:1V)         ║");
                ed.WriteMessage("\n  ╚══════════════════════════════════════════════════════════╝");

                if (invertClearance < 0.9)
                    ed.WriteMessage(
                        $"\n  WARNING: Clearance {invertClearance:F3} ft is less than expected — check geometry.");

                // ── Step 5: Modify pressure network ─────────────────────────────
                bool networkModified = TryApplyDuck(
                    db, pipeId, alignId,
                    staUL, staLL, staLR, staUR,
                    elevUL, elevUR, elevLow,
                    ed);

                ed.WriteMessage(networkModified
                    ? "\n  ✓ Pressure network modified — 4 vertical bends inserted."
                    : "\n  ⚠ Network auto-edit skipped — apply geometry manually.");
                ed.WriteMessage("\n═══════════════════════════════════════════════════════════\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[ALDT ERROR] EEEBEND: {ex.Message}\n");
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        //  PRESSURE NETWORK MODIFICATION — uses PressurePipeRun.AddVerticalBendByPVI
        // ════════════════════════════════════════════════════════════════════════

        private static bool TryApplyDuck(
            Database db,
            ObjectId pipeId,
            ObjectId alignId,
            double staUL, double staLL, double staLR, double staUR,
            double elevUL, double elevUR, double elevLow,
            Editor ed)
        {
            try
            {
                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var pipe = tx.GetObject(pipeId, OpenMode.ForRead) as CivilDB.PressurePipe;
                    if (pipe == null) { tx.Abort(); return false; }

                    // Get the parent pressure network
                    ObjectId networkId = pipe.NetworkId;
                    if (networkId.IsNull)
                    {
                        ed.WriteMessage("\n  Could not find pressure network on selected pipe.");
                        tx.Abort();
                        return false;
                    }

                    var network = tx.GetObject(networkId, OpenMode.ForWrite)
                                  as CivilDB.PressurePipeNetwork;
                    if (network == null)
                    {
                        ed.WriteMessage("\n  Could not open PressurePipeNetwork.");
                        tx.Abort();
                        return false;
                    }

                    // PressurePipeRunCollection is IEnumerable<PressurePipeRun> — iterate directly
                    // (PressurePipeRun is NOT a DBObject; do not use tx.GetObject on it)
                    CivilDB.PressurePipeRun? targetRun = null;
                    foreach (CivilDB.PressurePipeRun run in network.PipeRuns)
                    {
                        try
                        {
                            if (run.GetPartIds().Contains(pipeId))
                            {
                                targetRun = run;
                                break;
                            }
                        }
                        catch { }
                    }

                    if (targetRun == null)
                    {
                        ed.WriteMessage(
                            "\n  Selected pipe is not part of a PressurePipeRun." +
                            " Use a pipe that belongs to a pressure pipe run.");
                        tx.Abort();
                        return false;
                    }

                    // Upper bends at original pipe grade; inner bends at duck depth/height.
                    // Civil 3D computes the diagonal grade automatically from these PVI elevations.
                    targetRun.AddVerticalBendByPVI(staUL, elevUL);   // Upper-Left  – original grade
                    targetRun.AddVerticalBendByPVI(staLL, elevLow);  // Inner-Left  – duck depth
                    targetRun.AddVerticalBendByPVI(staLR, elevLow);  // Inner-Right – duck depth
                    targetRun.AddVerticalBendByPVI(staUR, elevUR);   // Upper-Right – original grade

                    tx.Commit();
                    ed.WriteMessage("\n  4 vertical bends inserted into the pressure pipe run.");
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n  Note: Network auto-edit skipped ({ex.Message}).");
                return false;
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        //  HELPERS
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Finds the pressure pipe closest (by elevation) to the pick point within
        /// a profile view, using alignment.StationOffset on pipe endpoints.
        /// </summary>
        private static ObjectId FindPressurePipeNear(
            CivilDB.ProfileView pv, Point3d pickPt,
            Transaction tx, Database db, Editor ed,
            CivilDB.Alignment aln)
        {
            double station = 0, elevation = 0;
            if (!pv.FindStationAndElevationAtXY(pickPt.X, pickPt.Y, ref station, ref elevation))
                return ObjectId.Null;

            ObjectId bestId   = ObjectId.Null;
            double   bestDist = double.MaxValue;
            int      checked_ = 0;

            var bt = tx.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            var ms = tx.GetObject(
                     bt![BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;

            foreach (ObjectId id in ms!)
            {
                try
                {
                    if (tx.GetObject(id, OpenMode.ForRead) is not CivilDB.PressurePipe pp)
                        continue;

                    checked_++;

                    // Get pipe station range using the profile view's alignment
                    double sta1 = 0, off1 = 0, sta2 = 0, off2 = 0;
                    aln.StationOffset(pp.StartPoint.X, pp.StartPoint.Y, ref sta1, ref off1);
                    aln.StationOffset(pp.EndPoint.X,   pp.EndPoint.Y,   ref sta2, ref off2);

                    double minSta = Math.Min(sta1, sta2);
                    double maxSta = Math.Max(sta1, sta2);

                    // Clamp picked station to pipe range and interpolate elevation
                    double clampedSta = Math.Max(minSta, Math.Min(maxSta, station));
                    double t = Math.Abs(sta2 - sta1) < 0.001
                               ? 0.5
                               : (clampedSta - sta1) / (sta2 - sta1);
                    t = Math.Max(0.0, Math.Min(1.0, t));

                    double pipeZ = pp.StartPoint.Z + t * (pp.EndPoint.Z - pp.StartPoint.Z);
                    double dist  = Math.Abs(elevation - pipeZ);

                    // Station distance penalty when click is outside pipe's range
                    double staDist = Math.Abs(station - clampedSta);
                    double combined = dist + (staDist > 1.0 ? staDist * 0.1 : 0);

                    if (combined < bestDist)
                    {
                        bestDist = combined;
                        bestId   = id;
                    }
                }
                catch { }
            }

            ed.WriteMessage($"\n  Searched {checked_} pressure pipe(s), best match dist = {bestDist:F2}");
            return bestDist <= 15.0 ? bestId : ObjectId.Null;
        }

        /// <summary>
        /// Returns the first ProfileView whose bounds contain the given world point.
        /// </summary>
        private static CivilDB.ProfileView? FindProfileViewAtPoint(
            Point3d pt, Transaction tx, Database db)
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
                    if (pv.FindStationAndElevationAtXY(pt.X, pt.Y, ref sta, ref elev))
                        return pv;
                }
                catch { }
            }
            return null;
        }
    }
}
