using System;
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
                DrawSection(doc.Database, profile, ppr.Value);

                ed.WriteMessage($"\n  Section \"{profile.Name}\" placed " +
                    $"({profile.LeftSegments.Count}L + {profile.RightSegments.Count}R segments).\n");
            }
            catch (System.Exception ex)
            {
                var ed = AcadApp.DocumentManager.MdiActiveDocument?.Editor;
                ed?.WriteMessage($"\n  [SECDRAW ERROR] {ex.Message}\n");
            }
        }

        private static void DrawSection(Database db, SectionProfile profile, Point3d insertPt)
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

            // Left polyline (no centerline in output)
            if (geo.LeftPoints.Count >= 2)
            {
                var pl = new AcDbPolyline();
                for (int i = 0; i < geo.LeftPoints.Count; i++)
                {
                    var p = geo.LeftPoints[i];
                    pl.AddVertexAt(i,
                        new Point2d(insertPt.X + p.X, insertPt.Y + p.Y), 0, 0, 0);
                }
                pl.LayerId = layerId;
                pl.ColorIndex = 4;
                btr.AppendEntity(pl);
                tx.AddNewlyCreatedDBObject(pl, true);
            }

            // Right polyline
            if (geo.RightPoints.Count >= 2)
            {
                var pl = new AcDbPolyline();
                for (int i = 0; i < geo.RightPoints.Count; i++)
                {
                    var p = geo.RightPoints[i];
                    pl.AddVertexAt(i,
                        new Point2d(insertPt.X + p.X, insertPt.Y + p.Y), 0, 0, 0);
                }
                pl.LayerId = layerId;
                pl.ColorIndex = 4;
                btr.AppendEntity(pl);
                tx.AddNewlyCreatedDBObject(pl, true);
            }

            tx.Commit();
        }
    }
}
