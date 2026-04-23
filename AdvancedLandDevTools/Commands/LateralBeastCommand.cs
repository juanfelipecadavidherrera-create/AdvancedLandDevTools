using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CivilDB = Autodesk.Civil.DatabaseServices;
using AdvancedLandDevTools.Engine;
using AdvancedLandDevTools.Helpers;
using AdvancedLandDevTools.UI;

[assembly: CommandClass(typeof(AdvancedLandDevTools.Commands.LateralBeastCommand))]

namespace AdvancedLandDevTools.Commands
{
    public class LateralBeastCommand
    {
        private static string _targetLayer = "0";

        // ─────────────────────────────────────────────────────────────────────
        //  Candidate entity found on the target layer inside a profile view.
        // ─────────────────────────────────────────────────────────────────────
        private struct TargetLineCandidate
        {
            public Entity Entity;
            public double MinX, MaxX, MinY, MaxY;
            public double Length; // drawing-space length (used for tie-breaking)
            public double MidX;   // horizontal centre (used for tie-breaking)
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Compute the padded drawing-space bounding box of a profile view
        //  from its Civil 3D station/elevation range (not geometric extents,
        //  which include labels/bands and may not match the data area).
        // ─────────────────────────────────────────────────────────────────────
        private static bool TryGetProfileViewDrawingBounds(
            CivilDB.ProfileView pv,
            out double xMin, out double xMax,
            out double yMin, out double yMax)
        {
            // 20 drawing units of padding on every edge so that lines that
            // start slightly outside the view are still picked up.
            const double Pad = 20.0;
            xMin = xMax = yMin = yMax = 0;
            try
            {
                double x = 0, y = 0;
                var xs = new List<double>(); var ys = new List<double>();
                if (!pv.FindXYAtStationAndElevation(pv.StationStart, pv.ElevationMin, ref x, ref y)) return false;
                xs.Add(x); ys.Add(y);
                if (!pv.FindXYAtStationAndElevation(pv.StationEnd,   pv.ElevationMin, ref x, ref y)) return false;
                xs.Add(x); ys.Add(y);
                if (!pv.FindXYAtStationAndElevation(pv.StationStart, pv.ElevationMax, ref x, ref y)) return false;
                xs.Add(x); ys.Add(y);
                if (!pv.FindXYAtStationAndElevation(pv.StationEnd,   pv.ElevationMax, ref x, ref y)) return false;
                xs.Add(x); ys.Add(y);

                xMin = xs.Min() - Pad;  xMax = xs.Max() + Pad;
                yMin = ys.Min() - Pad;  yMax = ys.Max() + Pad;
                return true;
            }
            catch { return false; }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Scan a BlockTableRecord for entities on <layer> whose bounding box
        //  overlaps the profile view area AND that are roughly vertical.
        //  Handles Line, Polyline (lwpolyline), Polyline2d, and Polyline3d.
        // ─────────────────────────────────────────────────────────────────────
        private static List<TargetLineCandidate> CollectTargetCandidates(
            Transaction tx, BlockTableRecord btr, string layer,
            double pvXMin, double pvXMax, double pvYMin, double pvYMax)
        {
            var result = new List<TargetLineCandidate>();
            foreach (ObjectId id in btr)
            {
                Entity ent = null;
                try { ent = tx.GetObject(id, OpenMode.ForRead) as Entity; } catch { continue; }
                if (ent == null) continue;
                if (!ent.Layer.Equals(layer, StringComparison.OrdinalIgnoreCase)) continue;

                double eMinX, eMaxX, eMinY, eMaxY, len;
                try
                {
                    if (ent is Line ln)
                    {
                        eMinX = Math.Min(ln.StartPoint.X, ln.EndPoint.X);
                        eMaxX = Math.Max(ln.StartPoint.X, ln.EndPoint.X);
                        eMinY = Math.Min(ln.StartPoint.Y, ln.EndPoint.Y);
                        eMaxY = Math.Max(ln.StartPoint.Y, ln.EndPoint.Y);
                        len   = ln.Length;
                    }
                    else if (ent is Polyline || ent is Polyline2d || ent is Polyline3d)
                    {
                        var ext = ent.GeometricExtents;
                        eMinX = ext.MinPoint.X; eMaxX = ext.MaxPoint.X;
                        eMinY = ext.MinPoint.Y; eMaxY = ext.MaxPoint.Y;
                        // Polyline (lightweight) exposes Length directly; use bounding-box
                        // diagonal as a proxy for Polyline2d / Polyline3d (tie-breaking only).
                        len = ent is Polyline lp
                              ? lp.Length
                              : Math.Sqrt((eMaxX - eMinX) * (eMaxX - eMinX) +
                                          (eMaxY - eMinY) * (eMaxY - eMinY));
                    }
                    else continue; // unsupported type — skip
                }
                catch { continue; }

                // ── Bounding-box overlap with the padded profile view area ────
                if (eMaxX < pvXMin || eMinX > pvXMax) continue;
                if (eMaxY < pvYMin || eMinY > pvYMax) continue;

                // ── Orientation filter: reject clearly horizontal lines ────────
                // The target line is a property/curb line — nearly vertical.
                // ySpan must be meaningful AND dominate xSpan.
                double xSpan = eMaxX - eMinX;
                double ySpan = eMaxY - eMinY;
                if (ySpan < 2.0) continue;           // degenerate / point
                if (ySpan <= xSpan * 0.5) continue;  // too horizontal

                result.Add(new TargetLineCandidate
                {
                    Entity = ent,
                    MinX   = eMinX, MaxX = eMaxX,
                    MinY   = eMinY, MaxY = eMaxY,
                    Length = len,
                    MidX   = (eMinX + eMaxX) / 2.0
                });
            }
            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Find the one target-line entity for a profile view.
        //  Returns a list with 0 or 1 elements; writes diagnostic messages.
        // ─────────────────────────────────────────────────────────────────────
        private static List<Entity> FindTargetLines(
            Transaction tx, BlockTableRecord btr,
            CivilDB.ProfileView pv, Database db,
            string layer, Editor ed)
        {
            if (!TryGetProfileViewDrawingBounds(pv,
                    out double xMin, out double xMax,
                    out double yMin, out double yMax))
            {
                ed.WriteMessage($"\n  ⚠ Could not compute drawing bounds for PV '{pv.Name}'.");
                return new List<Entity>();
            }

            double pvMidX = (xMin + xMax) / 2.0;

            // ── Pass 1: host model space ──────────────────────────────────────
            var candidates = CollectTargetCandidates(tx, btr, layer, xMin, xMax, yMin, yMax);

            // ── Pass 2: XREF databases (fallback when nothing found in host) ──
            if (candidates.Count == 0)
            {
                try
                {
                    var bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                    foreach (ObjectId btrId in bt)
                    {
                        BlockTableRecord btrDef = null;
                        try { btrDef = tx.GetObject(btrId, OpenMode.ForRead) as BlockTableRecord; }
                        catch { continue; }
                        if (btrDef == null || !btrDef.IsFromExternalReference) continue;

                        // Retrieve the insertion transform so we can map XREF coords → host WCS.
                        var xform = Matrix3d.Identity;
                        try
                        {
                            foreach (ObjectId refId in btrDef.GetBlockReferenceIds(false, true))
                            {
                                var bref = tx.GetObject(refId, OpenMode.ForRead) as BlockReference;
                                if (bref != null) { xform = bref.BlockTransform; break; }
                            }
                        }
                        catch { }

                        Database xDb = null;
                        try { xDb = btrDef.GetXrefDatabase(false); }
                        catch { continue; }
                        if (xDb == null) continue;

                        try
                        {
                            // Map the PV bounds into XREF space using the inverse transform.
                            var invXform = xform.Inverse();
                            var p1h = new Point3d(xMin, yMin, 0).TransformBy(invXform);
                            var p2h = new Point3d(xMax, yMax, 0).TransformBy(invXform);
                            double xMinX = Math.Min(p1h.X, p2h.X);
                            double xMaxX = Math.Max(p1h.X, p2h.X);
                            double xMinY = Math.Min(p1h.Y, p2h.Y);
                            double xMaxY = Math.Max(p1h.Y, p2h.Y);

                            using (var xTx = xDb.TransactionManager.StartTransaction())
                            {
                                var xBt = (BlockTable)xTx.GetObject(xDb.BlockTableId, OpenMode.ForRead);
                                var xMs = (BlockTableRecord)xTx.GetObject(
                                              xBt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                                var xCands = CollectTargetCandidates(
                                    xTx, xMs, layer, xMinX, xMaxX, xMinY, xMaxY);

                                // We can't return XREF entities for IntersectWith in the host tx.
                                // Instead, materialise each candidate as a temporary host-space Line
                                // spanning the full view height at the entity's MidX (transformed).
                                foreach (var xc in xCands)
                                {
                                    double midXHost = new Point3d(xc.MidX, 0, 0).TransformBy(xform).X;
                                    // Construct a proxy Line in host drawing space.
                                    var proxy = new Line(
                                        new Point3d(midXHost, yMin, 0),
                                        new Point3d(midXHost, yMax, 0));
                                    candidates.Add(new TargetLineCandidate
                                    {
                                        Entity = proxy,
                                        MinX   = midXHost, MaxX = midXHost,
                                        MinY   = yMin,     MaxY = yMax,
                                        Length = yMax - yMin,
                                        MidX   = midXHost
                                    });
                                }
                                xTx.Abort();
                            }
                        }
                        catch { }

                        if (candidates.Count > 0) break;
                    }
                }
                catch { }
            }

            if (candidates.Count == 0)
            {
                ed.WriteMessage($"\n  ⚠ No target lines on layer '{layer}' in PV '{pv.Name}'.");
                return new List<Entity>();
            }

            // ── Pick the single best candidate ────────────────────────────────
            Entity best;
            if (candidates.Count == 1)
            {
                best = candidates[0].Entity;
            }
            else
            {
                // Step A: longest entity is almost always the genuine property line.
                candidates.Sort((a, b) => b.Length.CompareTo(a.Length));
                if (candidates[0].Length > candidates[1].Length * 2.0)
                {
                    best = candidates[0].Entity;
                }
                else
                {
                    // Step B: closest horizontal midpoint to the profile-view centre.
                    candidates.Sort((a, b) =>
                        Math.Abs(a.MidX - pvMidX).CompareTo(Math.Abs(b.MidX - pvMidX)));
                    best = candidates[0].Entity;

                    // Step C: warn when the result is ambiguous.
                    if (candidates.Count >= 2 &&
                        Math.Abs(candidates[0].MidX - candidates[1].MidX) < 2.0)
                    {
                        ed.WriteMessage(
                            $"\n  ⚠ PV '{pv.Name}': {candidates.Count} candidate target lines " +
                            "found — using the one closest to the view centre.");
                    }
                }
            }

            return new List<Entity> { best };
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Crossing pipe from another network that intersects the lateral path.
        // ─────────────────────────────────────────────────────────────────────
        private struct LateralCrossing
        {
            public double Station;    // alignment station
            public double DrawingX;   // host WCS X (horizontal 1:1 from reference)
            public double InvertElev; // real pipe invert elevation (ft)
            public double OuterDiam;  // real outer diameter (ft)
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Scan model space for pipes from networks OTHER than selectedNetworkId
        //  that cross the alignment within [stationMin, stationMax].
        // ─────────────────────────────────────────────────────────────────────
        private static List<LateralCrossing> CollectLateralCrossings(
            Transaction tx,
            Database    db,
            ObjectId    pvId,
            ObjectId    selectedNetworkId,
            double      cpDrawingX,
            double      cpStation,
            double      stationMin,
            double      stationMax)
        {
            var result = new List<LateralCrossing>();
            try
            {
                var pv = tx.GetObject(pvId, OpenMode.ForRead) as CivilDB.ProfileView;
                if (pv == null) return result;

                CivilDB.Alignment aln = null;
                try { aln = tx.GetObject(pv.AlignmentId, OpenMode.ForRead) as CivilDB.Alignment; }
                catch { }
                if (aln == null) return result;

                var btr = tx.GetObject(db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                if (btr == null) return result;

                double pvStaStart = pv.StationStart;
                double pvStaEnd   = pv.StationEnd;

                foreach (ObjectId id in btr)
                {
                    try
                    {
                        var obj = tx.GetObject(id, OpenMode.ForRead);

                        double   outerDiam = 0;
                        ObjectId netId     = ObjectId.Null;
                        Point3d  startPt   = Point3d.Origin, endPt = Point3d.Origin;
                        double   startInv  = 0, endInv = 0;
                        bool     isPipe    = false;

                        if (obj is CivilDB.Pipe gp)
                        {
                            netId     = gp.NetworkId;
                            outerDiam = gp.OuterDiameterOrWidth;
                            double innerR = gp.InnerDiameterOrWidth / 2.0;
                            startPt  = gp.StartPoint;  endPt   = gp.EndPoint;
                            startInv = startPt.Z - innerR; endInv = endPt.Z - innerR;
                            isPipe   = true;
                        }
                        else if (obj is CivilDB.PressurePipe pp)
                        {
                            netId     = pp.NetworkId;
                            outerDiam = pp.OuterDiameter;
                            double outerR = outerDiam / 2.0;
                            startPt  = pp.StartPoint;  endPt   = pp.EndPoint;
                            startInv = startPt.Z - outerR; endInv = endPt.Z - outerR;
                            isPipe   = true;
                        }

                        if (!isPipe || netId == selectedNetworkId) continue;

                        foreach (var c in PipeAlignmentIntersector.FindCrossings(id, aln, tx))
                        {
                            if (c.Station < pvStaStart - 0.5   || c.Station > pvStaEnd + 0.5) continue;
                            if (c.Station < stationMin - 0.5   || c.Station > stationMax + 0.5) continue;

                            double t      = ParamT(c.IntersectionPointWCS, startPt, endPt);
                            double invert = startInv + t * (endInv - startInv);

                            result.Add(new LateralCrossing
                            {
                                Station    = c.Station,
                                DrawingX   = cpDrawingX + (c.Station - cpStation),
                                InvertElev = invert,
                                OuterDiam  = outerDiam
                            });
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        // Parameter t (0–1) of crossPt projected onto pipe axis in plan.
        private static double ParamT(Point3d crossPt, Point3d pipeStart, Point3d pipeEnd)
        {
            double dx   = pipeEnd.X - pipeStart.X;
            double dy   = pipeEnd.Y - pipeStart.Y;
            double len2 = dx * dx + dy * dy;
            if (len2 < 1e-9) return 0.0;
            double t = ((crossPt.X - pipeStart.X) * dx +
                        (crossPt.Y - pipeStart.Y) * dy) / len2;
            return Math.Max(0.0, Math.Min(1.0, t));
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Surface elevation: host alignment first, then XREF databases.
        //  Returns real-world elevation in ft, or NaN when nothing is found.
        // ─────────────────────────────────────────────────────────────────────
        private static double GetSurfaceElevation(
            Transaction tx, Database hostDb, ObjectId pvId, double station)
        {
            // 1. Host: pv → alignment → profiles
            try
            {
                var pv  = tx.GetObject(pvId, OpenMode.ForRead) as CivilDB.ProfileView;
                var aln = pv != null && !pv.AlignmentId.IsNull
                          ? tx.GetObject(pv.AlignmentId, OpenMode.ForRead) as CivilDB.Alignment
                          : null;
                if (aln != null)
                {
                    double r = ScanProfilesForElevation(aln.GetProfileIds(), tx, station);
                    if (!double.IsNaN(r)) return r;
                }
            }
            catch { }

            // 2. XREF databases: find an alignment whose station range covers 'station'
            try
            {
                var bt = (BlockTable)tx.GetObject(hostDb.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId btrId in bt)
                {
                    BlockTableRecord btrDef = null;
                    try { btrDef = tx.GetObject(btrId, OpenMode.ForRead) as BlockTableRecord; }
                    catch { continue; }
                    if (btrDef == null || !btrDef.IsFromExternalReference) continue;

                    Database xDb = null;
                    try { xDb = btrDef.GetXrefDatabase(false); }
                    catch { continue; }
                    if (xDb == null) continue;

                    double r = ScanXrefForSurface(xDb, station);
                    if (!double.IsNaN(r)) return r;
                }
            }
            catch { }

            return double.NaN;
        }

        private static double ScanXrefForSurface(Database xDb, double station)
        {
            try
            {
                using (var xTx = xDb.TransactionManager.StartTransaction())
                {
                    var xBt = (BlockTable)xTx.GetObject(xDb.BlockTableId, OpenMode.ForRead);
                    var xMs = (BlockTableRecord)xTx.GetObject(
                                  xBt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId xId in xMs)
                    {
                        CivilDB.Alignment xAln = null;
                        try { xAln = xTx.GetObject(xId, OpenMode.ForRead) as CivilDB.Alignment; }
                        catch { continue; }
                        if (xAln == null) continue;
                        try
                        {
                            if (station < xAln.StartingStation - 0.5 ||
                                station > xAln.EndingStation   + 0.5)
                                continue;
                        }
                        catch { continue; }
                        double r = ScanProfilesForElevation(xAln.GetProfileIds(), xTx, station);
                        if (!double.IsNaN(r)) { xTx.Abort(); return r; }
                    }
                    xTx.Abort();
                }
            }
            catch { }
            return double.NaN;
        }

        private static double ScanProfilesForElevation(
            System.Collections.IEnumerable profileIds, Transaction tx, double station)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            double bestSurface = double.PositiveInfinity;
            double bestAny     = double.PositiveInfinity;

            foreach (ObjectId pid in profileIds)
            {
                CivilDB.Profile prof = null;
                try { prof = tx.GetObject(pid, OpenMode.ForRead) as CivilDB.Profile; }
                catch { continue; }
                if (prof == null) continue;

                double z;
                try { z = prof.ElevationAt(station); }
                catch { continue; }
                if (double.IsNaN(z) || double.IsInfinity(z)) continue;

                if (z < bestAny) bestAny = z;

                bool isSurface = false;
                try
                {
                    var p = prof.GetType().GetProperty("SurfaceId", flags);
                    if (p != null) { var v = (ObjectId)p.GetValue(prof); if (!v.IsNull) isSurface = true; }
                }
                catch { }
                if (!isSurface)
                {
                    try
                    {
                        var p = prof.GetType().GetProperty("ProfileType", flags);
                        if (p != null)
                        {
                            string u = (p.GetValue(prof)?.ToString() ?? "").ToUpperInvariant();
                            if (u.Contains("EG") || u.Contains("EXISTING") ||
                                u.Contains("SURFACE") || u.Contains("GROUND"))
                                isSurface = true;
                        }
                    }
                    catch { }
                }
                if (!isSurface)
                {
                    string n = "";
                    try { n = (prof.Name ?? "").ToUpperInvariant(); } catch { }
                    if (n.Contains("EG") || n.Contains("EXISTING") ||
                        n.Contains("SURFACE") || n.Contains("GROUND"))
                        isSurface = true;
                }

                if (isSurface && z < bestSurface) bestSurface = z;
            }

            if (!double.IsPositiveInfinity(bestSurface)) return bestSurface;
            if (!double.IsPositiveInfinity(bestAny))     return bestAny;
            return double.NaN;
        }

        // ── Red X marker — drawn when constraints are infeasible ─────────────
        private static void DrawRedCross(
            Transaction tx, BlockTableRecord btr,
            double x, double y, double pipeGap)
        {
            double half = Math.Max(pipeGap * 0.75, 2.0);
            var red = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                          Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 1);

            var arm1 = new Line(new Point3d(x - half, y - half, 0), new Point3d(x + half, y + half, 0));
            arm1.Color = red; arm1.Layer = "0";
            var arm2 = new Line(new Point3d(x + half, y - half, 0), new Point3d(x - half, y + half, 0));
            arm2.Color = red; arm2.Layer = "0";

            btr.AppendEntity(arm1); tx.AddNewlyCreatedDBObject(arm1, true);
            btr.AppendEntity(arm2); tx.AddNewlyCreatedDBObject(arm2, true);
        }

        private List<string> GetAllLayers(Database db)
        {
            var layers = new List<string>();
            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tx.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in lt)
                {
                    var ltr = (LayerTableRecord)tx.GetObject(id, OpenMode.ForRead);
                    layers.Add(ltr.Name);
                }
                tx.Abort();
            }
            layers.Sort();
            return layers;
        }

        [CommandMethod("LATERALBEAST", CommandFlags.Modal)]
        public void Execute()
        {
            try
            {
                if (!Engine.LicenseManager.EnsureLicensed()) return;

                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                Editor   ed = doc.Editor;
                Database db = doc.Database;

                ed.WriteMessage("\n═══════════════════════════════════════════════════════\n");
                ed.WriteMessage("  LATERAL BEAST  –  Auto-Draw Laterals in Profile Views\n");
                ed.WriteMessage("═══════════════════════════════════════════════════════\n");

                // ── Step 1: Select one or more profile views ─────────────────────────────
                var profileViewIds = new List<ObjectId>();
                var filter = new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Start, "AECC_PROFILE_VIEW")
                });

                var pso = new PromptSelectionOptions
                {
                    MessageForAdding    = $"\nSelect profile view(s) [Settings] (Current Layer: {_targetLayer}): ",
                    MessageForRemoval   = "\nRemove profile view(s): ",
                    RejectObjectsOnLockedLayers = false
                };
                pso.Keywords.Add("Settings");
                pso.KeywordInput += (s, e) =>
                {
                    var allLayers   = GetAllLayers(db);
                    var layerWindow = new LayerSelectionWindow(allLayers, _targetLayer);
                    if (Application.ShowModalWindow(layerWindow) == true)
                    {
                        _targetLayer = layerWindow.SelectedLayer;
                        ed.WriteMessage($"\nCurrent Layer set to: {_targetLayer}\n");
                    }
                    else
                        ed.WriteMessage("\nLayer selection cancelled.\n");
                };

                PromptSelectionResult psr;
                while (true)
                {
                    psr = ed.GetSelection(pso, filter);
                    if (psr.Status == PromptStatus.Keyword) continue;
                    break;
                }

                if (psr.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\n  Cancelled.\n");
                    return;
                }

                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    foreach (SelectedObject so in psr.Value)
                    {
                        if (so != null && tx.GetObject(so.ObjectId, OpenMode.ForRead) is CivilDB.ProfileView)
                            profileViewIds.Add(so.ObjectId);
                    }
                    tx.Abort();
                }

                if (profileViewIds.Count == 0) return;
                ed.WriteMessage($"\n  ✓  {profileViewIds.Count} profile view(s) selected.\n");

                // ── Step 2: Find Crossings & Select Network ───────────────────────────────
                var allCrossings = new List<CrossingLabelPoint>();
                foreach (var pvId in profileViewIds)
                    allCrossings.AddRange(LLabelGenEngine.FindCrossingPoints(pvId, db));

                if (allCrossings.Count == 0)
                {
                    ed.WriteMessage("\n  ⚠ No crossing pipe networks found in selected profile views.\n");
                    return;
                }

                var networkMap = new Dictionary<ObjectId, string>();
                foreach (var cp in allCrossings)
                    if (!cp.NetworkId.IsNull && !networkMap.ContainsKey(cp.NetworkId))
                        networkMap[cp.NetworkId] = string.IsNullOrEmpty(cp.NetworkName)
                                                   ? "(unknown network)" : cp.NetworkName;

                ObjectId selectedNetworkId = ObjectId.Null;
                if (networkMap.Count == 1)
                {
                    selectedNetworkId = networkMap.Keys.First();
                    ed.WriteMessage($"\n  Only one network found: {networkMap.Values.First()}\n");
                }
                else
                {
                    ed.WriteMessage("\n  Networks found:");
                    var netList = networkMap.ToList();
                    for (int i = 0; i < netList.Count; i++)
                        ed.WriteMessage($"\n    [{i + 1}] {netList[i].Value}");

                    var pio = new PromptIntegerOptions($"\n  Select the MAIN network [1-{netList.Count}]: ");
                    pio.LowerLimit = 1;
                    pio.UpperLimit = netList.Count;
                    var pir = ed.GetInteger(pio);
                    if (pir.Status != PromptStatus.OK) return;
                    selectedNetworkId = netList[pir.Value - 1].Key;
                }

                // ── Step 3: Input Parameters ──────────────────────────────────────────────
                var allDbLayers = GetAllLayers(db);
                var window = new LateralBeastWindow(allDbLayers);
                if (Application.ShowModalWindow(window) != true)
                {
                    ed.WriteMessage("\n  Cancelled by user.\n");
                    return;
                }

                string targetLineLayer = window.TargetLayer;
                bool   isLeft          = window.IsLeft;
                double angleDeg        = window.AngleDeg;
                double verticalOffset  = window.PipeGap;

                // ── Step 4: Draw Laterals ─────────────────────────────────────────────────
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    var btr = (BlockTableRecord)tx.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                    var lt = (LayerTable)tx.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (!lt.Has(_targetLayer))
                    {
                        lt.UpgradeOpen();
                        var newLayer = new LayerTableRecord { Name = _targetLayer };
                        lt.Add(newLayer);
                        tx.AddNewlyCreatedDBObject(newLayer, true);
                    }

                    int lateralsDrawn = 0;

                    foreach (var pvId in profileViewIds)
                    {
                        var pv = tx.GetObject(pvId, OpenMode.ForRead) as CivilDB.ProfileView;
                        if (pv == null) continue;

                        var pvCrossings = LLabelGenEngine.FindCrossingPoints(pvId, db)
                            .Where(c => c.NetworkId == selectedNetworkId).ToList();
                        if (pvCrossings.Count == 0) continue;

                        // Find the one target line for this profile view using robust
                        // multi-strategy detection (civil bounds, multi-type, XREF fallback).
                        var targetLines = FindTargetLines(tx, btr, pv, db, targetLineLayer, ed);
                        if (targetLines.Count == 0) continue;

                        foreach (var cp in pvCrossings)
                        {
                            double angleRad = angleDeg * Math.PI / 180.0;
                            if (isLeft) angleRad = (180.0 - angleDeg) * Math.PI / 180.0;
                            Vector3d dir = new Vector3d(Math.Cos(angleRad), Math.Sin(angleRad), 0);

                            // Standardized offsets from invert (derived from real example data):
                            // Invert=(870749.3842,567624.2606)
                            // Bottom: dx=0.7869, dy=1.1784  |  Top: dx=0.6834, dy=6.1939
                            double startX  = cp.DrawingX + (isLeft ? -0.7869 :  0.7869);
                            double topOffX = cp.DrawingX + (isLeft ? -0.6834 :  0.6834);
                            const double bottomDy = 1.1784;
                            // Absolute minimum drawing Y for the lateral start.
                            // The 1.0 extra drawing unit = 0.1 real ft of additional downward
                            // slack below the pipe invert offset (10× profile exaggeration).
                            double minStartY = cp.DrawingY + bottomDy - 1.0;

                            // ── Phase 1: probe intersection at minimum elevation ───────────
                            // Gives us checkX (2.5 ft before target line) and endStation.
                            var probeRay = new Ray
                                { BasePoint = new Point3d(startX, minStartY, 0), UnitDir = dir };
                            Point3d? probeHit = null;
                            double   probeDist = double.MaxValue;
                            foreach (var tl in targetLines)
                            {
                                var pts = new Point3dCollection();
                                probeRay.IntersectWith(tl, Intersect.OnBothOperands,
                                                       pts, IntPtr.Zero, IntPtr.Zero);
                                if (pts.Count > 0)
                                {
                                    double d = probeRay.BasePoint.DistanceTo(pts[0]);
                                    if (d < probeDist) { probeDist = d; probeHit = pts[0]; }
                                }
                            }
                            probeRay.Dispose();

                            if (!probeHit.HasValue)
                            {
                                ed.WriteMessage(
                                    $"\n  ⚠ No intersection with target line at Sta {cp.Station:F2}.");
                                continue;
                            }

                            // ── Phase 2: iterative resolution ─────────────────────────────
                            //
                            // Algorithm:
                            //   1. Start at the surface-cover position (3 ft cover back-calc).
                            //      This is the highest valid position given surface.
                            //   2. Sort other-network crossings by proximity (closest first).
                            //   3. Walk through crossings: if an above-pipe needs clearance
                            //      push actualStartY DOWN; if a below-pipe needs clearance
                            //      push it UP (fails immediately if that would exceed surface).
                            //   4. Repeat until stable or actualStartY < minStartY (infeasible).
                            //   5. Final validation: re-check surface AND every crossing before
                            //      committing to draw.

                            double tanAngle = Math.Abs(dir.X) > 1e-9 ? dir.Y / dir.X : 0.0;

                            // ── Step 1: surface cover → initial starting position ───────────
                            double checkX       = isLeft ? probeHit.Value.X + 2.5
                                                         : probeHit.Value.X - 2.5;
                            double checkStation = cp.Station + (checkX - cp.DrawingX);

                            double surfaceElev   = GetSurfaceElevation(tx, db, pvId, checkStation);
                            double surfaceStartY = double.NaN; // upper limit from cover
                            if (!double.IsNaN(surfaceElev))
                            {
                                double cx2 = 0, cy2 = 0;
                                if (pv.FindXYAtStationAndElevation(
                                        checkStation, surfaceElev - 3.0, ref cx2, ref cy2))
                                    surfaceStartY = cy2 - (checkX - startX) * tanAngle;
                            }
                            else
                            {
                                ed.WriteMessage(
                                    $"\n  ⚠ No surface at Sta {checkStation:F2} — cover not checked.");
                            }

                            // Start as high as surface allows (never below absolute minimum).
                            double actualStartY = double.IsNaN(surfaceStartY)
                                                  ? minStartY
                                                  : Math.Max(surfaceStartY, minStartY);

                            // ── Step 2: collect other-network crossings in lateral span ─────
                            double endStation = cp.Station + (probeHit.Value.X - cp.DrawingX);
                            double stMin = Math.Min(cp.Station, endStation);
                            double stMax = Math.Max(cp.Station, endStation);

                            var otherCrossings = CollectLateralCrossings(
                                tx, db, pvId, selectedNetworkId,
                                cp.DrawingX, cp.Station, stMin, stMax);

                            // Closest crossings first so we resolve the tightest conflicts early.
                            otherCrossings.Sort((a, b) =>
                                Math.Abs(a.DrawingX - startX)
                                    .CompareTo(Math.Abs(b.DrawingX - startX)));

                            // ── Step 3: iterative downward resolution ───────────────────────
                            bool resolveOk = true;
                            bool changed   = true;
                            int  maxIter   = (otherCrossings.Count + 1) * 3;
                            int  iter      = 0;

                            while (changed && resolveOk && iter++ < maxIter)
                            {
                                changed = false;
                                foreach (var lc in otherCrossings)
                                {
                                    double dx_c = lc.DrawingX - startX;
                                    double dummy = 0, cyInv = 0, cyCrn = 0;
                                    if (!pv.FindXYAtStationAndElevation(
                                            lc.Station, lc.InvertElev, ref dummy, ref cyInv))
                                        continue;
                                    if (!pv.FindXYAtStationAndElevation(
                                            lc.Station, lc.InvertElev + lc.OuterDiam,
                                            ref dummy, ref cyCrn))
                                        continue;

                                    double latBot    = actualStartY + dx_c * tanAngle;
                                    double latTop    = latBot + verticalOffset;
                                    double latCtr    = (latBot + latTop)  / 2.0;
                                    double crssCtr   = (cyInv  + cyCrn)   / 2.0;
                                    bool   aboveLat  = crssCtr > latCtr;

                                    if (aboveLat)
                                    {
                                        // Lateral invert must stay ≥ 1 ft (10 drawing units) below
                                        // crossing invert — measured invert-to-invert.
                                        double cyReq = 0;
                                        if (!pv.FindXYAtStationAndElevation(
                                                lc.Station, lc.InvertElev - 1.0,
                                                ref dummy, ref cyReq))
                                            continue;
                                        // latBot = actualStartY + dx_c*tan ≤ cyReq
                                        double maxStart = cyReq - dx_c * tanAngle;
                                        if (actualStartY > maxStart + 0.001)
                                        {
                                            actualStartY = maxStart;
                                            if (actualStartY < minStartY - 0.001)
                                            {
                                                resolveOk = false; break;
                                            }
                                            actualStartY = Math.Max(actualStartY, minStartY);
                                            changed = true;
                                        }
                                    }
                                    else
                                    {
                                        // Lateral invert must stay ≥ 0.5 ft (5 drawing units) above
                                        // crossing invert — measured invert-to-invert.
                                        double cyReq = 0;
                                        if (!pv.FindXYAtStationAndElevation(
                                                lc.Station, lc.InvertElev + 0.5,
                                                ref dummy, ref cyReq))
                                            continue;
                                        // latBot = actualStartY + dx_c*tan ≥ cyReq
                                        double minStart = cyReq - dx_c * tanAngle;
                                        if (actualStartY < minStart - 0.001)
                                        {
                                            // Must go UP — only valid if still within surface limit.
                                            if (!double.IsNaN(surfaceStartY) &&
                                                minStart > surfaceStartY + 0.001)
                                            {
                                                resolveOk = false; break;
                                            }
                                            actualStartY = minStart;
                                            changed = true;
                                        }
                                    }
                                }
                            }

                            if (!resolveOk || actualStartY < minStartY - 0.001)
                            {
                                DrawRedCross(tx, btr, cp.DrawingX, cp.DrawingY, verticalOffset);
                                ed.WriteMessage(
                                    $"\n  ✗ Sta {cp.Station:F2}: no valid position satisfies all" +
                                    " constraints — red X drawn, skipped.");
                                continue;
                            }

                            // ── Step 4: final validation (surface + ALL crossings) ──────────
                            bool finalOk = true;

                            // Surface cover check
                            if (!double.IsNaN(surfaceStartY) && actualStartY > surfaceStartY + 0.01)
                                finalOk = false;

                            // All crossing-pipe checks
                            if (finalOk)
                            {
                                foreach (var lc in otherCrossings)
                                {
                                    double dx_c = lc.DrawingX - startX;
                                    double dummy = 0, cyInv = 0, cyCrn = 0;
                                    if (!pv.FindXYAtStationAndElevation(
                                            lc.Station, lc.InvertElev, ref dummy, ref cyInv))
                                        continue;
                                    if (!pv.FindXYAtStationAndElevation(
                                            lc.Station, lc.InvertElev + lc.OuterDiam,
                                            ref dummy, ref cyCrn))
                                        continue;

                                    double latBot   = actualStartY + dx_c * tanAngle;
                                    double latTop   = latBot + verticalOffset;
                                    double latCtr   = (latBot + latTop) / 2.0;
                                    double crssCtr  = (cyInv  + cyCrn) / 2.0;
                                    bool   aboveLat = crssCtr > latCtr;

                                    double cyReq = 0;
                                    if (aboveLat)
                                    {
                                        // Invert-to-invert: lateral invert ≥ 1 ft below crossing invert
                                        if (pv.FindXYAtStationAndElevation(
                                                lc.Station, lc.InvertElev - 1.0,
                                                ref dummy, ref cyReq)
                                            && latBot > cyReq + 0.01)
                                        { finalOk = false; break; }
                                    }
                                    else
                                    {
                                        // Invert-to-invert: lateral invert ≥ 0.5 ft above crossing invert
                                        if (pv.FindXYAtStationAndElevation(
                                                lc.Station, lc.InvertElev + 0.5,
                                                ref dummy, ref cyReq)
                                            && latBot < cyReq - 0.01)
                                        { finalOk = false; break; }
                                    }
                                }
                            }

                            if (!finalOk)
                            {
                                DrawRedCross(tx, btr, cp.DrawingX, cp.DrawingY, verticalOffset);
                                ed.WriteMessage(
                                    $"\n  ✗ Sta {cp.Station:F2}: final validation failed" +
                                    " — red X drawn, skipped.");
                                continue;
                            }

                            // ── Phase 3: shoot rays from adjusted start points and draw ──────
                            // Both bottom and top shift by the same deltaY → whole symbol moves.
                            double deltaY = actualStartY - minStartY;

                            Point3d bottomStart = new Point3d(startX,  actualStartY, 0);
                            Point3d topStart    = new Point3d(topOffX,
                                                     cp.DrawingY + bottomDy + verticalOffset + deltaY, 0);

                            var rayB = new Ray { BasePoint = bottomStart, UnitDir = dir };
                            var rayT = new Ray { BasePoint = topStart,    UnitDir = dir };

                            Point3d? bestB = null, bestT = null;
                            double dB = double.MaxValue, dT = double.MaxValue;

                            foreach (var tl in targetLines)
                            {
                                var pB = new Point3dCollection();
                                rayB.IntersectWith(tl, Intersect.OnBothOperands,
                                                   pB, IntPtr.Zero, IntPtr.Zero);
                                if (pB.Count > 0)
                                {
                                    double d = bottomStart.DistanceTo(pB[0]);
                                    if (d < dB) { dB = d; bestB = pB[0]; }
                                }

                                var pT = new Point3dCollection();
                                rayT.IntersectWith(tl, Intersect.OnBothOperands,
                                                   pT, IntPtr.Zero, IntPtr.Zero);
                                if (pT.Count > 0)
                                {
                                    double d = topStart.DistanceTo(pT[0]);
                                    if (d < dT) { dT = d; bestT = pT[0]; }
                                }
                            }

                            rayB.Dispose();
                            rayT.Dispose();

                            if (!bestB.HasValue || !bestT.HasValue)
                            {
                                ed.WriteMessage(
                                    $"\n  ⚠ No intersection after adjustment at Sta {cp.Station:F2}.");
                                continue;
                            }

                            var lineB = new Line(bottomStart, bestB.Value) { Layer = _targetLayer };
                            btr.AppendEntity(lineB);
                            tx.AddNewlyCreatedDBObject(lineB, true);

                            var lineT = new Line(topStart, bestT.Value) { Layer = _targetLayer };
                            btr.AppendEntity(lineT);
                            tx.AddNewlyCreatedDBObject(lineT, true);

                            lateralsDrawn++;
                        }
                    }

                    tx.Commit();
                    ed.WriteMessage($"\n  ✓ LATERALBEAST complete. {lateralsDrawn} laterals drawn.\n");
                }
            }
            catch (System.Exception ex)
            {
                var d = Application.DocumentManager.MdiActiveDocument;
                d?.Editor.WriteMessage($"\n[LATERALBEAST ERROR] {ex.Message}\n");
            }
        }
    }
}
