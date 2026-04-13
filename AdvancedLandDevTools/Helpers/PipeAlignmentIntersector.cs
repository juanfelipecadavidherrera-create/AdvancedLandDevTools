using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivilDB = Autodesk.Civil.DatabaseServices;

namespace AdvancedLandDevTools.Helpers
{
    /// <summary>
    /// One verified intersection between a pipe and an alignment.
    /// A single pipe can produce multiple crossings (curved pipes or
    /// alignments that double back).
    /// </summary>
    public class PipeAlignmentCrossing
    {
        /// <summary>XY from geometric intersection, Z from pipe 3D geometry.</summary>
        public Point3d IntersectionPointWCS { get; set; }

        /// <summary>Station along the alignment at the crossing.</summary>
        public double Station { get; set; }

        /// <summary>Offset from alignment center (should be near zero at a true crossing).</summary>
        public double Offset { get; set; }

        /// <summary>Pipe centerline Z at the crossing point.</summary>
        public double PipeCenterlineZ { get; set; }

        /// <summary>Inner radius of the pipe at the crossing.</summary>
        public double InnerRadius { get; set; }

        /// <summary>Invert elevation (centerline Z minus inner radius).</summary>
        public double InvertElevation { get; set; }

        /// <summary>Crown elevation (centerline Z plus inner radius).</summary>
        public double CrownElevation { get; set; }

        /// <summary>Surface/terrain Z at the crossing XY (null if no surface provided).</summary>
        public double? SurfaceElevation { get; set; }

        /// <summary>Cover depth: surface Z minus crown Z (null if no surface).</summary>
        public double? CoverDepth { get; set; }

        /// <summary>ObjectId of the pipe entity.</summary>
        public ObjectId PipeId { get; set; }

        /// <summary>Display name of the pipe.</summary>
        public string PipeName { get; set; } = "";

        /// <summary>"Gravity" or "Pressure".</summary>
        public string PipeKind { get; set; } = "";
    }

    /// <summary>
    /// Computes exact geometric intersections between pipes and Civil 3D alignments.
    /// Uses Entity.IntersectWith for precise detection, falling back to an endpoint
    /// heuristic when the geometric API is unavailable.
    /// </summary>
    public static class PipeAlignmentIntersector
    {
        private const double StationTolerance = 1.0;   // ft beyond alignment ends
        private const double OffsetTolerance  = 1.0;   // ft max offset for a valid crossing
        private const double ZeroOffset       = 0.01;  // ft — treat as "on" the alignment

        // ─────────────────────────────────────────────────────────────
        //  Primary API: find all crossings for a single pipe
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns every point where <paramref name="pipeId"/> crosses
        /// <paramref name="alignment"/>, with full invert/crown/surface data.
        /// </summary>
        public static List<PipeAlignmentCrossing> FindCrossings(
            ObjectId            pipeId,
            CivilDB.Alignment   alignment,
            Transaction         tx,
            ObjectId?           surfaceId = null)
        {
            var results = new List<PipeAlignmentCrossing>();
            var obj = tx.GetObject(pipeId, OpenMode.ForRead);

            if (obj is CivilDB.Pipe gravityPipe)
                FindGravityCrossings(gravityPipe, pipeId, alignment, tx, surfaceId, results);
            else if (obj is CivilDB.PressurePipe pressurePipe)
                FindPressureCrossings(pressurePipe, pipeId, alignment, tx, surfaceId, results);

            return results;
        }

