using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AdvancedLandDevTools.Engine;
using AdvancedLandDevTools.UI;
using CivilDB  = Autodesk.Civil.DatabaseServices;
using CivilApp = Autodesk.Civil.ApplicationServices;
using AcApp    = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AdvancedLandDevTools.Commands.LLabelGenCommand))]

namespace AdvancedLandDevTools.Commands
{
    public class LLabelGenCommand
    {
        [CommandMethod("LLABELGEN", CommandFlags.Modal)]
        public void Execute()
        {
            try
            {
                if (!Engine.LicenseManager.EnsureLicensed()) return;

                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                Editor   ed = doc.Editor;
                Database db = doc.Database;

                ed.WriteMessage("\n");
                ed.WriteMessage("═══════════════════════════════════════════════════════════\n");
                ed.WriteMessage("  Advanced Land Development Tools  |  Label Generator       \n");
                ed.WriteMessage("  Places station-elevation labels at crossing pipe inverts.  \n");
                ed.WriteMessage("═══════════════════════════════════════════════════════════\n");

                // ── Step 1: Select a profile view ─────────────────────────────
                var peo = new PromptEntityOptions(
                    "\n  Select a profile view: ");
                peo.AllowNone = false;
                peo.AllowObjectOnLockedLayer = true;

                var per = ed.GetEntity(peo);
                if (per.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\n  Cancelled.\n");
                    return;
                }

                ObjectId pvId = ObjectId.Null;
                string pvName = "";

                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    // Try direct cast, then point-based search
                    var ent = tx.GetObject(per.ObjectId, OpenMode.ForRead);
                    var pv  = ent as CivilDB.ProfileView
                              ?? FindProfileViewAtPoint(per.PickedPoint, tx, db);

                    if (pv == null)
                    {
                        ed.WriteMessage("\n  ❌ Selected object is not a profile view.\n");
                        tx.Abort();
                        return;
                    }

                    pvId   = pv.ObjectId;
                    pvName = pv.Name;
                    tx.Abort();
                }

                ed.WriteMessage($"\n  Profile View: '{pvName}'");

                // ── Step 2: Find crossing pipe proxies ────────────────────────
                var crossings = LLabelGenEngine.FindCrossingPoints(pvId, db);
                ed.WriteMessage($"\n  Crossing pipe proxies found: {crossings.Count}");

                if (crossings.Count == 0)
                {
                    ed.WriteMessage(
                        "\n  ⚠ No crossing pipe parts found in this profile view." +
                        "\n  Make sure pipes are drawn into the profile view" +
                        " (PIPEMAGIC / ADDNETWORKPARTSTOPROF).\n");
                    return;
                }

                // Report each crossing
                foreach (var cp in crossings)
                {
                    ed.WriteMessage(
                        $"\n    Sta {cp.Station:F2}  Elev {cp.Elevation:F3}" +
                        $"  at ({cp.DrawingX:F2}, {cp.DrawingY:F2})");
                }

                // ── Step 3: Collect label styles ──────────────────────────────
                var labelStyles  = new List<StyleItem>();
                var markerStyles = new List<StyleItem>();

                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    try
                    {
                        CivilApp.CivilDocument civDoc =
                            CivilApp.CivilDocument.GetCivilDocument(db);

                        // Station Elevation Label Styles for Profile Views
                        // Path: Styles > LabelStyles > ProfileViewLabelStyles >
                        //       StationElevationLabelStyles
                        try
                        {
                            var seStyles = civDoc.Styles.LabelStyles
                                                .ProfileViewLabelStyles
                                                .StationElevationLabelStyles;
                            CollectLabelStyles(seStyles, labelStyles, tx);
                        }
                        catch (System.Exception ex)
                        {
                            ed.WriteMessage(
                                $"\n  ⚠ Could not read StationElevationLabelStyles: {ex.Message}");
                        }

                        // Marker Styles
                        foreach (ObjectId id in civDoc.Styles.MarkerStyles)
                        {
                            try
                            {
                                var ms = tx.GetObject(id, OpenMode.ForRead)
                                         as CivilDB.Styles.MarkerStyle;
                                if (ms != null)
                                    markerStyles.Add(new StyleItem { Name = ms.Name, Id = id });
                            }
                            catch { }
                        }
                    }
                    catch (System.Exception ex)
                    {
                        ed.WriteMessage($"\n  ⚠ Style scan warning: {ex.Message}");
                    }
                    tx.Abort();
                }

