using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using CivilApp = Autodesk.Civil.ApplicationServices;
using CivilDB  = Autodesk.Civil.DatabaseServices;
using AdvancedLandDevTools.Models;
// NOTE: Autodesk.AutoCAD.Runtime is imported for RXObject.GetClass() only.
// All catch blocks use System.Exception (fully qualified) to avoid the
// ambiguity with Autodesk.AutoCAD.Runtime.Exception.

namespace AdvancedLandDevTools.Engine
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Result model
    // ─────────────────────────────────────────────────────────────────────────
    public class BulkProfileResult
    {
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public List<string> Log { get; } = new();

        public void AddSuccess(string msg) { SuccessCount++; Log.Add($"  ✓  {msg}"); }
        public void AddFailure(string msg) { FailureCount++; Log.Add($"  ✗  {msg}"); }
        public void AddInfo   (string msg) { Log.Add($"  ℹ  {msg}"); }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  BulkSurfaceProfileEngine
    // ─────────────────────────────────────────────────────────────────────────
    public static class BulkSurfaceProfileEngine
    {
        public static BulkProfileResult Run(BulkProfileSettings settings)
        {
            var result = new BulkProfileResult();

            Document doc = Autodesk.AutoCAD.ApplicationServices.Application
                               .DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                result.AddFailure("No active document found.");
                return result;
            }

            Database db = doc.Database;
            var createdPvIds = new List<ObjectId>();

            // ── Transaction 1: Create all profiles and profile views ─────────────
            using (Transaction trans = db.TransactionManager.StartTransaction())
            {
                try
                {
                    CivilApp.CivilDocument civDoc =
                        CivilApp.CivilDocument.GetCivilDocument(db);

                    settings.LabelSetStyleId = ResolveLabelSetStyle(civDoc, trans);

                    // [FIX] Do NOT throw if BandSetStyleId is Null.
                    // ObjectId.Null tells Civil 3D to create the view with NO BANDS –
                    // this is valid and prevents the "ObjectId expected" crash.
                    settings.BandSetStyleId = ResolveBandSetStyle(civDoc, db, trans);

                    // ── Deep diagnostic: dump everything in ProfileViewBandSetStyles ──
                    {
                        var diag = new System.Text.StringBuilder();
                        int cnt = civDoc.Styles.ProfileViewBandSetStyles.Count;
                        diag.Append($"BandSetStyles count={cnt} | ");
                        int idx = 0;
                        foreach (ObjectId id in civDoc.Styles.ProfileViewBandSetStyles)
                        {
                            try
                            {
                                var obj = trans.GetObject(id, OpenMode.ForRead);
                                string typeName = obj.GetType().FullName;
                                string name = (obj is CivilDB.Styles.StyleBase sb)
                                    ? $"Name=\"{sb.Name}\"" : "(not StyleBase)";
                                diag.Append($"[{idx}] {typeName} {name} Handle={id.Handle} | ");
                            }
                            catch (System.Exception ex)
                            {
                                diag.Append($"[{idx}] OpenFailed:{ex.Message} | ");
                            }
                            idx++;
                        }
                        if (idx == 0) diag.Append("(empty)");
                        result.AddInfo($"DIAG-BandSet: {diag}");
                    }

                    BlockTable bt = (BlockTable)
                        trans.GetObject(db.BlockTableId, OpenMode.ForRead);
                    BlockTableRecord msp = (BlockTableRecord)
                        trans.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                    HashSet<string> existingPvNames = GetExistingProfileViewNames(db, trans);

                    int viewIndex = 0;

                    foreach (AlignmentItem item in settings.SelectedAlignments)
                    {
                        try
                        {
                            ObjectId pvId = ProcessOneAlignment(
                                item, settings, civDoc, db, trans, msp,
                                existingPvNames, ref viewIndex, result);

                            if (!pvId.IsNull)
                                createdPvIds.Add(pvId);
                        }
                        catch (System.Exception ex)
                        {
                            result.AddFailure($"{item.Name} → {ex.Message}");
                        }
                    }

                    trans.Commit();
                }
                catch (System.Exception ex)
                {
                    result.AddFailure($"Fatal error (Tx1): {ex.Message}");
                    trans.Abort();
                    return result;
                }
            }

            // ── Transaction 2: Set elevation range on committed views ─────────────
            //
            //  Now using CreateMultiple (no split mode), ElevationMin/Max should be
            //  writable via the managed API. Split-mode was the root cause of the
            //  "invalid state" error in all previous attempts.
            //
            if (createdPvIds.Count > 0)
            {
                double eMin = settings.ElevationMin ?? DEFAULT_ELEV_MIN;  // 2.0
                double eMax = settings.ElevationMax ?? DEFAULT_ELEV_MAX;  // 12.0
                if (settings.ElevationMin.HasValue && !settings.ElevationMax.HasValue)
                    eMax = eMin + 10.0;
                if (!settings.ElevationMin.HasValue && settings.ElevationMax.HasValue)
                    eMin = eMax - 10.0;
                if (eMax <= eMin) eMax = eMin + 10.0;

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

                                string stepLog = $"Tx2 {eMin:F1}–{eMax:F1}: ";
                                stepLog += $"CurrentMode={pv.ElevationRangeMode} ";
                                stepLog += $"CurrentMin={pv.ElevationMin:F2} ";
                                stepLog += $"CurrentMax={pv.ElevationMax:F2} | ";

                                // Step 1: Switch to UserSpecified
                                try
                                {
                                    pv.ElevationRangeMode = CivilDB.ElevationRangeType.UserSpecified;
                                    stepLog += "SetUser=OK ";
                                }
                                catch (System.Exception ex)
                                { stepLog += $"SetUser=FAIL({ex.Message}) "; }

                                // Step 2: Set Min
                                try
                                {
                                    pv.ElevationMin = eMin;
                                    stepLog += $"Min={eMin:F1}=OK ";
                                }
                                catch (System.Exception ex)
                                { stepLog += $"Min=FAIL({ex.Message}) "; }

                                // Step 3: Set Max
                                try
                                {
                                    pv.ElevationMax = eMax;
                                    stepLog += $"Max={eMax:F1}=OK ";
                                }
                                catch (System.Exception ex)
                                { stepLog += $"Max=FAIL({ex.Message}) "; }

                                stepLog += $"→ Final:{pv.ElevationMin:F2}–{pv.ElevationMax:F2}";
                                result.AddInfo(stepLog);
                            }
                            catch (System.Exception ex)
                            {
                                result.AddFailure($"Tx2 elev error: {ex.Message}");
                            }
                        }
                        tx2.Commit();
                    }
                    catch (System.Exception ex)
                    {
                        result.AddFailure($"Tx2 fatal: {ex.Message}");
                        tx2.Abort();
                    }
                }
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        private static ObjectId ProcessOneAlignment(
            AlignmentItem          item,
            BulkProfileSettings    settings,
            CivilApp.CivilDocument civDoc,
            Database               db,
            Transaction            trans,
            BlockTableRecord       msp,
            HashSet<string>        existingPvNames,
            ref int                viewIndex,
            BulkProfileResult      result)
        {
            CivilDB.Alignment al = (CivilDB.Alignment)
                trans.GetObject(item.Id, OpenMode.ForRead);

            // ── 1. Station range ──────────────────────────────────────────────
            //
            //  User's requested range (stStart/stEnd) is used for the ProfileView
            //  display range — Civil 3D allows views wider than the alignment.
            //  The sampling range (sampleStart/sampleEnd) is clamped to the
            //  alignment extents because CreateFromSurface cannot sample outside.
            //
            double stStart = settings.StationStart ?? al.StartingStation;
            double stEnd   = settings.StationEnd   ?? al.EndingStation;

            // Display range: pad -50/+50 beyond alignment when user didn't specify
            double displayStart = settings.StationStart ?? (al.StartingStation - 50.0);
            double displayEnd   = settings.StationEnd   ?? (al.EndingStation   + 50.0);

            if (stStart >= stEnd)
                throw new InvalidOperationException(
                    $"Station range invalid: start={stStart:F4} end={stEnd:F4}. " +
                    $"Alignment may be too short or station override is reversed.");

            // Clamp sampling to alignment extents (+ 0.01 inset to avoid boundary rejection)
            double sampleStart = Math.Max(stStart, al.StartingStation) + 0.01;
            double sampleEnd   = Math.Min(stEnd,   al.EndingStation)   - 0.01;

            if (sampleStart >= sampleEnd)
                throw new InvalidOperationException(
                    $"Requested station range ({stStart:F4}–{stEnd:F4}) does not overlap " +
                    $"alignment \"{al.Name}\" ({al.StartingStation:F4}–{al.EndingStation:F4}).");

            // ── 2. Unique profile name ────────────────────────────────────────
            string profileName = MakeUniqueName(
                al.Name + settings.ProfileNameSuffix,
                GetExistingProfileNames(al, trans));

            // ── 3. Resolve layer "0" ObjectId ────────────────────────────────
            //
            //  Civil 3D 2026 runtime validates that the layerId arg must point to a
            //  real LayerTableRecord. ObjectId.Null is rejected at runtime with the
            //  error: "Object id of LayerTableRecord is expected. (Parameter 'layerId')"
            //  We resolve layer "0" which is guaranteed to exist in every DWG.
            //
            LayerTable lt = (LayerTable)trans.GetObject(
                db.LayerTableId, OpenMode.ForRead);
            ObjectId layer0Id = lt["0"];

            // ── 4. Create surface profile ─────────────────────────────────────
            //
            //  Civil 3D 2026 confirmed signature – arg 6 is layerId (ObjectId),
            //  NOT bandSetStyleId. There is no bandSetStyleId in this overload.
            //
            //    Profile.CreateFromSurface(
            //        string   name,              arg 1
            //        ObjectId alignmentId,       arg 2
            //        ObjectId surfaceId,         arg 3
            //        ObjectId profileStyleId,    arg 4
            //        ObjectId labelSetStyleId,   arg 5
            //        ObjectId layerId,           arg 6  ← valid LayerTableRecord required
            //        double   sampleStartStation,arg 7
            //        double   sampleEndStation,  arg 8
            //        double   offset)            arg 9
            //
            // ── Validate all ObjectIds before calling – avoids opaque runtime errors ──
            //
            //  Civil 3D 2026 throws "LayerTableRecord expected (Parameter 'layerId')"
            //  when ANY ObjectId parameter is Null or points to the wrong object type.
            //  We validate every ObjectId upfront and throw a clear message instead.
            //
            if (settings.ProfileStyleId.IsNull)
                throw new InvalidOperationException(
                    "ProfileStyleId is null – select a Profile Style in the dialog.");

            if (layer0Id.IsNull)
                throw new InvalidOperationException(
                    "Layer '0' ObjectId could not be resolved from the drawing.");

            // LabelSetStyleId is optional – resolve a valid one if Null
            ObjectId resolvedLabelSetId = settings.LabelSetStyleId;
            if (resolvedLabelSetId.IsNull)
                resolvedLabelSetId = ResolveAnyLabelSetStyle(civDoc, trans);

            // ── Try CreateFromSurface with known-good IDs ──────────────────────
            //
            //  If this still throws layerId, the arg ORDER in this overload is wrong.
            //  We catch it with a descriptive message so the next fix is targeted.
            //
            // ── CONFIRMED argument order via diagnostic (all IDs valid, layerId ──
            //    error persisted until layer was moved to position 4):
            //
            //    Profile.CreateFromSurface(
            //        string   name,              arg 1
            //        ObjectId alignmentId,       arg 2
            //        ObjectId surfaceId,         arg 3
            //        ObjectId layerId,           arg 4  ← layer FIRST, before styles
            //        ObjectId profileStyleId,    arg 5
            //        ObjectId labelSetStyleId,   arg 6
            //        double   sampleStartStation,arg 7
            //        double   sampleEndStation,  arg 8
            //        double   offset)            arg 9
            //
            ObjectId profileId;
            try
            {
                // Diagnostic proved all IDs valid but stations still failed.
                // Civil 3D error "sampleStart should be less than sampleEnd" with
                // stStart=0 stEnd=377 means the doubles are in the wrong slot.
                // Passing 0.0 offset LAST caused Civil 3D to read it as sampleEnd.
                // CONFIRMED order: offset comes BEFORE sampleStart and sampleEnd.
                //
                //   arg 4 – layerId           (ObjectId)
                //   arg 5 – profileStyleId    (ObjectId)
                //   arg 6 – labelSetStyleId   (ObjectId)
                //   arg 7 – offset            (double)  ← offset FIRST
                //   arg 8 – sampleStart       (double)
                //   arg 9 – sampleEnd         (double)
                //
                profileId = CivilDB.Profile.CreateFromSurface(
                    profileName,
                    item.Id,                    // alignmentId       (arg 2)
                    settings.SurfaceId,         // surfaceId         (arg 3)
                    layer0Id,                   // layerId           (arg 4)
                    settings.ProfileStyleId,    // profileStyleId    (arg 5)
                    resolvedLabelSetId,         // labelSetStyleId   (arg 6)
                    0.0,                        // offset            (arg 7 – 0 = centreline)
                    sampleStart,                // sampleStartStation(arg 8) – clamped to alignment
                    sampleEnd);                 // sampleEndStation  (arg 9) – clamped to alignment
            }
            catch (System.Exception ex)
            {
                // Re-throw with diagnostic context so the error report is useful
                throw new InvalidOperationException(
                    $"CreateFromSurface failed → {ex.Message} | " +
                    $"ProfileStyle={settings.ProfileStyleId.IsNull} " +
                    $"LabelSet={resolvedLabelSetId.IsNull} " +
                    $"Layer0={layer0Id.IsNull} " +
                    $"Surface={settings.SurfaceId.IsNull} " +
                    $"sampleStart={sampleStart:F4} sampleEnd={sampleEnd:F4} " +
                    $"stStart={stStart:F4} stEnd={stEnd:F4} " +
                    $"alStart={al.StartingStation:F4} alEnd={al.EndingStation:F4}",
                    ex);
            }

            // ── 4b. Explicitly set profile style post-creation ───────────────────
            //
            //  Profile.CreateFromSurface does not reliably apply the style passed
            //  as an argument in Civil 3D 2026 – the style must be set again on the
            //  returned object. This is the same behaviour as _AeccCreateProfileFromSurface
            //  which also sets style as a post-creation property assignment.
            //
            //  Also set the profile name explicitly here to guarantee it matches
            //  our unique name (Civil 3D can silently rename on collision).
            //
            try
            {
                var profile = trans.GetObject(profileId, OpenMode.ForWrite)
                              as CivilDB.Profile;
                if (profile != null)
                {
                    profile.StyleId = settings.ProfileStyleId;
                    profile.Name    = profileName;   // re-assert in case Civil 3D renamed it
                }
            }
            catch { /* non-fatal – profile was still created */ }

            // ── 5. Insertion point ────────────────────────────────────────────
            Point3d insertPt = new Point3d(
                settings.BaseInsertionPoint.X,
                settings.BaseInsertionPoint.Y - (viewIndex * settings.ViewSpacing),
                0.0);

            // ── 6. Unique profile view name ───────────────────────────────────
            string pvName = MakeUniqueName(
                al.Name + settings.ProfileViewNameSuffix,
                existingPvNames);

            // ── 7. Create the Profile View ────────────────────────────────────
            //
            //  Civil 3D 2026 confirmed constructors (from compiler errors):
            //
            //  StackedProfileViewsCreationOptions(
            //      ObjectId topViewStyleId,     ← all three are REQUIRED
            //      ObjectId middleViewStyleId,
            //      ObjectId bottomViewStyleId)
            //
            //  SplitProfileViewCreationOptions(
            //      double   viewHeight,          ← REQUIRED; use large value = no split
            //      ObjectId firstSegStyleId,
            //      ObjectId middleSegStyleId,
            //      ObjectId lastSegStyleId)
            //
            //  ProfileViewCreationOptions does NOT exist in Civil 3D 2026.
            //  The view name must be assigned after creation via pv.Name.
            //
            //  ProfileView.Create returns ObjectIdCollection – one entry per segment.
            //  For a single non-stacked view all three style slots use the same style.
            //
            if (settings.ProfileViewStyleId.IsNull)
                throw new InvalidOperationException(
                    "ProfileViewStyleId is null – select a Profile View Style in the dialog.");

            // ── 7. Create the Profile View via CreateMultiple (3-arg overload) ────
            //
            //  WHY NOT ProfileView.Create(5-arg):
            //    → Requires bandSetStyleId; Civil 3D rejects it even with valid style.
            //
            //  WHY NOT ProfileView.Create(stacked/split overload):
            //    → Permanently enables split mode, locking ElevationMin/Max to the
            //      surface datum. SetAuto/SetUser work but Min/Max throw "invalid state".
            //
            //  WHY CreateMultiple(3-arg):
            //    → No bandSetStyleId parameter.
            //    → No SplitProfileViewCreationOptions → no split-mode locking.
            //    → Setting LengthOfEachView > alignment length = single view, no splits.
            //    → Returns ObjectIdCollection; we take the first (and only) entry.
            //
            if (settings.ProfileViewStyleId.IsNull)
                throw new InvalidOperationException(
                    "ProfileViewStyleId is null – select a Profile View Style.");

            double viewLength = displayEnd - displayStart;  // padded range for view display
            var mOpts = new CivilDB.MultipleProfileViewsCreationOptions();
            mOpts.LengthOfEachView = viewLength;  // covers full user range = single segment, no splits

            ObjectIdCollection pvIdCol = CivilDB.ProfileView.CreateMultiple(
                item.Id,   // alignmentId
                insertPt,  // insertionPoint
                mOpts);    // no bandSetStyleId, no split options

            if (pvIdCol == null || pvIdCol.Count == 0)
                throw new InvalidOperationException(
                    "ProfileView.CreateMultiple returned no views.");

            // CreateMultiple may return more than 1 view even when LengthOfEachView
            // equals the alignment length. Erase all extras — only keep index 0.
            ObjectId pvId = pvIdCol[0];
            for (int xi = 1; xi < pvIdCol.Count; xi++)
            {
                try
                {
                    var extra = trans.GetObject(pvIdCol[xi], OpenMode.ForWrite) as Entity;
                    extra?.Erase();
                }
                catch { }
            }
            existingPvNames.Add(pvName);

            // ── 8. Set name, style, station/elevation on the view ─────────────
            CivilDB.ProfileView pv;
            try
            {
                pv = (CivilDB.ProfileView)trans.GetObject(pvId, OpenMode.ForWrite);
            }
            catch (System.Exception ex)
            {
                throw new InvalidOperationException($"Step 8a GetObject(pv) → {ex.Message}", ex);
            }

            try { pv.Name = pvName; }
            catch (System.Exception ex)
            {
                throw new InvalidOperationException($"Step 8b pv.Name → {ex.Message}", ex);
            }

            try { pv.StyleId = settings.ProfileViewStyleId; }
            catch (System.Exception ex)
            {
                throw new InvalidOperationException($"Step 8c pv.StyleId → {ex.Message}", ex);
            }

            try { ApplyStationRange(pv, settings, displayStart, displayEnd); }
            catch (System.Exception ex)
            {
                throw new InvalidOperationException($"Step 8d ApplyStationRange → {ex.Message}", ex);
            }

            // Elevation is applied in Tx2 after this transaction commits.
            // With the simple (non-split) overload, ElevationMin/Max are fully
            // writable once the view is committed to the database.

            viewIndex++;

            result.AddSuccess(
                $"{al.Name}  →  Profile: \"{profileName}\"  |  " +
                $"View: \"{pvName}\"  @ ({insertPt.X:F1}, {insertPt.Y:F1})");

            return pvId;   // returned to Run() for Tx2 elevation pass
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void ApplyStationRange(
            CivilDB.ProfileView pv,
            BulkProfileSettings settings,
            double stStart,
            double stEnd)
        {
            // Always apply — defaults include -50/+50 padding beyond alignment
            pv.StationRangeMode = CivilDB.StationRangeType.UserSpecified;
            pv.StationStart     = stStart;
            pv.StationEnd       = stEnd;
        }

        // Default elevation constants – applied when user leaves both fields blank
        private const double DEFAULT_ELEV_MIN = 2.0;
        private const double DEFAULT_ELEV_MAX = 12.0;

        private static void ApplyElevationRange(
            CivilDB.ProfileView pv,
            BulkProfileSettings settings)
        {
            // ── Resolve min/max (never reads pv.ElevationMin/Max) ────────────────
            double eMin, eMax;

            if (!settings.ElevationMin.HasValue && !settings.ElevationMax.HasValue)
            {
                eMin = DEFAULT_ELEV_MIN;   // 2.0
                eMax = DEFAULT_ELEV_MAX;   // 12.0
            }
            else if (settings.ElevationMin.HasValue && settings.ElevationMax.HasValue)
            {
                eMin = settings.ElevationMin.Value;
                eMax = settings.ElevationMax.Value;
            }
            else if (settings.ElevationMin.HasValue)
            {
                eMin = settings.ElevationMin.Value;
                eMax = eMin + 10.0;
            }
            else
            {
                eMax = settings.ElevationMax!.Value;
                eMin = eMax - 10.0;
            }

            if (eMax <= eMin)
                throw new InvalidOperationException(
                    $"Elevation Max ({eMax:F2}) must be > Min ({eMin:F2}).");

            // ── Apply with per-line diagnostics ────────────────────────────────
            //
            //  Research note: ElevationRangeMode must be set BEFORE ElevationMin/Max.
            //  In some drawing templates, ElevationLocked must also be set to True
            //  for the manual values to persist through view updates.
            //
            // ── Set mode first (mandatory), then Min, then Max ─────────────────
            //
            //  Civil 3D derives ElevationMax = ElevationMin + Height internally.
            //  To override both, set ElevationMin first, then ElevationMax.
            //  Setting Max before Min can fail if Max < current Min.
            //
            try
            {
                pv.ElevationRangeMode = CivilDB.ElevationRangeType.UserSpecified;
            }
            catch (System.Exception ex)
            {
                throw new InvalidOperationException(
                    $"ElevationRangeMode failed: {ex.Message}", ex);
            }

            // Set Min first so Max is always > Min when we assign it
            try
            {
                pv.ElevationMin = eMin;
            }
            catch (System.Exception ex)
            {
                throw new InvalidOperationException(
                    $"ElevationMin={eMin:F2} failed: {ex.Message}", ex);
            }

            try
            {
                pv.ElevationMax = eMax;
            }
            catch (System.Exception ex)
            {
                throw new InvalidOperationException(
                    $"ElevationMax={eMax:F2} failed: {ex.Message}", ex);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        private static ObjectId ResolveLabelSetStyle(
            CivilApp.CivilDocument civDoc, Transaction trans)
        {
            ObjectId fallback = ObjectId.Null;
            foreach (ObjectId id in civDoc.Styles.LabelSetStyles.ProfileLabelSetStyles)
            {
                try
                {
                    var ls = trans.GetObject(id, OpenMode.ForRead)
                             as CivilDB.Styles.ProfileLabelSetStyle;
                    if (ls == null) continue;

                    if (ls.Name.Contains("No Label", StringComparison.OrdinalIgnoreCase))
                        return id;

                    if (fallback.IsNull) fallback = id;
                }
                catch { }
            }
            return fallback;
        }

        /// <summary>
        /// Resolves a valid ProfileViewBandSetStyle ObjectId.
        private static ObjectId ResolveBandSetStyle(
            CivilApp.CivilDocument civDoc, Database db, Transaction trans)
        {
            try
            {
                foreach (ObjectId id in civDoc.Styles.ProfileViewBandSetStyles)
                {
                    // Return the first ID whose object opens successfully.
                    // We don't cast to a specific type – the collection is already
                    // typed to ProfileViewBandSetStyle entries by Civil 3D.
                    try
                    {
                        var obj = trans.GetObject(id, OpenMode.ForRead);
                        if (obj != null) return id;
                    }
                    catch { }
                }
            }
            catch { }
            return ObjectId.Null;
        }
        /// Fallback: returns ANY valid ProfileLabelSetStyle when ResolveLabelSetStyle
        /// returns ObjectId.Null (drawing has no "No Labels" style).
        /// Civil 3D 2026 rejects ObjectId.Null for the labelSetStyleId argument.
        /// </summary>
        private static ObjectId ResolveAnyLabelSetStyle(
            CivilApp.CivilDocument civDoc, Transaction trans)
        {
            foreach (ObjectId id in civDoc.Styles.LabelSetStyles.ProfileLabelSetStyles)
            {
                try
                {
                    var s = trans.GetObject(id, OpenMode.ForRead)
                            as CivilDB.Styles.ProfileLabelSetStyle;
                    if (s != null) return id;
                }
                catch { }
            }
            return ObjectId.Null;  // will produce a clear error downstream
        }

        private static HashSet<string> GetExistingProfileNames(
            CivilDB.Alignment al, Transaction trans)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in al.GetProfileIds())
            {
                try
                {
                    var p = trans.GetObject(id, OpenMode.ForRead) as CivilDB.Profile;
                    if (p != null) names.Add(p.Name);
                }
                catch { }
            }
            return names;
        }

        // ── Existing profile view names – iterate ModelSpace by RXClass ───────
        //
        //  CivilDocument does NOT expose GetProfileViewIds() in Civil 3D 2026.
        //  The correct pattern is to filter the ModelSpace BlockTableRecord
        //  using RXObject.GetClass() for type-safe iteration.
        //
        private static HashSet<string> GetExistingProfileViewNames(
            Database db, Transaction trans)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                RXClass pvClass = RXObject.GetClass(typeof(CivilDB.ProfileView));

                BlockTable bt = (BlockTable)
                    trans.GetObject(db.BlockTableId, OpenMode.ForRead);
                BlockTableRecord msp = (BlockTableRecord)
                    trans.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in msp)
                {
                    if (!id.ObjectClass.IsDerivedFrom(pvClass)) continue;
                    try
                    {
                        var pv = trans.GetObject(id, OpenMode.ForRead)
                                 as CivilDB.ProfileView;
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
