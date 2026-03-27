using System.Collections.Generic;
using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using CivilDB = Autodesk.Civil.DatabaseServices;
using AdvancedLandDevTools.Engine;

[assembly: CommandClass(typeof(AdvancedLandDevTools.Commands.PipeMagicCommand))]

namespace AdvancedLandDevTools.Commands
{
    public class PipeMagicCommand
    {
        [CommandMethod("PIPEMAGIC")]
        public void Execute()
        {
            try
            {
            if (!Engine.LicenseManager.EnsureLicensed()) return;
            Document doc = Autodesk.AutoCAD.ApplicationServices.Application
                               .DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            Editor   ed = doc.Editor;
            Database db = doc.Database;

            ed.WriteMessage("\n═══════════════════════════════════════════════════════\n");
            ed.WriteMessage("  PIPE MAGIC  –  Auto-Project Crossing Pipes to Profile Views\n");
            ed.WriteMessage("═══════════════════════════════════════════════════════\n");

            // ── Select one or more profile views ─────────────────────────────
            // Build a selection filter that only accepts ProfileView entities
            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "AECC_PROFILE_VIEW")
            });

            var pso = new PromptSelectionOptions
            {
                MessageForAdding  = "\nSelect profile view(s) (window, crossing, or click): ",
                MessageForRemoval = "\nRemove profile view(s): ",
                RejectObjectsOnLockedLayers = false
            };

            PromptSelectionResult psr = ed.GetSelection(pso, filter);

            if (psr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\n  Cancelled.\n");
                return;
            }

            // ── Validate each selected object is a ProfileView ────────────────
            var profileViewIds = new List<ObjectId>();
            using (Transaction tv = db.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject so in psr.Value)
                {
                    if (so == null) continue;
                    var obj = tv.GetObject(so.ObjectId, OpenMode.ForRead);

                    if (obj is CivilDB.ProfileView)
                    {
                        profileViewIds.Add(so.ObjectId);
                    }
                    else
                    {
                        ed.WriteMessage(
                            $"\n  ⚠  Object {so.ObjectId.Handle} is not a " +
                            $"valid Profile View – skipped.");
                    }
                }
                tv.Abort();
            }

            if (profileViewIds.Count == 0)
            {
                ed.WriteMessage(
                    "\n  ✗  No valid Profile Views selected. " +
                    "Please select Civil 3D Profile View objects.\n");
                return;
            }

            ed.WriteMessage(
                $"\n  ✓  {profileViewIds.Count} profile view(s) selected.\n");
            ed.WriteMessage(
                "\n  Scanning for crossing pipe networks…\n");

            // ── Run engine ────────────────────────────────────────────────────
            PipeMagicResult result = PipeMagicEngine.Run(profileViewIds);

            // ── Write full log to command line ────────────────────────────────
            ed.WriteMessage(
                "\n  ─── PIPE MAGIC  –  RESULTS ────────────────────────\n");
            foreach (string line in result.Log)
                ed.WriteMessage($"  {line}\n");
            ed.WriteMessage(
                "  ────────────────────────────────────────────────────\n");
            ed.WriteMessage(
                $"  Profile views processed : {result.ProfileViewsProcessed}\n");
            ed.WriteMessage(
                $"  Gravity networks added  : {result.GravityNetworksAdded}\n");
            ed.WriteMessage(
                $"  Pressure pipes added : {result.PressurePipesAdded}\n");
            ed.WriteMessage(
                "═══════════════════════════════════════════════════════\n");

            // ── Popup summary ─────────────────────────────────────────────────
            int total = result.GravityNetworksAdded + result.PressurePipesAdded;
            string summary =
                $"Profile Views processed : {result.ProfileViewsProcessed}\n" +
                $"Gravity networks added  : {result.GravityNetworksAdded}\n" +
                $"Pressure pipes added : {result.PressurePipesAdded}\n\n" +
                "── Log ──\n" +
                string.Join("\n", result.Log);

            MessageBox.Show(
                summary,
                total > 0
                    ? "Pipe Magic – Complete"
                    : "Pipe Magic – No networks projected",
                MessageBoxButton.OK,
                total > 0
                    ? MessageBoxImage.Information
                    : MessageBoxImage.Warning);
            }
            catch (System.Exception ex)
            {
                var d = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                d?.Editor.WriteMessage($"\n[ALDT ERROR] PIPEMAGIC: {ex.Message}\n");
            }
        }
    }
}
