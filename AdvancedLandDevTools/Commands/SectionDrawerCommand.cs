using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AdvancedLandDevTools.Engine;
using AdvancedLandDevTools.UI;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcDbPolyline = Autodesk.AutoCAD.DatabaseServices.Polyline;

namespace AdvancedLandDevTools.Commands
{
    public class SectionDrawerCommand
    {
        private const string LAYER_NAME = "ALDT-SECTION";

        [CommandMethod("SECDRAW", CommandFlags.Modal)]
        public void SectionDraw()
        {
            try
            {
                if (!LicenseManager.EnsureLicensed()) return;
                var doc = AcadApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                var ed = doc.Editor;
                ed.WriteMessage("\n");
                ed.WriteMessage("========================================================\n");
                ed.WriteMessage("  Advanced Land Development Tools  |  Section Drawer     \n");
                ed.WriteMessage("========================================================\n");

                // 1. Show the section designer window
                var win = new SectionDrawerWindow();
                bool? result = AcadApp.ShowModalWindow(win);

                if (result != true || win.ResultProfile == null)
                {
                    ed.WriteMessage("\n  Command cancelled.\n");
                    return;
                }

                var profile = win.ResultProfile;

                // 2. Prompt for insertion point
                var ppo = new PromptPointOptions(
                    "\nClick insertion point for the section (centerline base): ")
                { AllowNone = false };

                var ppr = ed.GetPoint(ppo);
                if (ppr.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\n  No point selected - cancelled.\n");
                    return;
                }

                // 3. Draw the section as polylines at 1:1
                DrawSection(doc.Database, profile, ppr.Value, win.DrawSegmentLines);

                ed.WriteMessage($"\n  Section \"{profile.Name}\" placed " +
                    $"({profile.LeftSegments.Count}L + {profile.RightSegments.Count}R segments).\n");
            }
            catch (System.Exception ex)
            {
                var ed = AcadApp.DocumentManager.MdiActiveDocument?.Editor;
                ed?.WriteMessage($"\n  [SECDRAW ERROR] {ex.Message}\n");
            }
        }

        private static void DrawSection(Database db, SectionProfile profile,
            Point3d insertPt, bool drawSegmentLines)
        {
            var geo = SectionDrawerEngine.ComputePoints(profile);

            using var tx = db.TransactionManager.StartTransaction();
            var bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
            var btr = (BlockTableRecord)tx.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            // Ensure layer
            var lt = (LayerTable)tx.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(LAYER_NAME))
            {
                lt.UpgradeOpen();
                var lr = new LayerTableRecord
                {
                    Name = LAYER_NAME,
                    Color = Color.FromColorIndex(ColorMethod.ByAci, 4) // cyan
                };
                lt.Add(lr);
                tx.AddNewlyCreatedDBObject(lr, true);
            }
            var layerId = lt[LAYER_NAME];

            // Left + right surface lines — split at block segments so lines don't overdraw block outlines
            DrawSurfaceLines(btr, tx, insertPt, layerId, geo.LeftPoints,  profile.LeftSegments);
            DrawSurfaceLines(btr, tx, insertPt, layerId, geo.RightPoints, profile.RightSegments);

            // Block outlines (closed polylines for curbs/gutters) — always with concrete hatch
            foreach (var block in geo.BlockOutlines)
            {
                if (block.Count < 3) continue;
                var pl = new AcDbPolyline();
                for (int i = 0; i < block.Count; i++)
                {
                    var p = block[i];
                    pl.AddVertexAt(i,
                        new Point2d(insertPt.X + p.X, insertPt.Y + p.Y), 0, 0, 0);
                }
                pl.Closed = true;
                pl.LayerId = layerId;
                pl.ColorIndex = 8; // gray for concrete blocks
                btr.AppendEntity(pl);
                tx.AddNewlyCreatedDBObject(pl, true);

                try
                {
                    var hatch = new Hatch();
                    btr.AppendEntity(hatch);
                    tx.AddNewlyCreatedDBObject(hatch, true);
                    hatch.LayerId = layerId;
                    hatch.ColorIndex = 8;
                    hatch.SetHatchPattern(HatchPatternType.PreDefined, "AR-CONC");
                    hatch.PatternScale = 0.25;   // 0.05 was too small to render visibly
                    hatch.HatchStyle = HatchStyle.Normal;
                    hatch.Associative = false;
                    var ids = new ObjectIdCollection { pl.ObjectId };
                    hatch.AppendLoop(HatchLoopTypes.Polyline, ids);  // more stable for closed polylines
                    hatch.EvaluateHatch(true);
                }
                catch { }
            }

