using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AdvancedLandDevTools.Engine;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AdvancedLandDevTools.Commands
{
    public class GroundwaterMayCommand
    {
        [CommandMethod("GWMAY", CommandFlags.Modal)]
        public void GroundwaterMay()
        {
            try
            {
            if (!Engine.LicenseManager.EnsureLicensed()) return;
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;
            ed.WriteMessage("\n");
            ed.WriteMessage("═══════════════════════════════════════════════════════════\n");
            ed.WriteMessage("  Advanced Land Development Tools  |  Water Table May Avg \n");
            ed.WriteMessage("═══════════════════════════════════════════════════════════\n");

            PromptPointResult ppr = ed.GetPoint(
                "\n  Select point for groundwater level lookup (Average May): ");
            if (ppr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\n  Command cancelled.\n");
                return;
            }

            GroundwaterMayEngine.LookupAtPoint(doc, ppr.Value);
            }
            catch (System.Exception ex)
            {
                var d = AcApp.DocumentManager.MdiActiveDocument;
                d?.Editor.WriteMessage($"\n[ALDT ERROR] GWMAY: {ex.Message}\n");
            }
        }
    }
}
