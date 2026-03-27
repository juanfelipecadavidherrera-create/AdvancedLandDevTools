using System;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;

[assembly: CommandClass(typeof(AdvancedLandDevTools.Commands.AreaManagerCommand))]

namespace AdvancedLandDevTools.Commands
{
    public class AreaManagerCommand
    {
        private static PaletteSet? _paletteSet;

        [CommandMethod("AREAMANAGER", CommandFlags.Modal)]
        public void ToggleAreaManager()
        {
            try
            {
                if (!Engine.LicenseManager.EnsureLicensed()) return;

                if (_paletteSet == null)
                {
                    // Create the PaletteSet (dockable, like Toolspace)
                    _paletteSet = new PaletteSet(
                        "ALDT Area Manager",
                        "ALDT_AREAMANAGER",
                        new Guid("A1D7E3C0-5F2B-4A8E-9D1C-3E7F0B2A4C6D"))
                    {
                        Style = PaletteSetStyles.ShowPropertiesMenu
                              | PaletteSetStyles.ShowAutoHideButton
                              | PaletteSetStyles.ShowCloseButton
                              | PaletteSetStyles.Snappable,
                        MinimumSize = new System.Drawing.Size(280, 300),
                        DockEnabled = DockSides.Left | DockSides.Right,
                        Dock = DockSides.None
                    };

                    // Host the WPF UserControl via ElementHost
                    var palette = new UI.AreaManagerPalette();
                    var host = new System.Windows.Forms.Integration.ElementHost
                    {
                        Dock = System.Windows.Forms.DockStyle.Fill,
                        Child = palette
                    };

                    _paletteSet.Add("Area Manager", host);
                    _paletteSet.KeepFocus = false;
                }

                // Toggle visibility
                _paletteSet.Visible = !_paletteSet.Visible;
            }
            catch (System.Exception ex)
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application
                    .DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage($"\n[ALDT ERROR] AREAMANAGER: {ex.Message}\n");
            }
        }
    }
}
