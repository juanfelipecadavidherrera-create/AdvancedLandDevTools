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
        private const double CLEARANCE_ABOVE   = 1.0;
        private const double CLEARANCE_BELOW   = 0.5;
        private const double CIRCLE_RADIUS     = 1.0;   // profile-view drawing units
        private const int    COLOR_GREEN       = 3;
        private const int    COLOR_RED         = 1;

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
                ed.WriteMessage("\n═══════════════════════════════════════════════════════════");

                var per1   = PickPart(ed, "\n  Select FIRST fitting (path start): ");
                if (per1 == null) { ed.WriteMessage("\n  Cancelled.\n"); return; }

                var per2   = PickPart(ed, "\n  Select SECOND fitting (path end): ");
                if (per2 == null) { ed.WriteMessage("\n  Cancelled.\n"); return; }

                var perDir = PickPart(ed,
                    "\n  Select pipe connected to first fitting (sets path direction): ");
                if (perDir == null) { ed.WriteMessage("\n  Cancelled.\n"); return; }

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

                var align = tx.GetObject(pv.AlignmentId, OpenMode.ForRead) as CivilDB.Alignment;
                if (align == null)
                { ed.WriteMessage("\n  Could not read alignment from profile view."); tx.Abort(); return; }

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

                // ── Find crossing pipes from every other network ───────────────
                var crossings = new List<CrossingInfo>();
                CollectCrossings(db, tx, align, pressNetId, stMin, stMax, pv, crossings);
                ed.WriteMessage($"\n  Crossings found: {crossings.Count}");

                // ── Draw circles, report clearances ───────────────────────────
                var btr = tx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite)
                          as BlockTableRecord;
                int okCount = 0, badCount = 0;

                foreach (var ci in crossings)
                {
                    // Pressure pipe at crossing station
                    double pressElev  = InterpolateElev(ci.Station, segments);
                    if (double.IsNaN(pressElev)) continue;

                    double pressOuterR = InterpolateRadius(ci.Station, segments);
                    double pressCrown  = pressElev + pressOuterR;
                    double pressInvert = pressElev - pressOuterR;

                    double crossInvert = ci.Invert;
                    double crossCrown  = ci.Invert + ci.OuterDiameter;

                    // Above/below: compare actual centerline Z values
                    bool   above    = ci.CenterZ > pressElev;
                    double clr      = above ? crossInvert - pressCrown   // invert of crossing − crown of pressure
                                            : pressInvert - crossCrown;  // invert of pressure − crown of crossing
                    double required = above ? CLEARANCE_ABOVE : CLEARANCE_BELOW;
                    bool   isOk     = clr >= required;

                    if (isOk) okCount++; else badCount++;

                    // Place circle at the profile-view ellipse of the crossing pipe:
                    // bottom of ellipse (MinPoint.Y) when crossing is above pressure pipe,
                    // top of ellipse (MaxPoint.Y) when crossing is below.
                    // Falls back to FindXYAtStationAndElevation if entity not found.
                    double cx = 0, cy = 0;
                    var partPt = FindPartProfilePoint(
                        ci.PipeId, pv, ci.Station, atBottom: above, db, tx);
                    if (partPt.HasValue)
                    {
                        cx = partPt.Value.X;
                        cy = partPt.Value.Y;
                    }
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
                ed.WriteMessage($"\n  OK: {okCount}   Violations: {badCount}\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[RRNETWORKCHECK ERROR] {ex.Message}\n");
            }
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
