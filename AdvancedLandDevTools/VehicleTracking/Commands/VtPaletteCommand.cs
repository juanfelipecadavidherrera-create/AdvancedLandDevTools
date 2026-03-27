using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;
using System.Windows.Forms.Integration;
using AdvancedLandDevTools.VehicleTracking.UI;

[assembly: CommandClass(typeof(AdvancedLandDevTools.VehicleTracking.Commands.VtPaletteCommand))]

namespace AdvancedLandDevTools.VehicleTracking.Commands
{
    /// <summary>
    /// VTPANEL — Toggle the Vehicle Tracking dockable palette.
    /// </summary>
    public class VtPaletteCommand
    {
        private static PaletteSet? _paletteSet;

        [CommandMethod("VTPANEL", CommandFlags.Modal)]
        public void Execute()
        {
            try
            {
                if (!Engine.LicenseManager.EnsureLicensed()) return;

                if (_paletteSet == null)
                {
                    _paletteSet = new PaletteSet(
                        "Vehicle Tracking",
                        "VT_PALETTE",
                        new System.Guid("A7E3B1C9-4D2F-4A8E-B5C6-9D1E0F3A2B7C"));

                    _paletteSet.Style =
                        PaletteSetStyles.ShowAutoHideButton |
                        PaletteSetStyles.ShowCloseButton |
                        PaletteSetStyles.Snappable;
                    _paletteSet.MinimumSize = new System.Drawing.Size(380, 500);
                    _paletteSet.DockEnabled = DockSides.Left | DockSides.Right;
                    _paletteSet.Dock = DockSides.Right;

                    // Host the WPF control
                    var host = new ElementHost
                    {
                        AutoSize = true,
                        Dock = System.Windows.Forms.DockStyle.Fill,
                        Child = new VtMainPanel()
                    };

                    _paletteSet.Add("Vehicle Tracking", host);
                }

                _paletteSet.Visible = !_paletteSet.Visible;
            }
            catch (System.Exception ex)
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage($"\n[VTPANEL ERROR] {ex.Message}\n");
            }
        }
    }
}
