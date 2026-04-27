using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
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
        private const int BatchSize = 20;

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

            // ── Pre-compute all template data in a read-only transaction ─────
            double intersectStation;
            double θ0;
            List<(double vx, double vy)> localVectors;
            ObjectId styleId, layerId, labelSetId;
            double crossStartStation;
            double endStation;
            int crossVertexCount;
            string crossAlignName;

            using (Transaction readTx = db.TransactionManager.StartTransaction())
            {
                try
                {
                    CivilApp.CivilDocument civDoc =
                        CivilApp.CivilDocument.GetCivilDocument(db);

                    var mainAl  = readTx.GetObject(mainAlignId,  OpenMode.ForRead) as CivilDB.Alignment;
                    var crossAl = readTx.GetObject(crossAlignId, OpenMode.ForRead) as CivilDB.Alignment;

                    if (mainAl == null || crossAl == null)
                        throw new InvalidOperationException("Could not open one or both alignments.");

                    // 1. Find intersection station
                    intersectStation = FindIntersectionStation(mainAl, crossAl);
                    result.AddInfo($"Intersection at station {FormatStation(intersectStation)}");

                    // 2. Reference angle at intersection
                    θ0 = GetTangentAngle(mainAl, intersectStation);

                    // 3. Extract vertices from cross alignment
                    var crossVertices = ExtractVertices(crossAl);
                    if (crossVertices.Count < 2)
                        throw new InvalidOperationException(
                            "Cross alignment has fewer than 2 vertices.");

                    crossVertexCount = crossVertices.Count;
                    result.AddInfo($"Cross alignment vertices: {crossVertexCount}");

                    Point2d crossFirst = crossVertices[0];
                    Point2d crossLast  = crossVertices[crossVertices.Count - 1];
                    Point2d crossMid   = new Point2d(
                        (crossFirst.X + crossLast.X) / 2.0,
                        (crossFirst.Y + crossLast.Y) / 2.0);

                    localVectors = new List<(double vx, double vy)>();
                    foreach (var pt in crossVertices)
                        localVectors.Add((pt.X - crossMid.X, pt.Y - crossMid.Y));

                    // 4. Resolve style/layer/name from cross alignment
                    styleId          = crossAl.StyleId;
                    layerId          = crossAl.LayerId;
                    crossStartStation = crossAl.StartingStation;
                    crossAlignName    = crossAl.Name;

                    labelSetId = ResolveAlignmentLabelSet(civDoc, readTx);
                    endStation = mainAl.EndingStation;
                }
                catch (System.Exception ex)
                {
                    result.AddFailure($"Fatal: {ex.Message}");
                    readTx.Abort();
                    return result;
                }
                readTx.Abort(); // read-only
            }

            // ── Build name set once (avoids O(n²) rescanning) ────────────────
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (Transaction nameTx = db.TransactionManager.StartTransaction())
            {
                try
                {
                    var civDoc = CivilApp.CivilDocument.GetCivilDocument(db);
                    foreach (ObjectId id in civDoc.GetAlignmentIds())
                    {
                        try
                        {
                            var al = nameTx.GetObject(id, OpenMode.ForRead) as CivilDB.Alignment;
                            if (al != null) usedNames.Add(al.Name);
                        }
                        catch { }
                    }
                }
                catch { }
                nameTx.Abort();
            }

            // ── Create alignments in batches ─────────────────────────────────
            double currentStation = intersectStation + offset;
            int    batchCount     = 0;
            Transaction tx        = null;
            CivilApp.CivilDocument batchCivDoc = null;

            try
            {
                while (currentStation <= endStation + 0.001)
                {
                    // Start a new batch transaction if needed
                    if (tx == null)
                    {
                        tx = db.TransactionManager.StartTransaction();
                        batchCivDoc = CivilApp.CivilDocument.GetCivilDocument(db);
                        batchCount = 0;
                    }

                    try
                    {
                        var mainAl = tx.GetObject(mainAlignId, OpenMode.ForRead)
                                     as CivilDB.Alignment;
                        if (mainAl == null) throw new InvalidOperationException("Cannot open main alignment.");

                        // Point on main alignment at this station
                        double px = 0, py = 0;
                        mainAl.PointLocation(currentStation, 0, ref px, ref py);
                        Point2d deployPt = new Point2d(px, py);

                        // Rotation delta from reference angle
                        double θn    = GetTangentAngle(mainAl, currentStation);
                        double delta = θn - θ0;

                        // Transform vertices
                        var transformedPts = new List<Point2d>();
                        foreach (var (vx, vy) in localVectors)
                            transformedPts.Add(RotateAndTranslate(vx, vy, delta, deployPt));

                        // Build unique name without rescanning all alignments
                        string baseName = $"{crossAlignName}-{FormatStation(currentStation)}";
                        string name = MakeUniqueName(baseName, usedNames);
                        usedNames.Add(name);

                        // Use ObjectId.Null for site to avoid topology rebuild per alignment
                        ObjectId newAlId = CivilDB.Alignment.Create(
                            batchCivDoc, name, ObjectId.Null, layerId, styleId, labelSetId);

                        var newAl = tx.GetObject(newAlId, OpenMode.ForWrite)
                                    as CivilDB.Alignment
                                    ?? throw new InvalidOperationException("Failed to open new alignment for write.");

                        for (int vi = 0; vi < transformedPts.Count - 1; vi++)
                        {
                            newAl.Entities.AddFixedLine(
                                new Point3d(transformedPts[vi].X,     transformedPts[vi].Y,     0),
                                new Point3d(transformedPts[vi + 1].X, transformedPts[vi + 1].Y, 0));
                        }

                        newAl.ReferencePointStation = crossStartStation;

                        result.AddSuccess(
                            $"{name}  @ station {FormatStation(currentStation)}  " +
                            $"({transformedPts[0].X:F1},{transformedPts[0].Y:F1}) → " +
                            $"({transformedPts[transformedPts.Count - 1].X:F1}," +
                            $"{transformedPts[transformedPts.Count - 1].Y:F1})  " +
                            $"[{transformedPts.Count} pts, {transformedPts.Count - 1} seg(s)]");

                        batchCount++;
                    }
                    catch (System.Exception ex)
                    {
                        result.AddFailure(
                            $"Station {FormatStation(currentStation)}: {ex.Message}");
                    }

                    currentStation += offset;

                    // Commit batch to free Civil 3D's undo buffer
                    if (batchCount >= BatchSize || currentStation > endStation + 0.001)
                    {
                        tx.Commit();
                        tx.Dispose();
                        tx = null;

                        // Let Civil 3D process events between batches
                        System.Windows.Forms.Application.DoEvents();
                    }
                }
            }
            catch (System.Exception ex)
            {
                result.AddFailure($"Fatal: {ex.Message}");
                if (tx != null)
                {
                    try { tx.Abort(); } catch { }
                    tx.Dispose();
                }
                return result;
            }
            finally
            {
                if (tx != null)
                {
                    try { tx.Commit(); } catch { try { tx.Abort(); } catch { } }
                    tx.Dispose();
                }
            }

            result.AddInfo(
                $"Total copies created: {result.CreatedCount}  |  " +
                $"Interval: {offset:F2}ft  |  " +
                $"From sta {FormatStation(intersectStation + offset)} " +
                $"to sta {FormatStation(currentStation - offset)}");

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Find the station on mainAl closest to where crossAl crosses it.
        //  Uses Entity.IntersectWith for an exact answer, with a brute-force
        //  fallback if the API call fails.
        // ─────────────────────────────────────────────────────────────────────
        private static double FindIntersectionStation(
            CivilDB.Alignment mainAl,
            CivilDB.Alignment crossAl)
        {
            // Try exact geometric intersection first
            try
            {
                var pts = new Point3dCollection();
                ((Entity)mainAl).IntersectWith(
                    (Entity)crossAl,
                    Intersect.OnBothOperands,
                    pts,
                    IntPtr.Zero,
                    IntPtr.Zero);

                if (pts.Count > 0)
                {
                    double station = 0, offset = 0;
                    mainAl.StationOffset(pts[0].X, pts[0].Y, ref station, ref offset);
                    return station;
                }
            }
            catch { }

            // Fallback: sampling approach
            int    samples  = 500;
            double bestDist = double.MaxValue;
            double bestSta  = mainAl.StartingStation;

            double mainLen  = mainAl.EndingStation  - mainAl.StartingStation;
            double crossLen = crossAl.EndingStation - crossAl.StartingStation;

            var crossPts = new List<Point2d>();
            for (int ci = 0; ci <= 20; ci++)
            {
                double cSta = crossAl.StartingStation + (crossLen * ci / 20.0);
                double cx = 0, cy = 0;
                crossAl.PointLocation(cSta, 0, ref cx, ref cy);
                crossPts.Add(new Point2d(cx, cy));
            }

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
        //  Station formatting  →  "12+34.56"  (handles negative stations)
        // ─────────────────────────────────────────────────────────────────────
        private static string FormatStation(double station)
        {
            string sign = "";
            double abs  = station;
            if (station < 0)
            {
                sign = "-";
                abs  = -station;
            }
            int    hundreds  = (int)(abs / 100);
            double remainder = abs - hundreds * 100.0;
            return $"{sign}{hundreds}+{remainder:00.00}";
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Build a unique name from the pre-built set (O(1) per call)
        // ─────────────────────────────────────────────────────────────────────
        private static string MakeUniqueName(string baseName, HashSet<string> usedNames)
        {
            if (!usedNames.Contains(baseName)) return baseName;

            for (int i = 2; i < 9999; i++)
            {
                string candidate = $"{baseName}({i})";
                if (!usedNames.Contains(candidate)) return candidate;
            }
            return baseName + Guid.NewGuid().ToString("N")[..6];
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Extract ordered vertices from an alignment (handles PIs)
        //  Returns [start, PI1, PI2, ..., end] as Point2d list.
        // ─────────────────────────────────────────────────────────────────────
        private static List<Point2d> ExtractVertices(CivilDB.Alignment al)
        {
            var pts = new List<Point2d>();

            try
            {
                double start  = al.StartingStation;
                double end    = al.EndingStation;
                double length = end - start;
                int    steps  = Math.Max(500, (int)(length / 0.5));
                double step   = length / steps;

                double x0 = 0, y0 = 0;
                al.PointLocation(start, 0, ref x0, ref y0);
                pts.Add(new Point2d(x0, y0));

                double prevAngle = GetTangentAngle(al, start + step);

                for (int i = 2; i < steps; i++)
                {
                    double sta   = start + i * step;
                    double angle = GetTangentAngle(al, sta);

                    double diff = angle - prevAngle;
                    while (diff >  Math.PI) diff -= 2 * Math.PI;
                    while (diff < -Math.PI) diff += 2 * Math.PI;

                    if (Math.Abs(diff) > 0.5 * Math.PI / 180.0)
                    {
                        double piSta = sta - step;
                        double x = 0, y = 0;
                        al.PointLocation(piSta, 0, ref x, ref y);
                        Point2d piPt = new Point2d(x, y);

                        if (pts[pts.Count - 1].GetDistanceTo(piPt) > 0.1)
                            pts.Add(piPt);
                    }

                    prevAngle = angle;
                }

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
                        if (obj is CivilDB.Styles.StyleBase sb &&
                            sb.Name.Contains("No Label", StringComparison.OrdinalIgnoreCase))
                            return id;
                        if (fallback.IsNull) fallback = id;
                    }
                    catch { }
                }
            }
            catch { }
            return fallback;
        }
    }
}
