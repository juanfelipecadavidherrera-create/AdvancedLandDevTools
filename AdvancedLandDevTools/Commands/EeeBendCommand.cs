using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
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
    ///   2. Click the crossing pipe in the profile — both station AND elevation
    ///      are read from the click (no typing required).
    ///   3. The command computes 4 bend points, modifies the pressure network,
    ///      and draws a magenta guide on layer EEEBEND-GUIDE.
    ///
    /// Duck geometry (slope 1H : 10V, leg = 11.6 ft):
    ///   Upper-Left  = crossing_sta − 10 ft,   original_elev
    ///   Lower-Left  = crossing_sta − 10 + ΔH, original_elev − ΔV
    ///   Lower-Right = crossing_sta + 10 − ΔH, original_elev − ΔV
    ///   Upper-Right = crossing_sta + 10 ft,   original_elev
    /// </summary>
    public class EeeBendCommand
    {
        // ── Duck geometry constants ────────────────────────────────────────────
        // The user specified "1 ft H : 10 ft V" as PROFILE VIEW units (10x vertical exag).
        // Real-world slope is 1H:1V.  DiagLegFt is also in profile-view units.
        private const double HorizOffset   = 10.0;   // ft from crossing centre to upper bends
        private const double DiagLegFt     = 11.6;   // diagonal leg length in profile-view units
        private const double SlopeH        = 1.0;    // horizontal slope component (profile view)
        private const double SlopeV        = 10.0;   // vertical slope component  (profile view)
        private const double ProfileVExag  = 10.0;   // profile view vertical exaggeration
        private static readonly double SlopeMag  = Math.Sqrt(SlopeH * SlopeH + SlopeV * SlopeV);
        private static readonly double LegDeltaH = DiagLegFt * SlopeH / SlopeMag;               // ≈ 1.154 ft real
        private static readonly double LegDeltaV = DiagLegFt * SlopeV / SlopeMag / ProfileVExag; // ≈ 1.154 ft real

        private const string GuideLayer = "EEEBEND-GUIDE";

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
                        // Try to find which profile view the user was clicking in
                        var pv2 = FindProfileViewAtPoint(pickPt, tx, db);
                        if (pv2 != null) { profileViewId = pv2.ObjectId; alignId = pv2.AlignmentId; }
                    }
                    else
                    {
                        // Clicked the profile view background, a label, or a fitting
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

                // ── Step 2: Click the crossing pipe in the profile ───────────────
                // Both the STATION and the ELEVATION of the crossing are read from
                // this single click — no numeric input needed.
                // For DOWN: click the invert (bottom of ellipse) of the crossing pipe.
                // For UP  : click the crown (top of ellipse) of the crossing pipe.
                ed.WriteMessage("\n  Click on the crossing pipe in the profile to set its location and elevation:");
                var ppoPt = new PromptPointOptions(
                    "\n  Click crossing pipe: ");
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

                // ── Step 3: Resolve geometry ─────────────────────────────────────
                double crossingSta     = 0;
                double crossingInvElev = 0;   // elevation from the click on the crossing pipe
                double pipeElevAtCross = 0;
                Point3d pipeOrigStart  = Point3d.Origin;
                Point3d pipeOrigEnd    = Point3d.Origin;

                Point3d ptUpperLeft  = Point3d.Origin;
                Point3d ptLowerLeft  = Point3d.Origin;
                Point3d ptLowerRight = Point3d.Origin;
                Point3d ptUpperRight = Point3d.Origin;
                double elevLow = 0;
                double elevUL  = 0;  // centerline at Upper-Left  bend = original pipe grade at staUL
                double elevUR  = 0;  // centerline at Upper-Right bend = original pipe grade at staUR
                double pipeRadius = 0;  // OuterDiameter / 2 of the selected pressure pipe
                double staUL = 0, staLL = 0, staLR = 0, staUR = 0;

                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var pipe = tx.GetObject(pipeId, OpenMode.ForRead) as CivilDB.PressurePipe;
                    if (pipe == null) { tx.Abort(); return; }

                    pipeOrigStart = pipe.StartPoint;
                    pipeOrigEnd   = pipe.EndPoint;

                    // Find the profile view that contains the crossing click
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

                    // Extract crossing station AND elevation from the user's click
                    if (!pv.FindStationAndElevationAtXY(
                            crossingPickPt.X, crossingPickPt.Y,
                            ref crossingSta, ref crossingInvElev))
                    {
                        ed.WriteMessage(
                            "\n  Crossing click is outside the profile view bounds.\n");
                        tx.Abort();
                        return;
                    }

                    ed.WriteMessage($"\n  Crossing → Station {crossingSta:F2}, Elevation {crossingInvElev:F3} ft");

                    // Use alignment.StationOffset on pipe endpoints to get their alignment stations
                    // (more reliable than pipe.StartStation which may be pipe-local)
                    double sta1 = 0, off1 = 0, sta2 = 0, off2 = 0;
                    aln.StationOffset(pipeOrigStart.X, pipeOrigStart.Y, ref sta1, ref off1);
                    aln.StationOffset(pipeOrigEnd.X,   pipeOrigEnd.Y,   ref sta2, ref off2);

                    double minSta = Math.Min(sta1, sta2);
                    double maxSta = Math.Max(sta1, sta2);

                    if (crossingSta < minSta - 1.0 || crossingSta > maxSta + 1.0)
                        ed.WriteMessage(
                            $"\n  WARNING: Crossing station {crossingSta:F2} is outside this pipe's range" +
                            $" [{minSta:F2}–{maxSta:F2}]. Proceeding anyway.");

                    // Interpolate pipe CENTERLINE elevation at crossing station (for display only)
                    double t = Math.Abs(sta2 - sta1) < 0.001
                               ? 0.5
                               : (crossingSta - sta1) / (sta2 - sta1);
                    t = Math.Max(0.0, Math.Min(1.0, t));
                    pipeElevAtCross = pipeOrigStart.Z + t * (pipeOrigEnd.Z - pipeOrigStart.Z);

                    // Station values of the 4 bend points:
                    //   Bottom bends (LL, LR) sit at exactly ±HorizOffset from crossing.
                    //   Upper bends (UL, UR) are LegDeltaH OUTSIDE the bottom bends —
                    //   the pipe descends from original grade INTO the ±10 ft bottom zone.
                    staLL = crossingSta - HorizOffset;
                    staLR = crossingSta + HorizOffset;
                    staUL = staLL - LegDeltaH;   // outside (lower station)
                    staUR = staLR + LegDeltaH;   // outside (higher station)

                    pipeRadius = pipe.OuterDiameter / 2.0;

                    if (goUp)
                    {
                        // UP duck — pressure pipe arcs OVER the crossing.
                        // User clicks the crown (top) of the crossing pipe.
                        //
                        // PVIs are inserted at the pressure pipe CENTERLINE, but clearance
                        // is measured from the pressure pipe INVERT (centerline − pipeRadius).
                        // Required: pressure_invert ≥ crossingCrown + 1.0
                        //   → centerline ≥ crossingCrown + 1.0 + pipeRadius
                        elevLow = crossingInvElev + 1.0 + pipeRadius;  // "elevLow" = inner (high) bends
                        elevUL  = elevLow - LegDeltaV;  // outer-left  transitions DOWN from high
                        elevUR  = elevLow - LegDeltaV;  // outer-right transitions DOWN from high
                    }
                    else
                    {
                        // DOWN duck — pressure pipe ducks UNDER the crossing.
                        // User clicks the invert (bottom) of the crossing pipe.
                        // Required clearance of 1.0 ft below crossing invert (centerline-based).
                        elevLow = crossingInvElev - 1.0;
                        elevUL  = elevLow + LegDeltaV;  // outer-left  transitions UP from low
                        elevUR  = elevLow + LegDeltaV;  // outer-right transitions UP from low
                    }

                    // Convert to 3D world points for the visual guide
                    ptUpperLeft  = StationElevToPoint3d(aln, staUL, elevUL);   // outer-left
                    ptLowerLeft  = StationElevToPoint3d(aln, staLL, elevLow);  // inner-left
                    ptLowerRight = StationElevToPoint3d(aln, staLR, elevLow);  // inner-right
                    ptUpperRight = StationElevToPoint3d(aln, staUR, elevUR);   // outer-right

                    tx.Abort();
                }

                // ── Step 4: Report geometry ──────────────────────────────────────
                string duckDir   = goUp ? "UP (over)" : "DOWN (under)";
                string innerLabel = goUp ? "Top C/L  " : "Bottom C/L";
                // Clearance measured at the inner span:
                //   DOWN: crossing_invert − elevLow  = 1.0 ft (centerline to invert of crossing)
                //   UP  : (elevLow − pipeRadius) − crossingInvElev = 1.0 ft (invert of press − crossing crown)
                double invertClearance = goUp
                    ? (elevLow - pipeRadius) - crossingInvElev
                    : crossingInvElev - elevLow;

                ed.WriteMessage("\n");
                ed.WriteMessage("\n  ╔══════════════════════════════════════════════════════════╗");
                ed.WriteMessage($"\n  ║          EEE BEND — DUCK {duckDir,-6} GEOMETRY              ║");
                ed.WriteMessage("\n  ╠══════════════════════════════════════════════════════════╣");
                ed.WriteMessage($"\n  ║  Pipe C/L at crossing  : {pipeElevAtCross,8:F3} ft                  ║");
                ed.WriteMessage($"\n  ║  Crossing click elev   : {crossingInvElev,8:F3} ft                  ║");
                ed.WriteMessage($"\n  ║  {innerLabel} (±1.0 ft)  : {elevLow,8:F3} ft (clr {invertClearance:F2} ft)  ║");
                ed.WriteMessage("\n  ╠══════════════════════════════════════════════════════════╣");
                ed.WriteMessage($"\n  ║  Outer-Left  Sta {staUL,10:F2} | C/L {elevUL,7:F3} ft            ║");
                ed.WriteMessage($"\n  ║  Inner-Left  Sta {staLL,10:F2} | C/L {elevLow,7:F3} ft            ║");
                ed.WriteMessage($"\n  ║  Inner-Right Sta {staLR,10:F2} | C/L {elevLow,7:F3} ft            ║");
                ed.WriteMessage($"\n  ║  Outer-Right Sta {staUR,10:F2} | C/L {elevUR,7:F3} ft            ║");
                ed.WriteMessage("\n  ╠══════════════════════════════════════════════════════════╣");
                ed.WriteMessage($"\n  ║  Inner span ±{HorizOffset:F0} ft  |  Leg ΔH {LegDeltaH:F3} ft               ║");
                ed.WriteMessage("\n  ╚══════════════════════════════════════════════════════════╝");

                if (invertClearance < 0.9)
                    ed.WriteMessage(
                        $"\n  WARNING: Clearance {invertClearance:F3} ft is less than expected — check your click location.");

                // ── Step 5: Modify pressure network ─────────────────────────────
                bool networkModified = TryApplyDuck(
                    db, pipeId, alignId,
                    staUL, staLL, staLR, staUR,
                    elevUL, elevUR, elevLow,
                    ed);

                // ── Step 6: Draw visual guide (always) ──────────────────────────
                DrawDuckGuide(db, pipeOrigStart, ptUpperLeft, ptLowerLeft,
                              ptLowerRight, ptUpperRight, pipeOrigEnd, ed);

                ed.WriteMessage(networkModified
                    ? "\n  Pressure network modified. Duck guide drawn on layer 'EEEBEND-GUIDE'."
                    : "\n  Guide drawn on layer 'EEEBEND-GUIDE' — apply geometry manually to the network.");
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

                    // Upper bends at original pipe grade; bottom bends at required clearance depth.
                    // Civil 3D computes the diagonal grade automatically from these PVI elevations.
                    targetRun.AddVerticalBendByPVI(staUL, elevUL);   // Upper-Left  – original grade
                    targetRun.AddVerticalBendByPVI(staLL, elevLow);  // Lower-Left  – bottom depth
                    targetRun.AddVerticalBendByPVI(staLR, elevLow);  // Lower-Right – bottom depth
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
        //  VISUAL GUIDE — magenta 3D polyline along the full duck path
        // ════════════════════════════════════════════════════════════════════════

        private static void DrawDuckGuide(
            Database db,
            Point3d pipeOrigStart,
            Point3d ptUpperLeft, Point3d ptLowerLeft,
            Point3d ptLowerRight, Point3d ptUpperRight,
            Point3d pipeOrigEnd,
            Editor  ed)
        {
            try
            {
                using (var tx = db.TransactionManager.StartTransaction())
                {
                    // Ensure guide layer exists (magenta)
                    var lt = tx.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
                    if (lt != null && !lt.Has(GuideLayer))
                    {
                        lt.UpgradeOpen();
                        var lr = new LayerTableRecord
                        {
                            Name  = GuideLayer,
                            Color = Color.FromColorIndex(ColorMethod.ByAci, 6)
                        };
                        lt.Add(lr);
                        tx.AddNewlyCreatedDBObject(lr, true);
                    }

                    var pline = new Polyline3d();
                    pline.Layer = GuideLayer;

                    var bt = tx.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                    var ms = tx.GetObject(
                             bt![BlockTableRecord.ModelSpace], OpenMode.ForWrite)
                             as BlockTableRecord;
                    ms!.AppendEntity(pline);
                    tx.AddNewlyCreatedDBObject(pline, true);

                    foreach (var pt in new[]
                        { pipeOrigStart, ptUpperLeft, ptLowerLeft,
                          ptLowerRight,  ptUpperRight, pipeOrigEnd })
                    {
                        var v = new PolylineVertex3d(pt);
                        pline.AppendVertex(v);
                        tx.AddNewlyCreatedDBObject(v, true);
                    }

                    tx.Commit();
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n  Guide draw failed: {ex.Message}");
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        //  HELPERS
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Converts alignment station + elevation to a 3D world point at offset=0.
        /// Uses Alignment.PointLocation (Civil 3D confirmed API).
        /// </summary>
        private static Point3d StationElevToPoint3d(
            CivilDB.Alignment aln, double station, double elevation)
        {
            double x = 0, y = 0;
            aln.PointLocation(station, 0.0, ref x, ref y);
            return new Point3d(x, y, elevation);
        }

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
