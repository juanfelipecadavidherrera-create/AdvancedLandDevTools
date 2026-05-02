using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivilDB = Autodesk.Civil.DatabaseServices;
using AcApp   = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AdvancedLandDevTools.Engine
{
    // ─────────────────────────────────────────────────────────────────────────
    //  One pressure network grouping for the pre-label picker.
    // ─────────────────────────────────────────────────────────────────────────
    public class PressureNetworkGroup
    {
        public string         NetworkName { get; set; } = "";
        public ObjectId       NetworkId   { get; set; }
        public List<ObjectId> PipeIds     { get; set; } = new();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  PipePvLabelEngine
    //
    //  Mirrors LLabelGenEngine for the "drawn-in-PV pressure pipe endpoints"
    //  case used by PIPEPVLABEL.  Two responsibilities:
    //
    //   1. FindPressurePipesInPv → walk the AECC_GRAPH_PROFILE_PRESSURE_PART
    //      proxies owned by the chosen PV and group their underlying
    //      PressurePipe IDs by parent PressurePipeNetwork.
    //
    //   2. ComputeEndpointLabels(InXref) → for the selected network's pipes,
    //      project each pipe's StartPoint/EndPoint onto the alignment to get a
    //      station, clip to the PV station window, and build a
    //      CrossingLabelPoint at crown elevation (centerline Z + outer radius)
    //      so LLabelGenEngine.QueueLabelCommand can place the labels exactly
    //      the same way LLABELGEN does.
    // ═══════════════════════════════════════════════════════════════════════════
    public static class PipePvLabelEngine
    {
        private const string DXF_PRESSURE_PART = "AECC_GRAPH_PROFILE_PRESSURE_PART";

        // Drop each label this far below the pipe crown (drawing units — feet for US).
        private const double LABEL_Y_DROP = 0.4375;

        // Skip pipes whose absolute slope exceeds this fraction (0.80 = 80%).
        private const double MAX_SLOPE_ABS = 0.80;

        private static readonly string[] _partIdProps = {
            "ModelPartId", "PartId", "NetworkPartId", "BasePipeId",
            "SourceObjectId", "EntityId", "CrossingPipeId",
            "ComponentObjectId", "ReferencedObjectId", "SourceId",
            "PipeId", "StructureId"
        };

        // Tolerance for "this pipe runs along the PV's alignment".  The network
        // walk uses this to decide whether a pipe in the chosen network counts
        // as "drawn in" this PV when the proxy scan missed it.
        private const double ALIGN_OFFSET_TOL = 100.0;

        // ─────────────────────────────────────────────────────────────────────
        //  Native PV: scan the host DB for pressure-graph proxies owned by pvId.
        //  Returns one PressureNetworkGroup per distinct parent network.
        // ─────────────────────────────────────────────────────────────────────
        public static Dictionary<ObjectId, PressureNetworkGroup> FindPressurePipesInPv(
            ObjectId pvId, Database db)
        {
            var result = new Dictionary<ObjectId, PressureNetworkGroup>();

            try
            {
                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var pv = tx.GetObject(pvId, OpenMode.ForRead) as CivilDB.ProfileView;
                    if (pv == null) { tx.Abort(); return result; }

                    CollectFromDb(pv, db, tx, result);

                    // Network walk — catches pipes that have no proxy entity
                    // (Civil 3D skips proxy creation for some pipe-to-pipe joins).
                    var align = tx.GetObject(pv.AlignmentId, OpenMode.ForRead)
                                as CivilDB.Alignment;
                    if (align != null)
                        AugmentWithNetworkWalk(pv, align, tx, result);

                    tx.Abort();
                }
            }
            catch { }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  XREF PV: open the XREF DB, find the PV by name, scan its graph parts.
        //  Pipes are searched ONLY in the XREF DB — matching LLABELGEN's policy.
        // ─────────────────────────────────────────────────────────────────────
        public static Dictionary<ObjectId, PressureNetworkGroup> FindPressurePipesInPvXref(
            Database xrefDb, string pvName)
        {
            var result = new Dictionary<ObjectId, PressureNetworkGroup>();

            try
            {
                using (var xTx = xrefDb.TransactionManager.StartTransaction())
                {
                    var pv = FindPvByName(xrefDb, xTx, pvName);
                    if (pv == null) { xTx.Abort(); return result; }

                    CollectFromDb(pv, xrefDb, xTx, result);

                    // Network walk — catches pipes that have no proxy entity.
                    var align = xTx.GetObject(pv.AlignmentId, OpenMode.ForRead)
                                as CivilDB.Alignment;
                    if (align != null)
                        AugmentWithNetworkWalk(pv, align, xTx, result);

                    xTx.Abort();
                }
            }
            catch { }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Endpoint label computation — native PV.
        // ─────────────────────────────────────────────────────────────────────
        public static List<CrossingLabelPoint> ComputeEndpointLabels(
            ObjectId pvId, Database db, List<ObjectId> pipeIds)
        {
            var pts = new List<CrossingLabelPoint>();

            try
            {
                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var pv = tx.GetObject(pvId, OpenMode.ForRead) as CivilDB.ProfileView;
                    if (pv == null) { tx.Abort(); return pts; }

                    var align = tx.GetObject(pv.AlignmentId, OpenMode.ForRead) as CivilDB.Alignment;
                    if (align == null) { tx.Abort(); return pts; }

                    CollectEndpoints(pv, align, tx, pipeIds, Matrix3d.Identity, pts);
                    tx.Abort();
                }
            }
            catch { }

            return pts;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Endpoint label computation — XREF PV.
        // ─────────────────────────────────────────────────────────────────────
        public static List<CrossingLabelPoint> ComputeEndpointLabelsInXref(
            Database xrefDb, Matrix3d xrefToHost, string pvName, List<ObjectId> pipeIds)
        {
            var pts = new List<CrossingLabelPoint>();

            try
            {
                using (var xTx = xrefDb.TransactionManager.StartTransaction())
                {
                    var pv = FindPvByName(xrefDb, xTx, pvName);
                    if (pv == null) { xTx.Abort(); return pts; }

                    var align = xTx.GetObject(pv.AlignmentId, OpenMode.ForRead) as CivilDB.Alignment;
                    if (align == null) { xTx.Abort(); return pts; }

                    CollectEndpoints(pv, align, xTx, pipeIds, xrefToHost, pts);
                    xTx.Abort();
                }
            }
            catch { }

            return pts;
        }

        // ════════════════════════════════════════════════════════════════════
        //  Internals
        // ════════════════════════════════════════════════════════════════════

        private static CivilDB.ProfileView? FindPvByName(
            Database db, Transaction tx, string pvName)
        {
            var ms = tx.GetObject(db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
            if (ms == null) return null;

            foreach (ObjectId id in ms)
            {
                CivilDB.ProfileView? pv;
                try { pv = tx.GetObject(id, OpenMode.ForRead) as CivilDB.ProfileView; }
                catch { continue; }
                if (pv == null) continue;

                if (pv.Name.Equals(pvName, StringComparison.OrdinalIgnoreCase))
                    return pv;
            }
            return null;
        }

        // Walk all pressure-graph proxies in the same model space and keep the
        // ones whose OwnerId is this PV.  Resolve each proxy to the underlying
        // PressurePipe ObjectId and group by parent network.  Fittings and
        // appurtenances are skipped — only PressurePipe instances qualify.
        private static void CollectFromDb(
            CivilDB.ProfileView pv, Database db, Transaction tx,
            Dictionary<ObjectId, PressureNetworkGroup> result)
        {
            var ms = tx.GetObject(db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
            if (ms == null) return;

            var seen = new HashSet<ObjectId>();

            foreach (ObjectId entId in ms)
            {
                try
                {
                    if (entId.ObjectClass.DxfName != DXF_PRESSURE_PART) continue;

                    var proxy = tx.GetObject(entId, OpenMode.ForRead);

                    // Owner check — only proxies owned by THIS profile view.
                    // If OwnerId is unset for some reason, fall back to extents.
                    bool owned = false;
                    try { owned = (proxy.OwnerId == pv.ObjectId); } catch { }
                    if (!owned)
                    {
                        try
                        {
                            var pvExt    = ((Entity)pv).GeometricExtents;
                            var proxyExt = ((Entity)proxy).GeometricExtents;
                            var c        = new Point3d(
                                (proxyExt.MinPoint.X + proxyExt.MaxPoint.X) * 0.5,
                                (proxyExt.MinPoint.Y + proxyExt.MaxPoint.Y) * 0.5, 0);
                            owned = c.X >= pvExt.MinPoint.X && c.X <= pvExt.MaxPoint.X
                                 && c.Y >= pvExt.MinPoint.Y && c.Y <= pvExt.MaxPoint.Y;
                        }
                        catch { }
                    }
                    if (!owned) continue;

                    ObjectId partId = ResolvePartId(proxy);
                    if (partId.IsNull || seen.Contains(partId)) continue;

                    var part = tx.GetObject(partId, OpenMode.ForRead);
                    if (part is not CivilDB.PressurePipe pp) continue;   // skip fittings/appurtenances
                    seen.Add(partId);

                    ObjectId netId = ObjectId.Null;
                    try { netId = pp.NetworkId; } catch { }

                    if (!result.TryGetValue(netId, out var grp))
                    {
                        string netName = "(unknown network)";
                        if (!netId.IsNull)
                        {
                            try
                            {
                                var net = tx.GetObject(netId, OpenMode.ForRead)
                                          as CivilDB.PressurePipeNetwork;
                                if (net != null) netName = net.Name;
                            }
                            catch { }
                        }
                        grp = new PressureNetworkGroup
                        {
                            NetworkId   = netId,
                            NetworkName = netName
                        };
                        result[netId] = grp;
                    }
                    grp.PipeIds.Add(partId);
                }
                catch { }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Network walk — fills in pipes the proxy scan missed.
        //
        //  For every network already discovered by the proxy pass, enumerate
        //  ALL pipes in that network and add the ones whose endpoints both
        //  project onto this PV's alignment with a small offset and whose
        //  station range overlaps the PV's station window.  Civil 3D does not
        //  always emit an AECC_GRAPH_PROFILE_PRESSURE_PART proxy for pipes
        //  that join other pipes directly (no fitting between them) — those
        //  pipes are visible in the PV but invisible to the proxy scan.
        // ─────────────────────────────────────────────────────────────────────
        private static void AugmentWithNetworkWalk(
            CivilDB.ProfileView pv, CivilDB.Alignment align,
            Transaction tx,
            Dictionary<ObjectId, PressureNetworkGroup> result)
        {
            double pvStaStart = pv.StationStart;
            double pvStaEnd   = pv.StationEnd;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            // Snapshot keys — we modify the inner PipeIds lists during the loop.
            foreach (var kvp in result.ToList())
            {
                ObjectId networkId = kvp.Key;
                var      grp       = kvp.Value;
                if (networkId.IsNull) continue;

                var seen  = new HashSet<ObjectId>(grp.PipeIds);
                int added = 0;

                try
                {
                    var net = tx.GetObject(networkId, OpenMode.ForRead);
                    if (net == null) continue;

                    var getIds = net.GetType()
                        .GetMethod("GetPressurePipeIds", flags, null, Type.EmptyTypes, null);
                    if (getIds?.Invoke(net, null) is not System.Collections.IEnumerable allIds)
                        continue;

                    foreach (var item in allIds)
                    {
                        if (item is not ObjectId nid) continue;
                        if (seen.Contains(nid)) continue;

                        try
                        {
                            var pp = tx.GetObject(nid, OpenMode.ForRead) as CivilDB.PressurePipe;
                            if (pp == null) continue;

                            Point3d sp = default, ep = default;
                            try { sp = pp.StartPoint; } catch { continue; }
                            try { ep = pp.EndPoint;   } catch { continue; }

                            double sta_s = 0, off_s = 0, sta_e = 0, off_e = 0;
                            try
                            {
                                align.StationOffset(sp.X, sp.Y, ref sta_s, ref off_s);
                                align.StationOffset(ep.X, ep.Y, ref sta_e, ref off_e);
                            }
                            catch { continue; }

                            // Reject pipes that don't run along this alignment.
                            if (Math.Abs(off_s) > ALIGN_OFFSET_TOL ||
                                Math.Abs(off_e) > ALIGN_OFFSET_TOL) continue;

                            // Station range must overlap the PV window (±0.5).
                            double minSta = Math.Min(sta_s, sta_e);
                            double maxSta = Math.Max(sta_s, sta_e);
                            if (maxSta < pvStaStart - 0.5 || minSta > pvStaEnd + 0.5) continue;

                            grp.PipeIds.Add(nid);
                            seen.Add(nid);
                            added++;
                        }
                        catch { }
                    }
                }
                catch { }

                if (added > 0)
                    LogDiag(
                        $"  [PIPEPVLABEL] network walk: '{grp.NetworkName}' " +
                        $"+{added} along-alignment pipe(s) missed by proxy scan.");
            }
        }

        private static ObjectId ResolvePartId(DBObject proxy)
        {
            var type  = proxy.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (string name in _partIdProps)
            {
                try
                {
                    var prop = type.GetProperty(name, flags);
                    if (prop?.PropertyType == typeof(ObjectId))
                    {
                        var val = (ObjectId)prop.GetValue(proxy)!;
                        if (!val.IsNull) return val;
                    }
                }
                catch { }
            }
            return ObjectId.Null;
        }

        private static void CollectEndpoints(
            CivilDB.ProfileView pv, CivilDB.Alignment align,
            Transaction tx, List<ObjectId> pipeIds,
            Matrix3d entityToHostWCS,
            List<CrossingLabelPoint> pts)
        {
            double pvStaStart = pv.StationStart;
            double pvStaEnd   = pv.StationEnd;

            string netName = "";
            try
            {
                if (pipeIds.Count > 0)
                {
                    var firstPipe = tx.GetObject(pipeIds[0], OpenMode.ForRead)
                                    as CivilDB.PressurePipe;
                    if (firstPipe != null)
                    {
                        var net = tx.GetObject(firstPipe.NetworkId, OpenMode.ForRead)
                                  as CivilDB.PressurePipeNetwork;
                        if (net != null) netName = net.Name;
                    }
                }
            }
            catch { }

            int idx = 0, processed = 0, skippedSlope = 0, skippedOther = 0;
            int labelsBefore = pts.Count;

            foreach (ObjectId pid in pipeIds)
            {
                idx++;
                // Self-contained — guaranteed not to break the outer loop.
                ProcessOnePipe(
                    pid, idx, pv, align,
                    pvStaStart, pvStaEnd,
                    entityToHostWCS, netName, tx, pts,
                    ref processed, ref skippedSlope, ref skippedOther);
            }

            int labeled = pts.Count - labelsBefore;
            LogDiag(
                $"  [PIPEPVLABEL] summary: {pipeIds.Count} pipe(s) total — " +
                $"processed {processed}, skipped(slope>{MAX_SLOPE_ABS * 100.0:F0}%) {skippedSlope}, " +
                $"skipped(other) {skippedOther}, label points {labeled}.");
        }

        /// <summary>
        /// Process exactly one pipe.  Catches every exception internally so the
        /// outer loop in CollectEndpoints can never be derailed by a bad pipe.
        /// </summary>
        private static void ProcessOnePipe(
            ObjectId pid, int idx,
            CivilDB.ProfileView pv, CivilDB.Alignment align,
            double pvStaStart, double pvStaEnd,
            Matrix3d entityToHostWCS, string netName,
            Transaction tx, List<CrossingLabelPoint> pts,
            ref int processed, ref int skippedSlope, ref int skippedOther)
        {
            try
            {
                CivilDB.PressurePipe? pp = null;
                try { pp = tx.GetObject(pid, OpenMode.ForRead) as CivilDB.PressurePipe; }
                catch (Exception ex)
                {
                    LogDiag($"  [PIPEPVLABEL] pipe #{idx} (h={pid.Handle}): GetObject threw {ex.GetType().Name} — skipping");
                    skippedOther++;
                    return;
                }
                if (pp == null)
                {
                    LogDiag($"  [PIPEPVLABEL] pipe #{idx} (h={pid.Handle}): not a PressurePipe — skipping");
                    skippedOther++;
                    return;
                }

                // Endpoints — read each independently, never let a throw break us.
                Point3d sp = default, ep = default;
                bool gotSp = false, gotEp = false;
                try { sp = pp.StartPoint; gotSp = true; } catch { }
                try { ep = pp.EndPoint;   gotEp = true; } catch { }
                if (!gotSp || !gotEp)
                {
                    LogDiag($"  [PIPEPVLABEL] pipe '{SafeName(pp)}' (#{idx}): could not read endpoints — skipping");
                    skippedOther++;
                    return;
                }

                // Slope as the profile view actually displays it: rise (Δelev) over
                // run-along-alignment (Δstation), not 3D XY distance.  This matches
                // what you read off the PV and avoids false positives for pipes that
                // sit slightly off-axis.
                double sta_s = 0, off_s = 0, sta_e = 0, off_e = 0;
                bool gotSta = true;
                try { align.StationOffset(sp.X, sp.Y, ref sta_s, ref off_s); }
                catch { gotSta = false; }
                try { align.StationOffset(ep.X, ep.Y, ref sta_e, ref off_e); }
                catch { gotSta = false; }

                if (!gotSta)
                {
                    LogDiag($"  [PIPEPVLABEL] pipe '{SafeName(pp)}' (#{idx}): could not project to alignment — skipping");
                    skippedOther++;
                    return;
                }

                double staRun = Math.Abs(sta_e - sta_s);
                double dz     = ep.Z - sp.Z;
                if (staRun < 1e-6)
                {
                    LogDiag($"  [PIPEPVLABEL] pipe '{SafeName(pp)}' (#{idx}): zero alignment-station run — skipping");
                    skippedOther++;
                    return;
                }
                double slope = dz / staRun;
                if (Math.Abs(slope) > MAX_SLOPE_ABS)
                {
                    LogDiag($"  [PIPEPVLABEL] pipe '{SafeName(pp)}' (#{idx}): slope {slope * 100.0:F1}% > {MAX_SLOPE_ABS * 100.0:F0}% — skipped");
                    skippedSlope++;
                    return;
                }

                processed++;

                double crownOffset = 0.0;
                try { crownOffset = GetCrownOffset(pp); } catch { }

                // Each endpoint is independent — failures don't affect the other.
                try
                {
                    AddEndpointLabel(pv, align, pp, sp, crownOffset,
                        pvStaStart, pvStaEnd, entityToHostWCS, netName, pts);
                }
                catch (Exception ex)
                {
                    LogDiag($"  [PIPEPVLABEL] pipe '{SafeName(pp)}' (#{idx}) start: {ex.GetType().Name} — endpoint dropped");
                }

                try
                {
                    AddEndpointLabel(pv, align, pp, ep, crownOffset,
                        pvStaStart, pvStaEnd, entityToHostWCS, netName, pts);
                }
                catch (Exception ex)
                {
                    LogDiag($"  [PIPEPVLABEL] pipe '{SafeName(pp)}' (#{idx}) end: {ex.GetType().Name} — endpoint dropped");
                }
            }
            catch (Exception ex)
            {
                // Last-resort safety net — should never fire, but if it does we
                // log and move on rather than swallowing silently.
                LogDiag($"  [PIPEPVLABEL] pipe #{idx} (h={pid.Handle}): unexpected {ex.GetType().Name}: {ex.Message}");
                skippedOther++;
            }
        }

        // Crown = centerline Z + outer radius.  Try OuterDiameter / OutsideDiameter
        // properties — fall back to InnerDiameter, then 0.
        // Best-effort name read — never throws.
        private static string SafeName(CivilDB.PressurePipe pp)
        {
            try { return pp.Name ?? "(unnamed)"; }
            catch { return "(unnamed)"; }
        }

        // Diagnostic line emitted to the AutoCAD command line.  No-ops if no
        // active document exists (e.g. during plugin load).
        private static void LogDiag(string msg)
        {
            try
            {
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage("\n" + msg);
            }
            catch { }
        }

        private static double GetCrownOffset(CivilDB.PressurePipe pp)
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (string name in new[] { "OuterDiameter", "OutsideDiameter" })
            {
                try
                {
                    var prop = pp.GetType().GetProperty(name, flags);
                    if (prop?.PropertyType == typeof(double))
                    {
                        double d = (double)prop.GetValue(pp)!;
                        if (d > 0) return d * 0.5;
                    }
                }
                catch { }
            }
            try
            {
                double inner = pp.InnerDiameter;
                if (inner > 0) return inner * 0.5;
            }
            catch { }
            return 0.0;
        }

        private static void AddEndpointLabel(
            CivilDB.ProfileView pv, CivilDB.Alignment align,
            CivilDB.PressurePipe pp, Point3d endPt, double crownOffset,
            double pvStaStart, double pvStaEnd,
            Matrix3d entityToHostWCS, string netName,
            List<CrossingLabelPoint> pts)
        {
            try
            {
                double sta = 0, off = 0;
                try { align.StationOffset(endPt.X, endPt.Y, ref sta, ref off); }
                catch { return; }

                // Clip — endpoints outside the PV's station window are dropped.
                // 0.5-unit tolerance matches LLabelGenEngine and avoids losing
                // endpoints that sit exactly on the PV's start/end station.
                if (sta < pvStaStart - 0.5 || sta > pvStaEnd + 0.5) return;

                // Crown of pipe — start from there, then drop the label
                // LABEL_Y_DROP DRAWING units below the crown.  Profile views
                // typically have a vertical exaggeration (e.g. 10:1), so we
                // can't just subtract LABEL_Y_DROP from the elevation —
                // instead we map crown elev to drawing XY, drop the drawing Y
                // by LABEL_Y_DROP, and back-solve the elevation that lands
                // there using the PV's actual V-scale.
                double crownElev = endPt.Z + crownOffset;

                double cxCrown = 0, cyCrown = 0;
                if (!pv.FindXYAtStationAndElevation(sta, crownElev, ref cxCrown, ref cyCrown))
                    return;

                // Sample a second point 1.0 elev unit below crown to derive
                // drawing-Y per elevation-unit (the vertical exaggeration).
                double cxRef = 0, cyRef = 0;
                double vScale = 1.0;
                if (pv.FindXYAtStationAndElevation(sta, crownElev - 1.0, ref cxRef, ref cyRef))
                {
                    double sample = cyCrown - cyRef;     // drawing units per 1 elev unit
                    if (sample > 1e-9) vScale = sample;
                }

                double labelDrawingY = cyCrown - LABEL_Y_DROP;
                double labelElev     = crownElev - (LABEL_Y_DROP / vScale);

                var hostPt = new Point3d(cxCrown, labelDrawingY, 0).TransformBy(entityToHostWCS);

                ObjectId netId = ObjectId.Null;
                try { netId = pp.NetworkId; } catch { }

                pts.Add(new CrossingLabelPoint
                {
                    Station     = sta,
                    Elevation   = labelElev,   // dropped value — XREF LISP feeds this verbatim
                    DrawingX    = hostPt.X,
                    DrawingY    = hostPt.Y,
                    NetworkId   = netId,
                    PipeName    = pp.Name,
                    NetworkName = netName
                });
            }
            catch { }
        }
    }
}
