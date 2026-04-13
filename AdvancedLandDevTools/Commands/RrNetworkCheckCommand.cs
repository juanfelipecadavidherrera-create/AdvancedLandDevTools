using System;
using System.Collections.Generic;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp   = Autodesk.AutoCAD.ApplicationServices.Application;
using CivilDB = Autodesk.Civil.DatabaseServices;
using AdvancedLandDevTools.Helpers;

namespace AdvancedLandDevTools.Commands
{
    /// <summary>
    /// RRNETWORKCHECK — Checks vertical clearance between a pressure network path
    /// (defined by two profile-view fittings) and every crossing pipe from other networks.
    ///
    /// Rules (both in ft):
    ///   Crossing pipe ABOVE pressure pipe → required clearance ≥ 1.0 ft
    ///     measured as: crossing_invert − pressure_crown
    ///   Crossing pipe BELOW pressure pipe → required clearance ≥ 0.5 ft
    ///     measured as: pressure_invert − crossing_crown
    ///
    /// Output: 1-unit-radius circles drawn in Layer 0 at the crossing pipe invert
    ///   elevation in the profile view.  Green = OK, Red = violation.
    /// </summary>
    public class RrNetworkCheckCommand
    {
        private const string DXF_NETWORK_PART  = "AECC_GRAPH_PROFILE_NETWORK_PART";
        private const string DXF_PRESSURE_PART = "AECC_GRAPH_PROFILE_PRESSURE_PART";
        private const double CLEARANCE_ABOVE   = 1.0;   // crossing pipe above pressure — min clearance
        private const double CLEARANCE_BELOW   = 0.5;   // crossing pipe below pressure — min clearance
        private const double COVER_WARNING     = 4.0;   // path pipe surface cover — desired minimum
        private const double COVER_VIOLATION   = 3.0;   // path pipe surface cover — hard minimum
        private const double CIRCLE_RADIUS     = 1.0;   // profile-view drawing units
        private const int    COLOR_GREEN       = 3;
        private const int    COLOR_RED         = 1;

        // ── Auto-bend geometry (mirrors EeeBendCommand constants) ─────────────
        private const double BEND_HORIZ_OFFSET = 10.0;   // ft from crossing centre to inner bends
        private const double BEND_DIAG_LEG     = 11.6;   // diagonal leg in profile-view units
        private const double BEND_SLOPE_H      =  1.0;   // horizontal slope component
        private const double BEND_SLOPE_V      = 10.0;   // vertical slope component (profile-view)
        private const double BEND_VEXAG        = 10.0;   // vertical exaggeration
        private const double BEND_MIN_COVER    =  2.5;   // min surface cover (ft) to allow UP duck
        private static readonly double BendSlopeMag  = Math.Sqrt(BEND_SLOPE_H * BEND_SLOPE_H + BEND_SLOPE_V * BEND_SLOPE_V);
        private static readonly double BendLegDeltaH = BEND_DIAG_LEG * BEND_SLOPE_H / BendSlopeMag;
        private static readonly double BendLegDeltaV = BEND_DIAG_LEG * BEND_SLOPE_V / BendSlopeMag / BEND_VEXAG;

        private static readonly string[] _partIdProps = {
            "ModelPartId", "PartId", "NetworkPartId", "BasePipeId",
            "SourceObjectId", "EntityId", "ComponentObjectId",
            "ReferencedObjectId", "SourceId", "PipeId", "StructureId"
        };

        // ─────────────────────────────────────────────────────────────────────
        private sealed class PathSegment
        {
            public double StationStart, StationEnd;
            public double ElevStart, ElevEnd;     // centerline elevation
            public double OuterRadius;
        }

        private sealed class CrossingInfo
        {
            public string   PipeName      = "";
            public ObjectId PipeId;
            public double   Station;
            public double   CenterZ;       // centerline Z at the crossing point (above/below check)
            public double   Invert;        // inner invert at the crossing station
            public double   OuterDiameter; // full outer diameter (crown = Invert + OuterDiameter)
        }

        // ─────────────────────────────────────────────────────────────────────
        [CommandMethod("RRNETWORKCHECK", CommandFlags.Modal)]
        public void Execute()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;

