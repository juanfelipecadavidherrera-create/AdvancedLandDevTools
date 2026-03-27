using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Autodesk.AutoCAD.Runtime;
using AcDbPolyline = Autodesk.AutoCAD.DatabaseServices.Polyline;
using AdvancedLandDevTools.VehicleTracking.Core;
using AdvancedLandDevTools.VehicleTracking.Data;
using AdvancedLandDevTools.UI;

[assembly: CommandClass(typeof(AdvancedLandDevTools.VehicleTracking.Commands.VtDriveCommand))]
[assembly: CommandClass(typeof(AdvancedLandDevTools.VehicleTracking.Commands.VtEditCommand))]

namespace AdvancedLandDevTools.VehicleTracking.Commands
{
    // ═══════════════════════════════════════════════════════════════
    //  Data Models
    // ═══════════════════════════════════════════════════════════════

    internal class WaypointData
    {
        public Vec2 Position;   // front axle (tracking point)
        public double Heading;  // vehicle heading (front-facing direction)
        public bool IsReverse;
    }

    /// <summary>
    /// Pre-computed geometry for one path section between two waypoints.
    /// Computed ONCE by SectionSolver, then used for both jig preview and permanent output.
    /// </summary>
    internal class PathSection
    {
        public WaypointData Start = new();
        public WaypointData End = new();
        public bool IsError;
        public bool WasClamped;   // true = target unreachable, path shows max-turn arc

        public List<Vec2> CenterPts = new();     // front axle arc path
        public List<Vec2> LeftWheelPts = new();   // left rear wheel trace
        public List<Vec2> RightWheelPts = new();  // right rear wheel trace
        public List<Vec2> RearLeftPts = new();    // rear-left body corner trace (tail swing)
        public List<Vec2> RearRightPts = new();   // rear-right body corner trace (tail swing)

        /// <summary>Peak centerline steering angle during the section (max absolute value, with sign).
        /// Used for Ackermann wheel viz — represents the main turning phase, not the end correction.</summary>
        public double PeakSteerAngle;
    }

    // ═══════════════════════════════════════════════════════════════
    //  X Marker Block — single entity with center grip for drag editing
    // ═══════════════════════════════════════════════════════════════

    internal static class VtMarkerBlock
    {
        public const string NAME = "VT_XMARKER";

        public static ObjectId Ensure(Database db, Transaction tx)
        {
            var bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (bt.Has(NAME)) return bt[NAME];

            bt.UpgradeOpen();
            var btr = new BlockTableRecord { Name = NAME };
            var btrId = bt.Add(btr);
            tx.AddNewlyCreatedDBObject(btr, true);

            double sz = 1.5;
            var l1 = new Line(new Point3d(-sz, -sz, 0), new Point3d(sz, sz, 0));
            btr.AppendEntity(l1);
            tx.AddNewlyCreatedDBObject(l1, true);
            var l2 = new Line(new Point3d(sz, -sz, 0), new Point3d(-sz, sz, 0));
            btr.AppendEntity(l2);
            tx.AddNewlyCreatedDBObject(l2, true);
            return btrId;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  VtPathReactor — auto-detects grip-moved X markers and rebuilds
    //  adjacent arcs. No command needed: just grip-drag an X block.
    // ═══════════════════════════════════════════════════════════════

    internal static class VtPathReactor
    {
        private static readonly HashSet<ObjectId> _pending = new();
        private static bool _processing;
        private static bool _hooked;

        /// <summary>Set true to suppress the reactor (e.g. during DrawFinal).</summary>
        internal static bool Suppress { get; set; }

        public static void Hook(Document doc)
        {
            if (doc == null || _hooked) return;
            doc.Database.ObjectModified += OnObjectModified;
            doc.CommandEnded += OnCommandEnded;
            doc.CommandCancelled += (_, _) => _pending.Clear();
            _hooked = true;
        }

        private static void OnObjectModified(object? sender, ObjectEventArgs e)
        {
            if (_processing || Suppress) return;
            if (e.DBObject is BlockReference br && !br.IsErased)
            {
                try
                {
                    var xd = br.GetXDataForApplication(VtDriveCommand.XDATA_APP);
                    if (xd == null) return;
                    var vals = xd.AsArray();
                    // X markers have: appName, gid, wpIndex (int32)
                    if (vals.Length >= 3 && vals[2].TypeCode == (int)DxfCode.ExtendedDataInteger32)
                        _pending.Add(br.ObjectId);
                }
                catch { /* entity may not have xdata */ }
            }
        }

        private static void OnCommandEnded(object? sender, CommandEventArgs e)
        {
            if (_pending.Count == 0 || Suppress) return;

            var ids = new List<ObjectId>(_pending);
            _pending.Clear();

            _processing = true;
            try { RebuildFromMovedMarkers(ids); }
            catch (System.Exception ex)
            {
                Application.DocumentManager.MdiActiveDocument?.Editor
                    .WriteMessage($"\n  [VT] Auto-edit error: {ex.Message}\n");
            }
            finally { _processing = false; }
        }

        private static void RebuildFromMovedMarkers(List<ObjectId> movedIds)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;
            var ed = doc.Editor;

            // Group moved markers by group ID
            var moves = new Dictionary<string, List<(int idx, Vec2 newPos, ObjectId brId)>>();

            using (var tx = db.TransactionManager.StartTransaction())
            {
                foreach (var id in movedIds)
                {
                    if (id.IsErased) continue;
                    var br = tx.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    var xd = br.GetXDataForApplication(VtDriveCommand.XDATA_APP);
                    if (xd == null) continue;
                    var vals = xd.AsArray();
                    if (vals.Length < 3) continue;

                    string gid = (string)vals[1].Value;
                    int wpIdx = (int)vals[2].Value;
                    Vec2 pos = new(br.Position.X, br.Position.Y);

                    if (!moves.ContainsKey(gid)) moves[gid] = new();
                    moves[gid].Add((wpIdx, pos, id));
                }
                tx.Commit();
            }

            foreach (var kv in moves) ProcessGroupEdit(db, ed, kv.Key, kv.Value);
        }

        private static void ProcessGroupEdit(Database db, Editor ed, string gid,
            List<(int idx, Vec2 newPos, ObjectId brId)> markerMoves)
        {
            // Find path polyline with waypoints + all group entities
            string? vehSymbol = null;
            List<WaypointData>? waypoints = null;
            var groupIds = new List<ObjectId>();

            using (var tx = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                var btr = (BlockTableRecord)tx.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in btr)
                {
                    var ent = tx.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null || ent.IsErased) continue;
                    var xd = ent.GetXDataForApplication(VtDriveCommand.XDATA_APP);
                    if (xd == null) continue;
                    var vals = xd.AsArray();
                    if (vals.Length < 2 || (string)vals[1].Value != gid) continue;
                    groupIds.Add(id);
                    if (vals.Length >= 4 && waypoints == null && ent is AcDbPolyline)
                    {
                        vehSymbol = (string)vals[2].Value;
                        waypoints = VtDriveCommand.DeserializeWps((string)vals[3].Value);
                    }
                }
                tx.Commit();
            }

            if (waypoints == null || vehSymbol == null) return;

            // Find vehicle
            VehicleUnit? vehicle = null;
            foreach (var d in VehicleLibrary.GetDisplayList())
                if (d.Symbol == vehSymbol)
                {
                    vehicle = d.IsArticulated
                        ? VehicleLibrary.ArticulatedVehicles[d.Index].LeadUnit
                        : VehicleLibrary.SingleUnits[d.Index];
                    break;
                }
            if (vehicle == null) return;

            // Save originals for revert
            var origPositions = waypoints.Select(w => w.Position).ToList();
            var origHeadings = waypoints.Select(w => w.Heading).ToList();

            // Apply new positions
            foreach (var (idx, newPos, _) in markerMoves)
                if (idx > 0 && idx < waypoints.Count)
                    waypoints[idx].Position = newPos;

            // Rebuild ONLY sections adjacent to moved markers, keep others frozen
            var movedSet = markerMoves.Select(m => m.idx).ToHashSet();
            var newSecs = new List<PathSection>();
            bool valid = true;

            for (int i = 1; i < waypoints.Count; i++)
            {
                bool adjacent = movedSet.Contains(i) || movedSet.Contains(i - 1);

                if (adjacent)
                {
                    // Recompute this section — accept clamped (max-turn) arcs
                    var sec = SectionSolver.Solve(waypoints[i - 1].Position, waypoints[i - 1].Heading,
                        waypoints[i].Position, vehicle, waypoints[i].IsReverse,
                        VtDriveCommand._speedMph * 1.46667);
                    if (sec.IsError) { valid = false; break; }
                    // If clamped, snap waypoint to where vehicle actually arrives
                    if (sec.WasClamped)
                        waypoints[i].Position = sec.End.Position;
                    waypoints[i].Heading = sec.End.Heading;
                    newSecs.Add(sec);
                }
                else
                {
                    // Recompute with existing heading (position unchanged, should be near-identical)
                    var sec = SectionSolver.Solve(waypoints[i - 1].Position, waypoints[i - 1].Heading,
                        waypoints[i].Position, vehicle, waypoints[i].IsReverse,
                        VtDriveCommand._speedMph * 1.46667);
                    waypoints[i].Heading = sec.End.Heading;
                    newSecs.Add(sec);
                }
            }

            if (!valid)
            {
                // Revert block positions
                using var tx = db.TransactionManager.StartTransaction();
                foreach (var (idx, _, brId) in markerMoves)
                {
                    if (brId.IsErased || idx >= origPositions.Count) continue;
                    var br = tx.GetObject(brId, OpenMode.ForWrite) as BlockReference;
                    if (br != null)
                        br.Position = new Point3d(origPositions[idx].X, origPositions[idx].Y, 0);
                }
                tx.Commit();
                ed.WriteMessage("\n  [VT] Turn not possible — marker snapped back.\n");
                return;
            }

