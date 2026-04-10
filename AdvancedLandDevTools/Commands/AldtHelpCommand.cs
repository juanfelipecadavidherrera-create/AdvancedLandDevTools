using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AdvancedLandDevTools.UI;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AdvancedLandDevTools.Commands
{
    public class AldtHelpCommand
    {
        [CommandMethod("ALDTHELP", CommandFlags.Modal)]
        public void Execute()
        {
            try
            {
                AcApp.ShowModalWindow(new AldtHelpWindow());
            }
            catch (System.Exception ex)
            {
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage($"\n[ALDT ERROR] ALDTHELP: {ex.Message}\n");
            }
        }
    }
}
