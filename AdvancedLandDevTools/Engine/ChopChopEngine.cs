using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CivilDB = Autodesk.Civil.DatabaseServices;
using AdvancedLandDevTools.Helpers;
// NOTE: Autodesk.AutoCAD.Runtime is imported for RXObject.GetClass() only.
// All catch blocks use System.Exception (fully qualified) to avoid the
// ambiguity with Autodesk.AutoCAD.Runtime.Exception.

namespace AdvancedLandDevTools.Engine
{
    // ─────────────────────────────────────────────────────────────────────────
    //  ChopChopSettings — validated settings handed to the engine
    // ─────────────────────────────────────────────────────────────────────────
    public class ChopChopSettings
    {
        public ObjectId AlignmentId        { get; set; }
        public ObjectId ProfileViewStyleId { get; set; }
        public string   SourcePvName       { get; set; } = "PV";

        // Station range from original PV
        public double StationStart { get; set; }
        public double StationEnd   { get; set; }

        // Elevation from original PV (to copy)
        public double ElevationMin          { get; set; }
        public double ElevationMax          { get; set; }
        public bool   ElevIsUserSpecified   { get; set; }

        // Computed intervals — [(start, end), ...]
        public List<(double Start, double End)> Intervals { get; set; } = new();

        // Layout
        public double  HorizontalGap      { get; set; } = 100.0;
        public double  VerticalOffset      { get; set; } = 500.0;
        public Point3d OriginalPvMinPoint  { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  ChopChopEngine
    // ─────────────────────────────────────────────────────────────────────────
    public static class ChopChopEngine
    {
        public static BulkProfileResult Run(ChopChopSettings settings)
        {
            var result = new BulkProfileResult();

            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                result.AddFailure("No active document found.");
                return result;
            }

            Database db = doc.Database;
            var intervals = settings.Intervals;

            if (intervals == null || intervals.Count == 0)
            {
                result.AddFailure("No intervals to create.");
                return result;
            }

            // ── Transaction 1: Create all sub-views ──────────────────────────
            var createdPvIds = new List<ObjectId>();

            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                try
                {
                    // Base insertion: original PV bottom-left, shifted down
                    double baseX = settings.OriginalPvMinPoint.X;
                    double baseY = settings.OriginalPvMinPoint.Y - settings.VerticalOffset;

                    // Collect existing PV names for uniqueness
                    var existingNames = GetExistingProfileViewNames(db, tx);

                    double curX = baseX;

                    for (int i = 0; i < intervals.Count; i++)
                    {
                        var (staStart, staEnd) = intervals[i];

                        try
                        {
                            Point3d insertPt = new Point3d(curX, baseY, 0.0);

                            // Create via CreateMultiple (same pattern as BulkSurfaceProfileEngine)
                            // Set LengthOfEachView very large → single view, no splits.
                            var mOpts = new CivilDB.MultipleProfileViewsCreationOptions();
                            mOpts.LengthOfEachView = 1e7;

                            ObjectIdCollection pvIdCol = CivilDB.ProfileView.CreateMultiple(
                                settings.AlignmentId,
                                insertPt,
                                mOpts);

                            if (pvIdCol == null || pvIdCol.Count == 0)
                            {
                                result.AddFailure($"Segment {i + 1}: CreateMultiple returned no views.");
                                continue;
                            }

                            // Keep first, erase extras
                            ObjectId pvId = pvIdCol[0];
                            for (int xi = 1; xi < pvIdCol.Count; xi++)
                            {
                                try
                                {
                                    var extra = tx.GetObject(pvIdCol[xi], OpenMode.ForWrite) as Entity;
                                    extra?.Erase();
                                }
                                catch { }
                            }

                            // Configure the new view
                            var pv = (CivilDB.ProfileView)tx.GetObject(pvId, OpenMode.ForWrite);

                            // Name
                            string pvName = MakeUniqueName(
                                $"{settings.SourcePvName} ({i + 1})", existingNames);
                            pv.Name = pvName;
                            existingNames.Add(pvName);

                            // Style (copy from original)
                            try { pv.StyleId = settings.ProfileViewStyleId; }
                            catch { }

                            // Station range — clip to sub-interval
                            pv.StationRangeMode = CivilDB.StationRangeType.UserSpecified;
                            pv.StationStart     = staStart;
                            pv.StationEnd       = staEnd;

                            // Elevation range (copy from original)
                            if (settings.ElevIsUserSpecified)
                            {
                                try
                                {
                                    pv.ElevationRangeMode = CivilDB.ElevationRangeType.UserSpecified;
                                    pv.ElevationMin = settings.ElevationMin;
                                    pv.ElevationMax = settings.ElevationMax;
                                }
                                catch { }
                            }

                            createdPvIds.Add(pvId);

                            // Advance X for next view (horizontal layout)
                            double viewWidth = staEnd - staStart;
                            curX += viewWidth + settings.HorizontalGap;

                            result.AddSuccess(
                                $"Segment {i + 1}: \"{pvName}\"  " +
                                $"Sta {StationParser.Format(staStart)} to " +
                                $"{StationParser.Format(staEnd)}");
                        }
                        catch (System.Exception ex)
                        {
                            result.AddFailure($"Segment {i + 1}: {ex.Message}");
                        }
                    }

                    tx.Commit();
                }
                catch (System.Exception ex)
                {
                    result.AddFailure($"Fatal error: {ex.Message}");
                    tx.Abort();
                    return result;
                }
            }

            // ── Transaction 2: Re-apply elevation range after commit ─────────
            if (createdPvIds.Count > 0 && settings.ElevIsUserSpecified)
            {
                using (Transaction tx2 = db.TransactionManager.StartTransaction())
                {
                    try
                    {
                        foreach (ObjectId pvId in createdPvIds)
                        {
                            try
                            {
                                var pv = tx2.GetObject(pvId, OpenMode.ForWrite)
                                         as CivilDB.ProfileView;
                                if (pv == null) continue;

                                pv.ElevationRangeMode = CivilDB.ElevationRangeType.UserSpecified;
                                pv.ElevationMin = settings.ElevationMin;
                                pv.ElevationMax = settings.ElevationMax;
                            }
                            catch { }
                        }
                        tx2.Commit();
                    }
                    catch
                    {
                        tx2.Abort();
                    }
                }
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        private static HashSet<string> GetExistingProfileViewNames(
            Database db, Transaction tx)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                RXClass pvClass = RXObject.GetClass(typeof(CivilDB.ProfileView));
                var bt  = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                var msp = (BlockTableRecord)tx.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in msp)
                {
                    if (!id.ObjectClass.IsDerivedFrom(pvClass)) continue;
                    try
                    {
                        var pv = tx.GetObject(id, OpenMode.ForRead) as CivilDB.ProfileView;
                        if (pv != null) names.Add(pv.Name);
                    }
                    catch { }
                }
            }
            catch { }
            return names;
        }

        private static string MakeUniqueName(string desired, HashSet<string> existing)
        {
            if (!existing.Contains(desired)) return desired;
            for (int i = 2; i < 9999; i++)
            {
                string c = $"{desired} ({i})";
                if (!existing.Contains(c)) return c;
            }
            return $"{desired}_{Guid.NewGuid():N}";
        }
    }
}