            // Erase old group entities + AutoCAD Group, redraw
            using (var tx = db.TransactionManager.StartTransaction())
            {
                foreach (var id in groupIds)
                {
                    if (id.IsErased) continue;
                    var ent = tx.GetObject(id, OpenMode.ForWrite) as Entity;
                    ent?.Erase();
                }
                string groupName = $"VTDRIVE_{gid}";
                var groupDict = (DBDictionary)tx.GetObject(db.GroupDictionaryId, OpenMode.ForWrite);
                if (groupDict.Contains(groupName))
                {
                    var grpId = groupDict.GetAt(groupName);
                    var grp = tx.GetObject(grpId, OpenMode.ForWrite) as Group;
                    grp?.Erase();
                }
                tx.Commit();
            }

            VtDriveCommand.DrawFinal(db, vehicle, waypoints, newSecs, vehSymbol);
            ed.WriteMessage("\n  [VT] Path updated.\n");
            ed.Regen();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Wheel Visualization Helpers (shared by DriveJig3 + LiveEditJig)
    //  Ackermann geometry, wheel rectangles, steering % label.
    // ═══════════════════════════════════════════════════════════════

    internal static class WheelViz
    {
        /// <summary>
        /// Compute inner/outer wheel angles from centerline steering angle
        /// using ideal Ackermann geometry.
        /// </summary>
        public static (double inner, double outer) AckermannAngles(
            double centerAngle, double wheelbase, double trackWidth)
        {
            if (Math.Abs(centerAngle) < 1e-9) return (0, 0);
            double R = wheelbase / Math.Tan(Math.Abs(centerAngle));
            double inner = Math.Atan(wheelbase / (R - trackWidth / 2.0));
            double outer = Math.Atan(wheelbase / (R + trackWidth / 2.0));
            double sign = Math.Sign(centerAngle);
            return (sign * inner, sign * outer);
        }

        /// <summary>Draw a single wheel rectangle at given position and rotation.</summary>
        public static void DrawWheelRect(Geometry g, double cx, double cy,
            double rot, double tireW, double tireH)
        {
            double hw = tireW / 2.0, hh = tireH / 2.0;
            double cosR = Math.Cos(rot), sinR = Math.Sin(rot);

            var pts = new Point3dCollection();
            double[] dx = { -hw, hw, hw, -hw, -hw };
            double[] dy = { -hh, -hh, hh, hh, -hh };
            for (int i = 0; i < 5; i++)
                pts.Add(new Point3d(
                    cx + dx[i] * cosR - dy[i] * sinR,
                    cy + dx[i] * sinR + dy[i] * cosR, 0));
            g.Polyline(pts, Vector3d.ZAxis, IntPtr.Zero);
        }

