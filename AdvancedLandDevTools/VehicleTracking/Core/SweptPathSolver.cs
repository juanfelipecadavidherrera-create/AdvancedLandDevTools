using System;
using System.Collections.Generic;

namespace AdvancedLandDevTools.VehicleTracking.Core
{
    /// <summary>
    /// Incremental kinematic swept path solver using the bicycle model.
    /// Discretizes the front-axle path at ~25 mm (0.082 ft) steps and propagates
    /// rear axles, body corners, and trailer units.
    /// </summary>
    public class SweptPathSolver
    {
        /// <summary>Step size in feet (~25 mm = 0.082 ft).</summary>
        public double StepSize { get; set; } = 0.082;

        /// <summary>Vehicle speed in ft/s (used for lock-to-lock rate limiting). Default 15 mph.</summary>
        public double Speed { get; set; } = 22.0;

        /// <summary>Whether the vehicle is driving in reverse.</summary>
        public bool Reverse { get; set; }

        /// <summary>Interval (in steps) at which full body snapshots are recorded. 0 = every step.</summary>
        public int SnapshotInterval { get; set; } = 50;

        /// <summary>
        /// Run swept path simulation for a single-unit vehicle along a discretized path.
        /// </summary>
        /// <param name="vehicle">The vehicle definition.</param>
        /// <param name="pathPoints">Ordered front-axle centerline path points.</param>
        /// <returns>Simulation result with envelope, wheel paths, and snapshots.</returns>
        public SimulationResult Solve(VehicleUnit vehicle, List<Vec2> pathPoints)
        {
            if (pathPoints.Count < 2)
                return new SimulationResult();

            // Discretize path to uniform step size
            var samples = DiscretizePath(pathPoints, StepSize);
            if (samples.Count < 2) return new SimulationResult();

            var result = new SimulationResult();
            double maxSteer = vehicle.MaxSteeringAngle;
            double wb = vehicle.Wheelbase;
            double maxSteerRate = (2.0 * maxSteer) / vehicle.LockToLockTime; // rad/s
            double dt = StepSize / Math.Max(Speed, 0.1); // time per step

            // Initial state — rear axle starts behind front by wheelbase
            Vec2 frontPos = samples[0];
            Vec2 nextFront = samples[1];
            double heading = Math.Atan2(nextFront.Y - frontPos.Y, nextFront.X - frontPos.X);
            if (Reverse) heading += Math.PI;

            Vec2 rearPos = new Vec2(
                frontPos.X - wb * Math.Cos(heading),
                frontPos.Y - wb * Math.Sin(heading));

            double currentSteer = 0.0;
            double station = 0.0;

            // Track all body corner positions for envelope
            var outerCorners = new List<Vec2>();
            var innerLeftWheel = new List<Vec2>();
            var innerRightWheel = new List<Vec2>();

            for (int i = 0; i < samples.Count; i++)
            {
                frontPos = samples[i];

                // Compute required steering angle to reach next point
                if (i < samples.Count - 1)
                {
                    Vec2 next = samples[i + 1];
                    double desiredHeading = Math.Atan2(next.Y - frontPos.Y, next.X - frontPos.X);
                    if (Reverse) desiredHeading += Math.PI;

                    double headingDiff = NormalizeAngle(desiredHeading - heading);
                    double requiredSteer = Math.Atan2(headingDiff * wb, StepSize);

                    // Clamp to max steering angle
                    double targetSteer = Math.Clamp(requiredSteer, -maxSteer, maxSteer);
                    if (Math.Abs(requiredSteer) > maxSteer)
                        result.SteeringClamped = true;

                    // Apply lock-to-lock rate limiting
                    double steerDelta = targetSteer - currentSteer;
                    double maxDelta = maxSteerRate * dt;
                    if (Math.Abs(steerDelta) > maxDelta)
                        steerDelta = Math.Sign(steerDelta) * maxDelta;

                    currentSteer += steerDelta;
                }

                // Propagate rear axle using bicycle model
                double direction = Reverse ? -1.0 : 1.0;
                rearPos = new Vec2(
                    rearPos.X + StepSize * direction * Math.Cos(heading),
                    rearPos.Y + StepSize * direction * Math.Sin(heading));
                heading += StepSize * direction * Math.Tan(currentSteer) / wb;
                heading = NormalizeAngle(heading);

                // Compute body corners: FL, FR, RR, RL
                var corners = ComputeBodyCorners(vehicle, rearPos, heading);
                outerCorners.AddRange(corners);

                // Track wheel positions (rear wheels for offtracking)
                double hw = vehicle.TrackWidth * 0.5;
                double perpX = -Math.Sin(heading);
                double perpY = Math.Cos(heading);
                innerLeftWheel.Add(new Vec2(rearPos.X + hw * perpX, rearPos.Y + hw * perpY));
                innerRightWheel.Add(new Vec2(rearPos.X - hw * perpX, rearPos.Y - hw * perpY));

                // Record snapshot at intervals
                if (i % Math.Max(SnapshotInterval, 1) == 0 || i == samples.Count - 1)
                {
                    result.Snapshots.Add(new VehicleSnapshot
                    {
                        Station = station,
                        FrontAxle = frontPos,
                        RearAxle = rearPos,
                        Heading = heading,
                        SteeringAngle = currentSteer,
                        BodyCorners = corners
                    });
                }

                station += StepSize;
            }

            result.PathLength = station;

            // Build envelope from all body corner positions
            result.OuterEnvelope = ComputeConvexHull(outerCorners);

            // Determine which wheel path is inner/outer based on net turn direction
            result.InnerWheelPath = innerLeftWheel;
            result.OuterWheelPath = innerRightWheel;

            // Compute max swept width and offtracking
            ComputeMetrics(result, samples);

            return result;
        }

