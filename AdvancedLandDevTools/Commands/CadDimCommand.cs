using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AdvancedLandDevTools.Commands.CadDimCommand))]

namespace AdvancedLandDevTools.Commands
{
    /// <summary>
    /// CAD — Cross-section dimension tool.
    ///
    /// Pick two reference lines (Line or Polyline segment) and an anchor point
    /// on the first.  A perpendicular axis is constructed from the anchor up to
    /// the second reference line, and aligned dimensions are placed:
    ///   • Top dim: full span anchor → intersection (offset 5 du).
    ///   • Sub-dim row: subdivided at every other Line that crosses the axis
    ///     (offset 10 du).  Skipped if no obstacles are found.
    /// </summary>
    public class CadDimCommand
    {
        private const double TopDimOffset = 5.0;
        private const double SubDimOffset = 10.0;
        private const double Tol = 1e-7;

        [CommandMethod("CAD", CommandFlags.Modal)]
        public void Execute()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor   ed = doc.Editor;
            Database db = doc.Database;

            try
            {
                if (!Engine.LicenseManager.EnsureLicensed()) return;

                ed.WriteMessage("\n═══════════════════════════════════════════════════════════");
                ed.WriteMessage("\n  Advanced Land Development Tools  |  CAD Dimension");
                ed.WriteMessage("\n  Dimensions a perpendicular span between two reference lines,");
                ed.WriteMessage("\n  subdividing at every other line that crosses the axis.");
                ed.WriteMessage("\n═══════════════════════════════════════════════════════════");

                // ── Step 1: pick reference line 1 ────────────────────────
                if (!PickReferenceSegment(ed, "\n  Select reference line 1 (Line or Polyline segment): ",
                                          out Point3d r1Start, out Point3d r1End, out ObjectId r1Id))
                    return;

                // ── Step 2: pick reference line 2 ────────────────────────
                if (!PickReferenceSegment(ed, "\n  Select reference line 2 (Line or Polyline segment): ",
                                          out Point3d r2Start, out Point3d r2End, out ObjectId r2Id))
                    return;

                // ── Step 3: pick anchor point ────────────────────────────
                var anchorRes = ed.GetPoint(new PromptPointOptions(
                    "\n  Pick anchor point on reference line 1: "));
                if (anchorRes.Status != PromptStatus.OK) return;
                Point3d rawAnchor = anchorRes.Value;

                // Project anchor onto ref-line-1
                Vector3d r1Dir = r1End - r1Start;
                if (r1Dir.LengthSqrd < Tol)
                {
                    ed.WriteMessage("\n  Reference line 1 has zero length.");
                    return;
                }
                Vector3d r1U = r1Dir.GetNormal();
                Point3d anchorOnRef1 = ProjectOnLine(rawAnchor, r1Start, r1U);

                // Perpendicular direction (Rotate90 of ref1.Direction in XY plane)
                Vector3d perpDir = new Vector3d(-r1U.Y, r1U.X, 0.0);

                // ── Step 4: intersect perpendicular ray with ref-line-2 (infinite) ──
                if (!IntersectInfiniteLines2d(anchorOnRef1, perpDir,
                                              r2Start, (r2End - r2Start),
                                              out Point3d intersectionWithRef2))
                {
                    ed.WriteMessage("\n  The perpendicular axis is parallel to reference line 2 — they never meet.");
                    ed.WriteMessage("\n  Aborting.");
                    return;
                }

                // Lift Z to anchor's plane (use anchor Z for consistency)
                intersectionWithRef2 = new Point3d(intersectionWithRef2.X,
                                                    intersectionWithRef2.Y,
                                                    anchorOnRef1.Z);

                Vector3d pathVec = intersectionWithRef2 - anchorOnRef1;
                double pathLen = pathVec.Length;
                if (pathLen < Tol)
                {
                    ed.WriteMessage("\n  Anchor coincides with reference line 2 — zero-length span.");
                    return;
                }
                Vector3d pathDir = pathVec / pathLen;

                // Offset direction = +90° rotation of pathDir
                Vector3d offsetDir = new Vector3d(-pathDir.Y, pathDir.X, 0.0);

                ed.WriteMessage($"\n  Anchor on ref-line-1: ({anchorOnRef1.X:F3}, {anchorOnRef1.Y:F3})");
                ed.WriteMessage($"\n  Intersection ref-2:   ({intersectionWithRef2.X:F3}, {intersectionWithRef2.Y:F3})");
                ed.WriteMessage($"\n  Total span length:    {pathLen:F3} du");

                // ── Step 5: find obstacle crossings ──────────────────────
                var obstacles = new List<(double T, Point3d Pt)>();

                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var btr = tx.GetObject(db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                    if (btr == null)
                    {
                        ed.WriteMessage("\n  Cannot access current space.");
                        tx.Abort();
                        return;
                    }

                    // Build a temporary path Line for IntersectWith
                    using (var pathLine = new Line(anchorOnRef1, intersectionWithRef2))
                    {
                        foreach (ObjectId id in btr)
                        {
                            if (id == r1Id || id == r2Id) continue;
                            if (!id.ObjectClass.IsDerivedFrom(RXClass.GetClass(typeof(Line)))) continue;

                            var line = tx.GetObject(id, OpenMode.ForRead) as Line;
                            if (line == null) continue;

                            var pts = new Point3dCollection();
                            try
                            {
                                line.IntersectWith(pathLine, Intersect.OnBothOperands,
                                                    pts, IntPtr.Zero, IntPtr.Zero);
                            }
                            catch { continue; }

                            foreach (Point3d p in pts)
                            {
                                // Param along path: 0..1
                                double t = ((p - anchorOnRef1)).DotProduct(pathDir) / pathLen;
                                if (t > Tol && t < 1.0 - Tol)
                                {
                                    // Snap Z to anchor plane
                                    var snapped = new Point3d(p.X, p.Y, anchorOnRef1.Z);
                                    obstacles.Add((t, snapped));
                                }
                            }
                        }
                    }

                    tx.Commit();
                }

                // Sort + dedupe near-coincident crossings
                obstacles.Sort((a, b) => a.T.CompareTo(b.T));
                var dedup = new List<(double T, Point3d Pt)>();
                foreach (var o in obstacles)
                {
                    if (dedup.Count == 0 ||
                        Math.Abs(o.T - dedup[dedup.Count - 1].T) * pathLen > 1e-4)
                        dedup.Add(o);
                }
                obstacles = dedup;

                ed.WriteMessage($"\n  Obstacle crossings detected: {obstacles.Count}");

                // ── Step 6: place the dimensions ─────────────────────────
                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var btr = tx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite) as BlockTableRecord;
                    if (btr == null)
                    {
                        ed.WriteMessage("\n  Cannot open current space for write.");
                        tx.Abort();
                        return;
                    }

