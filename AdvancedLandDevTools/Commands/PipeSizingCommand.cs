using Autodesk.AutoCAD.Runtime;
using AdvancedLandDevTools.UI;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AdvancedLandDevTools.Commands
{
    public class PipeSizingCommand
    {
        [CommandMethod("PIPESIZING", CommandFlags.Modal)]
        public void PipeSizing()
        {
            try
            {
                if (!Engine.LicenseManager.EnsureLicensed()) return;

                var doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                doc.Editor.WriteMessage("\n");
                doc.Editor.WriteMessage("═══════════════════════════════════════════════════════════\n");
                doc.Editor.WriteMessage("  Advanced Land Development Tools  |  Pipe Sizing Calc    \n");
                doc.Editor.WriteMessage("═══════════════════════════════════════════════════════════\n");

                var dlg = new PipeSizingWindow();
                AcApp.ShowModalWindow(dlg);
            }
            catch (System.Exception ex)
            {
                var d = AcApp.DocumentManager.MdiActiveDocument;
                d?.Editor.WriteMessage($"\n[ALDT ERROR] PIPESIZING: {ex.Message}\n");
            }
        }
    }
}