            // ── Road structural layers (per-segment IsRoad) ────────────────
            foreach (var region in geo.RoadRegions)
            {
                if (region.Count < 2) continue;
                double[] offsets  = { 0.2, 1.5, 2.0 };
                string[] patterns = { "SOLID", "GRAVEL", "ANSI31" };
                double[] scales   = { 1.0, 0.5, 0.5 };
                short[] colors    = { 8, 9, 8 };

                double cumOffset = 0;
                for (int layer = 0; layer < 3; layer++)
                {
                    double topOff = cumOffset;
                    cumOffset += offsets[layer];
                    double botOff = cumOffset;

                    var boundary = new AcDbPolyline();
                    int vtx = 0;

                    // Top edge L→R
                    foreach (var p in region)
                        boundary.AddVertexAt(vtx++,
                            new Point2d(insertPt.X + p.X, insertPt.Y + p.Y - topOff), 0, 0, 0);

                    // Bottom edge R→L
                    for (int i = region.Count - 1; i >= 0; i--)
                    {
                        var p = region[i];
                        boundary.AddVertexAt(vtx++,
                            new Point2d(insertPt.X + p.X, insertPt.Y + p.Y - botOff), 0, 0, 0);
                    }
                    boundary.Closed = true;
                    boundary.LayerId = layerId;
                    boundary.ColorIndex = colors[layer];
                    btr.AppendEntity(boundary);
                    tx.AddNewlyCreatedDBObject(boundary, true);

                    try
                    {
                        var hatch = new Hatch();
                        btr.AppendEntity(hatch);
                        tx.AddNewlyCreatedDBObject(hatch, true);
                        hatch.LayerId = layerId;
                        hatch.ColorIndex = colors[layer];
                        hatch.SetHatchPattern(HatchPatternType.PreDefined, patterns[layer]);
                        hatch.PatternScale = scales[layer];
                        hatch.Associative = false;
                        var ids = new ObjectIdCollection { boundary.ObjectId };
                        hatch.AppendLoop(HatchLoopTypes.Default, ids);
                        hatch.EvaluateHatch(true);
                    }
                    catch { }

                    // Bottom line
                    var botLine = new AcDbPolyline();
                    for (int i = 0; i < region.Count; i++)
                    {
                        var p = region[i];
                        botLine.AddVertexAt(i,
                            new Point2d(insertPt.X + p.X, insertPt.Y + p.Y - botOff), 0, 0, 0);
                    }
                    botLine.LayerId = layerId;
                    botLine.ColorIndex = 8;
                    btr.AppendEntity(botLine);
                    tx.AddNewlyCreatedDBObject(botLine, true);
                }

                // Vertical closing lines at region edges through all layers
                var lPt = region[0];
                var rPt = region[region.Count - 1];
                foreach (var edgePt in new[] { lPt, rPt })
                {
                    var edgeLine = new Autodesk.AutoCAD.DatabaseServices.Line(
                        new Point3d(insertPt.X + edgePt.X, insertPt.Y + edgePt.Y, 0),
                        new Point3d(insertPt.X + edgePt.X, insertPt.Y + edgePt.Y - cumOffset, 0))
                    {
                        LayerId = layerId, ColorIndex = 8
                    };
                    btr.AppendEntity(edgeLine);
                    tx.AddNewlyCreatedDBObject(edgeLine, true);
                }
            }

