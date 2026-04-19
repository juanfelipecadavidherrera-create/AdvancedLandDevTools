using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AdvancedLandDevTools.Engine;
using AdvancedLandDevTools.Models;

using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AdvancedLandDevTools.Commands
{
    public class PropertyAppraisalCommand
    {
        // ═════════════════════════════════════════════════════════════════════
        //  FOLIO — single-point MDC Property Appraiser lookup
        // ═════════════════════════════════════════════════════════════════════
        [CommandMethod("FOLIO", CommandFlags.Modal)]
        public void PropertyAppraisal()
        {
            try
            {
                if (!Engine.LicenseManager.EnsureLicensed()) return;
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                var ed = doc.Editor;
                ed.WriteMessage("\n");
                ed.WriteMessage("═══════════════════════════════════════════════════════════\n");
                ed.WriteMessage("  Advanced Land Development Tools  |  Property Appraisal   \n");
                ed.WriteMessage("═══════════════════════════════════════════════════════════\n");

                PromptPointResult ppr = ed.GetPoint("\n  Select point to check property information: ");
                if (ppr.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\n  Command cancelled.\n");
                    return;
                }

                PropertyAppraisalResult result = PropertyAppraisalEngine.LookupSinglePoint(doc, ppr.Value);

                if (result == null || !result.Success)
                {
                    ed.WriteMessage($"\n  ** Query failed: {result?.ErrorMessage ?? "Unknown error"}");
                    ed.WriteMessage("\n  This may mean the point is outside Miami-Dade County mapped areas.\n");
                    return;
                }

                ed.WriteMessage("\n");
                ed.WriteMessage("\n  ╔══════════════════════════════════════════════════╗");
                ed.WriteMessage("\n  ║       MIAMI-DADE PROPERTY APPRAISER DATA         ║");
                ed.WriteMessage("\n  ╠══════════════════════════════════════════════════╣");
                ed.WriteMessage($"\n  ║  Folio:           {result.Folio,-30} ║");
                ed.WriteMessage($"\n  ║  Owner Name:      {Truncate(result.OwnerName, 30),-30} ║");
                ed.WriteMessage($"\n  ║  Address:         {Truncate(result.SiteAddress, 30),-30} ║");
                ed.WriteMessage($"\n  ║  City/Zip:        {Truncate(result.SiteCity + " " + result.SiteZipCode, 30),-30} ║");
                ed.WriteMessage("\n  ╚══════════════════════════════════════════════════╝");
                ed.WriteMessage("\n═══════════════════════════════════════════════════════════\n");

                // Draw parcel lines
                PropertyAppraisalEngine.DrawParcelsInRadius(doc, ppr.Value, 1000.0);
            }
            catch (System.Exception ex)
            {
                var d = AcApp.DocumentManager.MdiActiveDocument;
                d?.Editor.WriteMessage($"\n[ALDT ERROR] FOLIO: {ex.Message}\n");
            }
        }

        private string Truncate(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= maxChars ? value : value.Substring(0, maxChars - 3) + "...";
        }
    }
}
