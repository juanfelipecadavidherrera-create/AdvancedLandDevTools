using System;
using System.Collections.Generic;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CivilDB  = Autodesk.Civil.DatabaseServices;
using AcApp    = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AdvancedLandDevTools.Commands.CoverAdjustCommand))]

namespace AdvancedLandDevTools.Commands
{
    /// <summary>
    /// COVERADJUST — Adjusts two pressure pipe fittings so both have at least
    /// 4 ft of cover (crown to surface) and sit at the same PVI elevation.
    ///
    /// Workflow:
    ///   1. Click fitting 1 in the profile view (AECC_GRAPH_PROFILE_PRESSURE_PART proxy).
    ///   2. Click fitting 2.
    ///   3. Command auto-detects the ground-surface elevation at each fitting's
    ///      station by querying the alignment's surface profiles (no click —
    ///      when multiple surface profiles exist, the LOWEST elevation wins so
    ///      the cover check stays conservative).
    ///   4. Command computes target PVI elevation = the deeper of the two
    ///      "shallowest-possible" depths, so both get ≥ 4' of crown cover at
    ///      the same PVI elevation (one may receive more than 4' of cover).
    ///   5. Confirm → both PVIs are moved to that elevation.
    ///
    /// Slope geometry note (consistent with EEEBEND):
    ///   Real-world slope is 1H:1V.  The profile view uses 10× vertical
    ///   exaggeration, so it appears as 1H:10V.  Moving a PVI deeper spreads
    ///   adjacent diagonal legs; moving it shallower brings them closer.
    ///   This command only moves the two selected PVIs — adjacent ones are
    ///   untouched; Civil 3D re-computes the connecting pipe grades automatically.
    /// </summary>
    public class CoverAdjustCommand
    {
        // ── Constants ─────────────────────────────────────────────────────────────
        private const double MinCover        = 4.0;   // ft  — minimum crown cover
        private const string DXF_PRESS_PART = "AECC_GRAPH_PROFILE_PRESSURE_PART";

        // Reflection candidates for resolving proxy → real part (mirrors MarkFittings)
        private static readonly string[] _partIdProps = {
            "ModelPartId", "PartId", "NetworkPartId", "BasePipeId",
            "SourceObjectId", "EntityId", "ComponentObjectId",
            "ReferencedObjectId", "SourceId", "PipeId", "StructureId"
        };
        private static readonly string[] _partIdMethods = {
            "GetPartId", "GetNetworkPartId", "GetSourceId", "GetEntityId"
        };

        // ── Command entry point ────────────────────────────────────────────────────
        [CommandMethod("COVERADJUST", CommandFlags.Modal)]
        public void CoverAdjust()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor   ed  = doc.Editor;
            Database db  = doc.Database;