        /// <summary>
        /// Run swept path simulation for an articulated vehicle.
        /// </summary>
        public SimulationResult Solve(ArticulatedVehicle vehicle, List<Vec2> pathPoints)
        {
            if (pathPoints.Count < 2)
                return new SimulationResult();

            var samples = DiscretizePath(pathPoints, StepSize);
            if (samples.Count < 2) return new SimulationResult();

            var result = new SimulationResult();
            var lead = vehicle.LeadUnit;
            double maxSteer = lead.MaxSteeringAngle;
            double wb = lead.Wheelbase;
            double maxSteerRate = (2.0 * maxSteer) / lead.LockToLockTime;
            double dt = StepSize / Math.Max(Speed, 0.1);

            // Initial lead unit state
            Vec2 frontPos = samples[0];
            Vec2 nextFront = samples[1];
            double leadHeading = Math.Atan2(nextFront.Y - frontPos.Y, nextFront.X - frontPos.X);
            if (Reverse) leadHeading += Math.PI;

            Vec2 leadRear = new Vec2(
                frontPos.X - wb * Math.Cos(leadHeading),
                frontPos.Y - wb * Math.Sin(leadHeading));

            // Initialize trailer states
            int trailerCount = vehicle.Trailers.Length;
            var trailerRear = new Vec2[trailerCount];
            var trailerHeading = new double[trailerCount];

            Vec2 prevHitch = leadRear; // coupling point starts at lead rear axle
            for (int t = 0; t < trailerCount; t++)
            {
                var td = vehicle.Trailers[t];
                double ho = td.Coupling.HitchOffset;
                double kp = td.Coupling.KingpinOffset;
                double twb = td.Unit.Wheelbase;

                // Hitch point behind lead rear by hitchOffset
                Vec2 hitchPt = new Vec2(
                    prevHitch.X - ho * Math.Cos(leadHeading),
                    prevHitch.Y - ho * Math.Sin(leadHeading));

                trailerHeading[t] = leadHeading; // start aligned
                trailerRear[t] = new Vec2(
                    hitchPt.X - twb * Math.Cos(leadHeading),
                    hitchPt.Y - twb * Math.Sin(leadHeading));

                prevHitch = trailerRear[t];
            }

            double currentSteer = 0.0;
            double station = 0.0;
            var allCorners = new List<Vec2>();

            for (int i = 0; i < samples.Count; i++)
            {
                frontPos = samples[i];

                // Steering computation (same as single-unit)
                if (i < samples.Count - 1)
                {
                    Vec2 next = samples[i + 1];
                    double desiredHeading = Math.Atan2(next.Y - frontPos.Y, next.X - frontPos.X);
                    if (Reverse) desiredHeading += Math.PI;

                    double headingDiff = NormalizeAngle(desiredHeading - leadHeading);
                    double requiredSteer = Math.Atan2(headingDiff * wb, StepSize);
                    double targetSteer = Math.Clamp(requiredSteer, -maxSteer, maxSteer);
                    if (Math.Abs(requiredSteer) > maxSteer) result.SteeringClamped = true;

                    double steerDelta = targetSteer - currentSteer;
                    double maxDelta = maxSteerRate * dt;
                    if (Math.Abs(steerDelta) > maxDelta)
                        steerDelta = Math.Sign(steerDelta) * maxDelta;
                    currentSteer += steerDelta;
                }

                // Propagate lead rear axle
                double dir = Reverse ? -1.0 : 1.0;
                leadRear = new Vec2(
                    leadRear.X + StepSize * dir * Math.Cos(leadHeading),
                    leadRear.Y + StepSize * dir * Math.Sin(leadHeading));
                leadHeading += StepSize * dir * Math.Tan(currentSteer) / wb;
                leadHeading = NormalizeAngle(leadHeading);

                // Lead body corners
                var leadCorners = ComputeBodyCorners(lead, leadRear, leadHeading);
                allCorners.AddRange(leadCorners);

                // Propagate each trailer
                var trailerSnaps = new TrailerSnapshot[trailerCount];
                Vec2 prevCoupling = leadRear;
                double prevHead = leadHeading;

                for (int t = 0; t < trailerCount; t++)
                {
                    var td = vehicle.Trailers[t];
                    double ho = td.Coupling.HitchOffset;
                    double twb = td.Unit.Wheelbase;

                    // Hitch point on the preceding unit
                    Vec2 hitchPt = new Vec2(
                        prevCoupling.X - ho * Math.Cos(prevHead),
                        prevCoupling.Y - ho * Math.Sin(prevHead));

                    // Trailer rear follows hitch — bicycle model for trailer
                    double th = trailerHeading[t];
                    Vec2 tr = trailerRear[t];

                    // Direction from rear axle to hitch
                    double dx = hitchPt.X - tr.X;
                    double dy = hitchPt.Y - tr.Y;
                    double dist = Math.Sqrt(dx * dx + dy * dy);

                    if (dist > 1e-9)
                    {
                        // Update trailer heading to point toward hitch
                        double targetHead = Math.Atan2(dy, dx);
                        th = targetHead;

                        // Place rear axle at wheelbase distance behind hitch
                        tr = new Vec2(
                            hitchPt.X - twb * Math.Cos(th),
                            hitchPt.Y - twb * Math.Sin(th));
                    }

                    trailerHeading[t] = th;
                    trailerRear[t] = tr;

                    var tCorners = ComputeBodyCorners(td.Unit, tr, th);
                    allCorners.AddRange(tCorners);

                    trailerSnaps[t] = new TrailerSnapshot
                    {
                        FrontHitch = hitchPt,
                        RearAxle = tr,
                        Heading = th,
                        BodyCorners = tCorners
                    };

                    prevCoupling = tr;
                    prevHead = th;
                }

                // Record snapshot
                if (i % Math.Max(SnapshotInterval, 1) == 0 || i == samples.Count - 1)
                {
                    result.Snapshots.Add(new VehicleSnapshot
                    {
                        Station = station,
                        FrontAxle = frontPos,
                        RearAxle = leadRear,
                        Heading = leadHeading,
                        SteeringAngle = currentSteer,
                        BodyCorners = leadCorners,
                        TrailerSnapshots = trailerSnaps
                    });
                }

                station += StepSize;
            }

            result.PathLength = station;
            result.OuterEnvelope = ComputeConvexHull(allCorners);
            ComputeMetrics(result, samples);

            return result;
        }