        // ─────────────────────────────────────────────────────────────
        //  Quick boolean: does this pipe cross the alignment?
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true if the pipe crosses the alignment.
        /// Uses geometric intersection first, falls back to endpoint heuristic.
        /// </summary>
        public static bool PipeCrossesAlignment(
            ObjectId            pipeId,
            CivilDB.Alignment   alignment,
            Transaction         tx)
        {
            try
            {
                var crossings = FindCrossings(pipeId, alignment, tx);
                if (crossings.Count > 0) return true;
            }
            catch
            {
                // Geometric intersection failed — fall back to endpoints
            }

            // Fallback: endpoint heuristic
            var obj = tx.GetObject(pipeId, OpenMode.ForRead);
            Point3d start, end;
            if (obj is CivilDB.Pipe gp)          { start = gp.StartPoint; end = gp.EndPoint; }
            else if (obj is CivilDB.PressurePipe pp) { start = pp.StartPoint; end = pp.EndPoint; }
            else return false;

            return PipeCrossesAlignmentByEndpoints(start, end, alignment);
        }

        // ─────────────────────────────────────────────────────────────
        //  Batch: filter a collection of pipe IDs to crossing pipes
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns only those pipe IDs that cross <paramref name="alignment"/>.
        /// </summary>
        public static List<ObjectId> FilterCrossingPipes(
            IEnumerable<ObjectId> pipeIds,
            CivilDB.Alignment     alignment,
            Transaction           tx)
        {
            var result = new List<ObjectId>();
            foreach (var pid in pipeIds)
            {
                if (PipeCrossesAlignment(pid, alignment, tx))
                    result.Add(pid);
            }
            return result;
        }

        // ─────────────────────────────────────────────────────────────
        //  Legacy endpoint heuristic (preserved as fallback)
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Endpoint-only crossing check: returns true when the two pipe
        /// endpoints have opposite-sign offsets from the alignment.
        /// </summary>
        public static bool PipeCrossesAlignmentByEndpoints(
            Point3d startPt, Point3d endPt, CivilDB.Alignment al)
        {
            try
            {
                double sta1 = 0, off1 = 0, sta2 = 0, off2 = 0;
                al.StationOffset(startPt.X, startPt.Y, ref sta1, ref off1);
                al.StationOffset(endPt.X,   endPt.Y,   ref sta2, ref off2);

                bool inRange =
                    (sta1 >= al.StartingStation - StationTolerance &&
                     sta1 <= al.EndingStation   + StationTolerance) ||
                    (sta2 >= al.StartingStation - StationTolerance &&
                     sta2 <= al.EndingStation   + StationTolerance);
                if (!inRange) return false;

                if (Math.Abs(off1) < ZeroOffset || Math.Abs(off2) < ZeroOffset) return true;
                return (off1 * off2) < 0;
            }
            catch { return false; }
        }

        // ═════════════════════════════════════════════════════════════
        //  Internal: gravity pipe crossings
        // ═════════════════════════════════════════════════════════════

        private static void FindGravityCrossings(
            CivilDB.Pipe        pipe,
            ObjectId            pipeId,
            CivilDB.Alignment   alignment,
            Transaction         tx,
            ObjectId?           surfaceId,
            List<PipeAlignmentCrossing> results)
        {
            double innerRadius = pipe.InnerDiameterOrWidth / 2.0;
            string pipeName    = pipe.Name;

            // CivilDB.Pipe inherits from Curve — use Entity.IntersectWith
            var pipeEntity = (Entity)pipe;
            var alEntity   = (Entity)alignment;

            var intersections = new Point3dCollection();
            try
            {
                alEntity.IntersectWith(
                    pipeEntity,
                    Intersect.OnBothOperands,
                    intersections,
                    IntPtr.Zero,
                    IntPtr.Zero);
            }
            catch
            {
                // IntersectWith unavailable — fall back to endpoint interpolation
                AddEndpointFallback(pipe.StartPoint, pipe.EndPoint, innerRadius,
                                    pipeId, pipeName, "Gravity", alignment, tx,
                                    surfaceId, results);
                return;
            }

            if (intersections.Count == 0)
                return; // no crossing — correct result, not a failure

            var pipeCurve = (Curve)pipe;
            foreach (Point3d rawPt in intersections)
            {
                if (!ValidateStation(rawPt, alignment, out double station, out double offset))
                    continue;

                // Get 3D point on the pipe curve for accurate Z
                double pipeZ;
                try
                {
                    Point3d closest = pipeCurve.GetClosestPointTo(rawPt, false);
                    pipeZ = closest.Z;
                }
                catch
                {
                    // Fallback: linear Z interpolation
                    pipeZ = InterpolatePipeZ(pipe.StartPoint, pipe.EndPoint, rawPt);
                }

                var crossing = BuildCrossing(
                    new Point3d(rawPt.X, rawPt.Y, pipeZ),
                    station, offset, pipeZ, innerRadius,
                    pipeId, pipeName, "Gravity");

                TrySampleSurface(crossing, tx, surfaceId);
                results.Add(crossing);
            }
        }