        /// <summary>
        /// Draw all 4 wheels with Ackermann angles + steering % label.
        /// </summary>
        public static void DrawWheels(Geometry g, SubEntityTraits s,
            VehicleUnit v, WaypointData wp, double steerAngle, bool ghost)
        {
            double wb = v.Wheelbase;
            double tw = v.TrackWidth;
            double tireW = tw * 0.10;
            double tireH = tireW * 2.5;

            double h = wp.Heading;
            double cosH = Math.Cos(h), sinH = Math.Sin(h);
            double px = -sinH, py = cosH;

            double rx = wp.Position.X - wb * cosH;
            double ry = wp.Position.Y - wb * sinH;
            double fx = wp.Position.X, fy = wp.Position.Y;

            s.Color = ghost ? (short)30 : (short)40;
            s.LineWeight = ghost ? LineWeight.LineWeight015 : LineWeight.LineWeight020;

            // Front wheels (Ackermann)
            var (innerA, outerA) = AckermannAngles(steerAngle, wb, tw);
            bool leftIsInner = steerAngle > 0;
            double leftAngle  = leftIsInner ? innerA : outerA;
            double rightAngle = leftIsInner ? outerA : innerA;

            DrawWheelRect(g, fx + (tw / 2.0) * px, fy + (tw / 2.0) * py,
                h + leftAngle, tireW, tireH);
            DrawWheelRect(g, fx - (tw / 2.0) * px, fy - (tw / 2.0) * py,
                h + rightAngle, tireW, tireH);

            // Rear wheels (fixed)
            DrawWheelRect(g, rx + (tw / 2.0) * px, ry + (tw / 2.0) * py,
                h, tireW, tireH);
            DrawWheelRect(g, rx - (tw / 2.0) * px, ry - (tw / 2.0) * py,
                h, tireW, tireH);

            // Steering % label
            double pct = v.MaxSteeringAngle > 1e-9
                ? Math.Abs(steerAngle) / v.MaxSteeringAngle * 100.0 : 0;
            string dir = steerAngle > 0.01 ? "L" : (steerAngle < -0.01 ? "R" : "");
            string label = $"{pct:F0}%{dir}";

            double lblOff = v.Width * 0.7;
            double lx = fx + lblOff * px, ly = fy + lblOff * py;
            s.Color = ghost ? (short)8 : (short)2;
            g.Text(new Point3d(lx, ly, 0), Vector3d.ZAxis, Vector3d.XAxis,
                tireH * 1.5, 1.0, 0, label);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Bicycle-Model Section Solver
    //  Rate-limited steering (lock-to-lock time) for realistic turns.
    //  Tracks rear wheels AND rear body corners (tail swing).
    // ═══════════════════════════════════════════════════════════════

    internal static class SectionSolver
    {
        private const double STEP = 0.5;       // ft between simulation points
        private const int MAX_STEPS = 5000;    // safety cap

        /// <param name="speedFps">Vehicle speed in feet/second (affects steering rate limit).</param>
        public static PathSection Solve(Vec2 startPos, double startHeading,
            Vec2 target, VehicleUnit v, bool isReverse, double speedFps = 22.0)
        {
            var sec = new PathSection
            {
                Start = new WaypointData { Position = startPos, Heading = startHeading, IsReverse = isReverse },
                End = new WaypointData { Position = target, IsReverse = isReverse }
            };

            double totalDist = startPos.DistanceTo(target);
            if (totalDist < 0.5) { sec.IsError = true; sec.End.Heading = startHeading; return sec; }

            double wb = v.Wheelbase;
            double maxSteer = v.MaxSteeringAngle;
            double lockTime = Math.Max(v.LockToLockTime, 0.5);

            // Rate limit: full lock-to-lock (2·maxSteer) takes lockTime seconds
            double maxRate = (2.0 * maxSteer) / lockTime;            // rad/s
            double dt = STEP / Math.Max(speedFps, 1.0);              // time per step
            double maxDeltaPerStep = maxRate * dt;                    // max steer change per step

            // Initial state — rear axle derived from front axle + heading
            double heading = startHeading;
            Vec2 rearAxle = new(startPos.X - wb * Math.Cos(heading),
                                startPos.Y - wb * Math.Sin(heading));
            double steer = 0.0;
            int dir = isReverse ? -1 : 1;

            // Record initial points
            sec.CenterPts.Add(startPos);
            AddTracks(sec, v, rearAxle, heading);

            double prevDist = totalDist;
            bool closePhase = false;
            double peakSteer = 0;

            for (int i = 0; i < MAX_STEPS; i++)
            {
                Vec2 front = new(rearAxle.X + wb * Math.Cos(heading),
                                 rearAxle.Y + wb * Math.Sin(heading));
                // When reversing, track distance from rear axle to target
                double d = isReverse ? rearAxle.DistanceTo(target) : front.DistanceTo(target);

                // Termination checks
                if (d < STEP * 0.5) break;
                if (closePhase && d > prevDist + 0.1) break;
                if (d < wb) closePhase = true;
                prevDist = d;

                // ── Steering controller (pure-pursuit style) ────────
                // When reversing, steer from the REAR toward the target
                // (like looking in your mirror and backing toward a spot)
                double desSteer;
                if (isReverse)
                {
                    // Rear heading = heading + PI (rear faces opposite of front)
                    double rearH = Norm(heading + Math.PI);
                    double bearingR = Math.Atan2(target.Y - rearAxle.Y, target.X - rearAxle.X);
                    double hErrR = Norm(bearingR - rearH);
                    double lookR = Math.Max(rearAxle.DistanceTo(target), wb * 0.5);
                    // Invert the steer sign: when rear needs to go left, front wheels go right
                    desSteer = Math.Clamp(
                        -Math.Atan(2.0 * wb * Math.Sin(hErrR) / lookR),
                        -maxSteer, maxSteer);
                }
                else
                {
                    double bearing = Math.Atan2(target.Y - front.Y, target.X - front.X);
                    double hErr = Norm(bearing - heading);
                    double lookAhead = Math.Max(d, wb * 0.5);
                    desSteer = Math.Clamp(
                        Math.Atan(2.0 * wb * Math.Sin(hErr) / lookAhead),
                        -maxSteer, maxSteer);
                }

                // Rate-limit the steering change
                double delta = Math.Clamp(desSteer - steer, -maxDeltaPerStep, maxDeltaPerStep);
                steer += delta;
                if (Math.Abs(steer) > Math.Abs(peakSteer)) peakSteer = steer;

                // ── Bicycle-model kinematic step ────────────────────
                rearAxle = new(rearAxle.X + STEP * dir * Math.Cos(heading),
                               rearAxle.Y + STEP * dir * Math.Sin(heading));
                heading = Norm(heading + STEP * dir * Math.Tan(steer) / wb);

                // Record updated positions
                front = new(rearAxle.X + wb * Math.Cos(heading),
                            rearAxle.Y + wb * Math.Sin(heading));
                sec.CenterPts.Add(front);
                AddTracks(sec, v, rearAxle, heading);
            }

            // Final position check
            Vec2 finalFront = new(rearAxle.X + wb * Math.Cos(heading),
                                  rearAxle.Y + wb * Math.Sin(heading));
            double finalDist = isReverse ? rearAxle.DistanceTo(target) : finalFront.DistanceTo(target);

            sec.PeakSteerAngle = peakSteer;

            if (finalDist <= STEP * 3.0)
            {
                // Reached target — snap last point to exact target
                if (sec.CenterPts.Count > 0)
                    sec.CenterPts[sec.CenterPts.Count - 1] = target;
                sec.End.Heading = heading;
                sec.End.Position = target;
            }
            else
            {
                // Target unreachable — keep the max-turn path as-is.
                // The path shows the tightest arc the vehicle CAN do.
                // End position = where the vehicle actually ended up.
                sec.End.Heading = heading;
                sec.End.Position = finalFront;
                sec.WasClamped = true;  // flag so callers know this wasn't exact
            }
            return sec;
        }

        /// <summary>Record rear wheel + rear body corner positions at one simulation step.</summary>
        private static void AddTracks(PathSection sec, VehicleUnit v, Vec2 rearAxle, double heading)
        {
            double ht = v.TrackWidth * 0.5;  // half track width (wheel-to-wheel)
            double hw = v.Width * 0.5;        // half body width
            double ro = v.RearOverhang;       // distance behind rear axle to bumper

            double fx = Math.Cos(heading), fy = Math.Sin(heading);
            double px = -fy, py = fx;  // perpendicular-left unit vector

            // Rear wheel positions (at rear axle, offset by half track width)
            sec.LeftWheelPts.Add(new Vec2(rearAxle.X + ht * px, rearAxle.Y + ht * py));
            sec.RightWheelPts.Add(new Vec2(rearAxle.X - ht * px, rearAxle.Y - ht * py));

            // Rear body corners (behind rear axle by rearOverhang, offset by half body width)
            double rcx = rearAxle.X - ro * fx, rcy = rearAxle.Y - ro * fy;
            sec.RearLeftPts.Add(new Vec2(rcx + hw * px, rcy + hw * py));
            sec.RearRightPts.Add(new Vec2(rcx - hw * px, rcy - hw * py));
        }

        public static double Norm(double a)
        {
            while (a > Math.PI) a -= 2 * Math.PI;
            while (a < -Math.PI) a += 2 * Math.PI;
            return a;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  VTDRIVE Command
    // ═══════════════════════════════════════════════════════════════

    public class VtDriveCommand
    {
        internal static double _speedMph = 15.0;
        internal const string XDATA_APP = "VTDRIVE";
        internal static VtDrivePanel? _panel;

        // Flags set by panel button callbacks (checked in jig loop)
        internal static bool _undoFlag;
        internal static bool _finishFlag;
        internal static bool _cancelFlag;
        internal static bool _acceptFlag;

        [CommandMethod("VTDRIVE", CommandFlags.Modal)]
        public void Execute()
        {
            try
            {
                if (!Engine.LicenseManager.EnsureLicensed()) return;
                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                Editor ed = doc.Editor;

                // Lazy-hook the reactor (MUST NOT be done in Initialize — see AppLoader.cs)
                VtPathReactor.Hook(doc);
                Database db = doc.Database;

                // ── Show WPF panel ───────────────────────────────────
                _undoFlag = _finishFlag = _cancelFlag = _acceptFlag = false;
                _panel = new VtDrivePanel();
                _panel.UndoRequested += () => { _undoFlag = true; };
                _panel.FinishRequested += () => { _finishFlag = true; };
                _panel.CancelRequested += () => { _cancelFlag = true; };
                _panel.AcceptRequested += () => { _acceptFlag = true; };
                _panel.Show();

                try { RunDrive(ed, db); }
                finally { _panel?.Close(); _panel = null; }
            }
            catch (System.Exception ex)
            {
                Application.DocumentManager.MdiActiveDocument?.Editor
                    .WriteMessage($"\n[VTDRIVE ERROR] {ex.Message}\n");
            }
        }

        private static void RunDrive(Editor ed, Database db)
        {
            if (_panel == null) return;

            // Wait for user to select vehicle and click OK-ish start
            _panel.SetStatus("Select vehicle above, then click in drawing to place front axle.");

            // ── Vehicle from panel ───────────────────────────────
            var selEntry = _panel.SelectedVehicle;
            if (selEntry == null) return;
            var sel = selEntry.Value;
            VehicleUnit vehicle = sel.Item4
                ? VehicleLibrary.ArticulatedVehicles[sel.Item5].LeadUnit
                : VehicleLibrary.SingleUnits[sel.Item5];

            // ── Start position ───────────────────────────────────
            _panel.SetStatus("Click to place vehicle front axle...");
            var p1 = ed.GetPoint(new PromptPointOptions("\n  Click to place vehicle front axle: "));
            if (p1.Status != PromptStatus.OK || _cancelFlag) return;
            Point3d startPt = p1.Value;

            _panel.SetStatus("Click to set heading direction...");
            var p2 = ed.GetPoint(new PromptPointOptions("\n  Click to set heading: ")
            { UseBasePoint = true, BasePoint = startPt });
            if (p2.Status != PromptStatus.OK || _cancelFlag) return;
            double initHeading = Math.Atan2(p2.Value.Y - startPt.Y, p2.Value.X - startPt.X);

            // Re-read vehicle in case user changed it while clicking
            selEntry = _panel.SelectedVehicle;
            if (selEntry == null) return;
            sel = selEntry.Value;
            vehicle = sel.Item4
                ? VehicleLibrary.ArticulatedVehicles[sel.Item5].LeadUnit
                : VehicleLibrary.SingleUnits[sel.Item5];
            _speedMph = _panel.SpeedMph;

            // ── Interactive drive loop ───────────────────────────
            _panel.SetStatus("Move mouse to preview. Click to commit. Enter to finish.", "#66BB6A");
            _panel.EnableFinish(true);

                var waypoints = new List<WaypointData>
                {
                    new WaypointData { Position = new Vec2(startPt.X, startPt.Y),
                                       Heading = initHeading, IsReverse = false }
                };
                var sections = new List<PathSection>();
                var jig = new DriveJig3(vehicle, waypoints, sections);

                while (true)
                {
                    if (_cancelFlag) return;
                    if (_finishFlag) break;

                    // Check panel undo flag (set by Undo button)
                    if (_undoFlag)
                    {
                        _undoFlag = false;
                        if (waypoints.Count > 1)
                        {
                            waypoints.RemoveAt(waypoints.Count - 1);
                            if (sections.Count > 0) sections.RemoveAt(sections.Count - 1);
                            jig.Refresh(waypoints, sections);
                            _panel?.SetStatus($"Undo. {waypoints.Count} waypoints.", "#FFB74D");
                            _panel?.EnableUndo(waypoints.Count > 1);
                        }
                        continue;
                    }

                    // Read current speed from panel each iteration
                    if (_panel != null) _speedMph = _panel.SpeedMph;

                    var jr = ed.Drag(jig);
                    if (jr.Status == PromptStatus.OK)
                    {
                        if (waypoints.Count > 1 && jig.IsInUndoZone)
                        {
                            waypoints.RemoveAt(waypoints.Count - 1);
                            if (sections.Count > 0) sections.RemoveAt(sections.Count - 1);
                            jig.Refresh(waypoints, sections);
                            _panel?.SetStatus($"Undo. {waypoints.Count} waypoints.", "#FFB74D");
                            _panel?.EnableUndo(waypoints.Count > 1);
                            continue;
                        }
                        var ps = jig.PreviewSection;
                        if (ps != null && !ps.IsError)
                        {
                            if (ps.WasClamped)
                                _panel?.SetStatus("Max turn applied (beyond min radius).", "#FFB74D");
                            else
                                _panel?.SetStatus($"Waypoint {waypoints.Count} placed.", "#66BB6A");

                            waypoints.Add(ps.End);
                            sections.Add(ps);
                            jig.Refresh(waypoints, sections);
                            _panel?.EnableUndo(true);
                        }
                        continue;
                    }
                    break;
                }

                if (waypoints.Count < 2) return;

                // ── Permanent output ─────────────────────────────────
                string gid = DrawFinal(db, vehicle, waypoints, sections, sel.Item2);
                _panel?.SetStatus($"Path created ({waypoints.Count} waypoints). Edit X markers below.", "#66BB6A");
                ed.Regen();

                // ── Auto-enter live edit mode via panel ──────────────
                _panel?.EnterEditMode();
                LiveEditLoop(ed, db, vehicle, waypoints, sections, sel.Item2, ref gid);
        }

        // ── Draw permanent entities ──────────────────────────────────
        /// <summary>Draw permanent entities. Returns group ID for later erase.</summary>
        internal static string DrawFinal(Database db, VehicleUnit vehicle,
            List<WaypointData> waypoints, List<PathSection> sections, string vehSymbol)
        {
            VtPathReactor.Suppress = true;
            try { return DrawFinalCore(db, vehicle, waypoints, sections, vehSymbol); }
            finally { VtPathReactor.Suppress = false; }
        }

        /// <summary>Erase all entities in a VT group and remove the AutoCAD Group.</summary>
        internal static void EraseGroup(Database db, string gid)
        {
            VtPathReactor.Suppress = true;
            try
            {
                using var tx = db.TransactionManager.StartTransaction();
                var bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tx.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    var ent = tx.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null || ent.IsErased) continue;
                    var xd = ent.GetXDataForApplication(XDATA_APP);
                    if (xd == null) continue;
                    var vals = xd.AsArray();
                    if (vals.Length >= 2 && (string)vals[1].Value == gid)
                    { ent.UpgradeOpen(); ent.Erase(); }
                }

                // Remove the AutoCAD Group
                string groupName = $"VTDRIVE_{gid}";
                var groupDict = (DBDictionary)tx.GetObject(db.GroupDictionaryId, OpenMode.ForWrite);
                if (groupDict.Contains(groupName))
                {
                    var grpId = groupDict.GetAt(groupName);
                    var grp = tx.GetObject(grpId, OpenMode.ForWrite) as Group;
                    grp?.Erase();
                }

                tx.Commit();
            }
            finally { VtPathReactor.Suppress = false; }
        }

        private static string DrawFinalCore(Database db, VehicleUnit vehicle,
            List<WaypointData> waypoints, List<PathSection> sections, string vehSymbol)
        {
            using var tx = db.TransactionManager.StartTransaction();
            var bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
            var btr = (BlockTableRecord)tx.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
            VtLayerManager.EnsureLayers(db, tx);

            // Register XData app
            var rat = (RegAppTable)tx.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (!rat.Has(XDATA_APP))
            {
                rat.UpgradeOpen();
                var rae = new RegAppTableRecord { Name = XDATA_APP };
                rat.Add(rae); tx.AddNewlyCreatedDBObject(rae, true);
            }

            string gid = Guid.NewGuid().ToString("N")[..12];
            var groupEntIds = new List<ObjectId>();

            // Concatenate all section geometry
            var allC = new List<Vec2> { waypoints[0].Position };
            var allL = new List<Vec2>();
            var allR = new List<Vec2>();
            var allRL = new List<Vec2>();  // rear-left body corner
            var allRR = new List<Vec2>();  // rear-right body corner
            foreach (var sec in sections)
            {
                for (int i = 1; i < sec.CenterPts.Count; i++) allC.Add(sec.CenterPts[i]);
                int skip = sec == sections[0] ? 0 : 1;
                for (int i = skip; i < sec.LeftWheelPts.Count; i++) allL.Add(sec.LeftWheelPts[i]);
                for (int i = skip; i < sec.RightWheelPts.Count; i++) allR.Add(sec.RightWheelPts[i]);
                for (int i = skip; i < sec.RearLeftPts.Count; i++) allRL.Add(sec.RearLeftPts[i]);
                for (int i = skip; i < sec.RearRightPts.Count; i++) allRR.Add(sec.RearRightPts[i]);
            }

            void Tag(Entity e) { e.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, XDATA_APP),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, gid)); }