        // ── Helpers ──────────────────────────────────────────────────

        /// <summary>Compute 4 body corners from rear axle position and heading.</summary>
        public static Vec2[] ComputeBodyCorners(VehicleUnit v, Vec2 rearAxle, double heading)
        {
            double cosH = Math.Cos(heading);
            double sinH = Math.Sin(heading);
            double hw = v.Width * 0.5;
            double fo = v.FrontOverhang + v.Wheelbase; // front bumper distance from rear axle
            double ro = v.RearOverhang;

            // Perpendicular direction (left)
            double px = -sinH;
            double py = cosH;

            // Forward direction
            double fx = cosH;
            double fy = sinH;

            return new Vec2[]
            {
                new Vec2(rearAxle.X + fo * fx + hw * px, rearAxle.Y + fo * fy + hw * py), // FL
                new Vec2(rearAxle.X + fo * fx - hw * px, rearAxle.Y + fo * fy - hw * py), // FR
                new Vec2(rearAxle.X - ro * fx - hw * px, rearAxle.Y - ro * fy - hw * py), // RR
                new Vec2(rearAxle.X - ro * fx + hw * px, rearAxle.Y - ro * fy + hw * py), // RL
            };
        }

        /// <summary>Discretize a polyline path into uniform-distance samples.</summary>
        public static List<Vec2> DiscretizePath(List<Vec2> path, double step)
        {
            var result = new List<Vec2> { path[0] };
            double accum = 0;

            for (int i = 1; i < path.Count; i++)
            {
                Vec2 prev = path[i - 1];
                Vec2 curr = path[i];
                double segLen = prev.DistanceTo(curr);
                if (segLen < 1e-12) continue;

                double dx = (curr.X - prev.X) / segLen;
                double dy = (curr.Y - prev.Y) / segLen;

                double pos = step - accum; // remaining distance to next sample
                while (pos <= segLen)
                {
                    result.Add(new Vec2(prev.X + pos * dx, prev.Y + pos * dy));
                    pos += step;
                }
                accum = segLen - (pos - step);
            }

            // Always include last point
            var last = path[path.Count - 1];
            if (result.Count == 0 || result[result.Count - 1].DistanceTo(last) > 1e-6)
                result.Add(last);

            return result;
        }

