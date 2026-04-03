using System;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;

[assembly: CommandClass(typeof(AdvancedLandDevTools.Commands.TrenchManagerCommand))]

namespace AdvancedLandDevTools.Commands
{
    public class TrenchManagerCommand
    {
        private static PaletteSet? _paletteSet;

        [CommandMethod("EXF", CommandFlags.Modal)]
        public void ToggleTrenchManager()
        {
            try
            {
                if (!Engine.LicenseManager.EnsureLicensed()) return;

                if (_paletteSet == null)
                {
                    _paletteSet = new PaletteSet(
                        "EXF Trench Manager",
                        "ALDT_TRENCHMANAGER",
                        new Guid("B3F2A7D1-8E4C-4B9F-A2D5-6C1E0F3A5B8D"))
                    {
                        Style = PaletteSetStyles.ShowPropertiesMenu
                              | PaletteSetStyles.ShowAutoHideButton
                              | PaletteSetStyles.ShowCloseButton
                              | PaletteSetStyles.Snappable,
                        MinimumSize = new System.Drawing.Size(280, 300),
                        DockEnabled = DockSides.Left | DockSides.Right,
                        Dock = DockSides.None
                    };

                    var palette = new UI.TrenchManagerPalette();
                    var host = new System.Windows.Forms.Integration.ElementHost
                    {
                        Dock = System.Windows.Forms.DockStyle.Fill,
                        Child = palette
                    };

                    _paletteSet.Add("Trench Manager", host);
                    _paletteSet.KeepFocus = false;
                }

                _paletteSet.Visible = !_paletteSet.Visible;
            }
            catch (System.Exception ex)
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application
                    .DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage($"\n[ALDT ERROR] EXF: {ex.Message}\n");
            }
        }
    }
}
