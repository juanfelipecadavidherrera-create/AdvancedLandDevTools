using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(AdvancedLandDevTools.Commands.MiniToolbarCommand))]

namespace AdvancedLandDevTools.Commands
{
    public class MiniToolbarCommand
    {
        private static UI.MiniToolbar? _toolbar;

        [CommandMethod("ALDTTOOLBAR", CommandFlags.Modal)]
        public void Toggle()
        {
            try
            {
                if (_toolbar == null)
                {
                    _toolbar = new UI.MiniToolbar();
                    _toolbar.Show();
                }
                else if (_toolbar.IsVisible)
                {
                    _toolbar.Hide();
                }
                else
                {
                    _toolbar.Show();
                }
            }
            catch (System.Exception ex)
            {
                var d = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
                d?.Editor.WriteMessage($"\n[ALDT ERROR] ALDTTOOLBAR: {ex.Message}\n");
            }
        }
    }
}