        /// <summary>Compute convex hull using Andrew's monotone chain algorithm.</summary>
        public static List<Vec2> ComputeConvexHull(List<Vec2> points)
        {
            if (points.Count < 3) return new List<Vec2>(points);

            // Sort by X, then Y
            points.Sort((a, b) =>
            {
                int cx = a.X.CompareTo(b.X);
                return cx != 0 ? cx : a.Y.CompareTo(b.Y);
            });

            int n = points.Count;
            var hull = new Vec2[2 * n];
            int k = 0;

            // Lower hull
            for (int i = 0; i < n; i++)
            {
                while (k >= 2 && Cross(hull[k - 2], hull[k - 1], points[i]) <= 0)
                    k--;
                hull[k++] = points[i];
            }

            // Upper hull
            int lower = k + 1;
            for (int i = n - 2; i >= 0; i--)
            {
                while (k >= lower && Cross(hull[k - 2], hull[k - 1], points[i]) <= 0)
                    k--;
                hull[k++] = points[i];
            }

            var result = new List<Vec2>(k - 1);
            for (int i = 0; i < k - 1; i++) result.Add(hull[i]);
            return result;
        }

        private static double Cross(Vec2 o, Vec2 a, Vec2 b)
            => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        private static double NormalizeAngle(double a)
        {
            while (a > Math.PI) a -= 2.0 * Math.PI;
            while (a < -Math.PI) a += 2.0 * Math.PI;
            return a;
        }

        private static void ComputeMetrics(SimulationResult result, List<Vec2> frontPath)
        {
            // Max swept width: max distance between any outer envelope point
            // and the front-axle path at same station
            double maxWidth = 0;
            double maxOT = 0;

            foreach (var snap in result.Snapshots)
            {
                // Offtracking = distance between front and rear axle paths
                double ot = snap.FrontAxle.DistanceTo(snap.RearAxle) -
                            Math.Sqrt(snap.FrontAxle.DistanceTo(snap.RearAxle)); // placeholder

                // Swept width from body corners
                if (snap.BodyCorners.Length >= 4)
                {
                    for (int i = 0; i < snap.BodyCorners.Length; i++)
                        for (int j = i + 1; j < snap.BodyCorners.Length; j++)
                        {
                            double d = snap.BodyCorners[i].DistanceTo(snap.BodyCorners[j]);
                            if (d > maxWidth) maxWidth = d;
                        }
                }
            }

            // Offtracking: compare front-axle and rear-axle trace
            for (int i = 0; i < result.Snapshots.Count; i++)
            {
                var s = result.Snapshots[i];
                // perpendicular distance from rear axle to front-axle path tangent
                if (i < result.Snapshots.Count - 1)
                {
                    var s2 = result.Snapshots[i + 1];
                    double frontDist = s.FrontAxle.DistanceTo(s.RearAxle);
                    double directDist = Math.Sqrt(
                        (s.FrontAxle.X - s.RearAxle.X) * (s.FrontAxle.X - s.RearAxle.X) +
                        (s.FrontAxle.Y - s.RearAxle.Y) * (s.FrontAxle.Y - s.RearAxle.Y));
                    // offtracking is lateral displacement
                    double perpDx = -Math.Sin(s.Heading);
                    double perpDy = Math.Cos(s.Heading);
                    double lateralOffset = (s.FrontAxle.X - s.RearAxle.X) * perpDx +
                                           (s.FrontAxle.Y - s.RearAxle.Y) * perpDy;
                    double ot2 = Math.Abs(lateralOffset);
                    if (ot2 > maxOT) maxOT = ot2;
                }
            }

            result.MaxSweptWidth = maxWidth;
            result.MaxOfftracking = maxOT;
        }
    }

    /// <summary>
    /// Offtracking validation equations (SAE and WHI).
    /// </summary>
    public static class OfftrackingCalculator
    {
        /// <summary>
        /// SAE equation for single-unit low-speed steady-state offtracking.
        /// OT_max = sqrt(R² + WB²) - R
        /// </summary>
        public static double SAE(double turningRadius, double wheelbase)
        {
            return Math.Sqrt(turningRadius * turningRadius + wheelbase * wheelbase) - turningRadius;
        }

        /// <summary>
        /// WHI equation for multi-unit low-speed steady-state offtracking.
        /// OT_max = R - sqrt(R² - sum(Li²))
        /// </summary>
        public static double WHI(double turningRadius, double[] effectiveWheelbases)
        {
            double sumSq = 0;
            foreach (double l in effectiveWheelbases)
                sumSq += l * l;

            double inner = turningRadius * turningRadius - sumSq;
            if (inner < 0) return turningRadius; // can't make the turn
            return turningRadius - Math.Sqrt(inner);
        }
    }
}