            try
            {
                if (!Engine.LicenseManager.EnsureLicensed()) return;

                ed.WriteMessage("\n═══════════════════════════════════════════════════════════");
                ed.WriteMessage("\n  RR Network Check  |  Pressure Network Clearance");
                ed.WriteMessage("\n  Select two fittings to define path start and end,");
                ed.WriteMessage("\n  then a pipe from the first fitting to set direction.");
                ed.WriteMessage("\n  Surface is auto-detected from the profile view alignment.");
                ed.WriteMessage("\n═══════════════════════════════════════════════════════════");

                var per1   = PickPart(ed, "\n  Select FIRST fitting (path start): ");
                if (per1 == null) { ed.WriteMessage("\n  Cancelled.\n"); return; }

                var per2   = PickPart(ed, "\n  Select SECOND fitting (path end): ");
                if (per2 == null) { ed.WriteMessage("\n  Cancelled.\n"); return; }

                var perDir = PickPart(ed,
                    "\n  Select pipe connected to first fitting (sets path direction): ");
                if (perDir == null) { ed.WriteMessage("\n  Cancelled.\n"); return; }

                // ── Ask about bends — surface will be auto-detected inside the transaction ──
                ObjectId surfaceId = ObjectId.Null;   // resolved inside transaction
                bool drawBends = false;
                {
                    var kwdOpts = new PromptKeywordOptions(
                        "\n  Draw automatic bends for violations? [Yes/No] <No>: ");
                    kwdOpts.Keywords.Add("Yes");
                    kwdOpts.Keywords.Add("No");
                    kwdOpts.AllowNone = true;
                    var kwdRes = ed.GetKeywords(kwdOpts);
                    if (kwdRes.Status == PromptStatus.Cancel) { ed.WriteMessage("\n  Cancelled.\n"); return; }
                    drawBends = kwdRes.Status == PromptStatus.OK && kwdRes.StringResult == "Yes";
                }

                using var tx = db.TransactionManager.StartTransaction();

                // ── Detect profile view ───────────────────────────────────────
                ObjectId pvId = FindProfileViewFromPoint(per1, db, tx, ed);
                if (pvId.IsNull)
                {
                    ed.WriteMessage("\n  Could not detect profile view from selection.");
                    tx.Abort(); return;
                }
                var pv = tx.GetObject(pvId, OpenMode.ForRead) as CivilDB.ProfileView;
                if (pv == null) { tx.Abort(); return; }

                // ── Resolve proxies → model-space IDs ────────────────────────
                ObjectId fitting1Id = ResolvePartId(tx.GetObject(per1.ObjectId,   OpenMode.ForRead));
                ObjectId fitting2Id = ResolvePartId(tx.GetObject(per2.ObjectId,   OpenMode.ForRead));
                ObjectId dirPipeId  = ResolvePartId(tx.GetObject(perDir.ObjectId, OpenMode.ForRead));

                if (fitting1Id.IsNull || fitting2Id.IsNull || dirPipeId.IsNull)
                {
                    ed.WriteMessage("\n  Could not resolve one or more selected parts.");
                    tx.Abort(); return;
                }

                var fitting1 = tx.GetObject(fitting1Id, OpenMode.ForRead) as CivilDB.PressureFitting;
                var fitting2 = tx.GetObject(fitting2Id, OpenMode.ForRead) as CivilDB.PressureFitting;
                var dirPipe  = tx.GetObject(dirPipeId,  OpenMode.ForRead) as CivilDB.PressurePipe;

                if (fitting1 == null || fitting2 == null)
                { ed.WriteMessage("\n  Both selected parts must be pressure fittings."); tx.Abort(); return; }
                if (dirPipe == null)
                { ed.WriteMessage("\n  Direction selection must be a pressure pipe."); tx.Abort(); return; }

                ObjectId pressNetId = fitting1.NetworkId;

                ObjectId alignId = pv.AlignmentId;
                var align = tx.GetObject(alignId, OpenMode.ForRead) as CivilDB.Alignment;
                if (align == null)
                { ed.WriteMessage("\n  Could not read alignment from profile view."); tx.Abort(); return; }

                // ── Auto-detect TIN surface from the alignment's surface profiles ──
                surfaceId = AutoDetectSurface(align, tx, ed);
                if (drawBends && surfaceId.IsNull)
                    ed.WriteMessage("\n  No surface found — auto-bend skipped (cover check unavailable).");

                // ── Build path ────────────────────────────────────────────────
                var pathPipeIds = BuildPath(
                    fitting1Id, fitting2Id, dirPipeId, pressNetId, db, tx, ed);
                if (pathPipeIds == null || pathPipeIds.Count == 0)
                {
                    ed.WriteMessage("\n  No connected path found between the two fittings.");
                    tx.Abort(); return;
                }
                ed.WriteMessage($"\n  Path found: {pathPipeIds.Count} segment(s).");

                // ── Station/elevation segments for the path ───────────────────
                var segments  = new List<PathSegment>();
                double stMin  = double.MaxValue;
                double stMax  = double.MinValue;

                foreach (ObjectId pid in pathPipeIds)
                {
                    var pp = tx.GetObject(pid, OpenMode.ForRead) as CivilDB.PressurePipe;
                    if (pp == null) continue;

                    double sta1 = 0, off1 = 0, sta2 = 0, off2 = 0;
                    align.StationOffset(pp.StartPoint.X, pp.StartPoint.Y, ref sta1, ref off1);
                    align.StationOffset(pp.EndPoint.X,   pp.EndPoint.Y,   ref sta2, ref off2);

                    segments.Add(new PathSegment
                    {
                        StationStart = sta1,
                        StationEnd   = sta2,
                        ElevStart    = pp.StartPoint.Z,
                        ElevEnd      = pp.EndPoint.Z,
                        OuterRadius  = pp.OuterDiameter / 2.0
                    });

                    stMin = Math.Min(stMin, Math.Min(sta1, sta2));
                    stMax = Math.Max(stMax, Math.Max(sta1, sta2));
                }

                ed.WriteMessage($"\n  Station range: {stMin:F2} – {stMax:F2}");

                // ── Surface cover check for path pipes ────────────────────────
                if (!surfaceId.IsNull)
                    CheckPathCover(pathPipeIds, surfaceId, align, tx, ed);

                // ── Find crossing pipes from every other network ───────────────
                var crossings = new List<CrossingInfo>();
                CollectCrossings(db, tx, align, pressNetId, stMin, stMax, pv, crossings);
                ed.WriteMessage($"\n  Crossings found: {crossings.Count}");

                // ── Draw circles, report crossing clearances ──────────────────
                var btr = tx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite)
                          as BlockTableRecord;
                int okCount = 0, badCount = 0;
                var violations = new List<CrossingInfo>();

