using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AdvancedLandDevTools.Engine;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AdvancedLandDevTools.Commands
{
    public class GroundwaterCommand
    {
        [CommandMethod("GROUNDWATER", CommandFlags.Modal)]
        public void Groundwater()
        {
            try
            {
            if (!Engine.LicenseManager.EnsureLicensed()) return;
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;
            ed.WriteMessage("\n");
            ed.WriteMessage("═══════════════════════════════════════════════════════════\n");
            ed.WriteMessage("  Advanced Land Development Tools  |  Groundwater Level   \n");
            ed.WriteMessage("═══════════════════════════════════════════════════════════\n");

            PromptPointResult ppr = ed.GetPoint(
                "\n  Select point for groundwater level lookup: ");
            if (ppr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\n  Command cancelled.\n");
                return;
            }

            GroundwaterEngine.LookupAtPoint(doc, ppr.Value);
            }
            catch (System.Exception ex)
            {
                var d = AcApp.DocumentManager.MdiActiveDocument;
                d?.Editor.WriteMessage($"\n[ALDT ERROR] GROUNDWATER: {ex.Message}\n");
            }
        }
    }
}
