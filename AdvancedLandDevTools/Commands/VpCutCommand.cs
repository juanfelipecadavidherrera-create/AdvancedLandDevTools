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

                // ── Step 2: Switch into the viewport (model space) ───────
                // Use MSPACE to enter the viewport, then let user select shapes
                ed.WriteMessage("\n[VPCUT] Entering viewport — select closed shapes in model space.\n");
                ed.WriteMessage("[VPCUT] Select closed polylines, circles, or other closed curves.\n");

                // Switch to model space through the viewport
                ed.SwitchToModelSpace();

                // Activate the specific viewport
                try
                {
                    Application.SetSystemVariable("CVPORT", GetViewportNumber(doc.Database, vpId));
                }
                catch { /* viewport may already be active */ }

                // ── Step 3: Select closed shapes in model space ──────────
                var shapeResult = ed.GetSelection(
                    new PromptSelectionOptions
                    {
                        MessageForAdding = "\nSelect closed shapes (polylines, circles, etc.): ",
                        AllowDuplicates = false
                    });

                if (shapeResult.Status != PromptStatus.OK || shapeResult.Value.Count == 0)
                {
                    ed.WriteMessage("\n[VPCUT] No shapes selected — cancelled.\n");
                    ed.SwitchToPaperSpace();
                    return;
                }

                var shapeIds = new List<ObjectId>();
                foreach (SelectedObject so in shapeResult.Value)
                {
                    if (so != null)
                        shapeIds.Add(so.ObjectId);
                }

                // ── Step 4: Switch back to paper space ───────────────────
                ed.SwitchToPaperSpace();

                // ── Step 5: Run the engine ───────────────────────────────
                ed.WriteMessage($"\n[VPCUT] Processing {shapeIds.Count} shape(s)...\n");

                var result = VpCutEngine.Run(vpId, shapeIds);

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