                foreach (var ci in crossings)
                {
                    double pressElev   = InterpolateElev(ci.Station, segments);
                    if (double.IsNaN(pressElev)) continue;

                    double pressOuterR = InterpolateRadius(ci.Station, segments);
                    double pressCrown  = pressElev + pressOuterR;
                    double pressInvert = pressElev - pressOuterR;

                    double crossInvert = ci.Invert;
                    double crossCrown  = ci.Invert + ci.OuterDiameter;

                    bool   above    = ci.CenterZ > pressElev;
                    double clr      = above ? crossInvert - pressCrown
                                            : pressInvert - crossCrown;
                    double required = above ? CLEARANCE_ABOVE : CLEARANCE_BELOW;
                    bool   isOk     = clr >= required;

                    if (isOk) okCount++; else { badCount++; violations.Add(ci); }

                    double cx = 0, cy = 0;
                    var partPt = FindPartProfilePoint(
                        ci.PipeId, pv, ci.Station, atBottom: above, db, tx);
                    if (partPt.HasValue) { cx = partPt.Value.X; cy = partPt.Value.Y; }
                    else
                    {
                        double circleElev = above ? crossInvert : crossCrown;
                        if (!pv.FindXYAtStationAndElevation(ci.Station, circleElev, ref cx, ref cy))
                            continue;
                    }

                    var circle = new Circle(new Point3d(cx, cy, 0), Vector3d.ZAxis, CIRCLE_RADIUS);
                    circle.ColorIndex = isOk ? COLOR_GREEN : COLOR_RED;
                    circle.Layer      = "0";
                    btr!.AppendEntity(circle);
                    tx.AddNewlyCreatedDBObject(circle, true);

                    string tag = isOk ? "OK" : "VIOLATION";
                    string dir = above ? "above" : "below";
                    ed.WriteMessage(
                        $"\n  [{tag}] '{ci.PipeName}' ({dir})  " +
                        $"sta {ci.Station:F2}  " +
                        $"cross.inv={crossInvert:F3}  press.crown={pressCrown:F3}  " +
                        $"clr {clr:F2} ft (req {required:F2})");
                }

                tx.Commit();
                ed.WriteMessage($"\n\n  ═══ RRNETWORKCHECK COMPLETE ═══");
                ed.WriteMessage($"\n  Crossings — OK: {okCount}   Violations: {badCount}");

