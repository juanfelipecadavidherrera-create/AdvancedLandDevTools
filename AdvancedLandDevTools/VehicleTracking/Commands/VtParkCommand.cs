using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AdvancedLandDevTools.VehicleTracking.Core;
using AdvancedLandDevTools.VehicleTracking.Data;

[assembly: CommandClass(typeof(AdvancedLandDevTools.VehicleTracking.Commands.VtParkCommand))]

namespace AdvancedLandDevTools.VehicleTracking.Commands
{
    /// <summary>
    /// VTPARK — Generate a parking layout within a rectangular area.
    /// </summary>
    public class VtParkCommand
    {
        [CommandMethod("VTPARK", CommandFlags.Modal)]
        public void Execute()
        {
            try
            {
                if (!Engine.LicenseManager.EnsureLicensed()) return;

                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                Editor ed = doc.Editor;
                Database db = doc.Database;

                ed.WriteMessage("\n═══════════════════════════════════════════════════════\n");
                ed.WriteMessage("  VEHICLE TRACKING  –  Parking Layout Generator\n");
                ed.WriteMessage("═══════════════════════════════════════════════════════\n");

                // ── Step 1: Pick two corners for the parking area ─────────
                var pt1Res = ed.GetPoint(new PromptPointOptions("\n  Pick first corner of parking area: "));
                if (pt1Res.Status != PromptStatus.OK) return;

                var pt2Opt = new PromptCornerOptions("\n  Pick opposite corner: ", pt1Res.Value);
                var pt2Res = ed.GetCorner(pt2Opt);
                if (pt2Res.Status != PromptStatus.OK) return;

                Point3d p1 = pt1Res.Value;
                Point3d p2 = pt2Res.Value;
                double width = Math.Abs(p2.X - p1.X);
                double height = Math.Abs(p2.Y - p1.Y);
                Vec2 origin = new Vec2(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y));

                ed.WriteMessage($"\n  Area: {width:F1}' x {height:F1}' = {width * height:F0} sq ft\n");

                // ── Step 2: Parking angle ─────────────────────────────────
                var angleOpt = new PromptKeywordOptions(
                    "\n  Parking angle [90/60/45/30/Parallel] <90>: ");
                angleOpt.Keywords.Add("90");
                angleOpt.Keywords.Add("60");
                angleOpt.Keywords.Add("45");
                angleOpt.Keywords.Add("30");
                angleOpt.Keywords.Add("Parallel");
                angleOpt.Keywords.Default = "90";
                angleOpt.AllowNone = true;

                ParkingAngle parkAngle = ParkingAngle.Perpendicular;
                var angleRes = ed.GetKeywords(angleOpt);
                if (angleRes.Status == PromptStatus.OK || angleRes.Status == PromptStatus.None)
                {
                    parkAngle = angleRes.StringResult switch
                    {
                        "60" => ParkingAngle.Angle60,
                        "45" => ParkingAngle.Angle45,
                        "30" => ParkingAngle.Angle30,
                        "Parallel" => ParkingAngle.Parallel,
                        _ => ParkingAngle.Perpendicular
                    };
                }

                // ── Step 3: ADA spaces ────────────────────────────────────
                var adaOpt = new PromptIntegerOptions("\n  Number of ADA spaces <1>: ")
                {
                    DefaultValue = 1,
                    LowerLimit = 0,
                    UpperLimit = 50
                };
                adaOpt.UseDefaultValue = true;

                int adaCount = 1;
                var adaRes = ed.GetInteger(adaOpt);
                if (adaRes.Status == PromptStatus.OK)
                    adaCount = adaRes.Value;

                // ── Step 4: Generate layout ───────────────────────────────
                var dims = FloridaParkingDefaults.GetByAngle(parkAngle);
                var ada = FloridaParkingDefaults.GetAdaRequirements();

                var generator = new ParkingLayoutGenerator
                {
                    Dimensions = dims,
                    Ada = ada,
                    AdaSpacesRequired = adaCount,
                    TwoWayAisle = true
                };

                var layout = generator.Generate(origin, width, height);

                if (layout.Stalls.Count == 0)
                {
                    ed.WriteMessage("\n  Area too small to fit any parking stalls.\n");
                    return;
                }

                // ── Step 5: Draw it ───────────────────────────────────────
                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var btr = (BlockTableRecord)tx.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    VtDrawingWriter.DrawParking(db, tx, btr, layout);

                    // Draw boundary rectangle
                    var boundary = new Polyline();
                    boundary.AddVertexAt(0, new Point2d(origin.X, origin.Y), 0, 0, 0);
                    boundary.AddVertexAt(1, new Point2d(origin.X + width, origin.Y), 0, 0, 0);
                    boundary.AddVertexAt(2, new Point2d(origin.X + width, origin.Y + height), 0, 0, 0);
                    boundary.AddVertexAt(3, new Point2d(origin.X, origin.Y + height), 0, 0, 0);
                    boundary.Closed = true;
                    boundary.LayerId = VtLayerManager.GetLayerId(db, tx, VtLayerManager.PARKING);
                    btr.AppendEntity(boundary);
                    tx.AddNewlyCreatedDBObject(boundary, true);

                    tx.Commit();
                }

                ed.WriteMessage($"\n  ═══ PARKING LAYOUT ═══");
                ed.WriteMessage($"\n  Angle:     {(int)parkAngle}°");
                ed.WriteMessage($"\n  Stall:     {dims.StallWidth}' x {dims.StallDepth}'");
                ed.WriteMessage($"\n  Aisle:     {dims.AisleWidthTwoWay}' (two-way)");
                ed.WriteMessage($"\n  Regular:   {layout.TotalRegularSpaces}");
                ed.WriteMessage($"\n  ADA:       {layout.TotalAccessibleSpaces}");
                ed.WriteMessage($"\n  Total:     {layout.Stalls.Count}");
                ed.WriteMessage("\n═══════════════════════════════════════════════════════\n");
            }
            catch (System.Exception ex)
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage($"\n[VTPARK ERROR] {ex.Message}\n");
            }
        }
    }
}