            // ── Grass/Earth layer (per-segment IsGrass) ────────────────
            foreach (var region in geo.GrassRegions)
            {
                if (region.Count < 2) continue;

                const double grassDepth = 2.0;

                var boundary = new AcDbPolyline();
                int vtx = 0;

                // Top edge L→R
                foreach (var p in region)
                    boundary.AddVertexAt(vtx++,
                        new Point2d(insertPt.X + p.X, insertPt.Y + p.Y), 0, 0, 0);

                // Bottom edge R→L
                for (int i = region.Count - 1; i >= 0; i--)
                {
                    var p = region[i];
                    boundary.AddVertexAt(vtx++,
                        new Point2d(insertPt.X + p.X, insertPt.Y + p.Y - grassDepth), 0, 0, 0);
                }
                boundary.Closed = true;
                boundary.LayerId = layerId;
                boundary.ColorIndex = 3; // green
                btr.AppendEntity(boundary);
                tx.AddNewlyCreatedDBObject(boundary, true);

                try
                {
                    var hatch = new Hatch();
                    btr.AppendEntity(hatch);
                    tx.AddNewlyCreatedDBObject(hatch, true);
                    hatch.LayerId = layerId;
                    hatch.ColorIndex = 3;
                    hatch.SetHatchPattern(HatchPatternType.PreDefined, "EARTH");
                    hatch.PatternScale = 1.0;
                    hatch.Associative = false;
                    var ids = new ObjectIdCollection { boundary.ObjectId };
                    hatch.AppendLoop(HatchLoopTypes.Default, ids);
                    hatch.EvaluateHatch(true);
                }
                catch { }

                // Bottom line
                var botLine = new AcDbPolyline();
                for (int i = 0; i < region.Count; i++)
                {
                    var p = region[i];
                    botLine.AddVertexAt(i,
                        new Point2d(insertPt.X + p.X, insertPt.Y + p.Y - grassDepth), 0, 0, 0);
                }
                botLine.LayerId = layerId;
                botLine.ColorIndex = 3;
                btr.AppendEntity(botLine);
                tx.AddNewlyCreatedDBObject(botLine, true);

                // Vertical closing lines at region edges
                var lPt = region[0];
                var rPt = region[region.Count - 1];
                foreach (var edgePt in new[] { lPt, rPt })
                {
                    var edgeLine = new Autodesk.AutoCAD.DatabaseServices.Line(
                        new Point3d(insertPt.X + edgePt.X, insertPt.Y + edgePt.Y, 0),
                        new Point3d(insertPt.X + edgePt.X, insertPt.Y + edgePt.Y - grassDepth, 0))
                    {
                        LayerId = layerId, ColorIndex = 3
                    };
                    btr.AppendEntity(edgeLine);
                    tx.AddNewlyCreatedDBObject(edgeLine, true);
                }
            }

            // Centerline — extends from section surface UPWARD by CL height
            double surfaceY = geo.CenterlineTopY;
            double clHeight = profile.CenterlineHeight;
            double clTopAbove = surfaceY + clHeight;

            var clLine = new Autodesk.AutoCAD.DatabaseServices.Line(
                new Point3d(insertPt.X, insertPt.Y + surfaceY, 0),
                new Point3d(insertPt.X, insertPt.Y + clTopAbove, 0))
            {
                LayerId = layerId,
                ColorIndex = 2, // yellow
                LinetypeScale = 0.5
            };
            btr.AppendEntity(clLine);
            tx.AddNewlyCreatedDBObject(clLine, true);

            // Segment divider lines (when checked) — one vertical line at every segment boundary
            if (drawSegmentLines)
            {
                foreach (var bp in geo.SegmentBoundaries)
                {
                    var divLine = new Autodesk.AutoCAD.DatabaseServices.Line(
                        new Point3d(insertPt.X + bp.X, insertPt.Y + bp.Y, 0),
                        new Point3d(insertPt.X + bp.X, insertPt.Y + clTopAbove, 0))
                    {
                        LayerId = layerId,
                        ColorIndex = 8 // gray
                    };
                    btr.AppendEntity(divLine);
                    tx.AddNewlyCreatedDBObject(divLine, true);
                }
            }

            tx.Commit();
        }

        /// <summary>
        /// Draws surface profile lines for one side, breaking the polyline at each block-type
        /// segment (TypeF, TypeD, ValleyGutter) so the line never overdraw the block outlines.
        /// A new polyline run resumes from the far end of each block.
        /// </summary>
        private static void DrawSurfaceLines(
            BlockTableRecord btr, Transaction tx,
            Point3d insertPt, ObjectId layerId,
            List<(double X, double Y)> points,
            List<SectionSegment> segments)
        {
            if (points.Count < 2) return;

            var runs = new List<List<(double X, double Y)>>();
            var current = new List<(double X, double Y)> { points[0] };
            int ptIdx = 0;

            foreach (var seg in segments)
            {
                int count = SectionDrawerEngine.SubPointCount(seg);

                if (seg.Type == Engine.SegmentType.Normal)
                {
                    for (int k = 1; k <= count; k++)
                        current.Add(points[ptIdx + k]);
                }
                else
                {
                    // Block segment: close the current run at the block start (already last pt),
                    // then begin a fresh run from the block's far end point.
                    if (current.Count >= 2) runs.Add(current);
                    current = new List<(double X, double Y)> { points[ptIdx + count] };
                }

                ptIdx += count;
            }

            if (current.Count >= 2) runs.Add(current);

            foreach (var run in runs)
            {
                if (run.Count < 2) continue;
                var pl = new AcDbPolyline();
                for (int i = 0; i < run.Count; i++)
                {
                    var p = run[i];
                    pl.AddVertexAt(i,
                        new Point2d(insertPt.X + p.X, insertPt.Y + p.Y), 0, 0, 0);
                }
                pl.LayerId = layerId;
                pl.ColorIndex = 4; // cyan
                btr.AppendEntity(pl);
                tx.AddNewlyCreatedDBObject(pl, true);
            }
        }
    }
}
