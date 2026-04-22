using System;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AdvancedLandDevTools.Ribbon;
using AdvancedLandDevTools.Commands;
using AdvancedLandDevTools.VehicleTracking.Commands; // needed for CommandClass attributes

// ── Assembly attributes ───────────────────────────────────────────────────────
// ExtensionApplication: Civil 3D calls Initialize() when the DLL is loaded.
// CommandClass:         Registers all [CommandMethod] methods in this class.
//
// ╔══════════════════════════════════════════════════════════════════════════════╗
// ║  CRITICAL — DO NOT ADD <UseWindowsForms>true</UseWindowsForms> to csproj!  ║
// ║  It changes the framework ref from Microsoft.WindowsDesktop.App.WPF to     ║
// ║  Microsoft.WindowsDesktop.App (full desktop), which breaks Civil 3D's      ║
// ║  .NET 8 AssemblyLoadContext. ALL Civil 3D managed types (Alignment, etc.)   ║
// ║  resolve as base ImpCurve, making every command fail silently.             ║
// ║  ElementHost already works via UseWPF alone (WindowsFormsIntegration.dll   ║
// ║  is part of the WPF framework). See also: api-lessons-usewinforms.md       ║
// ╚══════════════════════════════════════════════════════════════════════════════╝
[assembly: ExtensionApplication(typeof(AdvancedLandDevTools.AppLoader))]
[assembly: CommandClass(typeof(BulkSurfaceProfileCommand))]
[assembly: CommandClass(typeof(FloodZoneCommand))]
[assembly: CommandClass(typeof(ChangeElevationCommand))]
[assembly: CommandClass(typeof(MarkLinesCommand))]
[assembly: CommandClass(typeof(GroundwaterCommand))]
[assembly: CommandClass(typeof(GroundwaterMayCommand))]
[assembly: CommandClass(typeof(VpCutCommand))]
[assembly: CommandClass(typeof(VtSweepCommand))]
[assembly: CommandClass(typeof(VtParkCommand))]
[assembly: CommandClass(typeof(VtPaletteCommand))]
[assembly: CommandClass(typeof(VtDriveCommand))]
[assembly: CommandClass(typeof(VtEditCommand))]
[assembly: CommandClass(typeof(SectionDrawerCommand))]
[assembly: CommandClass(typeof(BlockToSurfaceCommand))]
[assembly: CommandClass(typeof(TextToSurfaceCommand))]
[assembly: CommandClass(typeof(PipeSizingCommand))]
[assembly: CommandClass(typeof(TrenchManagerCommand))]
[assembly: CommandClass(typeof(EeeBendCommand))]
[assembly: CommandClass(typeof(ProfOffCommand))]
[assembly: CommandClass(typeof(AldtHelpCommand))]
[assembly: CommandClass(typeof(PvStyleCommand))]
[assembly: CommandClass(typeof(RrNetworkCheckCommand))]
[assembly: CommandClass(typeof(ChopChopCommand))]
[assembly: CommandClass(typeof(LLabelGenCommand))]
[assembly: CommandClass(typeof(MarkFittingsCommand))]
[assembly: CommandClass(typeof(PropertyAppraisalCommand))]
[assembly: CommandClass(typeof(CoralAsBuiltCommand))]
[assembly: CommandClass(typeof(TableDrawerCommand))]

