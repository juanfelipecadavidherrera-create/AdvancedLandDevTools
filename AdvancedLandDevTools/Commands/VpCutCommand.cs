using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AdvancedLandDevTools.Engine;

namespace AdvancedLandDevTools.Commands
{
    public class VpCutCommand
    {
        [CommandMethod("VPCUT", CommandFlags.Modal)]
        public void VpCut()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            try
            {
                if (!LicenseManager.EnsureLicensed()) return;

                // ── Must be in a layout (paper space), not model space ────
                if (doc.Database.TileMode)
                {
                    ed.WriteMessage("\n[VPCUT] Switch to a layout tab first (paper space).\n");
                    return;
                }

                // ── Step 1: Select the source viewport ───────────────────
                ed.WriteMessage("\n[VPCUT] Select the source viewport to cut:\n");

                var vpFilter = new SelectionFilter(new[] {
                    new TypedValue((int)DxfCode.Start, "VIEWPORT")
                });
                var vpResult = ed.GetEntity(
                    new PromptEntityOptions("\nSelect viewport: ")
                    {
                        AllowNone = false
                    });

                if (vpResult.Status != PromptStatus.OK) return;

                // Validate it's a viewport (not the paper space border viewport)
                ObjectId vpId = vpResult.ObjectId;
                using (Transaction txCheck = doc.Database.TransactionManager.StartTransaction())
                {
                    var vp = txCheck.GetObject(vpId, OpenMode.ForRead) as Viewport;
                    if (vp == null)
                    {
                        ed.WriteMessage("\n[VPCUT] Selected object is not a viewport.\n");
                        txCheck.Abort(); return;
                    }
                    if (vp.Number <= 1)
                    {
                        ed.WriteMessage("\n[VPCUT] Cannot cut the paper space border viewport. " +
                                       "Select a user-created viewport.\n");
                        txCheck.Abort(); return;
                    }
                    txCheck.Abort();
                }

                // ── Step 2: Offer paper-space polyline first ────────────
                // If the user has already drawn a closed polyline in paper space
                // representing the desired viewport shape, they can pick it here
                // and no model-space coordinate transform is needed.
                // Pressing Enter skips to the existing model-space selection flow.
                ed.WriteMessage("\n[VPCUT] Option A — select a closed polyline drawn in paper space,");
                ed.WriteMessage("\n         OR press Enter to select shapes inside the viewport.\n");

                var psOpt = new PromptEntityOptions(
                    "\nSelect paper-space closed polyline [Enter = pick inside viewport]: ");
                psOpt.AllowNone = true;
                psOpt.AllowObjectOnLockedLayer = true;
                var psResult = ed.GetEntity(psOpt);

                var shapeIds     = new List<ObjectId>();
                bool paperSpaceMode = false;

                if (psResult.Status == PromptStatus.OK)
                {
                    // Validate: must be a closed Polyline
                    using (var txV = doc.Database.TransactionManager.StartTransaction())
                    {
                        var pl = txV.GetObject(psResult.ObjectId, OpenMode.ForRead) as Polyline;
                        if (pl != null && pl.Closed)
                        {
                            shapeIds.Add(psResult.ObjectId);
                            paperSpaceMode = true;
                            ed.WriteMessage($"\n[VPCUT] Using paper-space polyline ({pl.NumberOfVertices} vertices).\n");
                        }
                        else
                        {
                            ed.WriteMessage("\n[VPCUT] Selected entity is not a closed polyline — " +
                                            "falling back to model-space selection.\n");
                        }
                        txV.Abort();
                    }
                }

                if (!paperSpaceMode)
                {
                    // ── Step 3: Enter viewport and select model-space shapes ──
                    ed.WriteMessage("\n[VPCUT] Entering viewport — select closed shapes in model space.\n");
                    ed.WriteMessage("[VPCUT] Select closed polylines, circles, or other closed curves.\n");

                    ed.SwitchToModelSpace();
                    try { Application.SetSystemVariable("CVPORT", GetViewportNumber(doc.Database, vpId)); }
                    catch { }

                    var shapeResult = ed.GetSelection(
                        new PromptSelectionOptions
                        {
                            MessageForAdding = "\nSelect closed shapes (polylines, circles, etc.): ",
                            AllowDuplicates = false
                        });

                    ed.SwitchToPaperSpace();

                    if (shapeResult.Status != PromptStatus.OK || shapeResult.Value.Count == 0)
                    {
                        ed.WriteMessage("\n[VPCUT] No shapes selected — cancelled.\n");
                        return;
                    }

                    foreach (SelectedObject so in shapeResult.Value)
                        if (so != null) shapeIds.Add(so.ObjectId);
                }

                // ── Step 4: Run the engine ────────────────────────────────
                ed.WriteMessage($"\n[VPCUT] Processing {shapeIds.Count} shape(s)...\n");

                var result = VpCutEngine.Run(vpId, shapeIds, paperSpaceMode);

                // ── Report ───────────────────────────────────────────────
                foreach (var line in result.Log)
                    ed.WriteMessage($"\n{line}");

                ed.WriteMessage($"\n\n[VPCUT] Done — created {result.ViewportsCreated} " +
                               $"clipped viewport(s).\n");

                if (result.ViewportsCreated > 0)
                {
                    System.Windows.MessageBox.Show(
                        $"Created {result.ViewportsCreated} clipped viewport(s) " +
                        "from the selected shapes.\n\n" +
                        "The original viewport has been deleted.",
                        "VPCUT — Complete",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[VPCUT] Error: {ex.Message}\n");
            }
        }

        private static int GetViewportNumber(Database db, ObjectId vpId)
        {
            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                var vp = tx.GetObject(vpId, OpenMode.ForRead) as Viewport;
                int num = vp?.Number ?? 2;
                tx.Abort();
                return num;
            }
        }
    }
}
