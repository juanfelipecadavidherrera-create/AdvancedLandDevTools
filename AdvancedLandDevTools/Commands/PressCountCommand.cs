using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AdvancedLandDevTools.Engine;
using AdvancedLandDevTools.UI;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AdvancedLandDevTools.Commands.PressCountCommand))]

namespace AdvancedLandDevTools.Commands
{
    public class PressCountCommand
    {
        [CommandMethod("PRESSCOUNT", CommandFlags.Modal)]
        public void Execute()
        {
            try
            {
                if (!LicenseManager.EnsureLicensed()) return;

                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                Editor   ed = doc.Editor;
                Database db = doc.Database;

                ed.WriteMessage("\n═══════════════════════════════════════════════════════\n");
                ed.WriteMessage("  PRESSCOUNT  –  Pressure Network Pipe Length & Fittings\n");
                ed.WriteMessage("═══════════════════════════════════════════════════════\n");

                // ── Step 1: Scan drawing for pressure networks ────────────
                ed.WriteMessage("\n  Scanning for pressure networks…");
                var networks = PressCountEngine.GetPressureNetworks(db);

                if (networks.Count == 0)
                {
                    ed.WriteMessage(
                        "\n  No pressure networks found in this drawing.\n");
                    MessageBox.Show(
                        "No pressure pipe networks were found in the current drawing.\n\n" +
                        "Make sure the drawing contains Civil 3D pressure pipe networks.",
                        "PRESSCOUNT — No Networks Found",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                ed.WriteMessage($" {networks.Count} network(s) found.");

                // ── Step 2: Show network selection dialog ─────────────────
                var dlg = new PressCountDialog(networks);
                bool? result = AcApp.ShowModalWindow(dlg);

                if (result != true || dlg.SelectedNetwork == null)
                {
                    ed.WriteMessage("\n  Cancelled.\n");
                    return;
                }

                var selected = dlg.SelectedNetwork;
                ed.WriteMessage($"\n  Network selected: '{selected.Name}'");

                // ── Step 3: Compute total 3D length + number fittings ─────
                ed.WriteMessage("\n  Computing…");
                var computed = PressCountEngine.ComputeNetwork(selected, db);

                // ── Step 4: Report results ────────────────────────────────
                string lengthStr = $"{computed.TotalLength3D:F3}";
                int    fitCount  = computed.Fittings.Count;

                ed.WriteMessage(
                    $"\n\n  ─── Results: '{selected.Name}' ───────────────────────────\n" +
                    $"  Total 3D pipe length : {lengthStr} (drawing units)\n" +
                    $"  Total pipes          : {selected.PipeCount}\n" +
                    $"  Total fittings       : {fitCount}\n" +
                    "  ──────────────────────────────────────────────────────");

                if (fitCount > 0)
                {
                    ed.WriteMessage("\n  Fitting numbers assigned:");
                    foreach (var fi in computed.Fittings)
                        ed.WriteMessage(
                            $"\n    [{fi.Number:D3}]  X={fi.Location.X:F2}  " +
                            $"Y={fi.Location.Y:F2}  Z={fi.Location.Z:F2}");
                }

                // ── Step 5: Ask about placing MText labels ────────────────
                if (fitCount == 0)
                {
                    ed.WriteMessage(
                        "\n  No fittings found — nothing to label.\n");
                    MessageBox.Show(
                        $"Network '{selected.Name}'\n\n" +
                        $"Total 3D pipe length : {lengthStr} (drawing units)\n" +
                        $"Pipes                : {selected.PipeCount}\n" +
                        $"Fittings             : 0",
                        "PRESSCOUNT – Results",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var ask = MessageBox.Show(
                    $"Network '{selected.Name}'\n\n" +
                    $"Total 3D pipe length : {lengthStr} (drawing units)\n" +
                    $"Pipes                : {selected.PipeCount}\n" +
                    $"Fittings             : {fitCount}\n\n" +
                    "Place numbered MText labels at each fitting location?",
                    "PRESSCOUNT – Place Labels?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (ask != MessageBoxResult.Yes)
                {
                    ed.WriteMessage("\n  Label placement skipped.\n");
                    ed.WriteMessage("═══════════════════════════════════════════════════════\n");
                    return;
                }

                // ── Step 6: Ask for text height ───────────────────────────
                var pdo = new PromptDoubleOptions("\n  Enter text height <1.0>: ")
                {
                    DefaultValue  = 1.0,
                    AllowNone     = true,
                    AllowNegative = false,
                    AllowZero     = false
                };

                var pdr = ed.GetDouble(pdo);
                double textHeight = (pdr.Status == PromptStatus.OK)
                    ? pdr.Value
                    : 1.0;

                // ── Step 7: Place labels ──────────────────────────────────
                int placed = PressCountEngine.PlaceFittingLabels(computed, db, textHeight);

                ed.WriteMessage(
                    $"\n  ✓  {placed} label(s) placed (text height = {textHeight:F3}).\n");
                ed.WriteMessage("═══════════════════════════════════════════════════════\n");
            }
            catch (System.Exception ex)
            {
                var d = AcApp.DocumentManager.MdiActiveDocument;
                d?.Editor.WriteMessage($"\n[ALDT ERROR] PRESSCOUNT: {ex.Message}\n");
            }
        }
    }
}