namespace AdvancedLandDevTools
{
    /// <summary>
    /// Entry point for the Advanced Land Development Tools plugin.
    /// Loaded automatically by Civil 3D via the PackageContents.xml bundle.
    /// </summary>
    public class AppLoader : IExtensionApplication
    {
        // ─────────────────────────────────────────────────────────────────────
        //  Initialize – called once when the DLL loads
        // ─────────────────────────────────────────────────────────────────────
        public void Initialize()
        {
            try
            {
                if (Autodesk.Windows.ComponentManager.Ribbon != null)
                {
                    // Ribbon already available (e.g. NETLOAD command used)
                    RibbonBuilder.BuildRibbon();
                    WriteStartupMessage();
                }
                else
                {
                    // Subscribe and build when the ribbon becomes available
                    Autodesk.Windows.ComponentManager.ItemInitialized +=
                        OnComponentManagerItemInitialized;
                }
            }
            catch (System.Exception ex)
            {
                // Non-fatal: log to command line and continue
                WriteError($"Ribbon init failed: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Terminate – called when AutoCAD unloads the plugin
        // ─────────────────────────────────────────────────────────────────────
        public void Terminate()
        {
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Event handler – fires when Ribbon becomes available on startup
        // ─────────────────────────────────────────────────────────────────────
        private void OnComponentManagerItemInitialized(
            object? sender,
            Autodesk.Windows.RibbonItemEventArgs e)
        {
            try
            {
                if (Autodesk.Windows.ComponentManager.Ribbon == null) return;

                // Unsubscribe – only need to build once
                Autodesk.Windows.ComponentManager.ItemInitialized -=
                    OnComponentManagerItemInitialized;

                RibbonBuilder.BuildRibbon();
                WriteStartupMessage();
            }
            catch (System.Exception ex)
            {
                WriteError($"Ribbon deferred init failed: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────────
        private static void WriteStartupMessage()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            string licStatus = Engine.LicenseManager.GetStatusText();

            doc.Editor.WriteMessage(
                "\n╔══════════════════════════════════════════════════════════╗" +
                "\n║  Advanced Land Development Tools v1.0                   ║" +
                "\n║  Civil 3D 2025/2026 Productivity Suite                  ║" +
               $"\n║  License: {licStatus,-46} ║" +
                "\n╠══════════════════════════════════════════════════════════╣" +
                "\n║  Profile Views:                                         ║" +
                "\n║    BULKSUR         – Bulk Surface Profile Creator       ║" +
                "\n║    GETPARENT       – Get Parent Alignment               ║" +
                "\n║    PIPEMAGIC       – Pipe Magic                         ║" +
                "\n║    MARKLINES       – Mark Crossing Lines in PV          ║" +
                "\n║    MARKFITTINGS    – Mark Profile View Pressure Fittings║" +
                "\n║  Alignments:                                            ║" +
                "\n║    ALIGNDEPLOY     – Align Deploy                       ║" +
                "\n║  Surfaces:                                              ║" +
                "\n║    BLOCKTOSURFACE  – Add Block Elevations to Surface    ║" +
                "\n║    TEXTTOSURFACE   – Add Text/Leader Elevations to Surf ║" +
                "\n║  Pipes:                                                 ║" +
                "\n║    INVERTPULLUP    – Invert Pull Up                     ║" +
                "\n║    CHANGEELEVATION – Pipe Elevation Equalizer           ║" +
                "\n║    LOWRIM          – Lowest Rim Elevation Finder        ║" +
                "\n║    ELEVSLOPE       – Elevation Sloper                   ║" +
                "\n║    PIPESIZING      – Pipe Sizing Calculator             ║" +
                "\n║    EEEBEND         – Pressure Pipe Duck (Bypass)        ║" +
                "\n║    PROFOFF         – Remove Part from Profile View      ║" +
                "\n║    PVSTYLE         – Profile View Style Override        ║" +
                "\n║    RRNETWORKCHECK  – Pressure Network Clearance Check   ║" +
                "\n║    CHOPCHOP        – Profile View Subdivider            ║" +
                "\n║    LLABELGEN       – PV Elevation Label Generator        ║" +
                "\n║  Cross Sections:                                        ║" +
                "\n║    SECDRAW         – Road Section Drawer                ║" +
                "\n║  Property Information:                                  ║" +
                "\n║    FOLIO           – MDC Property Appraiser Lookup      ║" +
                "\n║    FLOODZONE       – FEMA Flood Zone Lookup             ║" +
                "\n║    FLOODCRITERIA   – MDC County Flood Criteria          ║" +
                "\n║    SECTIONLOOKUP   – PLSS Township/Range/Section        ║" +
                "\n║    GWMAY           – Water Table (Avg May)              ║" +
                "\n║    GWOCT           – Water Table (October 2040)         ║" +
                "\n║  Areas & Excavation:                                    ║" +
                "\n║    AREAMANAGER     – Area Manager Palette               ║" +
                "\n║    EXF             – EXF Trench Manager Palette         ║" +
                "\n║  Viewports:                                             ║" +
                "\n║    VPCUT           – Viewport Cut (clip from shapes)    ║" +
                "\n║  Vehicle Tracking:                                      ║" +
                "\n║    VTPANEL         – Vehicle Tracking Palette           ║" +
                "\n║    VTSWEEP         – Swept Path Analysis                ║" +
                "\n║    VTDRIVE         – Interactive Drive Mode             ║" +
                "\n║    VTEDIT          – Edit Existing Drive Path           ║" +
                "\n║    VTPARK          – Parking Layout Generator           ║" +
                "\n║  As-Builts:                                             ║" +
                "\n║    CORALASBUILT    – Coral Gables Sewer As-Built Query  ║" +
                "\n║  Tables:                                                ║" +
                "\n║    TABLEDRAW       – Excel-Like Table Drawer            ║" +
                "\n║  Quick Access:                                          ║" +
                "\n║    ALDTTOOLBAR     – Toggle Mini Toolbar                ║" +
                "\n╠══════════════════════════════════════════════════════════╣" +
                "\n║  © 2026 Juan Felipe Cadavid. All rights reserved.      ║" +
                "\n╚══════════════════════════════════════════════════════════╝\n");
        }

        private static void WriteError(string msg)
        {
            var doc = AcadApp
                              .DocumentManager.MdiActiveDocument;
            doc?.Editor.WriteMessage($"\n[ALDT ERROR]  {msg}\n");
        }
    }
}