            // Path centerline (cyan)
            var pathPl = MakePoly(allC, false);
            pathPl.LayerId = VtLayerManager.GetLayerId(db, tx, VtLayerManager.PATH);
            pathPl.ColorIndex = 4;
            btr.AppendEntity(pathPl); tx.AddNewlyCreatedDBObject(pathPl, true);
            groupEntIds.Add(pathPl.ObjectId);
            // XData with waypoint info for VTEDIT
            pathPl.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, XDATA_APP),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, gid),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, vehSymbol),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, SerializeWps(waypoints)));

            // Left wheel path (green)
            if (allL.Count > 1)
            {
                var lp = MakePoly(allL, false);
                lp.LayerId = VtLayerManager.GetLayerId(db, tx, VtLayerManager.INNER_SWEEP);
                lp.ColorIndex = 3;
                btr.AppendEntity(lp); tx.AddNewlyCreatedDBObject(lp, true); Tag(lp);
                groupEntIds.Add(lp.ObjectId);
            }
            // Right wheel path (green)
            if (allR.Count > 1)
            {
                var rp = MakePoly(allR, false);
                rp.LayerId = VtLayerManager.GetLayerId(db, tx, VtLayerManager.INNER_SWEEP);
                rp.ColorIndex = 3;
                btr.AppendEntity(rp); tx.AddNewlyCreatedDBObject(rp, true); Tag(rp);
                groupEntIds.Add(rp.ObjectId);
            }
            // Rear-left body corner path (red — outer swept envelope)
            if (allRL.Count > 1)
            {
                var rlp = MakePoly(allRL, false);
                rlp.LayerId = VtLayerManager.GetLayerId(db, tx, VtLayerManager.OUTER_SWEEP);
                rlp.ColorIndex = 1;
                btr.AppendEntity(rlp); tx.AddNewlyCreatedDBObject(rlp, true); Tag(rlp);
                groupEntIds.Add(rlp.ObjectId);
            }
            // Rear-right body corner path (red — outer swept envelope)
            if (allRR.Count > 1)
            {
                var rrp = MakePoly(allRR, false);
                rrp.LayerId = VtLayerManager.GetLayerId(db, tx, VtLayerManager.OUTER_SWEEP);
                rrp.ColorIndex = 1;
                btr.AppendEntity(rrp); tx.AddNewlyCreatedDBObject(rrp, true); Tag(rrp);
                groupEntIds.Add(rrp.ObjectId);
            }

            // Vehicle outlines + wheels + X markers at waypoints
            for (int i = 0; i < waypoints.Count; i++)
            {
                var wp = waypoints[i];
                Vec2 rear = new(wp.Position.X - vehicle.Wheelbase * Math.Cos(wp.Heading),
                                wp.Position.Y - vehicle.Wheelbase * Math.Sin(wp.Heading));
                var corners = SweptPathSolver.ComputeBodyCorners(vehicle, rear, wp.Heading);
                var vpPl = MakePoly(corners.ToList(), true);
                vpPl.LayerId = VtLayerManager.GetLayerId(db, tx, VtLayerManager.VEHICLE);
                vpPl.ColorIndex = wp.IsReverse ? (short)5 : (short)150;
                btr.AppendEntity(vpPl); tx.AddNewlyCreatedDBObject(vpPl, true); Tag(vpPl);
                groupEntIds.Add(vpPl.ObjectId);

                // ── Permanent wheel rectangles (Ackermann) ──
                double steerAngle = (i > 0 && i - 1 < sections.Count) ? sections[i - 1].PeakSteerAngle : 0;
                var wheelIds = DrawPermanentWheels(btr, tx, db, vehicle, wp, steerAngle);
                foreach (var wid in wheelIds)
                {
                    if (tx.GetObject(wid, OpenMode.ForWrite) is Entity we) Tag(we);
                    groupEntIds.Add(wid);
                }

                if (i > 0)
                {
                    var blkId = VtMarkerBlock.Ensure(db, tx);
                    var xBlk = new BlockReference(
                        new Point3d(wp.Position.X, wp.Position.Y, 0), blkId);
                    xBlk.LayerId = VtLayerManager.GetLayerId(db, tx, VtLayerManager.LABELS);
                    xBlk.ColorIndex = 2;
                    btr.AppendEntity(xBlk);
                    tx.AddNewlyCreatedDBObject(xBlk, true);
                    groupEntIds.Add(xBlk.ObjectId);
                    // XData: appName, groupId, waypointIndex — reactor uses index to identify which wp
                    xBlk.XData = new ResultBuffer(
                        new TypedValue((int)DxfCode.ExtendedDataRegAppName, XDATA_APP),
                        new TypedValue((int)DxfCode.ExtendedDataAsciiString, gid),
                        new TypedValue((int)DxfCode.ExtendedDataInteger32, i));
                }
            }

            // Create AutoCAD Group containing all entities
            var groupDict = (DBDictionary)tx.GetObject(db.GroupDictionaryId, OpenMode.ForWrite);
            string groupName = $"VTDRIVE_{gid}";
            var grp = new Group($"Vehicle Tracking Drive Path {gid}", true);
            groupDict.SetAt(groupName, grp);
            tx.AddNewlyCreatedDBObject(grp, true);
            foreach (var eid in groupEntIds)
                grp.Append(eid);

            tx.Commit();
            return gid;
        }

        internal static AcDbPolyline MakePoly(List<Vec2> pts, bool closed)
        {
            var pl = new AcDbPolyline();
            for (int i = 0; i < pts.Count; i++)
                pl.AddVertexAt(i, new Point2d(pts[i].X, pts[i].Y), 0, 0, 0);
            pl.Closed = closed;
            return pl;
        }

        /// <summary>
        /// Create permanent wheel rectangle polylines + steering % MText for one waypoint.
        /// Returns ObjectIds of all created entities.
        /// </summary>
        private static List<ObjectId> DrawPermanentWheels(BlockTableRecord btr,
            Transaction tx, Database db, VehicleUnit v, WaypointData wp, double steerAngle)
        {
            var ids = new List<ObjectId>();
            double wb = v.Wheelbase, tw = v.TrackWidth;
            double tireW = tw * 0.10, tireH = tireW * 2.5;

            double h = wp.Heading;
            double cosH = Math.Cos(h), sinH = Math.Sin(h);
            double px = -sinH, py = cosH;

            double rx = wp.Position.X - wb * cosH, ry = wp.Position.Y - wb * sinH;
            double fx = wp.Position.X, fy = wp.Position.Y;

            var (innerA, outerA) = WheelViz.AckermannAngles(steerAngle, wb, tw);
            bool leftIsInner = steerAngle > 0;
            double leftAngle  = leftIsInner ? innerA : outerA;
            double rightAngle = leftIsInner ? outerA : innerA;

            var wheelLayerId = VtLayerManager.GetLayerId(db, tx, VtLayerManager.WHEELS);

            // 4 wheels: front-left, front-right, rear-left, rear-right
            (double cx, double cy, double rot)[] wheels = {
                (fx + (tw/2.0)*px, fy + (tw/2.0)*py, h + leftAngle),   // FL
                (fx - (tw/2.0)*px, fy - (tw/2.0)*py, h + rightAngle),  // FR
                (rx + (tw/2.0)*px, ry + (tw/2.0)*py, h),               // RL
                (rx - (tw/2.0)*px, ry - (tw/2.0)*py, h),               // RR
            };

            foreach (var (cx, cy, rot) in wheels)
            {
                double hw = tireW / 2.0, hh = tireH / 2.0;
                double cosR = Math.Cos(rot), sinR = Math.Sin(rot);
                double[] dx = { -hw, hw, hw, -hw };
                double[] dy = { -hh, -hh, hh, hh };
                var wPl = new AcDbPolyline();
                for (int j = 0; j < 4; j++)
                    wPl.AddVertexAt(j, new Point2d(
                        cx + dx[j] * cosR - dy[j] * sinR,
                        cy + dx[j] * sinR + dy[j] * cosR), 0, 0, 0);
                wPl.Closed = true;
                wPl.LayerId = wheelLayerId;
                wPl.ColorIndex = 40;
                btr.AppendEntity(wPl); tx.AddNewlyCreatedDBObject(wPl, true);
                ids.Add(wPl.ObjectId);
            }

            // Steering % MText label
            double pct = v.MaxSteeringAngle > 1e-9
                ? Math.Abs(steerAngle) / v.MaxSteeringAngle * 100.0 : 0;
            if (pct > 0.5) // only label if meaningful steering
            {
                string dir = steerAngle > 0.01 ? "L" : (steerAngle < -0.01 ? "R" : "");
                var mt = new MText();
                mt.Contents = $"{pct:F0}%{dir}";
                mt.TextHeight = tireH * 1.5;
                double lblOff = v.Width * 0.7;
                mt.Location = new Point3d(fx + lblOff * px, fy + lblOff * py, 0);
                mt.LayerId = wheelLayerId;
                mt.ColorIndex = 2;
                btr.AppendEntity(mt); tx.AddNewlyCreatedDBObject(mt, true);
                ids.Add(mt.ObjectId);
            }

            return ids;
        }

        internal static string SerializeWps(List<WaypointData> wps)
        {
            return string.Join("|", wps.Select(w =>
                $"{w.Position.X:F4},{w.Position.Y:F4},{w.Heading:F6},{(w.IsReverse ? 1 : 0)}"));
        }

        internal static List<WaypointData> DeserializeWps(string data)
        {
            var result = new List<WaypointData>();
            foreach (var part in data.Split('|'))
            {
                var v = part.Split(',');
                if (v.Length >= 4)
                    result.Add(new WaypointData
                    {
                        Position = new Vec2(double.Parse(v[0]), double.Parse(v[1])),
                        Heading = double.Parse(v[2]),
                        IsReverse = v[3] == "1"
                    });
            }
            return result;
        }

        // ── Live Edit Loop — auto-entered after path creation ──────
        internal static void LiveEditLoop(Editor ed, Database db, VehicleUnit vehicle,
            List<WaypointData> waypoints, List<PathSection> sections, string vehSymbol,
            ref string gid)
        {
            if (waypoints.Count < 3) return;
            _acceptFlag = false;

            while (true)
            {
                if (_acceptFlag || _cancelFlag) break;

                var ppr = ed.GetPoint(
                    new PromptPointOptions("\nClick near X to edit (Enter to accept): ") { AllowNone = true });
                if (ppr.Status != PromptStatus.OK || _acceptFlag) break;

                Vec2 click = new(ppr.Value.X, ppr.Value.Y);

                int bestIdx = -1;
                double bestDist = 8.0;
                for (int i = 1; i < waypoints.Count; i++)
                {
                    double d = click.DistanceTo(waypoints[i].Position);
                    if (d < bestDist) { bestDist = d; bestIdx = i; }
                }

                if (bestIdx < 0)
                {
                    _panel?.SetStatus("No X marker found nearby. Click closer to an X.", "#FF6B6B");
                    continue;
                }

                // Read current speed from panel
                if (_panel != null) _speedMph = _panel.SpeedMph;

                // Erase permanent entities — jig draws everything as transient
                EraseGroup(db, gid);

                var origWps = waypoints.Select(w => new WaypointData
                    { Position = w.Position, Heading = w.Heading, IsReverse = w.IsReverse }).ToList();
                var origSecs = new List<PathSection>(sections);

                _panel?.SetStatus($"Dragging X[{bestIdx}]... Click to place, ESC to cancel.", "#FFB74D");
                var jig = new LiveEditJig(vehicle, waypoints, sections, bestIdx);
                var jr = ed.Drag(jig);

                if (jr.Status == PromptStatus.OK && jig.IsValid)
                {
                    waypoints[bestIdx].Position = jig.FinalPos;
                    sections.Clear();
                    for (int i = 1; i < waypoints.Count; i++)
                    {
                        var sec = SectionSolver.Solve(waypoints[i - 1].Position,
                            waypoints[i - 1].Heading, waypoints[i].Position,
                            vehicle, waypoints[i].IsReverse,
                            VtDriveCommand._speedMph * 1.46667);
                        waypoints[i].Heading = sec.End.Heading;
                        sections.Add(sec);
                    }
                    gid = DrawFinal(db, vehicle, waypoints, sections, vehSymbol);
                    _panel?.SetStatus($"X[{bestIdx}] moved. Click another X or Accept.", "#66BB6A");
                }
                else
                {
                    waypoints.Clear(); waypoints.AddRange(origWps);
                    sections.Clear(); sections.AddRange(origSecs);
                    gid = DrawFinal(db, vehicle, waypoints, sections, vehSymbol);
                    _panel?.SetStatus("Edit cancelled. Click another X or Accept.", "#999");
                }
                ed.Regen();
            }
            _panel?.ExitEditMode();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  LiveEditJig — real-time arc preview while dragging X markers
    //  Recomputes adjacent sections on every mouse move.
    // ═══════════════════════════════════════════════════════════════

    internal class LiveEditJig : DrawJig
    {
        private readonly VehicleUnit _v;
        private readonly List<WaypointData> _wps;
        private readonly List<PathSection> _secs;
        private readonly int _editIdx;
        private Vec2 _mousePos;
        private PathSection? _secBefore;
        private PathSection? _secAfter;
        private bool _valid;

        public Vec2 FinalPos => _secBefore != null && !_secBefore.IsError && _secBefore.WasClamped
            ? _secBefore.End.Position : _mousePos;
        public bool IsValid => _valid;

        public LiveEditJig(VehicleUnit v, List<WaypointData> wps,
            List<PathSection> secs, int editIdx)
        {
            _v = v; _wps = wps; _secs = secs; _editIdx = editIdx;
            _mousePos = wps[editIdx].Position;
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var opts = new JigPromptPointOptions("\nDrag X to new position (ESC to cancel):")
            {
                BasePoint = new Point3d(_wps[_editIdx].Position.X, _wps[_editIdx].Position.Y, 0),
                UseBasePoint = true,
                Cursor = CursorType.RubberBand
            };
            var pr = prompts.AcquirePoint(opts);
            if (pr.Status != PromptStatus.OK) return SamplerStatus.Cancel;

            var np = new Vec2(pr.Value.X, pr.Value.Y);
            if (np.DistanceTo(_mousePos) < 0.05) return SamplerStatus.NoChange;
            _mousePos = np;

            // Recompute adjacent sections
            int i = _editIdx;
            _valid = true;

            // Section BEFORE: wp[i-1] → mouse
            _secBefore = SectionSolver.Solve(_wps[i - 1].Position, _wps[i - 1].Heading,
                _mousePos, _v, _wps[i].IsReverse,
                VtDriveCommand._speedMph * 1.46667);
            if (_secBefore.IsError) _valid = false;
            // WasClamped is OK — shows max-turn arc

            // Section AFTER: use clamped endpoint if before was clamped
            Vec2 afterStart = (_secBefore != null && !_secBefore.IsError && _secBefore.WasClamped)
                ? _secBefore.End.Position : _mousePos;
            double newH = (_secBefore != null && !_secBefore.IsError)
                ? _secBefore.End.Heading : _wps[i].Heading;
            if (i < _wps.Count - 1)
            {
                _secAfter = SectionSolver.Solve(afterStart, newH,
                    _wps[i + 1].Position, _v, _wps[i + 1].IsReverse,
                    VtDriveCommand._speedMph * 1.46667);
                if (_secAfter != null && _secAfter.IsError) _valid = false;
            }
            else
            {
                _secAfter = null;
            }

            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            var g = draw.Geometry;
            var s = draw.SubEntityTraits;

            // ── Draw all NON-adjacent frozen sections ────────────
            for (int si = 0; si < _secs.Count; si++)
            {
                // Section si connects wp[si] → wp[si+1].
                // Adjacent to editIdx: si == editIdx-1 (before) or si == editIdx (after)
                if (si == _editIdx - 1 || si == _editIdx) continue;
                DrawSection(g, s, _secs[si]);
            }

            // ── Draw live-recomputed adjacent sections ───────────
            if (_secBefore != null)
            {
                if (_secBefore.IsError)
                {
                    s.Color = 1; s.LineWeight = LineWeight.LineWeight020;
                    g.Polyline(new Point3dCollection {
                        new Point3d(_wps[_editIdx - 1].Position.X, _wps[_editIdx - 1].Position.Y, 0),
                        new Point3d(_mousePos.X, _mousePos.Y, 0)
                    }, Vector3d.ZAxis, IntPtr.Zero);
                }
                else DrawSection(g, s, _secBefore);
            }

            if (_secAfter != null)
            {
                if (_secAfter.IsError)
                {
                    Vec2 errStart = (_secBefore != null && !_secBefore.IsError && _secBefore.WasClamped)
                        ? _secBefore.End.Position : _mousePos;
                    s.Color = 1; s.LineWeight = LineWeight.LineWeight020;
                    g.Polyline(new Point3dCollection {
                        new Point3d(errStart.X, errStart.Y, 0),
                        new Point3d(_wps[_editIdx + 1].Position.X, _wps[_editIdx + 1].Position.Y, 0)
                    }, Vector3d.ZAxis, IntPtr.Zero);
                }
                else DrawSection(g, s, _secAfter);
            }

            // ── Vehicle outlines + wheels at all waypoints ────────
            for (int wi = 0; wi < _wps.Count; wi++)
            {
                Vec2 pos; double heading;
                if (wi == _editIdx)
                {
                    pos = (_secBefore != null && !_secBefore.IsError && _secBefore.WasClamped)
                        ? _secBefore.End.Position : _mousePos;
                    heading = (_secBefore != null && !_secBefore.IsError)
                        ? _secBefore.End.Heading : _wps[wi].Heading;
                }
                else
                {
                    pos = _wps[wi].Position;
                    heading = _wps[wi].Heading;
                }

                Vec2 rear = new(pos.X - _v.Wheelbase * Math.Cos(heading),
                                pos.Y - _v.Wheelbase * Math.Sin(heading));
                var corners = SweptPathSolver.ComputeBodyCorners(_v, rear, heading);
                var body = new Point3dCollection();
                foreach (var c in corners) body.Add(new Point3d(c.X, c.Y, 0));
                body.Add(new Point3d(corners[0].X, corners[0].Y, 0));
                s.Color = (wi == _editIdx) ? (short)5 : (short)150;
                s.LineWeight = LineWeight.LineWeight030;
                g.Polyline(body, Vector3d.ZAxis, IntPtr.Zero);

                // Wheels with Ackermann angles
                double sa;
                if (wi == _editIdx)
                    sa = (_secBefore != null && !_secBefore.IsError) ? _secBefore.PeakSteerAngle : 0;
                else
                    sa = (wi > 0 && wi - 1 < _secs.Count) ? _secs[wi - 1].PeakSteerAngle : 0;
                var wpData = new WaypointData { Position = pos, Heading = heading };
                bool isGhost = (wi == _editIdx);
                WheelViz.DrawWheels(g, s, _v, wpData, sa, isGhost);

                // X markers (non-edited ones)
                if (wi > 0 && wi != _editIdx)
                {
                    double sz = 1.5;
                    s.Color = 2; s.LineWeight = LineWeight.LineWeight020;
                    g.Polyline(new Point3dCollection {
                        new Point3d(pos.X - sz, pos.Y - sz, 0),
                        new Point3d(pos.X + sz, pos.Y + sz, 0)
                    }, Vector3d.ZAxis, IntPtr.Zero);
                    g.Polyline(new Point3dCollection {
                        new Point3d(pos.X + sz, pos.Y - sz, 0),
                        new Point3d(pos.X - sz, pos.Y + sz, 0)
                    }, Vector3d.ZAxis, IntPtr.Zero);
                }
            }

            // ── Dragged X marker (highlighted, larger) ───────────
            {
                bool clamped = _secBefore != null && !_secBefore.IsError && _secBefore.WasClamped;
                Vec2 xPos = clamped ? _secBefore.End.Position : _mousePos;
                double sz = 2.0;
                // Green=valid, Orange(30)=clamped, Red=error
                s.Color = !_valid ? (short)1 : clamped ? (short)30 : (short)2;
                s.LineWeight = LineWeight.LineWeight040;
                g.Polyline(new Point3dCollection {
                    new Point3d(xPos.X - sz, xPos.Y - sz, 0),
                    new Point3d(xPos.X + sz, xPos.Y + sz, 0)
                }, Vector3d.ZAxis, IntPtr.Zero);
                g.Polyline(new Point3dCollection {
                    new Point3d(xPos.X + sz, xPos.Y - sz, 0),
                    new Point3d(xPos.X - sz, xPos.Y + sz, 0)
                }, Vector3d.ZAxis, IntPtr.Zero);
            }

            return true;
        }

        private static void DrawSection(Geometry g, SubEntityTraits s, PathSection sec)
        {
            // Centerline (cyan)
            s.Color = 4; s.LineWeight = LineWeight.LineWeight020;
            if (sec.CenterPts.Count > 1) g.Polyline(ToP3d(sec.CenterPts), Vector3d.ZAxis, IntPtr.Zero);
            // Wheel tracks (green)
            s.Color = 3; s.LineWeight = LineWeight.LineWeight013;
            if (sec.LeftWheelPts.Count > 1) g.Polyline(ToP3d(sec.LeftWheelPts), Vector3d.ZAxis, IntPtr.Zero);
            if (sec.RightWheelPts.Count > 1) g.Polyline(ToP3d(sec.RightWheelPts), Vector3d.ZAxis, IntPtr.Zero);
            // Rear body corners (red)
            s.Color = 1; s.LineWeight = LineWeight.LineWeight013;
            if (sec.RearLeftPts.Count > 1) g.Polyline(ToP3d(sec.RearLeftPts), Vector3d.ZAxis, IntPtr.Zero);
            if (sec.RearRightPts.Count > 1) g.Polyline(ToP3d(sec.RearRightPts), Vector3d.ZAxis, IntPtr.Zero);
        }

        private static Point3dCollection ToP3d(List<Vec2> pts)
        {
            var c = new Point3dCollection();
            foreach (var p in pts) c.Add(new Point3d(p.X, p.Y, 0));
            return c;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  DriveJig3 — Interactive DrawJig
    //  Preview arcs on hover, click to commit, undo at last X.
    // ═══════════════════════════════════════════════════════════════

    internal class DriveJig3 : DrawJig
    {
        private readonly VehicleUnit _v;
        private List<WaypointData> _wps;
        private List<PathSection> _secs;
        private Point3d _mouse;
        private PathSection? _preview;
        private const double UNDO_R = 5.0;

        public bool IsInUndoZone { get; private set; }
        public PathSection? PreviewSection => _preview;

        public DriveJig3(VehicleUnit v, List<WaypointData> wps, List<PathSection> secs)
        {
            _v = v; _wps = wps; _secs = secs;
            var last = wps.Last();
            _mouse = new Point3d(last.Position.X, last.Position.Y, 0);
        }

        public void Refresh(List<WaypointData> wps, List<PathSection> secs)
        { _wps = wps; _secs = secs; }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var opt = new JigPromptPointOptions("\nClick target or Enter to finish: ")
            {
                UserInputControls = UserInputControls.Accept3dCoordinates |
                                    UserInputControls.NullResponseAccepted
            };
            var last = _wps.Last();
            opt.UseBasePoint = true;
            opt.BasePoint = new Point3d(last.Position.X, last.Position.Y, 0);

            var res = prompts.AcquirePoint(opt);
            if (res.Status != PromptStatus.OK) return SamplerStatus.Cancel;
            if (res.Value.DistanceTo(_mouse) < 0.05) return SamplerStatus.NoChange;
            _mouse = res.Value;

            // Undo zone
            IsInUndoZone = false;
            if (_wps.Count > 1)
            {
                double d = new Vec2(_mouse.X, _mouse.Y).DistanceTo(_wps.Last().Position);
                if (d < UNDO_R) IsInUndoZone = true;
            }

            // Preview section
            if (!IsInUndoZone)
            {
                var lw = _wps.Last();
                Vec2 tgt = new(_mouse.X, _mouse.Y);
                double bearing = Math.Atan2(tgt.Y - lw.Position.Y, tgt.X - lw.Position.X);
                bool rev = Math.Abs(SectionSolver.Norm(bearing - lw.Heading)) > Math.PI * 0.6;
                _preview = SectionSolver.Solve(lw.Position, lw.Heading, tgt, _v, rev,
                    VtDriveCommand._speedMph * 1.46667);
            }
            else _preview = null;

            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            var g = draw.Geometry;
            var s = draw.SubEntityTraits;

            // Committed sections
            foreach (var sec in _secs) DrawSec(g, s, sec, false);

            // Committed waypoints (vehicle outlines + X markers + wheels)
            for (int i = 0; i < _wps.Count; i++)
            {
                DrawWp(g, s, _wps[i], i > 0, false);
                // Draw wheels with the steer angle from the section ending at this waypoint
                double sa = (i > 0 && i - 1 < _secs.Count) ? _secs[i - 1].PeakSteerAngle : 0;
                DrawWheels(g, s, _wps[i], sa, false);
            }

            // Preview section
            if (_preview != null && !IsInUndoZone)
            {
                DrawSec(g, s, _preview, true);
                DrawWp(g, s, _preview.End, false, true);
                DrawWheels(g, s, _preview.End, _preview.PeakSteerAngle, true);
                DrawWhiskers(g, s, _preview.End);
            }

            // Undo indicator
            if (IsInUndoZone && _wps.Count > 1)
            {
                var lp = _wps.Last().Position;
                s.Color = 1; s.LineWeight = LineWeight.LineWeight050;
                var cp = new Point3dCollection();
                for (int i = 0; i <= 24; i++)
                {
                    double a = i * 2 * Math.PI / 24;
                    cp.Add(new Point3d(lp.X + UNDO_R * Math.Cos(a), lp.Y + UNDO_R * Math.Sin(a), 0));
                }
                g.Polyline(cp, Vector3d.ZAxis, IntPtr.Zero);
            }
            return true;
        }

        private void DrawSec(Geometry g, SubEntityTraits s, PathSection sec, bool preview)
        {
            if (sec.IsError)
            {
                s.Color = 1; s.LineWeight = LineWeight.LineWeight013;
                g.Polyline(new Point3dCollection {
                    new Point3d(sec.Start.Position.X, sec.Start.Position.Y, 0),
                    new Point3d(sec.End.Position.X, sec.End.Position.Y, 0)
                }, Vector3d.ZAxis, IntPtr.Zero);
                return;
            }

            // Centerline arc
            s.Color = preview
                ? (sec.End.IsReverse ? (short)141 : (short)140)
                : (sec.End.IsReverse ? (short)5 : (short)4);
            s.LineWeight = preview ? LineWeight.LineWeight013 : LineWeight.LineWeight020;
            if (sec.CenterPts.Count > 1) g.Polyline(ToP3d(sec.CenterPts), Vector3d.ZAxis, IntPtr.Zero);

            // Wheel tracks (rear wheels)
            s.Color = preview ? (short)80 : (short)3;
            s.LineWeight = preview ? LineWeight.LineWeight009 : LineWeight.LineWeight013;
            if (sec.LeftWheelPts.Count > 1) g.Polyline(ToP3d(sec.LeftWheelPts), Vector3d.ZAxis, IntPtr.Zero);
            if (sec.RightWheelPts.Count > 1) g.Polyline(ToP3d(sec.RightWheelPts), Vector3d.ZAxis, IntPtr.Zero);

            // Rear body corner traces (tail swing — outermost swept path)
            s.Color = preview ? (short)40 : (short)1;
            s.LineWeight = preview ? LineWeight.LineWeight009 : LineWeight.LineWeight013;
            if (sec.RearLeftPts.Count > 1) g.Polyline(ToP3d(sec.RearLeftPts), Vector3d.ZAxis, IntPtr.Zero);
            if (sec.RearRightPts.Count > 1) g.Polyline(ToP3d(sec.RearRightPts), Vector3d.ZAxis, IntPtr.Zero);
        }

        private void DrawWp(Geometry g, SubEntityTraits s, WaypointData wp, bool drawX, bool ghost)
        {
            Vec2 rear = new(wp.Position.X - _v.Wheelbase * Math.Cos(wp.Heading),
                            wp.Position.Y - _v.Wheelbase * Math.Sin(wp.Heading));
            var corners = SweptPathSolver.ComputeBodyCorners(_v, rear, wp.Heading);
            var body = new Point3dCollection();
            foreach (var c in corners) body.Add(new Point3d(c.X, c.Y, 0));
            body.Add(new Point3d(corners[0].X, corners[0].Y, 0));
            s.Color = ghost ? (short)8 : (wp.IsReverse ? (short)5 : (short)150);
            s.LineWeight = ghost ? LineWeight.LineWeight013 : LineWeight.LineWeight030;
            g.Polyline(body, Vector3d.ZAxis, IntPtr.Zero);

            // Axle lines
            double hw = _v.Width * 0.5;
            double px = -Math.Sin(wp.Heading), py = Math.Cos(wp.Heading);
            s.Color = 7; s.LineWeight = LineWeight.LineWeight020;
            g.Polyline(new Point3dCollection {
                new Point3d(rear.X + hw * px, rear.Y + hw * py, 0),
                new Point3d(rear.X - hw * px, rear.Y - hw * py, 0)
            }, Vector3d.ZAxis, IntPtr.Zero);
            g.Polyline(new Point3dCollection {
                new Point3d(wp.Position.X + hw * px, wp.Position.Y + hw * py, 0),
                new Point3d(wp.Position.X - hw * px, wp.Position.Y - hw * py, 0)
            }, Vector3d.ZAxis, IntPtr.Zero);

            if (drawX)
            {
                s.Color = 2; s.LineWeight = LineWeight.LineWeight030;
                double sz = 1.5;
                g.Polyline(new Point3dCollection {
                    new Point3d(wp.Position.X - sz, wp.Position.Y - sz, 0),
                    new Point3d(wp.Position.X + sz, wp.Position.Y + sz, 0)
                }, Vector3d.ZAxis, IntPtr.Zero);
                g.Polyline(new Point3dCollection {
                    new Point3d(wp.Position.X + sz, wp.Position.Y - sz, 0),
                    new Point3d(wp.Position.X - sz, wp.Position.Y + sz, 0)
                }, Vector3d.ZAxis, IntPtr.Zero);
            }
        }

        private void DrawWhiskers(Geometry g, SubEntityTraits s, WaypointData wp)
        {
            double rMin = _v.EffectiveMinRadius;
            double len = _v.Length * 2.5;
            s.LineWeight = LineWeight.LineWeight009;
            for (int side = -1; side <= 1; side += 2)
            {
                for (int pct = 0; pct < 2; pct++)
                {
                    double r = pct == 0 ? rMin * 2.0 : rMin;
                    double sweep = len / r * side;
                    double pa = wp.Heading + side * Math.PI * 0.5;
                    Vec2 C = new(wp.Position.X + r * Math.Cos(pa), wp.Position.Y + r * Math.Sin(pa));
                    double sa = Math.Atan2(wp.Position.Y - C.Y, wp.Position.X - C.X);
                    var pts = new Point3dCollection();
                    for (int i = 0; i <= 12; i++)
                    {
                        double a = sa + sweep * i / 12.0;
                        pts.Add(new Point3d(C.X + r * Math.Cos(a), C.Y + r * Math.Sin(a), 0));
                    }
                    s.Color = pct == 0 ? (short)8 : (short)251;
                    g.Polyline(pts, Vector3d.ZAxis, IntPtr.Zero);
                }
            }
        }

        private static Point3dCollection ToP3d(List<Vec2> pts)
        {
            var c = new Point3dCollection();
            foreach (var p in pts) c.Add(new Point3d(p.X, p.Y, 0));
            return c;
        }

        private void DrawWheels(Geometry g, SubEntityTraits s, WaypointData wp,
            double steerAngle, bool ghost) =>
            WheelViz.DrawWheels(g, s, _v, wp, steerAngle, ghost);
    }

    // ═══════════════════════════════════════════════════════════════
    //  VTEDIT Command — Click near any X marker or path to edit.
    //  Hides old geometry, shows live jig preview, redraws on finish.
    // ═══════════════════════════════════════════════════════════════

    public class VtEditCommand
    {
        [CommandMethod("VTEDIT", CommandFlags.Modal)]
        public void Execute()
        {
            try
            {
                if (!Engine.LicenseManager.EnsureLicensed()) return;
                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                Editor ed = doc.Editor;
                Database db = doc.Database;

                // Lazy-hook the reactor (MUST NOT be done in Initialize — see AppLoader.cs)
                VtPathReactor.Hook(doc);

                ed.WriteMessage("\n  VTEDIT — Click on or near a VT path / X marker to edit.\n");

                // ── Step 1: User clicks near any VT entity ───────────
                var pRes = ed.GetPoint(new PromptPointOptions(
                    "\n  Click near a VT path or X marker: ") { AllowNone = true });
                if (pRes.Status != PromptStatus.OK) return;

                Vec2 click = new(pRes.Value.X, pRes.Value.Y);
                string? groupId = null;
                string? vehSymbol = null;
                List<WaypointData>? waypoints = null;
                var groupEntityIds = new List<ObjectId>();

                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var btr = (BlockTableRecord)tx.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    // Find nearest VT entity to click point → get group ID
                    double bestDist = 15.0;
                    foreach (ObjectId id in btr)
                    {
                        var ent = tx.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null || ent.IsErased) continue;
                        var xd = ent.GetXDataForApplication(VtDriveCommand.XDATA_APP);
                        if (xd == null) continue;

                        double d = EntityDistToPoint(ent, click);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            var vals = xd.AsArray();
                            if (vals.Length >= 2) groupId = (string)vals[1].Value;
                        }
                    }

                    if (groupId == null)
                    { ed.WriteMessage("\n  No VT path found nearby.\n"); tx.Commit(); return; }

                    // Collect all entities in this group + find path polyline with waypoints
                    foreach (ObjectId id in btr)
                    {
                        var ent = tx.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null || ent.IsErased) continue;
                        var xd = ent.GetXDataForApplication(VtDriveCommand.XDATA_APP);
                        if (xd == null) continue;
                        var vals = xd.AsArray();
                        if (vals.Length < 2 || (string)vals[1].Value != groupId) continue;

                        groupEntityIds.Add(id);

                        // Path polyline has 4 XData values (appName, gid, vehSymbol, waypoints)
                        if (vals.Length >= 4 && waypoints == null)
                        {
                            vehSymbol = (string)vals[2].Value;
                            waypoints = VtDriveCommand.DeserializeWps((string)vals[3].Value);
                        }
                    }
                    tx.Commit();
                }

                if (waypoints == null || waypoints.Count < 2 || vehSymbol == null)
                { ed.WriteMessage("\n  Could not read path data.\n"); return; }

                // ── Step 2: Resolve vehicle ──────────────────────────
                VehicleUnit? vehicle = null;
                foreach (var d in VehicleLibrary.GetDisplayList())
                {
                    if (d.Symbol == vehSymbol)
                    {
                        vehicle = d.IsArticulated
                            ? VehicleLibrary.ArticulatedVehicles[d.Index].LeadUnit
                            : VehicleLibrary.SingleUnits[d.Index];
                        break;
                    }
                }
                if (vehicle == null)
                { ed.WriteMessage($"\n  Vehicle '{vehSymbol}' not found.\n"); return; }

                // ── Step 3: Hide old entities during edit ────────────
                SetGroupVisibility(db, groupEntityIds, false);
                ed.Regen();

                ed.WriteMessage($"\n  Editing {waypoints.Count - 1} waypoint(s). " +
                    "Click near X to drag, Enter to finish, Esc to cancel.\n");

                // ── Step 4: Edit loop ────────────────────────────────
                bool anyEdits = false;
                while (true)
                {
                    var pickRes = ed.GetPoint(new PromptPointOptions(
                        "\n  Click near X to drag it, Enter to finish: ") { AllowNone = true });
                    if (pickRes.Status != PromptStatus.OK) break;

                    Vec2 pick = new(pickRes.Value.X, pickRes.Value.Y);
                    int bestIdx = -1; double bestD = 10.0;
                    for (int i = 1; i < waypoints.Count; i++)
                    {
                        double d = waypoints[i].Position.DistanceTo(pick);
                        if (d < bestD) { bestD = d; bestIdx = i; }
                    }
                    if (bestIdx < 0) { ed.WriteMessage("\n  No X nearby.\n"); continue; }

                    var editJig = new EditJig(vehicle, waypoints, bestIdx);
                    var dr = ed.Drag(editJig);
                    if (dr.Status == PromptStatus.OK && editJig.NewPos.HasValue)
                    {
                        var origPos = waypoints[bestIdx].Position;
                        var origHeadings = waypoints.Select(w => w.Heading).ToList();

                        waypoints[bestIdx].Position = editJig.NewPos.Value;

                        bool valid = true;
                        for (int i = Math.Max(bestIdx - 1, 0); i < waypoints.Count - 1; i++)
                        {
                            var sec = SectionSolver.Solve(waypoints[i].Position, waypoints[i].Heading,
                                waypoints[i + 1].Position, vehicle, waypoints[i + 1].IsReverse,
                                VtDriveCommand._speedMph * 1.46667);
                            if (sec.IsError) { valid = false; break; }
                            // If clamped, snap waypoint to where vehicle actually arrives
                            if (sec.WasClamped)
                                waypoints[i + 1].Position = sec.End.Position;
                            waypoints[i + 1].Heading = sec.End.Heading;
                        }

                        if (valid)
                        {
                            anyEdits = true;
                            ed.WriteMessage($"\n  Waypoint {bestIdx} moved.\n");
                        }
                        else
                        {
                            waypoints[bestIdx].Position = origPos;
                            for (int i = 0; i < waypoints.Count; i++)
                                waypoints[i].Heading = origHeadings[i];
                            ed.WriteMessage("\n  Turn not possible — reverted.\n");
                        }
                    }
                }

                // ── Step 5: Finalize ─────────────────────────────────
                if (anyEdits)
                {
                    // Rebuild all sections with updated waypoints
                    var newSecs = new List<PathSection>();
                    for (int i = 1; i < waypoints.Count; i++)
                    {
                        var sec = SectionSolver.Solve(waypoints[i - 1].Position, waypoints[i - 1].Heading,
                            waypoints[i].Position, vehicle, waypoints[i].IsReverse,
                            VtDriveCommand._speedMph * 1.46667);
                        waypoints[i].Heading = sec.End.Heading;
                        newSecs.Add(sec);
                    }

                    // Erase old group, draw new
                    VtDriveCommand.EraseGroup(db, groupId);
                    VtDriveCommand.DrawFinal(db, vehicle, waypoints, newSecs, vehSymbol);
                    ed.WriteMessage("\n  Path updated.\n");
                }
                else
                {
                    // No edits — restore visibility of old entities
                    SetGroupVisibility(db, groupEntityIds, true);
                    ed.WriteMessage("\n  No changes.\n");
                }
                ed.Regen();
            }
            catch (System.Exception ex)
            {
                Application.DocumentManager.MdiActiveDocument?.Editor
                    .WriteMessage($"\n[VTEDIT ERROR] {ex.Message}\n");
            }
        }

        /// <summary>Approximate distance from an entity to a point.</summary>
        private static double EntityDistToPoint(Entity ent, Vec2 pt)
        {
            if (ent is BlockReference br)
                return pt.DistanceTo(new Vec2(br.Position.X, br.Position.Y));
            if (ent is Line line)
            {
                double d1 = pt.DistanceTo(new Vec2(line.StartPoint.X, line.StartPoint.Y));
                double d2 = pt.DistanceTo(new Vec2(line.EndPoint.X, line.EndPoint.Y));
                return Math.Min(d1, d2);
            }
            if (ent is AcDbPolyline pl && pl.NumberOfVertices > 0)
            {
                double best = double.MaxValue;
                for (int i = 0; i < Math.Min(pl.NumberOfVertices, 50); i++)
                {
                    var v = pl.GetPoint2dAt(i);
                    double d = pt.DistanceTo(new Vec2(v.X, v.Y));
                    if (d < best) best = d;
                }
                return best;
            }
            return double.MaxValue;
        }

        private static void SetGroupVisibility(Database db, List<ObjectId> ids, bool visible)
        {
            using var tx = db.TransactionManager.StartTransaction();
            foreach (var id in ids)
            {
                if (id.IsErased) continue;
                var ent = tx.GetObject(id, OpenMode.ForWrite) as Entity;
                if (ent != null) ent.Visible = visible;
            }
            tx.Commit();
        }

    }

    // ── Edit Jig: drag a single waypoint, show live recalculated arcs ──

    internal class EditJig : DrawJig
    {
        private readonly VehicleUnit _v;
        private readonly List<WaypointData> _wps;
        private readonly int _idx;
        private Point3d _mouse;
        public Vec2? NewPos { get; private set; }

        public EditJig(VehicleUnit v, List<WaypointData> wps, int idx)
        {
            _v = v; _wps = wps; _idx = idx;
            _mouse = new Point3d(wps[idx].Position.X, wps[idx].Position.Y, 0);
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var opt = new JigPromptPointOptions("\nDrag waypoint, click to place: ")
            { UserInputControls = UserInputControls.Accept3dCoordinates };
            var res = prompts.AcquirePoint(opt);
            if (res.Status != PromptStatus.OK) return SamplerStatus.Cancel;
            if (res.Value.DistanceTo(_mouse) < 0.05) return SamplerStatus.NoChange;
            _mouse = res.Value;
            NewPos = new Vec2(_mouse.X, _mouse.Y);
            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            if (!NewPos.HasValue) return true;
            var g = draw.Geometry; var s = draw.SubEntityTraits;

            // Temporary waypoints with dragged point
            var tmp = _wps.Select(w => new WaypointData
            { Position = w.Position, Heading = w.Heading, IsReverse = w.IsReverse }).ToList();
            tmp[_idx].Position = NewPos.Value;

            // Recompute all sections and draw
            bool hasError = false;
            var tmpSecs = new List<PathSection>();
            for (int i = 1; i < tmp.Count; i++)
            {
                var sec = SectionSolver.Solve(tmp[i - 1].Position, tmp[i - 1].Heading,
                    tmp[i].Position, _v, tmp[i].IsReverse,
                    VtDriveCommand._speedMph * 1.46667);
                if (sec.WasClamped)
                    tmp[i].Position = sec.End.Position;
                tmp[i].Heading = sec.End.Heading;
                tmpSecs.Add(sec);

                if (sec.IsError)
                {
                    hasError = true;
                    s.Color = 1; s.LineWeight = LineWeight.LineWeight030;
                    g.Polyline(new Point3dCollection {
                        new Point3d(tmp[i-1].Position.X, tmp[i-1].Position.Y, 0),
                        new Point3d(tmp[i].Position.X, tmp[i].Position.Y, 0)
                    }, Vector3d.ZAxis, IntPtr.Zero);
                    continue;
                }

                if (sec.CenterPts.Count > 1)
                {
                    s.Color = 4; s.LineWeight = LineWeight.LineWeight020;
                    var cp = new Point3dCollection();
                    foreach (var p in sec.CenterPts) cp.Add(new Point3d(p.X, p.Y, 0));
                    g.Polyline(cp, Vector3d.ZAxis, IntPtr.Zero);
                }

                s.Color = 3; s.LineWeight = LineWeight.LineWeight013;
                if (sec.LeftWheelPts.Count > 1)
                { var lp = new Point3dCollection(); foreach (var p in sec.LeftWheelPts) lp.Add(new Point3d(p.X, p.Y, 0)); g.Polyline(lp, Vector3d.ZAxis, IntPtr.Zero); }
                if (sec.RightWheelPts.Count > 1)
                { var rp = new Point3dCollection(); foreach (var p in sec.RightWheelPts) rp.Add(new Point3d(p.X, p.Y, 0)); g.Polyline(rp, Vector3d.ZAxis, IntPtr.Zero); }

                s.Color = 1; s.LineWeight = LineWeight.LineWeight013;
                if (sec.RearLeftPts.Count > 1)
                { var rlp = new Point3dCollection(); foreach (var p in sec.RearLeftPts) rlp.Add(new Point3d(p.X, p.Y, 0)); g.Polyline(rlp, Vector3d.ZAxis, IntPtr.Zero); }
                if (sec.RearRightPts.Count > 1)
                { var rrp = new Point3dCollection(); foreach (var p in sec.RearRightPts) rrp.Add(new Point3d(p.X, p.Y, 0)); g.Polyline(rrp, Vector3d.ZAxis, IntPtr.Zero); }
            }

            // Draw waypoint outlines + wheels + X markers
            for (int i = 0; i < tmp.Count; i++)
            {
                var wp = tmp[i];
                Vec2 rear = new(wp.Position.X - _v.Wheelbase * Math.Cos(wp.Heading),
                                wp.Position.Y - _v.Wheelbase * Math.Sin(wp.Heading));
                var corners = SweptPathSolver.ComputeBodyCorners(_v, rear, wp.Heading);
                var body = new Point3dCollection();
                foreach (var c in corners) body.Add(new Point3d(c.X, c.Y, 0));
                body.Add(new Point3d(corners[0].X, corners[0].Y, 0));
                s.Color = i == _idx ? (short)(hasError ? 1 : 10) : (short)150;
                s.LineWeight = LineWeight.LineWeight030;
                g.Polyline(body, Vector3d.ZAxis, IntPtr.Zero);

                // Wheels with Ackermann
                double sa = (i > 0 && i - 1 < tmpSecs.Count) ? tmpSecs[i - 1].PeakSteerAngle : 0;
                WheelViz.DrawWheels(g, s, _v, wp, sa, i == _idx);

                if (i > 0)
                {
                    s.Color = i == _idx ? (short)(hasError ? 1 : 10) : (short)2;
                    double sz = 1.5;
                    g.Polyline(new Point3dCollection {
                        new Point3d(wp.Position.X - sz, wp.Position.Y - sz, 0),
                        new Point3d(wp.Position.X + sz, wp.Position.Y + sz, 0)
                    }, Vector3d.ZAxis, IntPtr.Zero);
                    g.Polyline(new Point3dCollection {
                        new Point3d(wp.Position.X + sz, wp.Position.Y - sz, 0),
                        new Point3d(wp.Position.X - sz, wp.Position.Y + sz, 0)
                    }, Vector3d.ZAxis, IntPtr.Zero);
                }
            }
            return true;
        }
    }
}
