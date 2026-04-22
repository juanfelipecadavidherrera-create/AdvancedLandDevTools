// Advanced Land Development Tools
// Copyright © Juan Felipe Cadavid — All Rights Reserved
// Unauthorized copying or redistribution is prohibited.

using System;
using AdvancedLandDevTools.Engine;
using AdvancedLandDevTools.Models;
using AdvancedLandDevTools.UI;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AdvancedLandDevTools.Commands.TableDrawerCommand))]

namespace AdvancedLandDevTools.Commands
{
    /// <summary>
    /// TABLEDRAW — Opens the Table Drawer designer window.
    ///
    /// Workflow:
    ///   1. User designs the table layout (rows, cols, cell text, entity links, merges).
    ///   2. Click "Draw in Model" — prompts for top-left insertion point.
    ///   3. Table is drawn as AutoCAD Lines (grid) + MText (cell content).
    ///   4. Linked cells register with <see cref="TableReactor"/> for live auto-update
    ///      whenever the linked Civil 3D / AutoCAD entity changes.
    ///
    /// Layer: ALDT-TABLE (green, created if absent).
    /// </summary>
    public class TableDrawerCommand
    {
        [CommandMethod("TABLEDRAW", CommandFlags.Modal)]
        public void TableDraw()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            try
            {
                if (!LicenseManager.EnsureLicensed()) return;

                ed.WriteMessage("\n");
                ed.WriteMessage("========================================================\n");
                ed.WriteMessage("  Advanced Land Development Tools  |  Table Drawer       \n");
                ed.WriteMessage("========================================================\n");

                // Open the designer
                var win = new TableDrawerWindow(doc);
                bool? result = AcApp.ShowModalWindow(win);

                if (result != true || win.ResultTable == null)
                {
                    ed.WriteMessage("\n  Command cancelled.\n");
                    return;
                }

                var td = win.ResultTable;

                // Prompt for insertion point
                var ppo = new PromptPointOptions(
                    "\nClick top-left corner for the table: ")
                { AllowNone = false };

                var ppr = ed.GetPoint(ppo);
                if (ppr.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\n  No point selected — cancelled.\n");
                    return;
                }

                // Suppress reactor during draw — ObjectModified events from
                // newly created Lines/MText must not trigger the handler.
                TableReactor.Instance.Suppress();

                DrawnTable drawn;
                try
                {
                    // Draw into model space
                    drawn = TableDrawerEngine.DrawTable(doc, td, ppr.Value);

                    // Register for auto-update (non-fatal if this fails)
                    try { TableReactor.Instance.Register(doc, drawn, td); }
                    catch (System.Exception rx)
                    {
                        ed.WriteMessage($"\n  ⚠ Auto-update registration failed: {rx.Message}");
                    }
                }
                finally
                {
                    TableReactor.Instance.Resume();
                }

                ed.WriteMessage(
                    $"\n  Table \"{td.Name}\" placed — " +
                    $"{td.Rows} rows × {td.Cols} cols, " +
                    $"{drawn.CellMTextHandles.Count} cell labels.\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[ALDT ERROR] TABLEDRAW: {ex.Message}\n");
            }
        }
    }
}
