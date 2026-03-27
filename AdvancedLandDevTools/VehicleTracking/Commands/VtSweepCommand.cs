using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AdvancedLandDevTools.VehicleTracking.Core;
using AdvancedLandDevTools.VehicleTracking.Data;

[assembly: CommandClass(typeof(AdvancedLandDevTools.VehicleTracking.Commands.VtSweepCommand))]

namespace AdvancedLandDevTools.VehicleTracking.Commands
{
    /// <summary>
    /// VTSWEEP — Run a swept path analysis along a polyline or alignment.
    /// Command-line version (the palette provides the GUI version).
    /// </summary>
    public class VtSweepCommand
    {
        [CommandMethod("VTSWEEP", CommandFlags.Modal)]
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
                ed.WriteMessage("  VEHICLE TRACKING  –  Swept Path Analysis\n");
                ed.WriteMessage("═══════════════════════════════════════════════════════\n");

                // ── Step 1: Vehicle selection ─────────────────────────────
                var display = VehicleLibrary.GetDisplayList();
                ed.WriteMessage("\n  Available Design Vehicles:\n");
                for (int i = 0; i < display.Count; i++)
                {
                    var d = display[i];
                    string fl = d.Category == "Semi" && display[i].Name.Contains("Florida") ? " ★FL" : "";
                    ed.WriteMessage($"    [{i + 1}]  {d.Symbol,-12} {d.Name}{fl}\n");
                }

                var vehOpt = new PromptIntegerOptions($"\n  Select vehicle [1-{display.Count}]")
                {
                    LowerLimit = 1,
                    UpperLimit = display.Count,
                    DefaultValue = 1
                };
                vehOpt.UseDefaultValue = true;

                // Default to WB-62FL if it exists
                for (int i = 0; i < display.Count; i++)
                {
                    if (display[i].Symbol == "WB-62FL")
                    {
                        vehOpt.DefaultValue = i + 1;
                        break;
                    }
                }

                var vehRes = ed.GetInteger(vehOpt);
                if (vehRes.Status != PromptStatus.OK) return;

                int vIdx = vehRes.Value - 1;
                var selected = display[vIdx];
                ed.WriteMessage($"\n  Selected: {selected.Symbol} — {selected.Name}\n");

                // ── Step 2: Pick the path polyline ────────────────────────
                ed.WriteMessage("\n  Select a Polyline or LWPolyline as the vehicle path:\n");

                var entOpt = new PromptEntityOptions("\n  Pick path entity: ");
                entOpt.SetRejectMessage("\n  Must be a Polyline.");
                entOpt.AddAllowedClass(typeof(Polyline), true);
                entOpt.AddAllowedClass(typeof(Polyline2d), true);
                entOpt.AddAllowedClass(typeof(Polyline3d), true);

                var entRes = ed.GetEntity(entOpt);
                if (entRes.Status != PromptStatus.OK) return;

                // ── Step 3: Direction and speed options ────────────────────
                var dirOpt = new PromptKeywordOptions("\n  Direction [Forward/Reverse] <Forward>: ");
                dirOpt.Keywords.Add("Forward");
                dirOpt.Keywords.Add("Reverse");
                dirOpt.Keywords.Default = "Forward";
                dirOpt.AllowNone = true;

                bool reverse = false;
                var dirRes = ed.GetKeywords(dirOpt);
                if (dirRes.Status == PromptStatus.OK || dirRes.Status == PromptStatus.None)
                    reverse = dirRes.StringResult == "Reverse";

                var spdOpt = new PromptDoubleOptions("\n  Vehicle speed (mph) <15>: ")
                {
                    DefaultValue = 15.0,
                    AllowNegative = false,
                    AllowZero = false
                };
                spdOpt.UseDefaultValue = true;

                double speedMph = 15.0;
                var spdRes = ed.GetDouble(spdOpt);
                if (spdRes.Status == PromptStatus.OK)
                    speedMph = spdRes.Value;

                double speedFtPerSec = speedMph * 5280.0 / 3600.0;

                // ── Step 4: Extract path points and simulate ──────────────
                ed.WriteMessage("\n  Running simulation...\n");

                List<Vec2> pathPoints;

                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var ent = tx.GetObject(entRes.ObjectId, OpenMode.ForRead);
                    pathPoints = ExtractPathPoints(ent);
                    tx.Commit();
                }

                if (pathPoints.Count < 2)
                {
                    ed.WriteMessage("\n  Path has insufficient points.\n");
                    return;
                }

                var solver = new SweptPathSolver
                {
                    Speed = speedFtPerSec,
                    Reverse = reverse,
                    SnapshotInterval = 100 // body outline every ~8 ft
                };

                SimulationResult result;

                if (selected.IsArticulated)
                {
                    var av = VehicleLibrary.ArticulatedVehicles[selected.Index];
                    result = solver.Solve(av, pathPoints);
                }
                else
                {
                    var vu = VehicleLibrary.SingleUnits[selected.Index];
                    result = solver.Solve(vu, pathPoints);
                }

                // ── Step 5: Draw results ──────────────────────────────────
                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var btr = (BlockTableRecord)tx.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    VtDrawingWriter.DrawResult(db, tx, btr, result);
                    tx.Commit();
                }

                // ── Report ────────────────────────────────────────────────
                ed.WriteMessage($"\n  ═══ RESULTS ═══");
                ed.WriteMessage($"\n  Path Length:      {result.PathLength:F1} ft");
                ed.WriteMessage($"\n  Max Swept Width:  {result.MaxSweptWidth:F1} ft");
                ed.WriteMessage($"\n  Max Offtracking:  {result.MaxOfftracking:F1} ft");
                ed.WriteMessage($"\n  Steering Clamped: {(result.SteeringClamped ? "YES — vehicle may not make the turn" : "No")}");
                ed.WriteMessage($"\n  Collisions:       {result.Collisions.Count}");
                ed.WriteMessage($"\n  Snapshots:        {result.Snapshots.Count}");
                ed.WriteMessage("\n═══════════════════════════════════════════════════════\n");
            }
            catch (System.Exception ex)
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage($"\n[VTSWEEP ERROR] {ex.Message}\n");
            }
        }

        /// <summary>Extract ordered points from a Polyline entity.</summary>
        private static List<Vec2> ExtractPathPoints(DBObject ent)
        {
            var pts = new List<Vec2>();

            if (ent is Polyline pl)
            {
                for (int i = 0; i < pl.NumberOfVertices; i++)
                {
                    Point2d p = pl.GetPoint2dAt(i);
                    pts.Add(new Vec2(p.X, p.Y));
                }
            }
            else if (ent is Polyline2d pl2d)
            {
                foreach (ObjectId vid in pl2d)
                {
                    if (vid.GetObject(OpenMode.ForRead) is Vertex2d v)
                        pts.Add(new Vec2(v.Position.X, v.Position.Y));
                }
            }
            else if (ent is Polyline3d pl3d)
            {
                foreach (ObjectId vid in pl3d)
                {
                    if (vid.GetObject(OpenMode.ForRead) is PolylineVertex3d v)
                        pts.Add(new Vec2(v.Position.X, v.Position.Y));
                }
            }

            return pts;
        }
    }
}
