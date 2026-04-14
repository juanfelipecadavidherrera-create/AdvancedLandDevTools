using System;
using System.Windows;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AdvancedLandDevTools.Engine;
using AdvancedLandDevTools.UI;
using CivilDB = Autodesk.Civil.DatabaseServices;

// Alias resolves 'Application' clash
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AdvancedLandDevTools.Commands
{
    public class ChopChopCommand
    {
        [CommandMethod("CHOPCHOP", CommandFlags.Modal)]
        public void ChopChop()
        {
            try
            {
                if (!Engine.LicenseManager.EnsureLicensed()) return;

                var doc = AcadApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                var ed = doc.Editor;
                var db = doc.Database;

                ed.WriteMessage("\n");
                ed.WriteMessage("═══════════════════════════════════════════════════════════\n");
                ed.WriteMessage("  Advanced Land Development Tools  |  ChopChop\n");
                ed.WriteMessage("  Subdivide a profile view into smaller views\n");
                ed.WriteMessage("═══════════════════════════════════════════════════════════\n");

                // ── 1. Prompt user to select a profile view ───────────────────
                var per = ed.GetEntity(
                    new PromptEntityOptions("\n  Select a profile view to subdivide: ")
                    { AllowNone = false });

                if (per.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\n  Cancelled.\n");
                    return;
                }

                // ── 2. Read source PV properties ──────────────────────────────
                string   pvName          = "";
                ObjectId alignmentId     = ObjectId.Null;
                ObjectId pvStyleId       = ObjectId.Null;
                double   staStart        = 0;
                double   staEnd          = 0;
                double   elevMin         = 0;
                double   elevMax         = 0;
                bool     elevIsUser      = false;
                Point3d  pvMinPoint      = Point3d.Origin;

                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var pv = tx.GetObject(per.ObjectId, OpenMode.ForRead) as CivilDB.ProfileView;
                    if (pv == null)
                    {
                        ed.WriteMessage("\n  Selected entity is not a profile view.\n");
                        tx.Abort();
                        return;
                    }

                    pvName      = pv.Name;
                    alignmentId = pv.AlignmentId;
                    pvStyleId   = pv.StyleId;
                    staStart    = pv.StationStart;
                    staEnd      = pv.StationEnd;
                    elevMin     = pv.ElevationMin;
                    elevMax     = pv.ElevationMax;
                    elevIsUser  = pv.ElevationRangeMode == CivilDB.ElevationRangeType.UserSpecified;

                    try
                    {
                        var ent = pv as Entity;
                        if (ent != null)
                            pvMinPoint = ent.GeometricExtents.MinPoint;
                    }
                    catch { }

                    tx.Commit();
                }

                ed.WriteMessage($"\n  Profile View: \"{pvName}\"");
                ed.WriteMessage(
                    $"\n  Station: {Helpers.StationParser.Format(staStart)} to " +
                    $"{Helpers.StationParser.Format(staEnd)}");
                ed.WriteMessage(
                    $"\n  Elevation: {elevMin:F2} to {elevMax:F2}" +
                    (elevIsUser ? " (User)" : " (Auto)"));

                // ── 3. Show dialog ────────────────────────────────────────────
                var dialog = new ChopChopDialog();
                dialog.SourcePvName   = pvName;
                dialog.SourceStaStart = staStart;
                dialog.SourceStaEnd   = staEnd;

                bool? dlgResult = AcadApp.ShowModalWindow(dialog);

                if (dlgResult != true || dialog.Result == null)
                {
                    ed.WriteMessage("\n  Command cancelled.\n");
                    return;
                }

                // ── 4. Fill in remaining settings from the source PV ──────────
                var settings = dialog.Result;
                settings.AlignmentId        = alignmentId;
                settings.ProfileViewStyleId = pvStyleId;
                settings.SourcePvName       = pvName;
                settings.StationStart       = staStart;
                settings.StationEnd         = staEnd;
                settings.ElevationMin       = elevMin;
                settings.ElevationMax       = elevMax;
                settings.ElevIsUserSpecified = elevIsUser;
                settings.OriginalPvMinPoint = pvMinPoint;

                ed.WriteMessage($"\n  Creating {settings.Intervals.Count} sub-view(s)…\n");

                // ── 5. Run the engine ─────────────────────────────────────────
                BulkProfileResult result = ChopChopEngine.Run(settings);

                // ── 6. Report results ─────────────────────────────────────────
                ed.WriteMessage("\n");
                ed.WriteMessage("  ─── CHOPCHOP  –  RESULTS ───────────────────────────────\n");
                foreach (string line in result.Log)
                    ed.WriteMessage($"  {line}\n");

                ed.WriteMessage("  ─────────────────────────────────────────────────────────\n");
                ed.WriteMessage(
                    $"  Completed:  {result.SuccessCount} succeeded" +
                    (result.FailureCount > 0 ? $",  {result.FailureCount} FAILED" : "") +
                    "\n");
                ed.WriteMessage("═══════════════════════════════════════════════════════════\n");

                string icon    = result.FailureCount > 0 ? "Partial Failure" : "Success";
                string title   = $"ChopChop – {icon}";
                string fullLog = string.Join("\n", result.Log);
                string summary =
                    $"{result.SuccessCount} sub-view(s) created.\n" +
                    (result.FailureCount > 0
                        ? $"{result.FailureCount} segment(s) failed.\n" : "") +
                    $"\n── Full Log ──\n{fullLog}";

                MessageBox.Show(summary, title,
                    MessageBoxButton.OK,
                    result.FailureCount > 0
                        ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                var d = AcadApp.DocumentManager.MdiActiveDocument;
                d?.Editor.WriteMessage($"\n[ALDT ERROR] CHOPCHOP: {ex.Message}\n");
            }
        }
    }
}
