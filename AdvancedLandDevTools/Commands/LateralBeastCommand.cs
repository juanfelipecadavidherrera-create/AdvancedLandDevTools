using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CivilDB = Autodesk.Civil.DatabaseServices;
using AdvancedLandDevTools.Engine;
using AdvancedLandDevTools.UI;

[assembly: CommandClass(typeof(AdvancedLandDevTools.Commands.LateralBeastCommand))]

namespace AdvancedLandDevTools.Commands
{
    public class LateralBeastCommand
    {
        private static string _targetLayer = "0";

        private List<string> GetAllLayers(Database db)
        {
            var layers = new List<string>();
            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tx.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in lt)
                {
                    var ltr = (LayerTableRecord)tx.GetObject(id, OpenMode.ForRead);
                    layers.Add(ltr.Name);
                }
                tx.Abort();
            }
            layers.Sort();
            return layers;
        }

        [CommandMethod("LATERALBEAST", CommandFlags.Modal)]
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
                ed.WriteMessage("  LATERAL BEAST  –  Auto-Draw Laterals in Profile Views\n");
                ed.WriteMessage("═══════════════════════════════════════════════════════\n");

                // ── Step 1: Select one or more profile views ─────────────────────────────
                var profileViewIds = new List<ObjectId>();
                var filter = new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "AECC_PROFILE_VIEW")
                });

                var pso = new PromptSelectionOptions
                {
                    MessageForAdding = $"\nSelect profile view(s) [Settings] (Current Layer: {_targetLayer}): ",
                    MessageForRemoval = "\nRemove profile view(s): ",
                    RejectObjectsOnLockedLayers = false
                };
                pso.Keywords.Add("Settings");
                pso.KeywordInput += (s, e) =>
                {
                    var allLayers = GetAllLayers(db);
                    var layerWindow = new LayerSelectionWindow(allLayers, _targetLayer);
                    if (Application.ShowModalWindow(layerWindow) == true)
                    {
                        _targetLayer = layerWindow.SelectedLayer;
                        ed.WriteMessage($"\nCurrent Layer set to: {_targetLayer}\n");
                    }
                    else
                    {
                        ed.WriteMessage("\nLayer selection cancelled.\n");
                    }
                };

                PromptSelectionResult psr;
                while (true)
                {
                    psr = ed.GetSelection(pso, filter);
                    if (psr.Status == PromptStatus.Keyword)
                        continue;
                    break;
                }

                if (psr.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\n  Cancelled.\n");
                    return;
                }

                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    foreach (SelectedObject so in psr.Value)
                    {
                        if (so != null && tx.GetObject(so.ObjectId, OpenMode.ForRead) is CivilDB.ProfileView)
                        {
                            profileViewIds.Add(so.ObjectId);
                        }
                    }
                    tx.Abort();
                }

                if (profileViewIds.Count == 0) return;

                ed.WriteMessage($"\n  ✓  {profileViewIds.Count} profile view(s) selected.\n");

                // ── Step 2: Find Crossings & Select Network ────────────────────────────────
                var allCrossings = new List<CrossingLabelPoint>();
                foreach (var pvId in profileViewIds)
                {
                    allCrossings.AddRange(LLabelGenEngine.FindCrossingPoints(pvId, db));
                }

                if (allCrossings.Count == 0)
                {
                    ed.WriteMessage("\n  ⚠ No crossing pipe networks found in selected profile views.\n");
                    return;
                }

                var networkMap = new Dictionary<ObjectId, string>();
                foreach (var cp in allCrossings)
                {
                    if (!cp.NetworkId.IsNull && !networkMap.ContainsKey(cp.NetworkId))
                    {
                        networkMap[cp.NetworkId] = string.IsNullOrEmpty(cp.NetworkName) ? "(unknown network)" : cp.NetworkName;
                    }
                }

                ObjectId selectedNetworkId = ObjectId.Null;
                if (networkMap.Count == 1)
                {
                    selectedNetworkId = networkMap.Keys.First();
                    ed.WriteMessage($"\n  Only one network found: {networkMap.Values.First()}\n");
                }
                else
                {
                    ed.WriteMessage("\n  Networks found:");
                    var netList = networkMap.ToList();
                    for (int i = 0; i < netList.Count; i++)
                        ed.WriteMessage($"\n    [{i + 1}] {netList[i].Value}");

                    var pio = new PromptIntegerOptions($"\n  Select the MAIN network [1-{netList.Count}]: ");
                    pio.LowerLimit = 1;
                    pio.UpperLimit = netList.Count;
                    
                    var pir = ed.GetInteger(pio);
                    if (pir.Status != PromptStatus.OK) return;

                    selectedNetworkId = netList[pir.Value - 1].Key;
                }

                // ── Step 3: Input Parameters ────────────────────────────────────────────────
                var allDbLayers = GetAllLayers(db);
                var window = new LateralBeastWindow(allDbLayers);
                if (Application.ShowModalWindow(window) != true)
                {
                    ed.WriteMessage("\n  Cancelled by user.\n");
                    return;
                }

                string targetLineLayer = window.TargetLayer;
                bool isLeft = window.IsLeft;
                double angleDeg = window.AngleDeg;
                double verticalOffset = window.PipeGap;

                // ── Step 4: Draw Laterals ───────────────────────────────────────────────────
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    var btr = (BlockTableRecord)tx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                    // Ensure target layer exists
                    var lt = (LayerTable)tx.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (!lt.Has(_targetLayer))
                    {
                        lt.UpgradeOpen();
                        var newLayer = new LayerTableRecord { Name = _targetLayer };
                        lt.Add(newLayer);
                        tx.AddNewlyCreatedDBObject(newLayer, true);
                    }

                    int lateralsDrawn = 0;

                    foreach (var pvId in profileViewIds)
                    {
                        var pv = tx.GetObject(pvId, OpenMode.ForRead) as CivilDB.ProfileView;
                        if (pv == null) continue;

                        var pvCrossings = LLabelGenEngine.FindCrossingPoints(pvId, db)
                            .Where(c => c.NetworkId == selectedNetworkId).ToList();

                        if (pvCrossings.Count == 0) continue;

                        // Find target lines in this profile view's extents
                        var pvExtents = ((Entity)pv).GeometricExtents;
                        var targetLines = new List<Line>();
                        foreach (ObjectId entId in btr)
                        {
                            var ent = tx.GetObject(entId, OpenMode.ForRead) as Entity;
                            if (ent is Line line && line.Layer.Equals(targetLineLayer, StringComparison.OrdinalIgnoreCase))
                            {
                                // Check if inside PV
                                if (line.StartPoint.X >= pvExtents.MinPoint.X && line.StartPoint.X <= pvExtents.MaxPoint.X &&
                                    line.StartPoint.Y >= pvExtents.MinPoint.Y && line.StartPoint.Y <= pvExtents.MaxPoint.Y)
                                {
                                    targetLines.Add(line);
                                }
                            }
                        }

                        if (targetLines.Count == 0)
                        {
                            ed.WriteMessage($"\n  ⚠ No target lines on layer '{targetLineLayer}' found in PV {pv.Name}.");
                            continue;
                        }

                        // For each crossing of the selected network, draw the lateral
                        foreach (var cp in pvCrossings)
                        {
                            // Mathematics based on user example: Angle = 6 degrees.
                            double angleRad = angleDeg * Math.PI / 180.0;
                            if (isLeft) angleRad = (180.0 - angleDeg) * Math.PI / 180.0; 
                            Vector3d dirVector = new Vector3d(Math.Cos(angleRad), Math.Sin(angleRad), 0);

                            // The user requested to STANDARDIZE the reference offset from the invert:
                            // Based on example data:
                            // Invert = (870749.3842, 567624.2606)
                            // Line 1 Start = (870750.1711, 567625.4390) => dx = 0.7869, dy = 1.1784
                            // Line 2 Start = (870750.0676, 567630.4545) => dx = 0.6834, dy = 6.1939

                            double bottomDx = isLeft ? -0.7869 : 0.7869;
                            double bottomDy = 1.1784;
                            double topDx = isLeft ? -0.6834 : 0.6834;
                            
                            // If the user changed the pipe gap in the UI, we adjust the top Dy relative to the bottom.
                            // The default vertical gap in the example is 6.1939 - 1.1784 = 5.0155.
                            // We will use the user's vertical gap from the UI.
                            double topDy = bottomDy + verticalOffset; // verticalOffset is from UI (default 5.0)

                            Point3d bottomStartPoint = new Point3d(cp.DrawingX + bottomDx, cp.DrawingY + bottomDy, 0);
                            Point3d topStartPoint = new Point3d(cp.DrawingX + topDx, cp.DrawingY + topDy, 0);

                            // Create temporary infinite rays to find intersections
                            Ray bottomRay = new Ray { BasePoint = bottomStartPoint, UnitDir = dirVector };
                            Ray topRay = new Ray { BasePoint = topStartPoint, UnitDir = dirVector };

                            Point3d? bestBottomIntersection = null;
                            Point3d? bestTopIntersection = null;
                            double minBottomDist = double.MaxValue;
                            double minTopDist = double.MaxValue;

                            foreach (var targetLine in targetLines)
                            {
                                var ptsBottom = new Point3dCollection();
                                bottomRay.IntersectWith(targetLine, Intersect.OnBothOperands, ptsBottom, IntPtr.Zero, IntPtr.Zero);
                                if (ptsBottom.Count > 0)
                                {
                                    double dist = bottomStartPoint.DistanceTo(ptsBottom[0]);
                                    if (dist < minBottomDist) { minBottomDist = dist; bestBottomIntersection = ptsBottom[0]; }
                                }

                                var ptsTop = new Point3dCollection();
                                topRay.IntersectWith(targetLine, Intersect.OnBothOperands, ptsTop, IntPtr.Zero, IntPtr.Zero);
                                if (ptsTop.Count > 0)
                                {
                                    double dist = topStartPoint.DistanceTo(ptsTop[0]);
                                    if (dist < minTopDist) { minTopDist = dist; bestTopIntersection = ptsTop[0]; }
                                }
                            }

                            if (bestBottomIntersection.HasValue && bestTopIntersection.HasValue)
                            {
                                Line bottomLateral = new Line(bottomStartPoint, bestBottomIntersection.Value);
                                bottomLateral.Layer = _targetLayer;
                                btr.AppendEntity(bottomLateral);
                                tx.AddNewlyCreatedDBObject(bottomLateral, true);

                                Line topLateral = new Line(topStartPoint, bestTopIntersection.Value);
                                topLateral.Layer = _targetLayer;
                                btr.AppendEntity(topLateral);
                                tx.AddNewlyCreatedDBObject(topLateral, true);

                                lateralsDrawn++;
                            }
                            else
                            {
                                ed.WriteMessage($"\n  ⚠ Could not find intersection with target line in PV {pv.Name}.");
                            }
                            
                            bottomRay.Dispose();
                            topRay.Dispose();
                        }
                    }

                    tx.Commit();
                    ed.WriteMessage($"\n  ✓ LATERALBEAST complete. {lateralsDrawn} laterals drawn.\n");
                }
            }
            catch (System.Exception ex)
            {
                var d = Application.DocumentManager.MdiActiveDocument;
                d?.Editor.WriteMessage($"\n[LATERALBEAST ERROR] {ex.Message}\n");
            }
        }
    }
}
