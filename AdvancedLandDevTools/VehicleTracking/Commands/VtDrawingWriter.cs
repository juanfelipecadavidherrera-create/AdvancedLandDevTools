using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AdvancedLandDevTools.VehicleTracking.Core;

namespace AdvancedLandDevTools.VehicleTracking.Commands
{
    /// <summary>
    /// Writes swept path simulation results to the AutoCAD drawing.
    /// </summary>
    public static class VtDrawingWriter
    {
        /// <summary>
        /// Draw the complete simulation result (envelope, wheel paths, body outlines, collisions).
        /// Must be called within an open Transaction.
        /// </summary>
        public static void DrawResult(
            Database db, Transaction tx, BlockTableRecord btr,
            SimulationResult result, double labelInterval = 50.0)
        {
            VtLayerManager.EnsureLayers(db, tx);

            // ── Outer swept envelope ──────────────────────────────────
            if (result.OuterEnvelope.Count > 2)
            {
                var outerPl = CreatePolyline(result.OuterEnvelope, closed: true);
                outerPl.LayerId = VtLayerManager.GetLayerId(db, tx, VtLayerManager.OUTER_SWEEP);
                outerPl.ColorIndex = 1;
                btr.AppendEntity(outerPl);
                tx.AddNewlyCreatedDBObject(outerPl, true);

                // Hatch the envelope
                try
                {
                    var hatch = new Hatch();
                    hatch.LayerId = outerPl.LayerId;
                    hatch.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
                    hatch.ColorIndex = 1;
                    var transparency = new Autodesk.AutoCAD.Colors.Transparency(180); // ~70% transparent
                    hatch.Transparency = transparency;
                    btr.AppendEntity(hatch);
                    tx.AddNewlyCreatedDBObject(hatch, true);
                    var ids = new ObjectIdCollection { outerPl.ObjectId };
                    hatch.AppendLoop(HatchLoopTypes.Outermost, ids);
                    hatch.EvaluateHatch(true);
                }
                catch { /* hatch is optional, don't fail */ }
            }

            // ── Inner wheel path ──────────────────────────────────────
            if (result.InnerWheelPath.Count > 1)
            {
                var innerPl = CreatePolyline(result.InnerWheelPath, closed: false);
                innerPl.LayerId = VtLayerManager.GetLayerId(db, tx, VtLayerManager.INNER_SWEEP);
                innerPl.ColorIndex = 3;
                btr.AppendEntity(innerPl);
                tx.AddNewlyCreatedDBObject(innerPl, true);
            }

            // ── Outer wheel path ──────────────────────────────────────
            if (result.OuterWheelPath.Count > 1)
            {
                var outerWp = CreatePolyline(result.OuterWheelPath, closed: false);
                outerWp.LayerId = VtLayerManager.GetLayerId(db, tx, VtLayerManager.INNER_SWEEP);
                outerWp.ColorIndex = 3;
                btr.AppendEntity(outerWp);
                tx.AddNewlyCreatedDBObject(outerWp, true);
            }

            // ── Vehicle body outlines at snapshot intervals ───────────
            foreach (var snap in result.Snapshots)
            {
                if (snap.BodyCorners.Length >= 4)
                {
                    var bodyPl = CreatePolyline(
                        new List<Vec2>(snap.BodyCorners), closed: true);
                    bodyPl.LayerId = VtLayerManager.GetLayerId(db, tx, VtLayerManager.VEHICLE);
                    bodyPl.ColorIndex = 5;
                    btr.AppendEntity(bodyPl);
                    tx.AddNewlyCreatedDBObject(bodyPl, true);

                    // Also draw trailer outlines
                    foreach (var ts in snap.TrailerSnapshots)
                    {
                        if (ts.BodyCorners.Length >= 4)
                        {
                            var tPl = CreatePolyline(
                                new List<Vec2>(ts.BodyCorners), closed: true);
                            tPl.LayerId = VtLayerManager.GetLayerId(db, tx, VtLayerManager.VEHICLE);
                            tPl.ColorIndex = 150; // dark blue
                            btr.AppendEntity(tPl);
                            tx.AddNewlyCreatedDBObject(tPl, true);
                        }
                    }
                }
            }

            // ── Collision markers ─────────────────────────────────────
            foreach (var hit in result.Collisions)
            {
                var circle = new Circle(
                    new Point3d(hit.Location.X, hit.Location.Y, 0),
                    Vector3d.ZAxis, 1.5);
                circle.LayerId = VtLayerManager.GetLayerId(db, tx, VtLayerManager.COLLISION);
                circle.ColorIndex = 6;
                btr.AppendEntity(circle);
                tx.AddNewlyCreatedDBObject(circle, true);
            }

            // ── Swept width labels at intervals ──────────────────────
            double nextLabel = 0;
            foreach (var snap in result.Snapshots)
            {
                if (snap.Station >= nextLabel)
                {
                    // Compute local swept width from body corners
                    double sw = 0;
                    if (snap.BodyCorners.Length >= 4)
                    {
                        // Width perpendicular to heading
                        double perpX = -Math.Sin(snap.Heading);
                        double perpY = Math.Cos(snap.Heading);
                        double minProj = double.MaxValue, maxProj = double.MinValue;
                        foreach (var c in snap.BodyCorners)
                        {
                            double proj = c.X * perpX + c.Y * perpY;
                            if (proj < minProj) minProj = proj;
                            if (proj > maxProj) maxProj = proj;
                        }
                        sw = maxProj - minProj;
                    }

                    var mt = new MText();
                    mt.Location = new Point3d(
                        snap.FrontAxle.X + 3, snap.FrontAxle.Y + 3, 0);
                    mt.Contents = $"SW: {sw:F1}'";
                    mt.TextHeight = 1.5;
                    mt.LayerId = VtLayerManager.GetLayerId(db, tx, VtLayerManager.LABELS);
                    mt.ColorIndex = 2;
                    btr.AppendEntity(mt);
                    tx.AddNewlyCreatedDBObject(mt, true);

                    nextLabel = snap.Station + labelInterval;
                }
            }
        }