            try
            {
                if (!Engine.LicenseManager.EnsureLicensed()) return;

                ed.WriteMessage("\n═══════════════════════════════════════════════════════════");
                ed.WriteMessage("\n  Advanced Land Development Tools  |  Cover Adjust");
                ed.WriteMessage("\n  Adjusts two fittings to ≥ 4 ft cover at matching elevation.");
                ed.WriteMessage("\n═══════════════════════════════════════════════════════════");

                // ── Step 1: Fitting 1 ────────────────────────────────────────────
                if (!PickFitting(ed, db, 1,
                        out ObjectId fitting1Id, out ObjectId pv1Id,
                        out double fitting1Z, out double station1, out double radius1,
                        out string  fitting1Name))
                    return;

                // ── Step 2: Fitting 2 ────────────────────────────────────────────
                if (!PickFitting(ed, db, 2,
                        out ObjectId fitting2Id, out ObjectId pv2Id,
                        out double fitting2Z, out double station2, out double radius2,
                        out string  fitting2Name))
                    return;

                // ── Step 3: Auto-detect ground surface at each station ───────────
                if (!GetSurfaceElevation(db, pv1Id, station1, out double surface1, out string surf1Name, ed))
                { ed.WriteMessage("\n  Could not detect surface at fitting 1.\n"); return; }
                ed.WriteMessage($"\n  Surface at fitting 1 (Sta {station1:F2}): {surface1:F3} ft  [from '{surf1Name}']");

                if (!GetSurfaceElevation(db, pv2Id, station2, out double surface2, out string surf2Name, ed))
                { ed.WriteMessage("\n  Could not detect surface at fitting 2.\n"); return; }
                ed.WriteMessage($"\n  Surface at fitting 2 (Sta {station2:F2}): {surface2:F3} ft  [from '{surf2Name}']");

                // ── Step 5: Compute target elevation ─────────────────────────────
                //
                // Cover is measured from pipe CROWN (PVI + outer_radius) to surface.
                // Crown cover ≥ 4 ft  →  PVI ≤ surface − outer_radius − 4
                //
                // maxZ = shallowest PVI that still satisfies 4' crown cover.
                // target = min(maxZ1, maxZ2)  →  the deeper of the two requirements
                //   • The fitting at the shallower surface drives the depth.
                //   • Both land at the same PVI elevation.
                //   • The fitting with more surface room ends up with > 4' cover.

                double crown1 = fitting1Z + radius1;
                double crown2 = fitting2Z + radius2;
                double cover1 = surface1  - crown1;
                double cover2 = surface2  - crown2;

                double maxZ1   = surface1 - radius1 - MinCover;
                double maxZ2   = surface2 - radius2 - MinCover;
                double targetZ = Math.Min(maxZ1, maxZ2);

                double newCrown1 = targetZ + radius1;
                double newCrown2 = targetZ + radius2;
                double newCover1 = surface1 - newCrown1;
                double newCover2 = surface2 - newCrown2;

                bool already1 = Math.Abs(fitting1Z - targetZ) < 0.001;
                bool already2 = Math.Abs(fitting2Z - targetZ) < 0.001;

                // ── Step 6: Report ────────────────────────────────────────────────
                ed.WriteMessage("\n");
                ed.WriteMessage("\n  ╔══════════════════════════════════════════════════════════╗");
                ed.WriteMessage("\n  ║          COVER ADJUST — GEOMETRY REPORT                 ║");
                ed.WriteMessage("\n  ╠══════════════════════════════════════════════════════════╣");
                ed.WriteMessage($"\n  ║  Fitting 1: {fitting1Name,-17}  Sta {station1,8:F2}          ║");
                ed.WriteMessage($"\n  ║    Surface          : {surface1,8:F3} ft                    ║");
                ed.WriteMessage($"\n  ║    Current PVI      : {fitting1Z,8:F3} ft  cover {cover1,6:F3} ft  ║");
                ed.WriteMessage($"\n  ║    New PVI          : {targetZ,8:F3} ft  cover {newCover1,6:F3} ft  ║");
                ed.WriteMessage("\n  ╠══════════════════════════════════════════════════════════╣");
                ed.WriteMessage($"\n  ║  Fitting 2: {fitting2Name,-17}  Sta {station2,8:F2}          ║");
                ed.WriteMessage($"\n  ║    Surface          : {surface2,8:F3} ft                    ║");
                ed.WriteMessage($"\n  ║    Current PVI      : {fitting2Z,8:F3} ft  cover {cover2,6:F3} ft  ║");
                ed.WriteMessage($"\n  ║    New PVI          : {targetZ,8:F3} ft  cover {newCover2,6:F3} ft  ║");
                ed.WriteMessage("\n  ╚══════════════════════════════════════════════════════════╝");

                if (newCover1 < MinCover - 0.01 || newCover2 < MinCover - 0.01)
                    ed.WriteMessage(
                        "\n  ⚠ WARNING: Computed cover is below 4 ft — check surface profile data.");

                if (already1 && already2)
                {
                    ed.WriteMessage(
                        "\n  Both fittings are already at the target elevation — no changes needed.\n");
                    return;
                }

                // ── Step 7: Confirm ───────────────────────────────────────────────
                var kwdOpts = new PromptKeywordOptions(
                    "\n  Apply cover adjustment? [Yes/No] <Yes>: ");
                kwdOpts.Keywords.Add("Yes");
                kwdOpts.Keywords.Add("No");
                kwdOpts.AllowNone = true;
                var kwdRes = ed.GetKeywords(kwdOpts);
                if (kwdRes.Status == PromptStatus.Cancel ||
                    (kwdRes.Status == PromptStatus.OK && kwdRes.StringResult == "No"))
                {
                    ed.WriteMessage("\n  Cancelled.\n");
                    return;
                }

                // ── Step 8: Apply ─────────────────────────────────────────────────
                int applied = 0;
                using (var tx = db.TransactionManager.StartTransaction())
                {
                    if (!already1 && MoveFittingPVI(tx, db, fitting1Id, station1, fitting1Z, targetZ, ed))
                        applied++;
                    if (!already2 && MoveFittingPVI(tx, db, fitting2Id, station2, fitting2Z, targetZ, ed))
                        applied++;
                    tx.Commit();
                }

                int needed = (already1 ? 0 : 1) + (already2 ? 0 : 1);
                ed.WriteMessage(applied == needed
                    ? $"\n  ✓ Done — {applied} fitting(s) moved to PVI {targetZ:F3} ft."
                    : $"\n  ⚠ Applied {applied}/{needed} — see warnings above.");
                ed.WriteMessage("\n═══════════════════════════════════════════════════════════\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[ALDT ERROR] COVERADJUST: {ex.Message}\n");
            }
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Pick a fitting proxy in the profile view and resolve it.
        //  Returns false and prints a message on failure.
        // ═════════════════════════════════════════════════════════════════════════
        private static bool PickFitting(
            Editor   ed,
            Database db,
            int      number,
            out ObjectId fittingId,
            out ObjectId pvId,
            out double   fittingZ,
            out double   station,
            out double   radius,
            out string   name)
        {
            fittingId = ObjectId.Null;
            pvId      = ObjectId.Null;
            fittingZ  = 0;
            station   = 0;
            radius    = 0;
            name      = "";

            var peo = new PromptEntityOptions(
                $"\n  Click fitting {number} (fitting symbol in the profile view): ");
            peo.AllowObjectOnLockedLayer = true;
            var per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
            { ed.WriteMessage("\n  Cancelled.\n"); return false; }

            string dxf = per.ObjectId.ObjectClass.DxfName;
            if (dxf != DXF_PRESS_PART)
            {
                ed.WriteMessage(
                    $"\n  Entity type '{dxf}' is not a pressure fitting proxy.\n" +
                    "  Click the fitting symbol (ellipse/diamond) drawn inside the profile view.\n");
                return false;
            }

            using (var tx = db.TransactionManager.StartTransaction())
            {
                var proxy = tx.GetObject(per.ObjectId, OpenMode.ForRead);

                ObjectId partId = ResolvePartId(proxy);
                if (partId.IsNull)
                {
                    ed.WriteMessage("\n  Could not resolve fitting from profile proxy.\n");
                    tx.Abort();
                    return false;
                }

                var part = tx.GetObject(partId, OpenMode.ForRead);
                var fitting = part as CivilDB.PressureFitting;
                if (fitting == null)
                {
                    ed.WriteMessage(
                        $"\n  Resolved part is '{part.GetType().Name}' — expected PressureFitting.\n" +
                        "  Click a fitting (bend, tee, coupling), not a pipe.\n");
                    tx.Abort();
                    return false;
                }

                // Profile view context
                var pv = FindProfileViewAtPoint(per.PickedPoint, tx, db);
                if (pv == null)
                {
                    ed.WriteMessage("\n  Cannot find profile view at that click location.\n");
                    tx.Abort();
                    return false;
                }

                var aln = tx.GetObject(pv.AlignmentId, OpenMode.ForRead) as CivilDB.Alignment;
                if (aln == null)
                {
                    ed.WriteMessage("\n  Cannot open alignment for profile view.\n");
                    tx.Abort();
                    return false;
                }

                double sta = 0, offset = 0;
                aln.StationOffset(fitting.Position.X, fitting.Position.Y, ref sta, ref offset);

                // Outer radius for crown calculation
                double r = GetFittingRadius(fitting, tx);

                string fName = "";
                try { fName = fitting.Name; } catch { }
                if (string.IsNullOrEmpty(fName))
                    try { fName = fitting.PartDescription; } catch { }
                if (string.IsNullOrEmpty(fName)) fName = "fitting";

                ed.WriteMessage(
                    $"\n  Fitting {number}: '{fName}'  " +
                    $"Sta {sta:F2}  PVI {fitting.Position.Z:F3} ft  " +
                    $"Radius {r:F3} ft");

                fittingId = partId;
                pvId      = pv.ObjectId;
                fittingZ  = fitting.Position.Z;
                station   = sta;
                radius    = r;
                name      = fName;

                tx.Abort();
                return true;
            }
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Auto-detect the ground-surface elevation at a given alignment station
        //  by iterating the alignment's profiles and querying ElevationAt(sta).
        //
        //  Strategy:
        //   • Iterate every Profile on the alignment.
        //   • Try ElevationAt(station); skip if it fails or is non-finite.
        //   • Classify each profile as "surface-like" when it's tied to a TIN
        //     (SurfaceId not null / ProfileType mentions EG/Existing/Surface).
        //   • Among surface-like profiles, pick the LOWEST elevation
        //     (most restrictive for cover).
        //   • If none are surface-like, fall back to the LOWEST elevation
        //     across all profiles (still the conservative pick).
        // ═════════════════════════════════════════════════════════════════════════
        private static bool GetSurfaceElevation(
            Database db,
            ObjectId pvId,
            double   station,
            out double surfaceElevation,
            out string profileName,
            Editor   ed)
        {
            surfaceElevation = double.NaN;
            profileName      = "";

            using (var tx = db.TransactionManager.StartTransaction())
            {
                var pv = tx.GetObject(pvId, OpenMode.ForRead) as CivilDB.ProfileView;
                if (pv == null) { tx.Abort(); return false; }

                var aln = tx.GetObject(pv.AlignmentId, OpenMode.ForRead) as CivilDB.Alignment;
                if (aln == null) { tx.Abort(); return false; }

                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                double bestSurfaceZ = double.PositiveInfinity;
                string bestSurfaceName = "";
                double bestAnyZ       = double.PositiveInfinity;
                string bestAnyName    = "";

                foreach (ObjectId pid in aln.GetProfileIds())
                {
                    CivilDB.Profile? prof = null;
                    try { prof = tx.GetObject(pid, OpenMode.ForRead) as CivilDB.Profile; }
                    catch { continue; }
                    if (prof == null) continue;

                    double z;
                    try { z = prof.ElevationAt(station); }
                    catch { continue; }
                    if (double.IsNaN(z) || double.IsInfinity(z)) continue;

                    string pname = "";
                    try { pname = prof.Name ?? ""; } catch { }

                    // Track lowest among all profiles (fallback)
                    if (z < bestAnyZ) { bestAnyZ = z; bestAnyName = pname; }

                    // Detect surface-like profiles
                    bool isSurface = false;
                    try
                    {
                        var sidProp = prof.GetType().GetProperty("SurfaceId", flags);
                        if (sidProp != null)
                        {
                            var sid = (ObjectId)sidProp.GetValue(prof)!;
                            if (!sid.IsNull) isSurface = true;
                        }
                    }
                    catch { }
                    if (!isSurface)
                    {
                        try
                        {
                            var ptProp = prof.GetType().GetProperty("ProfileType", flags);
                            if (ptProp != null)
                            {
                                string ptStr = ptProp.GetValue(prof)?.ToString() ?? "";
                                string pu = ptStr.ToUpperInvariant();
                                if (pu.Contains("EG") || pu.Contains("EXISTING") ||
                                    pu.Contains("SURFACE") || pu.Contains("GROUND"))
                                    isSurface = true;
                            }
                        }
                        catch { }
                    }
                    if (!isSurface)
                    {
                        string nu = pname.ToUpperInvariant();
                        if (nu.Contains("EG") || nu.Contains("EXISTING") ||
                            nu.Contains("SURFACE") || nu.Contains("GROUND"))
                            isSurface = true;
                    }

                    if (isSurface && z < bestSurfaceZ)
                    { bestSurfaceZ = z; bestSurfaceName = pname; }
                }

                tx.Abort();

                if (!double.IsPositiveInfinity(bestSurfaceZ))
                {
                    surfaceElevation = bestSurfaceZ;
                    profileName      = string.IsNullOrEmpty(bestSurfaceName) ? "surface profile" : bestSurfaceName;
                    return true;
                }
                if (!double.IsPositiveInfinity(bestAnyZ))
                {
                    surfaceElevation = bestAnyZ;
                    profileName      = string.IsNullOrEmpty(bestAnyName) ? "profile (fallback)" : bestAnyName + " (fallback)";
                    return true;
                }
                return false;
            }
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Move the PVI on the PressurePipeRun that owns the given fitting.
        //  Strategy (tried in order):
        //    1. Reflection — look for a RemoveVerticalBend* method, then re-add.
        //    2. Reflection — find a PVI collection and set Elevation directly.
        //  Both attempts are wrapped in try/catch so failures degrade gracefully.
        // ═════════════════════════════════════════════════════════════════════════
        private static bool MoveFittingPVI(
            Transaction tx,
            Database    db,
            ObjectId    fittingId,
            double      station,
            double      currentZ,
            double      newZ,
            Editor      ed)
        {
            try
            {
                var fitting = tx.GetObject(fittingId, OpenMode.ForRead) as CivilDB.PressureFitting;
                if (fitting == null) return false;

                if (fitting.NetworkId.IsNull)
                {
                    ed.WriteMessage("\n  Fitting has no network — cannot modify PVI.");
                    return false;
                }

                var network = tx.GetObject(fitting.NetworkId, OpenMode.ForWrite)
                              as CivilDB.PressurePipeNetwork;
                if (network == null)
                {
                    ed.WriteMessage("\n  Cannot open PressurePipeNetwork.");
                    return false;
                }

                // Find the run that owns this fitting
                CivilDB.PressurePipeRun? targetRun = null;
                foreach (CivilDB.PressurePipeRun run in network.PipeRuns)
                {
                    try
                    {
                        if (run.GetPartIds().Contains(fittingId))
                        { targetRun = run; break; }
                    }
                    catch { }
                }

                if (targetRun == null)
                {
                    ed.WriteMessage(
                        "\n  Fitting is not part of any PressurePipeRun. " +
                        "The fitting must belong to a run for PVI editing.");
                    return false;
                }

                bool ok = TryMovePVI(targetRun, station, currentZ, newZ, ed);
                if (!ok)
                {
                    ed.WriteMessage(
                        $"\n  ⚠ Automatic PVI move failed for Sta {station:F2}." +
                        $"\n    Manually set the PVI at Sta {station:F2} to elevation {newZ:F3} ft.");
                }
                return ok;
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n  PVI move error at Sta {station:F2}: {ex.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  TryMovePVI: remove existing PVI at (station, currentZ) and re-add at
        //  (station, newZ).  Civil 3D's PressurePipeRun exposes both calls as
        //  (Double station, Double elevation) — identical pair of parameters.
        // ─────────────────────────────────────────────────────────────────────────
        private static bool TryMovePVI(
            CivilDB.PressurePipeRun run,
            double station,
            double currentZ,
            double newZ,
            Editor ed)
        {
            try
            {
                run.RemoveVerticalBendByPVI(station, currentZ);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n  RemoveVerticalBendByPVI failed at Sta {station:F2}, Z {currentZ:F3}: {ex.Message}");
                return false;
            }

            try
            {
                run.AddVerticalBendByPVI(station, newZ);
                ed.WriteMessage($"\n  PVI Sta {station:F2}: {currentZ:F3} → {newZ:F3} ft");
                return true;
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n  AddVerticalBendByPVI failed at Sta {station:F2}, Z {newZ:F3}: {ex.Message}");
                // Try to restore the old PVI so we don't leave the run broken
                try { run.AddVerticalBendByPVI(station, currentZ); } catch { }
                return false;
            }
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Get the outer radius for crown-cover calculation.
        //  Tries fitting.OuterDiameter / 2, then falls back to the first pipe
        //  in the same network.  Returns 0 (cover measured to centerline) if
        //  neither succeeds.
        // ═════════════════════════════════════════════════════════════════════════
        private static double GetFittingRadius(CivilDB.PressureFitting fitting, Transaction tx)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            // Try OuterDiameter directly on the fitting
            foreach (string prop in new[] { "OuterDiameter", "NominalDiameter", "PartDiameter" })
            {
                try
                {
                    var p = fitting.GetType().GetProperty(prop, flags);
                    if (p != null)
                    {
                        double d = Convert.ToDouble(p.GetValue(fitting));
                        if (d > 0) return d / 2.0;
                    }
                }
                catch { }
            }

            // Fall back: find a connected pipe and use its outer radius
            try
            {
                if (!fitting.NetworkId.IsNull)
                {
                    var net = tx.GetObject(fitting.NetworkId, OpenMode.ForRead)
                              as CivilDB.PressurePipeNetwork;
                    if (net != null)
                    {
                        foreach (CivilDB.PressurePipeRun run in net.PipeRuns)
                        {
                            foreach (ObjectId id in run.GetPartIds())
                            {
                                try
                                {
                                    var pipe = tx.GetObject(id, OpenMode.ForRead)
                                               as CivilDB.PressurePipe;
                                    if (pipe != null && pipe.OuterDiameter > 0)
                                        return pipe.OuterDiameter / 2.0;
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch { }

            return 0.0;   // caller treats cover as measured to PVI centerline
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Resolve the underlying Civil 3D object from a profile-view graph proxy.
        //  Mirrors MarkFittingsCommand.ResolvePartId.
        // ═════════════════════════════════════════════════════════════════════════
        private static ObjectId ResolvePartId(DBObject proxy)
        {
            var type  = proxy.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (string name in _partIdProps)
            {
                try
                {
                    var p = type.GetProperty(name, flags);
                    if (p?.PropertyType == typeof(ObjectId))
                    {
                        var val = (ObjectId)p.GetValue(proxy)!;
                        if (!val.IsNull) return val;
                    }
                }
                catch { }
            }

            foreach (string name in _partIdMethods)
            {
                try
                {
                    var m = type.GetMethod(name, flags, null, Type.EmptyTypes, null);
                    if (m?.ReturnType == typeof(ObjectId))
                    {
                        var val = (ObjectId)m.Invoke(proxy, null)!;
                        if (!val.IsNull) return val;
                    }
                }
                catch { }
            }

            return ObjectId.Null;
        }

        // ═════════════════════════════════════════════════════════════════════════
        //  Return the first ProfileView whose grid contains the given world point.
        //  (Identical helper used in EeeBend, MarkFittings, LLabelGen.)
        // ═════════════════════════════════════════════════════════════════════════
        private static CivilDB.ProfileView? FindProfileViewAtPoint(
            Point3d pt, Transaction tx, Database db)
        {
            var pvClass = RXObject.GetClass(typeof(CivilDB.ProfileView));
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
                    if (pv.FindStationAndElevationAtXY(pt.X, pt.Y, ref sta, ref elev))
                        return pv;
                }
                catch { }
            }
            return null;
        }
    }
}
