using System;
using System.Windows;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AdvancedLandDevTools.Engine;
using AdvancedLandDevTools.UI;

// Alias resolves 'Application' clash between
//   Autodesk.AutoCAD.ApplicationServices.Application
//   System.Windows.Application
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AdvancedLandDevTools.Commands
{
    public class BulkSurfaceProfileCommand
    {
        [CommandMethod("BULKSUR", CommandFlags.Modal)]
        public void BulkSurfaceProfile()
        {
            try
            {
            if (!Engine.LicenseManager.EnsureLicensed()) return;
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;
            ed.WriteMessage("\n");
            ed.WriteMessage("═══════════════════════════════════════════════════════════\n");
            ed.WriteMessage("  Advanced Land Development Tools  |  Bulk Surface Profile \n");
            ed.WriteMessage("═══════════════════════════════════════════════════════════\n");

            // ── 1. Show the settings dialog ───────────────────────────────────
            var dialog = new BulkSurfaceProfileDialog();
            bool? dlgResult = AcadApp.ShowModalWindow(dialog);

            if (dlgResult != true || dialog.Result == null)
            {
                ed.WriteMessage("\n  Command cancelled.\n");
                return;
            }

            var settings = dialog.Result;
            ed.WriteMessage($"\n  {settings.SelectedAlignments.Count} alignment(s) selected.");
            ed.WriteMessage("\n");

            // ── 2. Prompt for insertion point ─────────────────────────────────
            var ppo = new PromptPointOptions(
                "\nClick the insertion point for the FIRST profile view: ")
            {
                AllowNone = false
            };

            PromptPointResult ppr = ed.GetPoint(ppo);
            if (ppr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\n  No point selected – command cancelled.\n");
                return;
            }

            settings.BaseInsertionPoint = ppr.Value;

            ed.WriteMessage(
                $"\n  Insertion point: ({ppr.Value.X:F2}, {ppr.Value.Y:F2})\n");
            ed.WriteMessage("\n  Creating profiles and views…\n");

            // ── 3. Run the engine ─────────────────────────────────────────────
            BulkProfileResult result = BulkSurfaceProfileEngine.Run(settings);

            // ── 4. Write summary to command line ──────────────────────────────
            ed.WriteMessage("\n");
            ed.WriteMessage("  ─── BULK SURFACE PROFILE  –  RESULTS ───────────────────\n");
            foreach (string line in result.Log)
                ed.WriteMessage($"  {line}\n");

            ed.WriteMessage("  ─────────────────────────────────────────────────────────\n");
            ed.WriteMessage(
                $"  Completed:  {result.SuccessCount} succeeded" +
                (result.FailureCount > 0 ? $",  {result.FailureCount} FAILED" : "") +
                "\n");
            ed.WriteMessage("═══════════════════════════════════════════════════════════\n");

            // ── 5. Pop-up always showing full result log ──────────────────────
            string icon  = result.FailureCount > 0 ? "Partial Failure" : "Success";
            string title = $"Bulk Surface Profile – {icon}";
            string fullLog = string.Join("\n", result.Log);
            string summary = $"{result.SuccessCount} profile view(s) created.\n" +
                             (result.FailureCount > 0
                                ? $"{result.FailureCount} alignment(s) failed.\n" : "") +
                             $"\n── Full Log ──\n{fullLog}";

            MessageBox.Show(summary, title,
                MessageBoxButton.OK,
                result.FailureCount > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                var d = AcadApp.DocumentManager.MdiActiveDocument;
                d?.Editor.WriteMessage($"\n[ALDT ERROR] BULKSUR: {ex.Message}\n");
            }
        }
    }
}