                    ObjectId dimStyleId = db.Dimstyle;

                    // RotatedDimension's `rotation` is the angle of the dim line
                    // (the direction along which the measurement is taken),
                    // measured in radians from world X.  Locking it to the path
                    // direction means: if the user later moves an extension-line
                    // origin, the dimension's axis stays at this fixed angle and
                    // the displayed value is the projection of the new endpoint
                    // span onto that axis — i.e. the dim does NOT rotate.
                    double dimRotation = Math.Atan2(pathDir.Y, pathDir.X);

                    // Top dim: full span at TopDimOffset
                    Point3d topMid       = MidPoint(anchorOnRef1, intersectionWithRef2);
                    Point3d topDimLinePt = topMid + offsetDir * TopDimOffset;

                    var topDim = new RotatedDimension(dimRotation,
                                                       anchorOnRef1, intersectionWithRef2,
                                                       topDimLinePt, "", dimStyleId);
                    btr.AppendEntity(topDim);
                    tx.AddNewlyCreatedDBObject(topDim, true);

                    ed.WriteMessage($"\n  ✓ Top dim placed at ({topDimLinePt.X:F3}, {topDimLinePt.Y:F3})");

                    // Sub-dim row: only if obstacles exist
                    if (obstacles.Count > 0)
                    {
                        var allPts = new List<Point3d>();
                        allPts.Add(anchorOnRef1);
                        foreach (var o in obstacles) allPts.Add(o.Pt);
                        allPts.Add(intersectionWithRef2);

                        for (int i = 0; i < allPts.Count - 1; i++)
                        {
                            Point3d a = allPts[i];
                            Point3d b = allPts[i + 1];
                            Point3d mid = MidPoint(a, b);
                            Point3d subDimLinePt = mid + offsetDir * SubDimOffset;

                            var subDim = new RotatedDimension(dimRotation,
                                                               a, b,
                                                               subDimLinePt, "", dimStyleId);
                            btr.AppendEntity(subDim);
                            tx.AddNewlyCreatedDBObject(subDim, true);
                        }

                        ed.WriteMessage($"\n  ✓ Sub-dim row placed: {obstacles.Count + 1} segments at offset {SubDimOffset} du");
                    }
                    else
                    {
                        ed.WriteMessage("\n  No obstacles — sub-dim row skipped.");
                    }

                    tx.Commit();
                }

