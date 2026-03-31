using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
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
        //  FLOODCRITERIA — MDC County Flood Criteria 2022 lookup + draw
        // ═════════════════════════════════════════════════════════════════════
        private const string FLOOD_CRITERIA_LAYER = "ALDT-FLOOD-CRITERIA";

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

            Point3d pickedPt = ppr.Value;
            FloodCriteriaResult result = FloodCriteriaEngine.LookupPoint(doc, pickedPt);

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

            // Draw contour lines within 500 ft radius
            if (result.Contours.Count > 0)
            {
                int drawn = DrawFloodContours(doc, pickedPt, result.Contours);
                ed.WriteMessage($"\n  Drawing {drawn} contour segment(s) within 500 ft radius...");
            }
            else
            {
                ed.WriteMessage("\n  No contour lines found within 500 ft of picked point.");
            }

            ed.WriteMessage("\n═══════════════════════════════════════════════════════════\n");
            }
            catch (System.Exception ex)
            {
                var d = AcApp.DocumentManager.MdiActiveDocument;
                d?.Editor.WriteMessage($"\n[ALDT ERROR] FLOODCRITERIA: {ex.Message}\n");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Draw flood criteria contours + labels in model space
        // ═════════════════════════════════════════════════════════════════════
        private int DrawFloodContours(
            Autodesk.AutoCAD.ApplicationServices.Document doc,
            Point3d center, List<ContourLineData> contours)
        {
            Database db = doc.Database;
            int count = 0;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                // Get or create layer
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                ObjectId layerId;

                if (!lt.Has(FLOOD_CRITERIA_LAYER))
                {
                    lt.UpgradeOpen();
                    var layer = new LayerTableRecord
                    {
                        Name  = FLOOD_CRITERIA_LAYER,
                        Color = Color.FromColorIndex(ColorMethod.ByAci, 4) // cyan
                    };
                    layerId = lt.Add(layer);
                    tr.AddNewlyCreatedDBObject(layer, true);
                }
                else
                {
                    layerId = lt[FLOOD_CRITERIA_LAYER];
                }

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // Draw 500 ft radius reference circle
                var circle = new Circle(center, Vector3d.ZAxis, 500.0);
                circle.LayerId = layerId;
                circle.ColorIndex = 8;  // dark gray — subtle reference
                ms.AppendEntity(circle);
                tr.AddNewlyCreatedDBObject(circle, true);

                // Track elevations already labeled to avoid duplicate labels
                var labeledElevations = new HashSet<double>();

                foreach (var contour in contours)
                {
                    if (contour.Points.Count < 2) continue;

                    // Draw polyline
                    var pline = new Polyline();
                    for (int i = 0; i < contour.Points.Count; i++)
                        pline.AddVertexAt(i, contour.Points[i], 0, 0, 0);

                    pline.LayerId = layerId;
                    ms.AppendEntity(pline);
                    tr.AddNewlyCreatedDBObject(pline, true);
                    count++;

                    // Add elevation label at midpoint (once per unique elevation)
                    if (!labeledElevations.Contains(contour.Elevation))
                    {
                        labeledElevations.Add(contour.Elevation);

                        int mid = contour.Points.Count / 2;
                        var midPt = contour.Points[mid];

                        // Compute text rotation to follow the line direction
                        int i0 = Math.Max(0, mid - 1);
                        int i1 = Math.Min(contour.Points.Count - 1, mid + 1);
                        double angle = Math.Atan2(
                            contour.Points[i1].Y - contour.Points[i0].Y,
                            contour.Points[i1].X - contour.Points[i0].X);

                        // Keep text readable (not upside-down)
                        if (angle > Math.PI / 2)  angle -= Math.PI;
                        if (angle < -Math.PI / 2) angle += Math.PI;

                        // Offset text slightly above the line
                        double offsetX = -Math.Sin(angle) * 4.0;
                        double offsetY =  Math.Cos(angle) * 4.0;

                        var mtext = new MText();
                        mtext.Location   = new Point3d(midPt.X + offsetX, midPt.Y + offsetY, 0);
                        mtext.Contents   = $"FC ELEV: {contour.Elevation:F1}' NGVD";
                        mtext.TextHeight = 4.0;
                        mtext.Rotation   = angle;
                        mtext.Attachment = AttachmentPoint.BottomCenter;
                        mtext.LayerId    = layerId;
                        ms.AppendEntity(mtext);
                        tr.AddNewlyCreatedDBObject(mtext, true);
                    }
                }

                tr.Commit();
            }

            return count;
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