                ed.WriteMessage($"\n  Found: {labelStyles.Count} label style(s), " +
                                $"{markerStyles.Count} marker style(s)");

                if (labelStyles.Count == 0)
                {
                    ed.WriteMessage(
                        "\n  ❌ No Station Elevation Label Styles found in the drawing." +
                        "\n  Please ensure label styles exist under:" +
                        "\n    Settings > Profile View > Label Styles > Station Elevation\n");
                    return;
                }

                // Add "(None)" option for marker
                markerStyles.Insert(0, new StyleItem { Name = "(None)", Id = ObjectId.Null });

                // ── Step 4: Show dialog ───────────────────────────────────────
                var dlg = new LLabelGenDialog(labelStyles, markerStyles);
                bool? dlgResult = AcApp.ShowModalWindow(dlg);
                if (dlgResult != true)
                {
                    ed.WriteMessage("\n  Cancelled.\n");
                    return;
                }

                ObjectId chosenLabelStyleId  = dlg.SelectedLabelStyleId;
                ObjectId chosenMarkerStyleId = dlg.SelectedMarkerStyleId;

                ed.WriteMessage($"\n  Label style selected. Queuing {crossings.Count} label(s)...");

                // ── Step 5: Queue label placements ────────────────────────────
                int queued = LLabelGenEngine.QueueLabelJobs(
                    pvId, crossings,
                    chosenLabelStyleId, chosenMarkerStyleId,
                    db, doc);

                ed.WriteMessage(
                    $"\n  {queued} label job(s) queued." +
                    "\n  Labels will be placed after this command exits." +
                    "\n═══════════════════════════════════════════════════════════\n");
            }
            catch (System.Exception ex)
            {
                var d = AcApp.DocumentManager.MdiActiveDocument;
                d?.Editor.WriteMessage($"\n[ALDT ERROR] LLABELGEN: {ex.Message}\n");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Find a ProfileView by testing if a WCS point falls inside any PV
        //  bounding box.  Same approach as MarkLinesCommand / RrNetworkCheckCommand.
        // ─────────────────────────────────────────────────────────────────────
        private static CivilDB.ProfileView? FindProfileViewAtPoint(
            Point3d pickPoint, Transaction tx, Database db)
        {
            RXClass pvClass = RXObject.GetClass(typeof(CivilDB.ProfileView));

            var bt = tx.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            var ms = tx.GetObject(
                     bt![BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;

            foreach (ObjectId id in ms!)
            {
                if (!id.ObjectClass.IsDerivedFrom(pvClass)) continue;
                try
                {
                    var pv = tx.GetObject(id, OpenMode.ForRead) as CivilDB.ProfileView;
                    if (pv == null) continue;

                    double sta = 0, elev = 0;
                    if (pv.FindStationAndElevationAtXY(
                            pickPoint.X, pickPoint.Y, ref sta, ref elev))
                        return pv;
                }
                catch { }
            }
            return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Collect all label styles from a Civil 3D label style collection
        // ─────────────────────────────────────────────────────────────────────
        private static void CollectLabelStyles(
            CivilDB.Styles.LabelStyleCollection collection,
            List<StyleItem> list,
            Transaction tx)
        {
            try
            {
                foreach (ObjectId id in collection)
                {
                    try
                    {
                        var st = tx.GetObject(id, OpenMode.ForRead)
                                 as CivilDB.Styles.LabelStyle;
                        if (st != null)
                            list.Add(new StyleItem { Name = st.Name, Id = id });
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
