using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.EditorInput;
using CivilApp = Autodesk.Civil.ApplicationServices;
using CivilDB  = Autodesk.Civil.DatabaseServices;

namespace AdvancedLandDevTools.Engine
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Result
    // ─────────────────────────────────────────────────────────────────────────
    public class AlignDeployResult
    {
        public int CreatedCount { get; set; }
        public List<string> Log { get; } = new();

        public void AddSuccess(string msg) { CreatedCount++; Log.Add($"  ✓  {msg}"); }
        public void AddInfo   (string msg) { Log.Add($"  ℹ  {msg}"); }
        public void AddFailure(string msg) { Log.Add($"  ✗  {msg}"); }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Engine
    // ─────────────────────────────────────────────────────────────────────────
    public static class AlignDeployEngine
    {
        /// <summary>
        /// Main entry point – called by AlignDeployCommand after validation.
        /// </summary>
        public static AlignDeployResult Run(
            ObjectId mainAlignId,
            ObjectId crossAlignId,
            double   offset)
        {
            var result = new AlignDeployResult();

            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                result.AddFailure("No active document.");
                return result;
            }

            Database db  = doc.Database;

            using (Transaction trans = db.TransactionManager.StartTransaction())
            {
                try
                {
                    CivilApp.CivilDocument civDoc =
                        CivilApp.CivilDocument.GetCivilDocument(db);

                    var mainAl  = trans.GetObject(mainAlignId,  OpenMode.ForRead) as CivilDB.Alignment;
                    var crossAl = trans.GetObject(crossAlignId, OpenMode.ForRead) as CivilDB.Alignment;

                    if (mainAl == null || crossAl == null)
                        throw new InvalidOperationException("Could not open one or both alignments.");

                    // ── 1. Find intersection station on main alignment ────────────
                    double intersectStation = FindIntersectionStation(mainAl, crossAl);
                    result.AddInfo($"Intersection at station {FormatStation(intersectStation)}");

                    // ── 2. Reference angle at intersection ───────────────────────
                    double θ0 = GetTangentAngle(mainAl, intersectStation);

                    // ── 3. Extract ALL vertices from cross alignment ──────────────
                    //
                    //  A cross alignment with a PI has multiple tangent entities.
                    //  We iterate Entities in order and collect the ordered vertex
                    //  list: [start of entity 0, end of entity 0, end of entity 1, ...]
                    //  For a straight line (no PI) this gives exactly 2 points.
                    //  For one PI this gives 3 points, two PIs → 4 points, etc.
                    //
                    var crossVertices = ExtractVertices(crossAl);
                    if (crossVertices.Count < 2)
                        throw new InvalidOperationException(
                            "Cross alignment has fewer than 2 vertices.");

                    result.AddInfo($"Cross alignment vertices: {crossVertices.Count}");

                    // Compute midpoint of the whole cross alignment (first → last vertex)
                    // Used as the pivot point for rotation in local space.
                    Point2d crossFirst = crossVertices[0];
                    Point2d crossLast  = crossVertices[crossVertices.Count - 1];
                    Point2d crossMid   = new Point2d(
                        (crossFirst.X + crossLast.X) / 2.0,
                        (crossFirst.Y + crossLast.Y) / 2.0);

                    // Convert vertices to local vectors relative to midpoint
                    var localVectors = new List<(double vx, double vy)>();
                    foreach (var pt in crossVertices)
                        localVectors.Add((pt.X - crossMid.X, pt.Y - crossMid.Y));

                    // ── 4. Resolve style/layer/site from cross alignment ──────────
                    ObjectId styleId          = crossAl.StyleId;
                    ObjectId layerId          = crossAl.LayerId;
                    ObjectId siteId           = crossAl.SiteId;
                    double   crossStartStation = crossAl.StartingStation; // preserve e.g. -0+57

                    // Resolve alignment label set from document styles
                    ObjectId labelSetId = ResolveAlignmentLabelSet(civDoc, trans);

                    // ── 5. Loop along main alignment ─────────────────────────────
                    double currentStation = intersectStation + offset;
                    double endStation     = mainAl.EndingStation;
                    int    copyIndex      = 1;

                    while (currentStation <= endStation + 0.001)
                    {
                        try
                        {
                            // Point on main alignment at this station
                            double px = 0, py = 0;
                            mainAl.PointLocation(currentStation, 0, ref px, ref py);
                            Point2d deployPt = new Point2d(px, py);

                            // Rotation delta from reference angle
                            double θn    = GetTangentAngle(mainAl, currentStation);
                            double delta = θn - θ0;

                            // Transform ALL vertices
                            var transformedPts = new List<Point2d>();
                            foreach (var (vx, vy) in localVectors)
                                transformedPts.Add(RotateAndTranslate(vx, vy, delta, deployPt));

                            // Name: CrossName-STA12+34.56
                            string name = $"{crossAl.Name}-{FormatStation(currentStation)}";
                            name = EnsureUniqueName(name, civDoc, trans);

                            // Create new alignment
                            ObjectId newAlId = CivilDB.Alignment.Create(
                                civDoc, name, siteId, layerId, styleId, labelSetId);

                            var newAl = trans.GetObject(newAlId, OpenMode.ForWrite)
                                        as CivilDB.Alignment
                                        ?? throw new InvalidOperationException("Failed to open new alignment for write.");

                            // Add a fixed line segment between each consecutive vertex pair
                            // This correctly reproduces PIs: 2 pts = 1 segment,
                            // 3 pts = 2 segments (one PI), 4 pts = 3 segments, etc.
                            for (int vi = 0; vi < transformedPts.Count - 1; vi++)
                            {
                                newAl.Entities.AddFixedLine(
                                    new Point3d(transformedPts[vi].X,     transformedPts[vi].Y,     0),
                                    new Point3d(transformedPts[vi+1].X,   transformedPts[vi+1].Y,   0));
                            }

                            // Match starting station of original cross alignment (e.g. -0+57)
                            // StartingStation is read-only; ReferencePointStation shifts the datum.
                            newAl.ReferencePointStation = crossStartStation;

                            result.AddSuccess(
                                $"{name}  @ station {FormatStation(currentStation)}  " +
                                $"({transformedPts[0].X:F1},{transformedPts[0].Y:F1}) → " +
                                $"({transformedPts[transformedPts.Count-1].X:F1}," +
                                $"{transformedPts[transformedPts.Count-1].Y:F1})  " +
                                $"[{transformedPts.Count} pts, {transformedPts.Count-1} seg(s)]");

                            copyIndex++;
                        }
                        catch (System.Exception ex)
                        {
                            result.AddFailure(
                                $"Station {FormatStation(currentStation)}: {ex.Message}");
                        }

                        currentStation += offset;
                    }

                    result.AddInfo(
                        $"Total copies created: {result.CreatedCount}  |  " +
                        $"Interval: {offset:F2}ft  |  " +
                        $"From sta {FormatStation(intersectStation + offset)} " +
                        $"to sta {FormatStation(currentStation - offset)}");

                    trans.Commit();
                }
                catch (System.Exception ex)
                {
                    result.AddFailure($"Fatal: {ex.Message}");
                    trans.Abort();
                }
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Find the station on mainAl closest to where crossAl crosses it
        // ─────────────────────────────────────────────────────────────────────
        private static double FindIntersectionStation(
            CivilDB.Alignment mainAl,
            CivilDB.Alignment crossAl)
        {
            // Sample both alignments and find closest approach in XY
            int    samples  = 500;
            double bestDist = double.MaxValue;
            double bestSta  = mainAl.StartingStation;

            double mainLen  = mainAl.EndingStation  - mainAl.StartingStation;
            double crossLen = crossAl.EndingStation - crossAl.StartingStation;

            // Sample cross alignment at several points
            var crossPts = new List<Point2d>();
            for (int ci = 0; ci <= 20; ci++)
            {
                double cSta = crossAl.StartingStation + (crossLen * ci / 20.0);
                double cx = 0, cy = 0;
                crossAl.PointLocation(cSta, 0, ref cx, ref cy);
                crossPts.Add(new Point2d(cx, cy));
            }

            // Sample main alignment and find station closest to any cross point
            for (int mi = 0; mi <= samples; mi++)
            {
                double mSta = mainAl.StartingStation + (mainLen * mi / (double)samples);
                double mx = 0, my = 0;
                mainAl.PointLocation(mSta, 0, ref mx, ref my);
                Point2d mPt = new Point2d(mx, my);

                foreach (var cPt in crossPts)
                {
                    double d = mPt.GetDistanceTo(cPt);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestSta  = mSta;
                    }
                }
            }

            return bestSta;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Tangent angle at a station (radians) – computed numerically
        // ─────────────────────────────────────────────────────────────────────
        private static double GetTangentAngle(CivilDB.Alignment al, double station)
        {
            double step = 0.1;
            double s1   = Math.Max(al.StartingStation, station - step);
            double s2   = Math.Min(al.EndingStation,   station + step);

            double x1 = 0, y1 = 0, x2 = 0, y2 = 0;
            al.PointLocation(s1, 0, ref x1, ref y1);
            al.PointLocation(s2, 0, ref x2, ref y2);

            return Math.Atan2(y2 - y1, x2 - x1);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Rotate a local vector by delta and translate to target point
        // ─────────────────────────────────────────────────────────────────────
        private static Point2d RotateAndTranslate(
            double vx, double vy, double delta, Point2d origin)
        {
            double cosD = Math.Cos(delta);
            double sinD = Math.Sin(delta);
            return new Point2d(
                origin.X + vx * cosD - vy * sinD,
                origin.Y + vx * sinD + vy * cosD);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Station formatting  →  "12+34.56"
        // ─────────────────────────────────────────────────────────────────────
        private static string FormatStation(double station)
        {
            int    hundreds = (int)(station / 100);
            double remainder = station - hundreds * 100.0;
            return $"{hundreds}+{remainder:00.00}";
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Ensure name is unique within the Civil document
        // ─────────────────────────────────────────────────────────────────────
        private static string EnsureUniqueName(
            string baseName,
            CivilApp.CivilDocument civDoc,
            Transaction trans)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in civDoc.GetAlignmentIds())
            {
                try
                {
                    var al = trans.GetObject(id, OpenMode.ForRead) as CivilDB.Alignment;
                    if (al != null) existing.Add(al.Name);
                }
                catch { }
            }

            if (!existing.Contains(baseName)) return baseName;

            for (int i = 2; i < 999; i++)
            {
                string candidate = $"{baseName}({i})";
                if (!existing.Contains(candidate)) return candidate;
            }
            return baseName + Guid.NewGuid().ToString("N")[..6];
        }
        // ─────────────────────────────────────────────────────────────────────
        //  Extract ordered vertices from an alignment (handles PIs)
        //  Returns [start, PI1, PI2, ..., end] as Point2d list.
        //  AlignmentEntity has no StartPoint/EndPoint – we use StartStation/
        //  EndStation and call PointLocation on the parent alignment.
        // ─────────────────────────────────────────────────────────────────────
        private static List<Point2d> ExtractVertices(CivilDB.Alignment al)
        {
            var pts = new List<Point2d>();

            try
            {
                // AlignmentEntity exposes no useful geometry properties in Civil 3D 2026.
                // Instead: sample the alignment at fine intervals and detect PI locations
                // by finding where the tangent angle changes by more than a threshold.
                // For a straight alignment: 2 points. One PI: 3 points. Two PIs: 4, etc.

                double start  = al.StartingStation;
                double end    = al.EndingStation;
                double length = end - start;
                int    steps  = Math.Max(500, (int)(length / 0.5)); // sample every 0.5ft
                double step   = length / steps;

                // Always include the start point
                double x0 = 0, y0 = 0;
                al.PointLocation(start, 0, ref x0, ref y0);
                pts.Add(new Point2d(x0, y0));

                double prevAngle = GetTangentAngle(al, start + step);

                for (int i = 2; i < steps; i++)
                {
                    double sta   = start + i * step;
                    double angle = GetTangentAngle(al, sta);

                    // Normalise angle difference to [-π, π]
                    double diff = angle - prevAngle;
                    while (diff >  Math.PI) diff -= 2 * Math.PI;
                    while (diff < -Math.PI) diff += 2 * Math.PI;

                    // Angle change > 0.5° means we crossed a PI
                    if (Math.Abs(diff) > 0.5 * Math.PI / 180.0)
                    {
                        // Walk back to find exact PI location (bisection)
                        double piSta = sta - step;
                        double x = 0, y = 0;
                        al.PointLocation(piSta, 0, ref x, ref y);
                        Point2d piPt = new Point2d(x, y);

                        // Only add if not too close to last point
                        if (pts[pts.Count - 1].GetDistanceTo(piPt) > 0.1)
                            pts.Add(piPt);
                    }

                    prevAngle = angle;
                }

                // Always include the end point
                double xe = 0, ye = 0;
                al.PointLocation(end, 0, ref xe, ref ye);
                Point2d endPt = new Point2d(xe, ye);
                if (pts[pts.Count - 1].GetDistanceTo(endPt) > 0.1)
                    pts.Add(endPt);
            }
            catch
            {
                pts.Clear();
                double fx = 0, fy = 0, lx = 0, ly = 0;
                al.PointLocation(al.StartingStation, 0, ref fx, ref fy);
                al.PointLocation(al.EndingStation,   0, ref lx, ref ly);
                pts.Add(new Point2d(fx, fy));
                pts.Add(new Point2d(lx, ly));
            }

            return pts;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Resolve a valid AlignmentLabelSetStyle from the document
        // ─────────────────────────────────────────────────────────────────────
        private static ObjectId ResolveAlignmentLabelSet(
            CivilApp.CivilDocument civDoc, Transaction trans)
        {
            ObjectId fallback = ObjectId.Null;
            try
            {
                foreach (ObjectId id in civDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles)
                {
                    try
                    {
                        var obj = trans.GetObject(id, OpenMode.ForRead);
                        if (obj == null) continue;
                        // Prefer "No Labels" style to avoid cluttering the drawing
                        if (obj is CivilDB.Styles.StyleBase sb &&
                            sb.Name.Contains("No Label", StringComparison.OrdinalIgnoreCase))
                            return id;
                        if (fallback.IsNull) fallback = id;
                    }
                    catch { }
                }
            }
            catch { }
            return fallback; // ObjectId.Null is acceptable – Civil 3D uses default
        }
    }
}
