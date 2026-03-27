using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AdvancedLandDevTools.Engine;
using AdvancedLandDevTools.Models;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AdvancedLandDevTools.Commands
{
    public class FloodZoneCommand
    {
        // ═════════════════════════════════════════════════════════════════════
        //  FLOODZONE — single-point FEMA flood zone lookup (command-line only)
        // ═════════════════════════════════════════════════════════════════════
        [CommandMethod("FLOODZONE", CommandFlags.Modal)]
        public void FloodZone()
        {
            try
            {
            if (!Engine.LicenseManager.EnsureLicensed()) return;
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;
            ed.WriteMessage("\n");
            ed.WriteMessage("═══════════════════════════════════════════════════════════\n");
            ed.WriteMessage("  Advanced Land Development Tools  |  Flood Zone Lookup   \n");
            ed.WriteMessage("═══════════════════════════════════════════════════════════\n");

            PromptPointResult ppr = ed.GetPoint("\n  Select point for flood zone lookup: ");
            if (ppr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\n  Command cancelled.\n");
                return;
            }

            FloodZoneResult result = FloodZoneEngine.LookupSinglePoint(doc, ppr.Value);

            if (result == null || !result.Success)
            {
                ed.WriteMessage($"\n  ** Query failed: {result?.ErrorMessage ?? "Unknown error"}");
                ed.WriteMessage("\n  This may mean the point is outside FEMA mapped areas,");
                ed.WriteMessage("\n  or the FEMA service is temporarily unavailable.\n");
                return;
            }

            ed.WriteMessage("\n");
            ed.WriteMessage("\n  ╔══════════════════════════════════════════════════╗");
            ed.WriteMessage("\n  ║         FEMA FLOOD ZONE LOOKUP RESULTS          ║");
            ed.WriteMessage("\n  ╠══════════════════════════════════════════════════╣");
            ed.WriteMessage($"\n  ║  Flood Zone:      {result.FloodZone,-29} ║");
            ed.WriteMessage($"\n  ║  Zone Subtype:    {result.ZoneSubtype,-29} ║");
            ed.WriteMessage($"\n  ║  SFHA (Special):  {result.IsSFHA,-29} ║");
            ed.WriteMessage($"\n  ║  Base Flood Elev: {result.BaseFloodElevation,-29} ║");
            ed.WriteMessage($"\n  ║  Vertical Datum:  {result.VerticalDatum,-29} ║");
            ed.WriteMessage($"\n  ║  Floodway:        {result.Floodway,-29} ║");
            ed.WriteMessage($"\n  ║  Depth (ft):      {result.Depth,-29} ║");
            ed.WriteMessage($"\n  ║  FIRM Panel:      {result.FirmPanel,-29} ║");
            ed.WriteMessage("\n  ╚══════════════════════════════════════════════════╝");
            ed.WriteMessage("\n═══════════════════════════════════════════════════════════\n");
            }
            catch (System.Exception ex)
            {
                var d = AcApp.DocumentManager.MdiActiveDocument;
                d?.Editor.WriteMessage($"\n[ALDT ERROR] FLOODZONE: {ex.Message}\n");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  FLOODCRITERIA — MDC County Flood Criteria 2022 lookup
        // ═════════════════════════════════════════════════════════════════════
        [CommandMethod("FLOODCRITERIA", CommandFlags.Modal)]
        public void FloodCriteria()
        {
            try
            {
            if (!Engine.LicenseManager.EnsureLicensed()) return;
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;
            ed.WriteMessage("\n");
            ed.WriteMessage("═══════════════════════════════════════════════════════════\n");
            ed.WriteMessage("  Advanced Land Development Tools  |  County Flood Criteria\n");
            ed.WriteMessage("═══════════════════════════════════════════════════════════\n");

            PromptPointResult ppr = ed.GetPoint("\n  Select point for flood criteria lookup: ");
            if (ppr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\n  Command cancelled.\n");
                return;
            }

            FloodCriteriaResult result = FloodCriteriaEngine.LookupPoint(doc, ppr.Value);

            if (result == null || !result.Success)
            {
                ed.WriteMessage($"\n  ** Query failed: {result?.ErrorMessage ?? "Unknown error"}");
                ed.WriteMessage("\n  Make sure the point is within Miami-Dade County.\n");
                return;
            }

            ed.WriteMessage("\n");
            ed.WriteMessage("\n  ╔══════════════════════════════════════════════════╗");
            ed.WriteMessage("\n  ║     MIAMI-DADE COUNTY FLOOD CRITERIA 2022      ║");
            ed.WriteMessage("\n  ╠══════════════════════════════════════════════════╣");
            ed.WriteMessage($"\n  ║  Flood Criteria Elev: {result.Elevation,-25} ║");
            ed.WriteMessage($"\n  ║  Distance to Line:   {result.Distance,-25} ║");
            ed.WriteMessage("\n  ╠══════════════════════════════════════════════════╣");
            ed.WriteMessage("\n  ║  Note: Elevation is the minimum ground surface  ║");
            ed.WriteMessage("\n  ║  elevation for developed properties (ft NGVD).  ║");
            ed.WriteMessage("\n  ╚══════════════════════════════════════════════════╝");
            ed.WriteMessage("\n═══════════════════════════════════════════════════════════\n");
            }
            catch (System.Exception ex)
            {
                var d = AcApp.DocumentManager.MdiActiveDocument;
                d?.Editor.WriteMessage($"\n[ALDT ERROR] FLOODCRITERIA: {ex.Message}\n");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  SECTIONLOOKUP — Township/Range/Section (TTRRSS) lookup
        // ═════════════════════════════════════════════════════════════════════
        [CommandMethod("SECTIONLOOKUP", CommandFlags.Modal)]
        public void SectionLookup()
        {
            try
            {
            if (!Engine.LicenseManager.EnsureLicensed()) return;
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var ed = doc.Editor;
            ed.WriteMessage("\n");
            ed.WriteMessage("═══════════════════════════════════════════════════════════\n");
            ed.WriteMessage("  Advanced Land Development Tools  |  Section Lookup      \n");
            ed.WriteMessage("═══════════════════════════════════════════════════════════\n");

            PromptPointResult ppr = ed.GetPoint("\n  Select point for TTRRSS lookup: ");
            if (ppr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\n  Command cancelled.\n");
                return;
            }

            SectionLookupResult result = SectionLookupEngine.LookupPoint(doc, ppr.Value);

            if (result == null || !result.Success)
            {
                ed.WriteMessage($"\n  ** Query failed: {result?.ErrorMessage ?? "Unknown error"}");
                ed.WriteMessage("\n  Make sure the point is within the surveyed area.\n");
                return;
            }

            string twpDisplay = $"{result.Township}S";
            string rgeDisplay = $"{result.Range}E";

            ed.WriteMessage("\n");
            ed.WriteMessage("\n  ╔══════════════════════════════════════════════════╗");
            ed.WriteMessage("\n  ║     TOWNSHIP / RANGE / SECTION  (PLSS)          ║");
            ed.WriteMessage("\n  ╠══════════════════════════════════════════════════╣");
            ed.WriteMessage($"\n  ║  Section:     {result.Section,-33} ║");
            ed.WriteMessage($"\n  ║  Township:    {twpDisplay,-33} ║");
            ed.WriteMessage($"\n  ║  Range:       {rgeDisplay,-33} ║");
            ed.WriteMessage($"\n  ║  SSTTRR:      {result.SSTTRR,-33} ║");
            ed.WriteMessage("\n  ╚══════════════════════════════════════════════════╝");
            ed.WriteMessage("\n═══════════════════════════════════════════════════════════\n");
            }
            catch (System.Exception ex)
            {
                var d = AcApp.DocumentManager.MdiActiveDocument;
                d?.Editor.WriteMessage($"\n[ALDT ERROR] SECTIONLOOKUP: {ex.Message}\n");
            }
        }
    }
}