        /// <summary>
        /// Draw a parking layout result.
        /// </summary>
        public static void DrawParking(
            Database db, Transaction tx, BlockTableRecord btr,
            ParkingLayoutResult layout)
        {
            VtLayerManager.EnsureLayers(db, tx);

            foreach (var stall in layout.Stalls)
            {
                string layer = stall.IsAccessible ? VtLayerManager.ADA : VtLayerManager.PARKING;
                short color = stall.IsAccessible ? (short)30 : (short)7;

                var pl = CreatePolyline(new List<Vec2>(stall.Corners), closed: true);
                pl.LayerId = VtLayerManager.GetLayerId(db, tx, layer);
                pl.ColorIndex = color;
                btr.AppendEntity(pl);
                tx.AddNewlyCreatedDBObject(pl, true);

                // ADA symbol label
                if (stall.IsAccessible)
                {
                    var mt = new MText();
                    mt.Location = new Point3d(stall.Center.X, stall.Center.Y, 0);
                    mt.Contents = stall.IsVanAccessible ? "VAN" : "ADA";
                    mt.TextHeight = 1.2;
                    mt.Attachment = AttachmentPoint.MiddleCenter;
                    mt.LayerId = VtLayerManager.GetLayerId(db, tx, VtLayerManager.ADA);
                    mt.ColorIndex = 30;
                    btr.AppendEntity(mt);
                    tx.AddNewlyCreatedDBObject(mt, true);
                }
            }

            // Summary label
            var summary = new MText();
            var first = layout.Stalls.Count > 0 ? layout.Stalls[0] : null;
            double sx = first?.Center.X ?? 0;
            double sy = first?.Center.Y ?? 0;
            summary.Location = new Point3d(sx, sy - 10, 0);
            summary.Contents = $"PARKING SUMMARY\\P" +
                               $"Regular: {layout.TotalRegularSpaces}\\P" +
                               $"ADA: {layout.TotalAccessibleSpaces}\\P" +
                               $"Total: {layout.Stalls.Count}";
            summary.TextHeight = 2.0;
            summary.LayerId = VtLayerManager.GetLayerId(db, tx, VtLayerManager.LABELS);
            summary.ColorIndex = 2;
            btr.AppendEntity(summary);
            tx.AddNewlyCreatedDBObject(summary, true);
        }

        // ── Polyline helper ──────────────────────────────────────────

        private static Polyline CreatePolyline(List<Vec2> points, bool closed)
        {
            var pl = new Polyline();
            for (int i = 0; i < points.Count; i++)
                pl.AddVertexAt(i, new Point2d(points[i].X, points[i].Y), 0, 0, 0);
            pl.Closed = closed;
            return pl;
        }
    }
}
