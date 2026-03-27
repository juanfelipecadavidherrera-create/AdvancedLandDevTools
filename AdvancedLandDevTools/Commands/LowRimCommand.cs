using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AdvancedLandDevTools.Engine;

[assembly: CommandClass(typeof(AdvancedLandDevTools.Commands.LowRimCommand))]

namespace AdvancedLandDevTools.Commands
{
    public class LowRimCommand
    {
        [CommandMethod("LOWRIM")]
        public void Execute()
        {
            try
            {
                if (!LicenseManager.EnsureLicensed()) return;

                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                Editor ed = doc.Editor;

                ed.WriteMessage("\n═══════════════════════════════════════════════════════\n");
                ed.WriteMessage("  LOW RIM  –  Lowest Surface Elevation at Insertion Point\n");
                ed.WriteMessage("═══════════════════════════════════════════════════════\n");

                // ── Get pipe networks ──────────────────────────────────
                var networks = LowRimEngine.GetNetworks();
                if (networks.Count == 0)
                {
                    ed.WriteMessage("\n  No gravity pipe networks found in drawing.\n");
                    return;
                }

                // ── List networks for selection ────────────────────────
                ed.WriteMessage("\n  Available Pipe Networks:\n");
                for (int i = 0; i < networks.Count; i++)
                    ed.WriteMessage($"    [{i + 1}]  {networks[i].Name}\n");

                // ── Prompt user to pick one ────────────────────────────
                var intOpt = new PromptIntegerOptions(
                    $"\n  Select network [1-{networks.Count}]")
                {
                    LowerLimit  = 1,
                    UpperLimit  = networks.Count,
                    DefaultValue = 1
                };
                intOpt.UseDefaultValue = true;

                var intRes = ed.GetInteger(intOpt);
                if (intRes.Status != PromptStatus.OK) return;

                int idx = intRes.Value - 1;
                string netName = networks[idx].Name;

                ed.WriteMessage($"\n  Scanning \"{netName}\" ...\n");

                // ── Find lowest ────────────────────────────────────────
                var rim = LowRimEngine.FindLowest(networks[idx].Id);

                if (!rim.Found)
                {
                    ed.WriteMessage("\n  No structures found or no surface elevation data.\n");
                    return;
                }

                ed.WriteMessage("\n╔══════════════════════════════════════════════════════╗\n");
                ed.WriteMessage($"  Network:    {netName}\n");
                ed.WriteMessage($"  Structures: {rim.TotalStructures}\n");
                ed.WriteMessage($"  ──────────────────────────────────────────────────\n");
                ed.WriteMessage($"  LOWEST Surface Elev. at Insertion Point:\n");
                ed.WriteMessage($"    Structure:  {rim.StructureName}\n");
                ed.WriteMessage($"    Elevation:  {rim.Elevation:F3}\n");
                ed.WriteMessage("╚══════════════════════════════════════════════════════╝\n");
            }
            catch (System.Exception ex)
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage($"\n[LOWRIM ERROR] {ex.Message}\n");
            }
        }
    }
}