                ed.WriteMessage("\n  ═══ CAD COMPLETE ═══");
                ed.WriteMessage($"\n  Span: {pathLen:F3} du   Obstacles: {obstacles.Count}");
                ed.WriteMessage($"\n  Top dim offset: {TopDimOffset} du   Sub-dim offset: {SubDimOffset} du\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[CAD ERROR] {ex.Message}\n");
            }
        }

        // ─────────────────────────────────────────────────────────────────
        //  Pick a Line or Polyline segment.  For polylines, use the segment
        //  closest to the picked point.  Returns the segment's start/end
        //  endpoints in world coordinates plus the host entity's ObjectId.
        // ─────────────────────────────────────────────────────────────────
        private static bool PickReferenceSegment(
            Editor ed, string prompt,
            out Point3d segStart, out Point3d segEnd, out ObjectId hostId)
        {
            segStart = Point3d.Origin;
            segEnd   = Point3d.Origin;
            hostId   = ObjectId.Null;

            var peo = new PromptEntityOptions(prompt);
            peo.SetRejectMessage("\n  Must be a Line or Polyline.");
            peo.AddAllowedClass(typeof(Line),       false);
            peo.AddAllowedClass(typeof(Polyline),   false);
            peo.AddAllowedClass(typeof(Polyline2d), false);

            var per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK) return false;

            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            using (var tx = doc.Database.TransactionManager.StartTransaction())
            {
                var ent = tx.GetObject(per.ObjectId, OpenMode.ForRead);

                if (ent is Line line)
                {
                    segStart = line.StartPoint;
                    segEnd   = line.EndPoint;
                    hostId   = per.ObjectId;
                    tx.Commit();
                    return true;
                }

                if (ent is Polyline pl)
                {
                    int segIx = ClosestSegmentIndex(pl, per.PickedPoint);
                    if (segIx < 0) { tx.Abort(); return false; }
                    segStart = pl.GetPoint3dAt(segIx);
                    segEnd   = pl.GetPoint3dAt(segIx + 1);
                    hostId   = per.ObjectId;
                    tx.Commit();
                    return true;
                }

                if (ent is Polyline2d pl2)
                {
                    // Approximate: get vertices and pick closest segment.
                    var verts = new List<Point3d>();
                    foreach (ObjectId vid in pl2)
                    {
                        var v = tx.GetObject(vid, OpenMode.ForRead) as Vertex2d;
                        if (v != null) verts.Add(v.Position);
                    }
                    if (verts.Count < 2) { tx.Abort(); return false; }

                    int bestIx = 0;
                    double bestDist = double.MaxValue;
                    for (int i = 0; i < verts.Count - 1; i++)
                    {
                        double d = DistPointToSegment(per.PickedPoint, verts[i], verts[i + 1]);
                        if (d < bestDist) { bestDist = d; bestIx = i; }
                    }
                    segStart = verts[bestIx];
                    segEnd   = verts[bestIx + 1];
                    hostId   = per.ObjectId;
                    tx.Commit();
                    return true;
                }

                tx.Abort();
            }
            return false;
        }

        private static int ClosestSegmentIndex(Polyline pl, Point3d pickPt)
        {
            int n = pl.NumberOfVertices;
            if (n < 2) return -1;

            int last = pl.Closed ? n : n - 1;
            int bestIx = 0;
            double bestDist = double.MaxValue;

            for (int i = 0; i < last; i++)
            {
                Point3d a = pl.GetPoint3dAt(i);
                Point3d b = pl.GetPoint3dAt((i + 1) % n);
                double d = DistPointToSegment(pickPt, a, b);
                if (d < bestDist) { bestDist = d; bestIx = i; }
            }
            return bestIx;
        }

        private static double DistPointToSegment(Point3d p, Point3d a, Point3d b)
        {
            Vector3d ab = b - a;
            double len2 = ab.LengthSqrd;
            if (len2 < Tol) return (p - a).Length;
            double t = ((p - a).DotProduct(ab)) / len2;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            Point3d proj = a + ab * t;
            return (p - proj).Length;
        }

        private static Point3d ProjectOnLine(Point3d p, Point3d origin, Vector3d unitDir)
        {
            double t = (p - origin).DotProduct(unitDir);
            return origin + unitDir * t;
        }

        private static Point3d MidPoint(Point3d a, Point3d b)
        {
            return new Point3d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5);
        }

        // 2-D infinite-line intersection.  P + t*d  vs  Q + s*e.
        // Returns false if parallel.
        private static bool IntersectInfiniteLines2d(
            Point3d p, Vector3d d, Point3d q, Vector3d e, out Point3d hit)
        {
            hit = Point3d.Origin;
            double cross = d.X * e.Y - d.Y * e.X;
            if (Math.Abs(cross) < Tol) return false;

            double dx = q.X - p.X;
            double dy = q.Y - p.Y;
            double t = (dx * e.Y - dy * e.X) / cross;

            hit = new Point3d(p.X + d.X * t, p.Y + d.Y * t, p.Z);
            return true;
        }
    }
}