        // ═════════════════════════════════════════════════════════════
        //  Internal: pressure pipe crossings
        // ═════════════════════════════════════════════════════════════

        private static void FindPressureCrossings(
            CivilDB.PressurePipe pipe,
            ObjectId             pipeId,
            CivilDB.Alignment    alignment,
            Transaction          tx,
            ObjectId?            surfaceId,
            List<PipeAlignmentCrossing> results)
        {
            double innerRadius = pipe.InnerDiameter / 2.0;
            string pipeName    = pipe.Name;
            Point3d startPt    = pipe.StartPoint;
            Point3d endPt      = pipe.EndPoint;

            // Try Entity.IntersectWith first (PressurePipe is an Entity)
            var intersections = new Point3dCollection();
            bool geometricOk  = false;
            try
            {
                var alEntity   = (Entity)alignment;
                var pipeEntity = (Entity)pipe;
                alEntity.IntersectWith(
                    pipeEntity,
                    Intersect.OnBothOperands,
                    intersections,
                    IntPtr.Zero,
                    IntPtr.Zero);
                geometricOk = true;
            }
            catch
            {
                // PressurePipe may not support IntersectWith in some C3D versions
            }

            if (geometricOk && intersections.Count > 0)
            {
                foreach (Point3d rawPt in intersections)
                {
                    if (!ValidateStation(rawPt, alignment, out double station, out double offset))
                        continue;

                    double pipeZ = InterpolatePipeZ(startPt, endPt, rawPt);

                    var crossing = BuildCrossing(
                        new Point3d(rawPt.X, rawPt.Y, pipeZ),
                        station, offset, pipeZ, innerRadius,
                        pipeId, pipeName, "Pressure");

                    TrySampleSurface(crossing, tx, surfaceId);
                    results.Add(crossing);
                }
            }
            else
            {
                // Fallback for pressure pipes (always straight — endpoint heuristic is valid)
                AddEndpointFallback(startPt, endPt, innerRadius,
                                    pipeId, pipeName, "Pressure", alignment, tx,
                                    surfaceId, results);
            }
        }

        // ═════════════════════════════════════════════════════════════
        //  Shared helpers
        // ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Validates that a raw intersection point falls within the alignment's
        /// station range and is close to the alignment centerline.
        /// </summary>
        private static bool ValidateStation(
            Point3d pt, CivilDB.Alignment al,
            out double station, out double offset)
        {
            station = 0; offset = 0;
            try
            {
                al.StationOffset(pt.X, pt.Y, ref station, ref offset);
            }
            catch { return false; }

            if (station < al.StartingStation - StationTolerance ||
                station > al.EndingStation   + StationTolerance)
                return false;

            if (Math.Abs(offset) > OffsetTolerance)
                return false;

            return true;
        }

        /// <summary>
        /// Linearly interpolates the pipe centerline Z between start and end
        /// at the projection of <paramref name="pt"/> onto the 2D pipe axis.
        /// </summary>
        private static double InterpolatePipeZ(Point3d start, Point3d end, Point3d pt)
        {
            double dx  = end.X - start.X;
            double dy  = end.Y - start.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.001) return start.Z;