                if (drawBends && !surfaceId.IsNull && violations.Count > 0)
                    ApplyAutoBends(violations, crossings, segments, pathPipeIds,
                                   pressNetId, alignId, surfaceId, db, ed);
                ed.WriteMessage("\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[RRNETWORKCHECK ERROR] {ex.Message}\n");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Automatically insert EEEBEND-style vertical bends for each violation.
        //
        //  Direction priority:
        //    UP   = pressure pipe arcs OVER the crossing.
        //           elevInner = crossCrown + 1.0 + pressOuterR
        //           Allowed only when surfaceZ − (elevInner + pressOuterR) ≥ BEND_MIN_COVER
        //    DOWN = pressure pipe ducks UNDER the crossing (fallback).
        //           elevInner = crossInvert − 1.0
        //
        //  4 PVIs per crossing: staUL, staLL (inner), staLR (inner), staUR
        //    staLL/LR = crossing sta ± BEND_HORIZ_OFFSET
        //    staUL/UR = staLL/LR ∓ BendLegDeltaH  (outside the inner span)
        // ─────────────────────────────────────────────────────────────────────
        private static void ApplyAutoBends(
            List<CrossingInfo> violations,
            List<CrossingInfo> allCrossings,  // all crossings (OK + violations) for re-check
            List<PathSegment>  segments,
            List<ObjectId>     pathPipeIds,
            ObjectId           pressNetId,
            ObjectId           alignId,
            ObjectId           surfaceId,
            Database           db,
            Editor             ed)
        {
            ed.WriteMessage("\n\n  ── Auto-bend application ──");
            try
            {
                using var tx = db.TransactionManager.StartTransaction();

                var surface = tx.GetObject(surfaceId, OpenMode.ForRead) as CivilDB.TinSurface;
                var align   = tx.GetObject(alignId,   OpenMode.ForRead) as CivilDB.Alignment;
                var network = tx.GetObject(pressNetId, OpenMode.ForWrite) as CivilDB.PressurePipeNetwork;

                if (surface == null || align == null || network == null)
                { ed.WriteMessage("\n  Cannot open network/surface — bends skipped."); tx.Abort(); return; }

                CivilDB.PressurePipeRun? run = null;
                foreach (CivilDB.PressurePipeRun r in network.PipeRuns)
                {
                    try { if (r.GetPartIds().Contains(pathPipeIds[0])) { run = r; break; } }
                    catch { }
                }
                if (run == null)
                { ed.WriteMessage("\n  Could not find pressure pipe run — bends skipped."); tx.Abort(); return; }

                int applied = 0;
                foreach (var vi in violations)
                {
                    double pressOuterR = InterpolateRadius(vi.Station, segments);
                    double crossCrown  = vi.Invert + vi.OuterDiameter;
                    double crossInvert = vi.Invert;

                    double px = 0, py = 0;
                    align.PointLocation(vi.Station, 0, ref px, ref py);

                    double surfZ = double.NaN;
                    try { surfZ = surface.FindElevationAtXY(px, py); }
                    catch { }

                    double staLL = vi.Station - BEND_HORIZ_OFFSET;
                    double staLR = vi.Station + BEND_HORIZ_OFFSET;
                    double staUL = staLL - BendLegDeltaH;
                    double staUR = staLR + BendLegDeltaH;

                    // ── Try UP ────────────────────────────────────────────────
                    double elevInnerUp  = crossCrown  + 1.0 + pressOuterR;
                    double pressCrownUp = elevInnerUp + pressOuterR;
                    double elevOuterUp  = elevInnerUp - BendLegDeltaV;
                    bool   coverOkUp    = !double.IsNaN(surfZ)
                                         && (surfZ - pressCrownUp) >= BEND_MIN_COVER;
                    string? upConflict  = null;
                    if (coverOkUp)
                        upConflict = CheckBendClearance(
                            vi.Station, staUL, staLL, staLR, staUR,
                            elevOuterUp, elevInnerUp, pressOuterR,
                            allCrossings, segments);

                    // ── Try DOWN ──────────────────────────────────────────────
                    double elevInnerDown = crossInvert - 1.0;
                    double elevOuterDown = elevInnerDown + BendLegDeltaV;
                    string? downConflict = CheckBendClearance(
                        vi.Station, staUL, staLL, staLR, staUR,
                        elevOuterDown, elevInnerDown, pressOuterR,
                        allCrossings, segments);

                    // ── Pick direction ────────────────────────────────────────
                    bool   goUp;
                    double elevInner, elevOuter;
                    string bendLabel;

                    if (coverOkUp && upConflict == null)
                    {
                        goUp = true; elevInner = elevInnerUp; elevOuter = elevOuterUp;
                        bendLabel = "UP";
                    }
                    else if (downConflict == null)
                    {
                        goUp = false; elevInner = elevInnerDown; elevOuter = elevOuterDown;
                        bendLabel = "DOWN";
                    }
                    else
                    {
                        // Normal DOWN conflicts with another pipe in the bend range.
                        // Find the lowest invert among ALL crossings in [staUL, staUR]
                        // and build a deeper DOWN bend that clears all of them.
                        double lowestInvert = crossInvert;  // start from current violation pipe
                        string lowestPipeName = vi.PipeName;
                        foreach (var ci2 in allCrossings)
                        {
                            if (ci2.Station < staUL - 1.0 || ci2.Station > staUR + 1.0) continue;
                            if (ci2.Invert < lowestInvert)
                            {
                                lowestInvert    = ci2.Invert;
                                lowestPipeName  = ci2.PipeName;
                            }
                        }

                        double elevInnerDeep  = lowestInvert - 1.0;
                        double elevOuterDeep  = elevInnerDeep + BendLegDeltaV;
                        string? deepConflict  = CheckBendClearance(
                            vi.Station, staUL, staLL, staLR, staUR,
                            elevOuterDeep, elevInnerDeep, pressOuterR,
                            allCrossings, segments);

                        if (deepConflict == null)
                        {
                            goUp      = false;
                            elevInner = elevInnerDeep;
                            elevOuter = elevOuterDeep;
                            bendLabel = $"DOWN(deep, ref '{lowestPipeName}' inv {lowestInvert:F3})";
                        }
                        else
                        {
                            // All options exhausted — report and skip
                            string upReason = !coverOkUp
                                ? (double.IsNaN(surfZ)
                                   ? "no surface"
                                   : $"cover {surfZ - pressCrownUp:F2} ft < {BEND_MIN_COVER} ft")
                                : $"conflicts with {upConflict}";
                            ed.WriteMessage(
                                $"\n  [SKIP] '{vi.PipeName}' sta {vi.Station:F2}" +
                                $"  UP→ {upReason}" +
                                $"  |  DOWN→ {downConflict}" +
                                $"  |  DOWN(deep)→ {deepConflict}");
                            continue;
                        }
                    }

                    // ── Insert PVIs ───────────────────────────────────────────
                    try
                    {
                        run.AddVerticalBendByPVI(staUL, elevOuter);
                        run.AddVerticalBendByPVI(staLL, elevInner);
                        run.AddVerticalBendByPVI(staLR, elevInner);
                        run.AddVerticalBendByPVI(staUR, elevOuter);
                        applied++;

                        string dir = goUp ? "UP" : "DOWN";
                        string note = goUp
                            ? $"cover {surfZ - pressCrownUp:F2} ft"
                            : !coverOkUp && !double.IsNaN(surfZ)
                                ? $"UP cover {surfZ - pressCrownUp:F2} ft insufficient"
                                : "";
                        ed.WriteMessage(
                            $"\n  [BEND {dir}] '{vi.PipeName}'  sta {vi.Station:F2}" +
                            $"  inner C/L {elevInner:F3}  {note}");
                    }
                    catch (System.Exception ex)
                    {
                        ed.WriteMessage($"\n  Bend failed at sta {vi.Station:F2}: {ex.Message}");
                    }
                }

                tx.Commit();
                ed.WriteMessage($"\n  {applied}/{violations.Count} bend(s) applied.");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n  Auto-bend error: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Analytically validate a proposed bend against all other crossings
        //  in the affected station range [staUL, staUR].
        //
        //  Builds three PathSegments representing the bent profile:
        //    staUL→staLL : transition (elevOuter → elevInner)
        //    staLL→staLR : inner flat at elevInner
        //    staLR→staUR : transition (elevInner → elevOuter)
        //
        //  Returns null if all nearby crossings still clear.
        //  Returns a description of the first pipe that would violate clearance.
        // ─────────────────────────────────────────────────────────────────────
        private static string? CheckBendClearance(
            double             fixedStation,
            double staUL, double staLL, double staLR, double staUR,
            double elevOuter,  double elevInner,
            double pressOuterR,
            List<CrossingInfo> allCrossings,
            List<PathSegment>  originalSegments)
        {
            var bendSegs = new List<PathSegment>
            {
                new PathSegment { StationStart=staUL, StationEnd=staLL,
                                  ElevStart=elevOuter, ElevEnd=elevInner, OuterRadius=pressOuterR },
                new PathSegment { StationStart=staLL, StationEnd=staLR,
                                  ElevStart=elevInner, ElevEnd=elevInner, OuterRadius=pressOuterR },
                new PathSegment { StationStart=staLR, StationEnd=staUR,
                                  ElevStart=elevInner, ElevEnd=elevOuter, OuterRadius=pressOuterR }
            };

            foreach (var ci in allCrossings)
            {
                if (Math.Abs(ci.Station - fixedStation) < 0.5) continue; // skip the pipe being fixed
                if (ci.Station < staUL - 1.0 || ci.Station > staUR + 1.0) continue;

                double pressElev = InterpolateElev(ci.Station, bendSegs);
                if (double.IsNaN(pressElev))
                    pressElev = InterpolateElev(ci.Station, originalSegments);
                if (double.IsNaN(pressElev)) continue;

                double pressCrown  = pressElev + pressOuterR;
                double pressInvert = pressElev - pressOuterR;
                double crossInvert = ci.Invert;
                double crossCrown  = ci.Invert + ci.OuterDiameter;
                bool   above       = ci.CenterZ > pressElev;
                double clr         = above ? crossInvert - pressCrown
                                           : pressInvert - crossCrown;
                double required    = above ? CLEARANCE_ABOVE : CLEARANCE_BELOW;

                if (clr < required)
                    return $"'{ci.PipeName}' sta {ci.Station:F2} clr {clr:F2} ft";
            }
            return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Traverse pressure network from fitting1 → fitting2 via startPipe.
        //  Returns ordered list of PressurePipe ObjectIds along the path,
        //  or null if not reachable.
        // ─────────────────────────────────────────────────────────────────────
        private static List<ObjectId>? BuildPath(
            ObjectId fitting1Id, ObjectId fitting2Id, ObjectId startPipeId,
            ObjectId netId, Database db, Transaction tx, Editor ed)
        {
            // Scan model space: build adjacency map fitting → [pipe, ...]
            // and validate start pipe connects to fitting1.
            var fittingToPipes = new Dictionary<ObjectId, List<ObjectId>>();
            var pipeFittings   = new Dictionary<ObjectId, (ObjectId start, ObjectId end)>();

            var btr = tx.GetObject(db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
            if (btr == null) return null;

            foreach (ObjectId id in btr)
            {
                try
                {
                    var pp = tx.GetObject(id, OpenMode.ForRead) as CivilDB.PressurePipe;
                    if (pp == null || pp.NetworkId != netId) continue;

                    var sf = pp.StartPartId;
                    var ef = pp.EndPartId;
                    pipeFittings[id] = (sf, ef);

                    if (!sf.IsNull)
                    {
                        if (!fittingToPipes.ContainsKey(sf)) fittingToPipes[sf] = new();
                        fittingToPipes[sf].Add(id);
                    }
                    if (!ef.IsNull)
                    {
                        if (!fittingToPipes.ContainsKey(ef)) fittingToPipes[ef] = new();
                        fittingToPipes[ef].Add(id);
                    }
                }
                catch { }
            }

            if (!pipeFittings.TryGetValue(startPipeId, out var startEnds))
            {
                ed.WriteMessage("\n  Direction pipe not found in pressure network.");
                return null;
            }

            // Verify startPipe connects to fitting1
            if (startEnds.start != fitting1Id && startEnds.end != fitting1Id)
            {
                ed.WriteMessage("\n  Direction pipe does not connect to the first fitting.");
                return null;
            }

            // Greedy traversal: always pick the next pipe whose far end is closest to fitting2
            var path         = new List<ObjectId>();
            var visitedFits  = new HashSet<ObjectId> { fitting1Id };
            ObjectId curFit  = fitting1Id;
            ObjectId curPipe = startPipeId;

            // Pre-cache fitting positions for distance check
            var fitPos = new Dictionary<ObjectId, Point3d>();
            foreach (ObjectId id in btr)
            {
                try
                {
                    var f = tx.GetObject(id, OpenMode.ForRead) as CivilDB.PressureFitting;
                    if (f != null && f.NetworkId == netId) fitPos[id] = f.Position;
                }
                catch { }
            }
            var f2Pos = fitPos.TryGetValue(fitting2Id, out var p2) ? p2 : Point3d.Origin;

            const int MAX_ITER = 500;
            for (int i = 0; i < MAX_ITER; i++)
            {
                path.Add(curPipe);

                // Far fitting of current pipe
                var (ps, pe) = pipeFittings.TryGetValue(curPipe, out var ends)
                    ? ends : (ObjectId.Null, ObjectId.Null);
                ObjectId nextFit = (ps == curFit) ? pe : ps;

                if (nextFit.IsNull)
                { ed.WriteMessage("\n  Path broken — pipe has no far-end fitting."); return null; }

                if (nextFit == fitting2Id) break;   // reached destination

                if (visitedFits.Contains(nextFit))
                { ed.WriteMessage("\n  Path forms a loop before reaching the second fitting."); return null; }

                visitedFits.Add(nextFit);

                if (!fittingToPipes.TryGetValue(nextFit, out var candidates) || candidates.Count == 0)
                { ed.WriteMessage("\n  Dead end — no pipe exits from an intermediate fitting."); return null; }

                // Among candidates (excluding the pipe we came from), pick the one
                // whose far end is closest to fitting2. Also prioritise a direct connection.
                ObjectId nextPipe    = ObjectId.Null;
                double   bestDistSq  = double.MaxValue;

                foreach (ObjectId cand in candidates)
                {
                    if (cand == curPipe) continue;
                    var (cs, ce) = pipeFittings.TryGetValue(cand, out var ce2) ? ce2 : (ObjectId.Null, ObjectId.Null);
                    ObjectId farFit = (cs == nextFit) ? ce : cs;

                    if (visitedFits.Contains(farFit)) continue;

                    // Direct hit?
                    if (farFit == fitting2Id) { nextPipe = cand; break; }

                    // Distance heuristic
                    var pos = fitPos.TryGetValue(farFit, out var fp) ? fp : Point3d.Origin;
                    double dSq = (pos - f2Pos).LengthSqrd;
                    if (dSq < bestDistSq) { bestDistSq = dSq; nextPipe = cand; }
                }

                if (nextPipe.IsNull)
                { ed.WriteMessage("\n  Could not continue path from an intermediate fitting."); return null; }

                curFit  = nextFit;
                curPipe = nextPipe;
            }

            return path;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Check surface cover for every pipe in the selected pressure path.
        //  Samples the TIN surface at each pipe's start and end XY.
        //  Cover = surface_Z − pipe_crown_Z  (crown = centerline Z + outerRadius).
        //  Thresholds:
        //    < COVER_VIOLATION (3 ft) → VIOLATION
        //    < COVER_WARNING   (4 ft) → WARNING
        //    ≥ COVER_WARNING         → OK
        // ─────────────────────────────────────────────────────────────────────
        private static void CheckPathCover(
            List<ObjectId>    pathPipeIds,
            ObjectId          surfaceId,
            CivilDB.Alignment align,
            Transaction       tx,
            Editor            ed)
        {
            CivilDB.TinSurface? surface = null;
            try { surface = tx.GetObject(surfaceId, OpenMode.ForRead) as CivilDB.TinSurface; }
            catch { }
            if (surface == null)
            { ed.WriteMessage("\n  Could not open surface — cover check skipped."); return; }

            ed.WriteMessage("\n\n  ── Surface cover check (path pipes) ──");

            int ok = 0, warn = 0, viol = 0;

            foreach (ObjectId pid in pathPipeIds)
            {
                CivilDB.PressurePipe? pp = null;
                try { pp = tx.GetObject(pid, OpenMode.ForRead) as CivilDB.PressurePipe; }
                catch { }
                if (pp == null) continue;

                double outerR = pp.OuterDiameter / 2.0;
                double sta1 = 0, off1 = 0, sta2 = 0, off2 = 0;
                align.StationOffset(pp.StartPoint.X, pp.StartPoint.Y, ref sta1, ref off1);
                align.StationOffset(pp.EndPoint.X,   pp.EndPoint.Y,   ref sta2, ref off2);

                // Sample at start, mid, and end
                var checkPoints = new (Point3d pt, double sta, double pipeZ)[]
                {
                    (pp.StartPoint, sta1, pp.StartPoint.Z),
                    (new Point3d(
                        (pp.StartPoint.X + pp.EndPoint.X) / 2.0,
                        (pp.StartPoint.Y + pp.EndPoint.Y) / 2.0,
                        (pp.StartPoint.Z + pp.EndPoint.Z) / 2.0),
                     (sta1 + sta2) / 2.0,
                     (pp.StartPoint.Z + pp.EndPoint.Z) / 2.0),
                    (pp.EndPoint, sta2, pp.EndPoint.Z)
                };

                foreach (var (pt, sta, pipeZ) in checkPoints)
                {
                    double surfZ;
                    try { surfZ = surface.FindElevationAtXY(pt.X, pt.Y); }
                    catch { continue; }   // outside surface boundary — skip

                    double crown = pipeZ + outerR;
                    double cover = surfZ - crown;

                    string status;
                    if (cover < COVER_VIOLATION)      { status = "VIOLATION"; viol++; }
                    else if (cover < COVER_WARNING)   { status = "WARNING";   warn++; }
                    else                              { status = "OK";        ok++;   }

                    if (status != "OK")
                        ed.WriteMessage(
                            $"\n  [{status}] '{pp.Name}'  sta {sta:F2}" +
                            $"  crown {crown:F3}  surf {surfZ:F3}  cover {cover:F2} ft" +
                            $"  (min {COVER_VIOLATION:F0} ft / desired {COVER_WARNING:F0} ft)");
                }
            }

            ed.WriteMessage(
                $"\n  Cover check — OK: {ok}   Warnings: {warn}   Violations: {viol}");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Scan gravity pipes + other-network pressure pipes for crossings
        //  with the alignment.
        //
        //  Only crossings that fall inside BOTH the profile view station window
        //  [pv.StationStart, pv.StationEnd] AND the path station range
        //  [stMin, stMax] are kept — this ensures we only report pipes that are
        //  actually visible in the drawn profile view and relevant to the path.
        //
        //  Geometric intersection is performed via PipeAlignmentIntersector
        //  (Entity.IntersectWith first, endpoint heuristic as fallback).
        //  The crossings returned carry PipeCenterlineZ at the exact crossing
        //  point; outer invert is derived as  centerZ − outerRadius.
        // ─────────────────────────────────────────────────────────────────────
        private static void CollectCrossings(
            Database db, Transaction tx,
            CivilDB.Alignment align, ObjectId pressNetId,
            double stMin, double stMax,
            CivilDB.ProfileView pv,
            List<CrossingInfo> crossings)
        {
            double pvStart = pv.StationStart;
            double pvEnd   = pv.StationEnd;

            var btr = tx.GetObject(db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
            if (btr == null) return;

            foreach (ObjectId id in btr)
            {
                try
                {
                    var obj = tx.GetObject(id, OpenMode.ForRead);

                    if (obj is CivilDB.Pipe gp)
                    {
                        double outerD    = gp.OuterDiameterOrWidth;
                        double innerR    = gp.InnerDiameterOrWidth / 2.0;
                        Point3d startPt  = gp.StartPoint;
                        Point3d endPt    = gp.EndPoint;
                        // Inner invert at each end: centerline Z minus inner radius
                        // (same pattern as InvertPullUpEngine)
                        double startInv  = startPt.Z - innerR;
                        double endInv    = endPt.Z   - innerR;

                        foreach (var c in PipeAlignmentIntersector.FindCrossings(id, align, tx))
                        {
                            if (c.Station < pvStart - 0.5 || c.Station > pvEnd + 0.5) continue;
                            if (c.Station < stMin - 1.0   || c.Station > stMax + 1.0)  continue;

                            double t       = ComputeT(c.IntersectionPointWCS, startPt, endPt);
                            double centerZ = startPt.Z + t * (endPt.Z - startPt.Z);
                            double invert  = startInv  + t * (endInv  - startInv);

                            crossings.Add(new CrossingInfo
                            {
                                PipeName      = gp.Name,
                                PipeId        = id,
                                Station       = c.Station,
                                CenterZ       = centerZ,
                                Invert        = invert,
                                OuterDiameter = outerD
                            });
                        }
                    }
                    else if (obj is CivilDB.PressurePipe pp && pp.NetworkId != pressNetId)
                    {
                        double outerD   = pp.OuterDiameter;
                        double outerR   = outerD / 2.0;
                        Point3d startPt = pp.StartPoint;
                        Point3d endPt   = pp.EndPoint;
                        double startInv = startPt.Z - outerR;
                        double endInv   = endPt.Z   - outerR;

                        foreach (var c in PipeAlignmentIntersector.FindCrossings(id, align, tx))
                        {
                            if (c.Station < pvStart - 0.5 || c.Station > pvEnd + 0.5) continue;
                            if (c.Station < stMin - 1.0   || c.Station > stMax + 1.0)  continue;

                            double t       = ComputeT(c.IntersectionPointWCS, startPt, endPt);
                            double centerZ = startPt.Z + t * (endPt.Z - startPt.Z);
                            double invert  = startInv  + t * (endInv  - startInv);

                            crossings.Add(new CrossingInfo
                            {
                                PipeName      = pp.Name,
                                PipeId        = id,
                                Station       = c.Station,
                                CenterZ       = centerZ,
                                Invert        = invert,
                                OuterDiameter = outerD
                            });
                        }
                    }
                }
                catch { }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Find the profile-view proxy entity (AECC_GRAPH_PROFILE_NETWORK_PART /
        //  AECC_GRAPH_PROFILE_PRESSURE_PART) for crossingPipeId inside pv, then
        //  return the bottom (atBottom=true) or top of its bounding box.
        //
        //  atBottom=true  → MinPoint.Y (bottom of ellipse) used when crossing
        //                   pipe is ABOVE pressure pipe (closest to pressure crown)
        //  atBottom=false → MaxPoint.Y (top of ellipse) used when crossing pipe
        //                   is BELOW pressure pipe (closest to pressure invert)
        // ─────────────────────────────────────────────────────────────────────
        private static (double X, double Y)? FindPartProfilePoint(
            ObjectId            crossingPipeId,
            CivilDB.ProfileView pv,
            double              crossingStation,
            bool                atBottom,
            Database            db,
            Transaction         tx)
        {
            try
            {
                // Profile view extents — used to confirm the entity lives in this view
                Extents3d pvExt = ((Entity)pv).GeometricExtents;

                // X coordinate of the crossing station inside the profile view
                double stX = 0, stY = 0;
                if (!pv.FindXYAtStationAndElevation(
                        crossingStation, pv.ElevationMin, ref stX, ref stY))
                    return null;

                var btr = tx.GetObject(db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                if (btr == null) return null;

                (double X, double Y)? best = null;
                double bestDist = double.MaxValue;

                foreach (ObjectId id in btr)
                {
                    try
                    {
                        string dxf = id.ObjectClass.DxfName;
                        if (dxf != DXF_NETWORK_PART && dxf != DXF_PRESSURE_PART) continue;

                        var proxy = tx.GetObject(id, OpenMode.ForRead) as DBObject;
                        if (proxy == null) continue;

                        // Must resolve to our target pipe
                        if (ResolvePartId(proxy) != crossingPipeId) continue;

                        var ent = proxy as Entity;
                        if (ent == null) continue;

                        Extents3d ext = ent.GeometricExtents;

                        // Center of ellipse must be inside the profile view bounds
                        double cx = (ext.MinPoint.X + ext.MaxPoint.X) / 2.0;
                        double cy = (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0;
                        if (cx < pvExt.MinPoint.X || cx > pvExt.MaxPoint.X) continue;
                        if (cy < pvExt.MinPoint.Y || cy > pvExt.MaxPoint.Y) continue;

                        // Pick the entity whose center X is closest to the expected station X
                        double dist = Math.Abs(cx - stX);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            double pointY = atBottom ? ext.MinPoint.Y : ext.MaxPoint.Y;
                            best = (cx, pointY);
                        }
                    }
                    catch { }
                }

                return best;
            }
            catch { return null; }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Parameter t (0–1) of crossPt projected onto the pipe axis in plan.
        // ─────────────────────────────────────────────────────────────────────
        private static double ComputeT(Point3d crossPt, Point3d pipeStart, Point3d pipeEnd)
        {
            double dx   = pipeEnd.X - pipeStart.X;
            double dy   = pipeEnd.Y - pipeStart.Y;
            double len2 = dx * dx + dy * dy;
            if (len2 < 1e-9) return 0.0;
            double t = ((crossPt.X - pipeStart.X) * dx +
                        (crossPt.Y - pipeStart.Y) * dy) / len2;
            return Math.Max(0.0, Math.Min(1.0, t));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Interpolate pressure path centerline elevation at a station
        // ─────────────────────────────────────────────────────────────────────
        private static double InterpolateElev(double sta, List<PathSegment> segs)
        {
            foreach (var s in segs)
            {
                double lo = Math.Min(s.StationStart, s.StationEnd);
                double hi = Math.Max(s.StationStart, s.StationEnd);
                if (sta < lo - 0.1 || sta > hi + 0.1) continue;
                double t = hi - lo < 1e-9
                    ? 0.5
                    : (sta - s.StationStart) / (s.StationEnd - s.StationStart);
                return s.ElevStart + Math.Max(0, Math.Min(1, t)) * (s.ElevEnd - s.ElevStart);
            }
            return double.NaN;
        }

        private static double InterpolateRadius(double sta, List<PathSegment> segs)
        {
            foreach (var s in segs)
            {
                double lo = Math.Min(s.StationStart, s.StationEnd);
                double hi = Math.Max(s.StationStart, s.StationEnd);
                if (sta >= lo - 0.1 && sta <= hi + 0.1) return s.OuterRadius;
            }
            return segs.Count > 0 ? segs[0].OuterRadius : 0.0;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers shared with PROFOFF / PVSTYLE pattern
        // ─────────────────────────────────────────────────────────────────────
        private static PromptEntityResult? PickPart(Editor ed, string prompt)
        {
            var opt = new PromptEntityOptions(prompt) { AllowNone = false };
            var res = ed.GetEntity(opt);
            return res.Status == PromptStatus.OK ? res : null;
        }

        private static ObjectId ResolvePartId(DBObject proxy)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type  = proxy.GetType();
            foreach (string name in _partIdProps)
            {
                try
                {
                    var prop = type.GetProperty(name, flags);
                    if (prop?.PropertyType == typeof(ObjectId))
                    {
                        var val = (ObjectId)prop.GetValue(proxy)!;
                        if (!val.IsNull) return val;
                    }
                }
                catch { }
            }
            return proxy.ObjectId;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Auto-detect TIN surface from the alignment's surface profiles.
        //  Iterates all profiles on the alignment; returns the SurfaceId of the
        //  first surface-sampled profile found, preferring EG/Existing-named ones.
        // ─────────────────────────────────────────────────────────────────────
        private static ObjectId AutoDetectSurface(
            CivilDB.Alignment align, Transaction tx, Editor ed)
        {
            var flags  = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            ObjectId bestSurfId  = ObjectId.Null;
            string   bestDesc    = "";
            bool     bestIsEG    = false;

            var profIds = align.GetProfileIds();
            ed.WriteMessage($"\n  [DIAG] Alignment '{align.Name}' has {profIds.Count} profile(s).");

            foreach (ObjectId profId in profIds)
            {
                try
                {
                    var prof = tx.GetObject(profId, OpenMode.ForRead) as CivilDB.Profile;
                    if (prof == null) continue;

                    string typeName = prof.GetType().Name;
                    ed.WriteMessage($"\n  [DIAG] Profile '{prof.Name}'  type={typeName}");

                    // Dump all properties whose name contains "Surface" or "Id" (excluding ObjectId base props)
                    foreach (var prop in prof.GetType().GetProperties(flags))
                    {
                        string pn = prop.Name;
                        if (!pn.Contains("Surface", StringComparison.OrdinalIgnoreCase) &&
                            !pn.Contains("SurfId",  StringComparison.OrdinalIgnoreCase)) continue;
                        string val = "?";
                        try { val = prop.GetValue(prof)?.ToString() ?? "null"; } catch { val = "threw"; }
                        ed.WriteMessage($"\n  [DIAG]   .{pn} [{prop.PropertyType.Name}] = {val}");
                    }

                    // Probe known property names that could reference the sampled surface
                    ObjectId surfId = ObjectId.Null;
                    foreach (string candidate in new[] {
                        "SurfaceId", "SampleSurfaceId", "SampleSourceId",
                        "SourceSurfaceId", "BaseSurfaceId", "TerrainSurfaceId" })
                    {
                        var prop = prof.GetType().GetProperty(candidate, flags);
                        if (prop?.PropertyType != typeof(ObjectId)) continue;
                        try
                        {
                            var id = (ObjectId)prop.GetValue(prof)!;
                            if (!id.IsNull) { surfId = id; break; }
                        }
                        catch { }
                    }

                    if (surfId.IsNull) continue;

                    var surf = tx.GetObject(surfId, OpenMode.ForRead) as CivilDB.TinSurface;
                    if (surf == null) continue;

                    string profName = prof.Name;
                    bool isEG = profName.IndexOf("EG",      StringComparison.OrdinalIgnoreCase) >= 0
                             || profName.IndexOf("Existing", StringComparison.OrdinalIgnoreCase) >= 0
                             || profName.IndexOf("Ground",   StringComparison.OrdinalIgnoreCase) >= 0
                             || profName.IndexOf("Natural",  StringComparison.OrdinalIgnoreCase) >= 0;

                    if (bestSurfId.IsNull || (isEG && !bestIsEG))
                    {
                        bestSurfId = surfId;
                        bestDesc   = $"'{surf.Name}' (via profile '{profName}')";
                        bestIsEG   = isEG;
                        if (isEG) break;
                    }
                }
                catch { }
            }

            if (!bestSurfId.IsNull)
                ed.WriteMessage($"\n  Surface auto-detected: {bestDesc}");
            else
                ed.WriteMessage("\n  No surface profile found on alignment — cover check skipped.");

            return bestSurfId;
        }

        private static ObjectId FindProfileViewFromPoint(
            PromptEntityResult per, Database db, Transaction tx, Editor ed)
        {
            try
            {
                var proxy = tx.GetObject(per.ObjectId, OpenMode.ForRead);
                var owner = tx.GetObject(proxy.OwnerId, OpenMode.ForRead) as CivilDB.ProfileView;
                if (owner != null) return owner.ObjectId;
            }
            catch { }

            try
            {
                var pt  = per.PickedPoint;
                var btr = tx.GetObject(db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                if (btr == null) return ObjectId.Null;
                foreach (ObjectId id in btr)
                {
                    CivilDB.ProfileView pv;
                    try { pv = tx.GetObject(id, OpenMode.ForRead) as CivilDB.ProfileView; }
                    catch { continue; }
                    if (pv == null) continue;
                    var ext = pv.GeometricExtents;
                    if (pt.X >= ext.MinPoint.X && pt.X <= ext.MaxPoint.X &&
                        pt.Y >= ext.MinPoint.Y && pt.Y <= ext.MaxPoint.Y)
                        return pv.ObjectId;
                }
            }
            catch { }
            return ObjectId.Null;
        }
    }
}
