using System.Collections.Generic;
using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using CivilApp = Autodesk.Civil.ApplicationServices;
using CivilDB  = Autodesk.Civil.DatabaseServices;
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

            // ── Mode selection (mutually exclusive) ───────────────────────────
            // GetString is used instead of GetKeywords to avoid AutoCAD's
            // abbreviation conflict: both "On" and "Off" start with "O", so
            // GetKeywords matches "O" to the first registered keyword ("On").
            // ── 1. Pressure Fitting Detector ─────────────────────────────────
            var fdPrompt = new PromptStringOptions(
                "\n  Pressure Fitting Detector [On/Off] <Off>: ")
            { AllowSpaces = false };

            var fdResult = ed.GetString(fdPrompt);
            if (fdResult.Status == PromptStatus.Cancel) return;

            bool fittingDetector = fdResult.Status == PromptStatus.OK &&
                string.Equals(fdResult.StringResult.Trim(), "On", System.StringComparison.OrdinalIgnoreCase);

            if (fittingDetector)
                ed.WriteMessage(
                    "\n  Pressure Fitting Detector: ON — fittings at pipe ends will also be projected.\n");

            // ── 2. Manhole Mode (only asked when Fitting Detector is OFF) ─────
            bool manholeMode = false;
            var manholeNetworkIds = new List<ObjectId>();

            if (!fittingDetector)
            {
                var mhPrompt = new PromptStringOptions(
                    "\n  Manhole Mode [On/Off] <Off>: ")
                { AllowSpaces = false };

                var mhResult = ed.GetString(mhPrompt);
                if (mhResult.Status == PromptStatus.Cancel) return;

                manholeMode = mhResult.Status == PromptStatus.OK &&
                    string.Equals(mhResult.StringResult.Trim(), "On", System.StringComparison.OrdinalIgnoreCase);

                if (manholeMode)
                {
                    // List all gravity networks for the user to pick from
                    var netList = new List<(ObjectId Id, string Name)>();
                    using (var txn = db.TransactionManager.StartTransaction())
                    {
                        try
                        {
                            var civDoc = CivilApp.CivilDocument.GetCivilDocument(db);
                            foreach (ObjectId nid in civDoc.GetPipeNetworkIds())
                            {
                                var net = txn.GetObject(nid, OpenMode.ForRead) as CivilDB.Network;
                                if (net != null) netList.Add((nid, net.Name));
                            }
                        }
                        catch { }
                        txn.Abort();
                    }

                    if (netList.Count == 0)
                    {
                        ed.WriteMessage("\n  No gravity networks found — Manhole Mode disabled.\n");
                        manholeMode = false;
                    }
                    else
                    {
                        ed.WriteMessage("\n  Available gravity networks:\n");
                        for (int i = 0; i < netList.Count; i++)
                            ed.WriteMessage($"    [{i + 1}] {netList[i].Name}\n");

                        var pso2 = new PromptStringOptions(
                            "\n  Enter network numbers (comma-separated) or ALL: ")
                        { AllowSpaces = false };
                        var psr2 = ed.GetString(pso2);

                        if (psr2.Status == PromptStatus.OK)
                        {
                            string input = psr2.StringResult.Trim().ToUpperInvariant();
                            if (input == "ALL")
                            {
                                foreach (var (id, _) in netList) manholeNetworkIds.Add(id);
                            }
                            else
                            {
                                foreach (var part in input.Split(','))
                                {
                                    if (int.TryParse(part.Trim(), out int idx) &&
                                        idx >= 1 && idx <= netList.Count)
                                        manholeNetworkIds.Add(netList[idx - 1].Id);
                                }
                            }
                        }

                        if (manholeNetworkIds.Count == 0)
                        {
                            ed.WriteMessage("\n  No networks selected — Manhole Mode disabled.\n");
                            manholeMode = false;
                        }
                        else
                        {
                            ed.WriteMessage(
                                $"\n  Manhole Mode: ON — structures from {manholeNetworkIds.Count} " +
                                "network(s) will be projected.\n");
                        }
                    }
                }
            }

            ed.WriteMessage(
                "\n  Scanning for crossing pipe networks…\n");

            // ── Run engine ────────────────────────────────────────────────────
            PipeMagicResult result = PipeMagicEngine.Run(profileViewIds, fittingDetector, manholeNetworkIds);

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
            if (fittingDetector)
                ed.WriteMessage(
                    $"  Pressure fittings added : {result.FittingsAdded}\n");
            if (manholeMode)
                ed.WriteMessage(
                    $"  Manhole structures added : {result.ManholeStructuresAdded}\n");
            ed.WriteMessage(
                "═══════════════════════════════════════════════════════\n");

            // ── Popup summary ─────────────────────────────────────────────────
            int total = result.GravityNetworksAdded + result.PressurePipesAdded +
                        result.FittingsAdded + result.ManholeStructuresAdded;
            string fittingsLine  = fittingDetector
                ? $"Pressure fittings added  : {result.FittingsAdded}\n" : "";
            string manholeLine   = manholeMode
                ? $"Manhole structures added : {result.ManholeStructuresAdded}\n" : "";
            string summary =
                $"Profile Views processed : {result.ProfileViewsProcessed}\n" +
                $"Gravity networks added  : {result.GravityNetworksAdded}\n" +
                $"Pressure pipes added    : {result.PressurePipesAdded}\n" +
                fittingsLine +
                manholeLine +
                "\n" +
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
