using System;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;

[assembly: CommandClass(typeof(AdvancedLandDevTools.Commands.LateralManagerCommand))]

namespace AdvancedLandDevTools.Commands
{
    public class LateralManagerCommand
    {
        private static PaletteSet? _paletteSet;

        [CommandMethod("LATMANAGER", CommandFlags.Modal)]
        public void ToggleLateralManager()
        {
            try
            {
                if (!Engine.LicenseManager.EnsureLicensed()) return;

                if (_paletteSet == null)
                {
                    _paletteSet = new PaletteSet(
                        "ALDT Lateral Manager",
                        "ALDT_LATMANAGER",
                        new Guid("F9A3B5D1-C7E2-44A9-812C-3E90F4DBA7A6"))
                    {
                        Style = PaletteSetStyles.ShowPropertiesMenu
                              | PaletteSetStyles.ShowAutoHideButton
                              | PaletteSetStyles.ShowCloseButton
                              | PaletteSetStyles.Snappable,
                        MinimumSize = new System.Drawing.Size(280, 300),
                        DockEnabled = DockSides.Left | DockSides.Right,
                        Dock = DockSides.None
                    };

                    var palette = new UI.LateralManagerPalette();
                    var host = new System.Windows.Forms.Integration.ElementHost
                    {
                        Dock = System.Windows.Forms.DockStyle.Fill,
                        Child = palette
                    };

                    _paletteSet.Add("Lateral Manager", host);
                    _paletteSet.KeepFocus = false;
                }

                _paletteSet.Visible = !_paletteSet.Visible;
            }
            catch (System.Exception ex)
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application
                    .DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage($"\n[ALDT ERROR] LATMANAGER: {ex.Message}\n");
            }
        }
    }
}