            double t = ((pt.X - start.X) * dx + (pt.Y - start.Y) * dy) / (len * len);
            t = Math.Max(0.0, Math.Min(1.0, t));
            return start.Z + t * (end.Z - start.Z);
        }

        /// <summary>
        /// Builds a <see cref="PipeAlignmentCrossing"/> from computed values.
        /// </summary>
        private static PipeAlignmentCrossing BuildCrossing(
            Point3d wcs, double station, double offset,
            double centerZ, double innerRadius,
            ObjectId pipeId, string pipeName, string pipeKind)
        {
            return new PipeAlignmentCrossing
            {
                IntersectionPointWCS = wcs,
                Station              = station,
                Offset               = offset,
                PipeCenterlineZ      = centerZ,
                InnerRadius          = innerRadius,
                InvertElevation      = centerZ - innerRadius,
                CrownElevation       = centerZ + innerRadius,
                PipeId               = pipeId,
                PipeName             = pipeName,
                PipeKind             = pipeKind
            };
        }

        /// <summary>
        /// Attempts to sample a TIN surface at the crossing point and
        /// populate <see cref="PipeAlignmentCrossing.SurfaceElevation"/>
        /// and <see cref="PipeAlignmentCrossing.CoverDepth"/>.
        /// </summary>
        private static void TrySampleSurface(
            PipeAlignmentCrossing crossing,
            Transaction tx,
            ObjectId? surfaceId)
        {
            if (!surfaceId.HasValue || surfaceId.Value.IsNull)
                return;

            try
            {
                var surface = tx.GetObject(surfaceId.Value, OpenMode.ForRead)
                              as CivilDB.TinSurface;
                if (surface == null) return;

                double surfZ = surface.FindElevationAtXY(
                    crossing.IntersectionPointWCS.X,
                    crossing.IntersectionPointWCS.Y);
                crossing.SurfaceElevation = surfZ;
                crossing.CoverDepth       = surfZ - crossing.CrownElevation;
            }
            catch
            {
                // Point outside surface boundary — leave fields null
            }
        }

        /// <summary>
        /// Fallback when geometric intersection is unavailable: checks endpoints,
        /// and if the pipe crosses, computes a single crossing at the projected
        /// midpoint between the two endpoints.
        /// </summary>
        private static void AddEndpointFallback(
            Point3d startPt, Point3d endPt,
            double innerRadius,
            ObjectId pipeId, string pipeName, string pipeKind,
            CivilDB.Alignment alignment,
            Transaction tx,
            ObjectId? surfaceId,
            List<PipeAlignmentCrossing> results)
        {
            if (!PipeCrossesAlignmentByEndpoints(startPt, endPt, alignment))
                return;

            // Estimate crossing point: project pipe midpoint onto alignment via StationOffset
            double sta1 = 0, off1 = 0, sta2 = 0, off2 = 0;
            try
            {
                alignment.StationOffset(startPt.X, startPt.Y, ref sta1, ref off1);
                alignment.StationOffset(endPt.X,   endPt.Y,   ref sta2, ref off2);
            }
            catch { return; }

            // Interpolate parameter t where offset crosses zero
            double dOff = off2 - off1;
            double t = Math.Abs(dOff) > 0.001 ? -off1 / dOff : 0.5;
            t = Math.Max(0.0, Math.Min(1.0, t));

            double ix = startPt.X + t * (endPt.X - startPt.X);
            double iy = startPt.Y + t * (endPt.Y - startPt.Y);
            double iz = startPt.Z + t * (endPt.Z - startPt.Z);

            double station = 0, offset = 0;
            try { alignment.StationOffset(ix, iy, ref station, ref offset); }
            catch { return; }

            var crossing = BuildCrossing(
                new Point3d(ix, iy, iz),
                station, offset, iz, innerRadius,
                pipeId, pipeName, pipeKind);

            TrySampleSurface(crossing, tx, surfaceId);
            results.Add(crossing);
        }
    }
}
