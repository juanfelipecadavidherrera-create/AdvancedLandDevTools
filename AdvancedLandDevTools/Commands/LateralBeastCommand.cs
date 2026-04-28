using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
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
        //  Report types
        // ─────────────────────────────────────────────────────────────────────
        private enum LateralStatus
        {
            Drawn,
            SkippedNoIntersection,  // probe / adjusted ray found no target-line hit
            SkippedInfeasible,      // constraint resolution loop declared infeasible
            SkippedValidationFailed,// final re-check of surface+pipes failed
            SkippedNoTarget         // FindTargetLines returned empty for the whole PV
        }

        private struct LateralReport
        {
            public string        PvName;
            public double        Station;
            public LateralStatus Status;
            public double        ActualStartY;
            public double        MinStartY;
            public double        SurfaceStartY;    // NaN = not found
            public double        DeltaY;           // actualStartY - minStartY; NaN when skipped
            public string        ActiveConstraints; // e.g. "SurfaceCover|AbovePipe"
            public string        FailReason;
            public string        CrossingsInfo;     // multi-line dump of every crossing
                                                   // pipe seen at this lateral — included in
                                                   // the printed report so the user can paste
                                                   // it back for diagnosis.
        }

        // Build a multi-line dump of every crossing seen by a lateral.  Each line
        // includes alignment station, outer diameter, host-WCS X, real invert
        // elevation and the corresponding drawing-Y values for invert and crown.
        private static string BuildCrossingsInfo(
            CivilDB.ProfileView pv,
            List<LateralCrossing> crossings,
            double startX,
            double tanAngle,
            double actualStartY,
            double verticalOffset)
        {
            if (crossings == null || crossings.Count == 0)
                return "      (no other-network crossings)";
            var sb = new StringBuilder();
            for (int i = 0; i < crossings.Count; i++)
            {
                var lc = crossings[i];
                double dummy = 0, cyInv = double.NaN, cyCrn = double.NaN;
                bool okI = false, okC = false;
                try
                {
                    okI = pv.FindXYAtStationAndElevation(
                              lc.Station, lc.InvertElev,
                              ref dummy, ref cyInv);
                    okC = pv.FindXYAtStationAndElevation(
                              lc.Station, lc.InvertElev + lc.OuterDiam,
                              ref dummy, ref cyCrn);
                }
                catch { }

                double dx_c  = lc.DrawingX - startX;
                double latBot = double.IsNaN(actualStartY) ? double.NaN
                                : actualStartY + dx_c * tanAngle;
                double latTop = double.IsNaN(actualStartY) ? double.NaN
                                : latBot + verticalOffset;
                string side = "n/a";
                if (okI && okC && !double.IsNaN(latBot))
                    side = IsCrossingAboveLateral(latBot, latTop, cyInv, cyCrn)
                           ? "LATERAL-PASSES-BELOW-PIPE (above-rule, 0.555ft clear above lat)"
                           : "LATERAL-PASSES-ABOVE-PIPE (below-rule, 1.055ft clear below lat)";

                sb.Append("      • [").Append(i + 1).Append("] ");
                sb.Append("Sta=").Append(lc.Station.ToString("F2"));
                sb.Append("  Ø=").Append(lc.OuterDiam.ToString("F2")).Append("ft");
                sb.Append("  X=").Append(lc.DrawingX.ToString("F2"));
                sb.Append("  Inv=").Append(lc.InvertElev.ToString("F2")).Append("ft");
                sb.Append("  InvY=").Append(okI ? cyInv.ToString("F2") : "?");
                sb.Append("  CrnY=").Append(okC ? cyCrn.ToString("F2") : "?");
                if (!double.IsNaN(latBot))
                {
                    sb.Append("  latBotY=").Append(latBot.ToString("F2"));
                    sb.Append("  latTopY=").Append(latTop.ToString("F2"));
                }
                sb.Append("  side=").Append(side);
                if (i < crossings.Count - 1) sb.AppendLine();
            }
            return sb.ToString();
        }

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

            // Host model space only — this command always operates within the
            // current document.  Target lines must live in the host drawing.
            var candidates = CollectTargetCandidates(tx, btr, layer, xMin, xMax, yMin, yMax);

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
        //  Host-only — this command always operates on profile views, alignments
        //  and pipes that live in the current document (no XREF support).
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

        // ─────────────────────────────────────────────────────────────────────
        //  Decide whether a crossing pipe should be treated as ABOVE the
        //  lateral (push lateral DOWN) or BELOW the lateral (push lateral UP).
        //
        //  Two regimes:
        //   1. No vertical overlap → simple center comparison (cheap, correct).
        //   2. Overlap (clash already) → pick the side requiring less movement
        //      to clear, factoring in the asymmetric clearances
        //      (1.055 ft above-clearance vs 0.555 ft below-clearance).
        //
        //  The center-only check that this replaces consistently picked the
        //  wrong side when a big crossing pipe straddled the lateral, leading
        //  to spurious infeasibility verdicts.
        // ─────────────────────────────────────────────────────────────────────
        // Asymmetric pads, lateral-centric:
        //   ClearAboveDu — gap between LATERAL TOP and PIPE INVERT when the lateral
        //                  passes BELOW the crossing pipe.  Small pad: 0.555 ft.
        //   ClearBelowDu — gap between LATERAL BOTTOM and PIPE CROWN when the lateral
        //                  passes ABOVE the crossing pipe.  Big pad:   1.055 ft.
        private const double ClearAboveDu =  5.55;  // 0.555 ft × 10× V.E.  (lat below pipe)
        private const double ClearBelowDu = 10.55;  // 1.055 ft × 10× V.E.  (lat above pipe)

        // Minimum vertical drawing-distance between the surface (top of the
        // structure / cap) and the VISIBLE BOTTOM LATERAL LINE at the structure
        // station.  The user requires 30 drawing units of cover at the structure.
        private const double StructureCoverDu = 30.0;

        // Strict geometric classifier — no clearance bias.
        //   • Lateral fully BELOW the pipe invert  → pipe is ABOVE  → return true
        //     (this is a "from-below" crossing; above-rule, ClearAboveDu applies).
        //   • Lateral fully ABOVE the pipe crown   → pipe is BELOW  → return false
        //     (this is a "from-above" crossing; below-rule, ClearBelowDu applies).
        //   • Overlap regime → fall back to midpoint comparison (no clearance bias),
        //     so the asymmetric pads can no longer flip the verdict.
        private static bool IsCrossingAboveLateral(
            double latBot, double latTop,
            double cyInv,  double cyCrn)
        {
            if (latTop <= cyInv) return true;   // lateral entirely below pipe invert
            if (latBot >= cyCrn) return false;  // lateral entirely above pipe crown
            double latMid  = 0.5 * (latBot + latTop);
            double pipeMid = 0.5 * (cyInv  + cyCrn);
            return pipeMid > latMid;            // pipe centre above lateral centre → above-rule
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
        //  Surface elevation: host PV → host alignment → host profiles.
        //  Returns real-world elevation in ft, or NaN when nothing is found.
        //  Host-only — no XREF traversal.
        // ─────────────────────────────────────────────────────────────────────
        private static double GetSurfaceElevation(
            Transaction tx, Database hostDb, ObjectId pvId, double station)
        {
            try
            {
                var pv  = tx.GetObject(pvId, OpenMode.ForRead) as CivilDB.ProfileView;
                var aln = pv != null && !pv.AlignmentId.IsNull
                          ? tx.GetObject(pv.AlignmentId, OpenMode.ForRead) as CivilDB.Alignment
                          : null;
                if (aln != null)
                    return ScanProfilesForElevation(aln.GetProfileIds(), tx, station);
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

                    // Ensure the SEWERP layer exists for the wye symbol.
                    const string SewerpLayer = "SEWERP";
                    if (!lt.Has(SewerpLayer))
                    {
                        if (!lt.IsWriteEnabled) lt.UpgradeOpen();
                        var sewL = new LayerTableRecord { Name = SewerpLayer };
                        lt.Add(sewL);
                        tx.AddNewlyCreatedDBObject(sewL, true);
                    }

                    int lateralsDrawn = 0;
                    var reports = new List<LateralReport>();

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
                                reports.Add(new LateralReport
                                {
                                    PvName = pv.Name, Station = cp.Station,
                                    Status = LateralStatus.SkippedNoIntersection,
                                    ActualStartY = double.NaN, MinStartY = minStartY,
                                    SurfaceStartY = double.NaN, DeltaY = double.NaN,
                                    ActiveConstraints = "",
                                    FailReason = "No intersection with target line at probe elevation"
                                });
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
                            // Hard rule: the VISIBLE BOTTOM LATERAL LINE at the structure
                            // station must be at least StructureCoverDu (30 du) below the
                            // surface (i.e. below the top of the cap/structure).
                            //
                            // The visible bottom line at any X equals
                            //   visBottomY(X) = (actualStartY + 1.0) + (X − startX)·tanAngle
                            // (the +1.0 du is the slack offset between actualStartY and the
                            // line that's actually drawn).  We anchor the cover check at the
                            // structure end, which is exactly where the top ray hits the
                            // target line — approximated here by probeHit.Value.X (the
                            // structure stub is drawn within ~3 du of that X).
                            double checkX       = probeHit.Value.X;
                            double checkStation = cp.Station + (checkX - cp.DrawingX);

                            double surfaceElev   = GetSurfaceElevation(tx, db, pvId, checkStation);
                            double surfaceStartY = double.NaN; // upper limit from cover
                            if (!double.IsNaN(surfaceElev))
                            {
                                double cxSurf = 0, cySurf = 0;
                                if (pv.FindXYAtStationAndElevation(
                                        checkStation, surfaceElev, ref cxSurf, ref cySurf))
                                {
                                    // Solve for the upper bound on actualStartY:
                                    //   visBottomY(checkX) ≤ cySurf − StructureCoverDu
                                    //   (actualStartY + 1.0) + (checkX − startX)·tanAngle
                                    //                                 ≤ cySurf − StructureCoverDu
                                    surfaceStartY = cySurf
                                                  - StructureCoverDu
                                                  - 1.0
                                                  - (checkX - startX) * tanAngle;
                                }
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

                            // ── Step 3: Interval-intersection feasibility solver ────────────
                            //
                            // For each crossing, the lateral position must satisfy
                            //   actualStartY ≤ maxStartIfAbove   (lateral passes UNDER pipe)
                            //   OR  actualStartY ≥ minStartIfBelow   (lateral passes OVER pipe)
                            // i.e. it must avoid the open forbidden interval
                            //   (maxStartIfAbove, minStartIfBelow).
                            //
                            // We start with the global feasible region [minStartY, surfaceStartY]
                            // and subtract each crossing's forbidden interval from it.  The result
                            // is a (possibly multi-piece) feasible set.  If non-empty we pick the
                            // highest point — minimising lateral depth — except when no surface
                            // profile was found, in which case we keep the lateral deep (original
                            // behaviour).
                            //
                            // This replaces the older sequential-push loop, which oscillated when
                            // two crossings forced opposing sides and could declare infeasibility
                            // on the first per-crossing both-sides-blocked verdict before
                            // considering whether the OVERALL region was non-empty.

                            bool   resolveOk    = true;
                            string failReason   = "";
                            const double NoSurfacePad = 1000.0; // unbounded above when surface missing
                            double upperBound = double.IsNaN(surfaceStartY)
                                                ? minStartY + NoSurfacePad
                                                : surfaceStartY;

                            var feasible = new List<(double Lo, double Hi)>
                                { (minStartY, upperBound) };

                            foreach (var lc in otherCrossings)
                            {
                                double dx_c   = lc.DrawingX - startX;
                                double dummy  = 0, cyReqAbove = 0, cyReqBelow = 0;
                                // Lateral-below-pipe (above-rule): lat top must clear pipe invert
                                // by 0.555 ft.  Lateral-above-pipe (below-rule): lat bottom must
                                // clear pipe crown by 1.055 ft.
                                if (!pv.FindXYAtStationAndElevation(
                                        lc.Station, lc.InvertElev - 0.555,
                                        ref dummy, ref cyReqAbove)) continue;
                                if (!pv.FindXYAtStationAndElevation(
                                        lc.Station, lc.InvertElev + lc.OuterDiam + 1.055,
                                        ref dummy, ref cyReqBelow)) continue;

                                double dxTan           = dx_c * tanAngle;
                                double maxStartIfAbove = cyReqAbove - verticalOffset - dxTan;
                                double minStartIfBelow = cyReqBelow - dxTan;

                                // Subtract the open forbidden interval from each piece.
                                var next = new List<(double Lo, double Hi)>();
                                foreach (var iv in feasible)
                                {
                                    double lo = iv.Lo, hi = iv.Hi;
                                    // Forbidden lies entirely outside this piece — keep as-is.
                                    if (minStartIfBelow <= lo + 0.001 ||
                                        maxStartIfAbove >= hi - 0.001)
                                    {
                                        next.Add((lo, hi));
                                        continue;
                                    }
                                    // Lower fragment that survives: [lo, maxStartIfAbove]
                                    if (maxStartIfAbove > lo + 0.001)
                                        next.Add((lo, maxStartIfAbove));
                                    // Upper fragment that survives: [minStartIfBelow, hi]
                                    if (minStartIfBelow < hi - 0.001)
                                        next.Add((minStartIfBelow, hi));
                                }
                                feasible = next;

                                if (feasible.Count == 0)
                                {
                                    resolveOk = false;
                                    failReason =
                                        $"Crossing at Sta {lc.Station:F2} (Ø{lc.OuterDiam:F2}ft) " +
                                        $"blocks the envelope: need actualStartY ≤ " +
                                        $"{maxStartIfAbove:F2} or ≥ {minStartIfBelow:F2} within " +
                                        $"[{minStartY:F2}, {upperBound:F2}]";
                                    break;
                                }
                            }

                            if (resolveOk)
                            {
                                // Highest feasible point if surface profile is known
                                // (minimises lateral depth).  Lowest if not (stay deep).
                                actualStartY = double.IsNaN(surfaceStartY)
                                               ? feasible.Min(iv => iv.Lo)
                                               : feasible.Max(iv => iv.Hi);
                                if (actualStartY < minStartY) actualStartY = minStartY;
                            }

                            if (!resolveOk || actualStartY < minStartY - 0.001)
                            {
                                DrawRedCross(tx, btr, cp.DrawingX, cp.DrawingY, verticalOffset);
                                ed.WriteMessage(
                                    $"\n  ✗ Sta {cp.Station:F2}: no valid position satisfies all" +
                                    " constraints — red X drawn, skipped.");
                                reports.Add(new LateralReport
                                {
                                    PvName = pv.Name, Station = cp.Station,
                                    Status = LateralStatus.SkippedInfeasible,
                                    ActualStartY = actualStartY, MinStartY = minStartY,
                                    SurfaceStartY = surfaceStartY, DeltaY = double.NaN,
                                    ActiveConstraints = "",
                                    FailReason = resolveOk
                                        ? "actualStartY fell below minStartY after resolution"
                                        : failReason,
                                    CrossingsInfo = BuildCrossingsInfo(
                                        pv, otherCrossings, startX, tanAngle,
                                        actualStartY, verticalOffset)
                                });
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

                                    double latBot = actualStartY + dx_c * tanAngle;

                                    // Symmetric check: the lateral validates against this
                                    // crossing if it clears it from EITHER side.
                                    //   • Lateral-below-pipe clear: latTop ≤ invert − 0.555
                                    //     (rewritten in terms of latBot:
                                    //      latBot ≤ (invert − 0.555) − verticalOffset)
                                    //   • Lateral-above-pipe clear: latBot ≥ crown + 1.055
                                    double cyAbove = 0, cyBelow = 0;
                                    bool gotA = pv.FindXYAtStationAndElevation(
                                                    lc.Station, lc.InvertElev - 0.555,
                                                    ref dummy, ref cyAbove);
                                    bool gotB = pv.FindXYAtStationAndElevation(
                                                    lc.Station, lc.InvertElev + lc.OuterDiam + 1.055,
                                                    ref dummy, ref cyBelow);

                                    bool clearsAbove = gotA && latBot <= (cyAbove - verticalOffset) + 0.01;
                                    bool clearsBelow = gotB && latBot >= cyBelow - 0.01;

                                    if (!clearsAbove && !clearsBelow)
                                    { finalOk = false; break; }
                                }
                            }

                            if (!finalOk)
                            {
                                DrawRedCross(tx, btr, cp.DrawingX, cp.DrawingY, verticalOffset);
                                ed.WriteMessage(
                                    $"\n  ✗ Sta {cp.Station:F2}: final validation failed" +
                                    " — red X drawn, skipped.");
                                reports.Add(new LateralReport
                                {
                                    PvName = pv.Name, Station = cp.Station,
                                    Status = LateralStatus.SkippedValidationFailed,
                                    ActualStartY = actualStartY, MinStartY = minStartY,
                                    SurfaceStartY = surfaceStartY, DeltaY = double.NaN,
                                    ActiveConstraints = "",
                                    FailReason = "Final validation check failed (surface or crossing constraint)",
                                    CrossingsInfo = BuildCrossingsInfo(
                                        pv, otherCrossings, startX, tanAngle,
                                        actualStartY, verticalOffset)
                                });
                                continue;
                            }

                            // ── Phase 3: shoot rays from adjusted start points and draw ──────
                            // Both bottom and top shift by the same deltaY → whole symbol moves.
                            double deltaY = actualStartY - minStartY;

                            // actualStartY includes 1 drawing unit of downward slack added to
                            // minStartY for constraint resolution.  The drawn bottom line sits
                            // 1 unit above it so the visual gap to the top line equals exactly
                            // verticalOffset (the user-configured pipe gap).
                            Point3d bottomStart = new Point3d(startX, actualStartY + 1.0, 0);
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
                                reports.Add(new LateralReport
                                {
                                    PvName = pv.Name, Station = cp.Station,
                                    Status = LateralStatus.SkippedNoIntersection,
                                    ActualStartY = actualStartY, MinStartY = minStartY,
                                    SurfaceStartY = surfaceStartY, DeltaY = double.NaN,
                                    ActiveConstraints = "",
                                    FailReason = "No ray intersection after position adjustment",
                                    CrossingsInfo = BuildCrossingsInfo(
                                        pv, otherCrossings, startX, tanAngle,
                                        actualStartY, verticalOffset)
                                });
                                continue;
                            }

                            // xDir drives mirroring for both the bottom extension and wye symbol.
                            double xDir = isLeft ? -1.0 : 1.0;

                            // ── Pre-compute connector intersection points (when lateral was
                            //    pushed up). These become the START of the lateral lines so
                            //    the 1:10 connectors meet the parallel lines flush — no gap.
                            //    Connector eq:  Y = pipeY + xDir·10·(X − cp.DrawingX)
                            //    Lateral eq:    Y = lineStartY + tanAngle·(X − lineStartX)
                            const double Slope            = 10.0;
                            const double PipeOuterDiamDu  = 8.0 / 12.0 * 10.0; // 6.6667 du
                            double cpInvertY = cp.DrawingY;
                            double cpCrownY  = cp.DrawingY + PipeOuterDiamDu;

                            Point3d lineBStart = bottomStart;
                            Point3d lineTStart = topStart;
                            bool    drawConnectors = deltaY > 0.001;

                            double xBot = 0, yBot = 0, xTop = 0, yTop = 0;
                            if (drawConnectors)
                            {
                                double denom = xDir * Slope - tanAngle;
                                xBot = (bottomStart.Y - cpInvertY
                                        - tanAngle * bottomStart.X
                                        + xDir * Slope * cp.DrawingX) / denom;
                                yBot = cpInvertY + xDir * Slope * (xBot - cp.DrawingX);

                                xTop = (topStart.Y - cpCrownY
                                        - tanAngle * topStart.X
                                        + xDir * Slope * cp.DrawingX) / denom;
                                yTop = cpCrownY + xDir * Slope * (xTop - cp.DrawingX);

                                lineBStart = new Point3d(xBot, yBot, 0);
                                lineTStart = new Point3d(xTop, yTop, 0);
                            }

                            // Bottom line: extend past the target-line hit point by the
                            // standard diagonal (4.7902 horizontal, 0.4745 vertical).
                            var bExtEnd = new Point3d(
                                bestB.Value.X + xDir * 4.7902,
                                bestB.Value.Y + 0.4745,
                                0);
                            var lineB = new Line(lineBStart, bExtEnd) { Layer = _targetLayer };
                            btr.AppendEntity(lineB);
                            tx.AddNewlyCreatedDBObject(lineB, true);

                            var lineT = new Line(lineTStart, bestT.Value) { Layer = _targetLayer };
                            btr.AppendEntity(lineT);
                            tx.AddNewlyCreatedDBObject(lineT, true);

                            // ── Service wye symbol at the top-line intersection ────────────
                            // Shape is anchored at bestT (where the top ray hits the target
                            // line). All offsets are in drawing units; mirror horizontally
                            // for left-going laterals and flip arc directions accordingly.
                            //
                            //  idx  dx_right    dy        bulge_right   bulge_left
                            //   0    0.0000    0.0000        0             0
                            //   1    4.8365    0.4944       -0.5902       +0.5902
                            //   2    4.8365   -2.0056       +0.6039       -0.6039
                            //   3    4.7902   -4.5364       +0.5914       -0.5914
                            //   4    4.8118   -2.0056        0             0
                            var wyeDx  = new double[] { 0.0000, 4.8365, 4.8365, 4.7902, 4.8118 };
                            var wyeDy  = new double[] { 0.0000, 0.4944,-2.0056,-4.5364,-2.0056 };
                            var wyeBlg = new double[] { 0,     -0.5902, 0.6039, 0.5914, 0      };

                            var wyePl = new Polyline();
                            wyePl.Layer      = _targetLayer;
                            wyePl.LineWeight = LineWeight.LineWeight035;
                            wyePl.Elevation  = 0.0;
                            wyePl.Normal     = Vector3d.ZAxis;
                            wyePl.Closed     = false;

                            for (int vi = 0; vi < wyeDx.Length; vi++)
                            {
                                wyePl.AddVertexAt(vi,
                                    new Point2d(bestT.Value.X + xDir * wyeDx[vi],
                                                bestT.Value.Y +         wyeDy[vi]),
                                    wyeBlg[vi] * xDir,   // flip arc direction when mirroring
                                    0.0, 0.0);
                            }

                            btr.AppendEntity(wyePl);
                            tx.AddNewlyCreatedDBObject(wyePl, true);

                            // ── Vertical pipe-stub + U-cap symbol ─────────────────────────
                            // Two vertical lines rise from bestT up to the cap bottom.
                            // The U-cap top always touches the surface profile at this station.
                            // Structure extends LEFT of anchor for right laterals, RIGHT for left
                            // laterals → use mult = -xDir for all horizontal offsets.
                            //
                            // IMPORTANT: all geometry is fully validated BEFORE any entity is
                            // appended to the btr.  A partially-registered entity can corrupt the
                            // outer transaction and prevent tx.Commit() — which would silently
                            // discard the already-drawn lateral lines.
                            try
                            {
                                double topStation  = cp.Station + (bestT.Value.X - cp.DrawingX);
                                double capSurfElev = GetSurfaceElevation(tx, db, pvId, topStation);

                                if (double.IsNaN(capSurfElev) || !double.IsFinite(capSurfElev))
                                {
                                    ed.WriteMessage(
                                        $"\n  ⚠ No surface at Sta {topStation:F2}" +
                                        " — cap symbol skipped.");
                                }
                                else
                                {
                                    double sx2 = 0, capTopY = 0;
                                    bool cvtOk = pv.FindXYAtStationAndElevation(
                                                     topStation, capSurfElev, ref sx2, ref capTopY);

                                    // Lift the cap top a small pad above the surface so the
                                    // structure visibly sits proud of the ground line.
                                    if (cvtOk) capTopY += 0.116;

                                    // Guard 1: coordinate conversion must succeed and be finite.
                                    if (!cvtOk || !double.IsFinite(capTopY))
                                    {
                                        ed.WriteMessage(
                                            $"\n  ⚠ Sta {cp.Station:F2}: could not convert surface" +
                                            " elevation to drawing Y — cap symbol skipped.");
                                    }
                                    else
                                    {
                                        double capStepY   = capTopY  - 0.1875;   // inner ledge
                                        double capBottomY = capTopY  - 2.0625;   // top of vert lines

                                        // The top lateral line is diagonal, so its Y at xInnerR/L
                                        // differs from bestT.Value.Y by (ΔX × tanAngle).
                                        // Compute each stub's bottom independently so they meet
                                        // the top line exactly at their respective X positions.
                                        double mult       = -xDir;
                                        double xInnerR    = bestT.Value.X + mult * 2.2500;
                                        double xInnerL    = bestT.Value.X + mult * 2.7500;
                                        double vertBotYR  = bestT.Value.Y + (xInnerR - bestT.Value.X) * tanAngle;
                                        double vertBotYL  = bestT.Value.Y + (xInnerL - bestT.Value.X) * tanAngle;

                                        // Guard 2: surface must sit strictly above the top-line
                                        // intersection; otherwise the stubs would have zero or
                                        // inverted length → bad geometry that corrupts the transaction.
                                        if (capBottomY <= vertBotYR)
                                        {
                                            ed.WriteMessage(
                                                $"\n  ⚠ Sta {cp.Station:F2}: surface too close to" +
                                                " lateral top line — cap symbol skipped.");
                                        }
                                        else
                                        {
                                            // ── X positions (fixed offsets, mirrored by -xDir) ──
                                            double xMidR   = bestT.Value.X + mult * 1.7500;
                                            double xMidL   = bestT.Value.X + mult * 3.2500;
                                            double xOuterR = bestT.Value.X + mult * 1.3750;
                                            double xOuterL = bestT.Value.X + mult * 3.6250;

                                            // ── Two vertical lines ────────────────────────────
                                            // Each stub bottom is computed at its own X so it
                                            // meets the diagonal top line exactly (no gap).
                                            var vLineR = new Line(
                                                new Point3d(xInnerR, vertBotYR, 0),
                                                new Point3d(xInnerR, capBottomY, 0));
                                            vLineR.Layer      = _targetLayer;
                                            vLineR.LineWeight = LineWeight.LineWeight035;
                                            btr.AppendEntity(vLineR);
                                            tx.AddNewlyCreatedDBObject(vLineR, true);

                                            var vLineL = new Line(
                                                new Point3d(xInnerL, vertBotYL, 0),
                                                new Point3d(xInnerL, capBottomY, 0));
                                            vLineL.Layer      = _targetLayer;
                                            vLineL.LineWeight = LineWeight.LineWeight035;
                                            btr.AppendEntity(vLineL);
                                            tx.AddNewlyCreatedDBObject(vLineL, true);

                                            // ── U-cap polyline (10 vertices, no arcs) ─────────
                                            //  idx  X           Y
                                            //   0   xInnerR     capBottomY
                                            //   1   xMidR       capBottomY
                                            //   2   xMidR       capStepY
                                            //   3   xOuterR     capStepY
                                            //   4   xOuterR     capTopY    ← surface touch right
                                            //   5   xOuterL     capTopY    ← surface touch left
                                            //   6   xOuterL     capStepY
                                            //   7   xMidL       capStepY
                                            //   8   xMidL       capBottomY
                                            //   9   xInnerL     capBottomY
                                            var capVerts = new (double x, double y)[]
                                            {
                                                (xInnerR, capBottomY),
                                                (xMidR,   capBottomY),
                                                (xMidR,   capStepY),
                                                (xOuterR, capStepY),
                                                (xOuterR, capTopY),
                                                (xOuterL, capTopY),
                                                (xOuterL, capStepY),
                                                (xMidL,   capStepY),
                                                (xMidL,   capBottomY),
                                                (xInnerL, capBottomY),
                                            };

                                            var capPl = new Polyline();
                                            capPl.Layer      = _targetLayer;
                                            capPl.LineWeight = LineWeight.LineWeight035;
                                            capPl.Elevation  = 0.0;
                                            capPl.Normal     = Vector3d.ZAxis;
                                            capPl.Closed     = false;

                                            for (int vi = 0; vi < capVerts.Length; vi++)
                                                capPl.AddVertexAt(vi,
                                                    new Point2d(capVerts[vi].x, capVerts[vi].y),
                                                    0.0, 0.0, 0.0);

                                            btr.AppendEntity(capPl);
                                            tx.AddNewlyCreatedDBObject(capPl, true);
                                        }
                                    }
                                }
                            }
                            catch (System.Exception capEx)
                            {
                                ed.WriteMessage(
                                    $"\n  ⚠ Cap symbol error at Sta {cp.Station:F2}: {capEx.Message}");
                            }

                            // ── Pipe-to-lateral connector lines ───────────────────────────
                            // Drawn ONLY when the lateral was pushed up from its absolute
                            // lowest position (deltaY > 0).  Two diagonals visually link the
                            // existing pipe (selected network) to the lateral lines:
                            //   • Bottom connector: pipe INVERT  → bottom lateral line
                            //   • Top connector:    pipe CROWN   → top lateral line
                            // Slope is 1 du H : 10 du V (= 45° in real space, 10× V.E.).
                            // Endpoints (xBot/yBot, xTop/yTop) were computed earlier so the
                            // lateral lines start exactly there → no gap with the connectors.
                            if (drawConnectors)
                            {
                                try
                                {
                                    var connBot = new Line(
                                        new Point3d(cp.DrawingX, cpInvertY, 0),
                                        new Point3d(xBot, yBot, 0));
                                    connBot.Layer      = _targetLayer;
                                    connBot.LineWeight = LineWeight.LineWeight035;
                                    btr.AppendEntity(connBot);
                                    tx.AddNewlyCreatedDBObject(connBot, true);

                                    var connTop = new Line(
                                        new Point3d(cp.DrawingX, cpCrownY, 0),
                                        new Point3d(xTop, yTop, 0));
                                    connTop.Layer      = _targetLayer;
                                    connTop.LineWeight = LineWeight.LineWeight035;
                                    btr.AppendEntity(connTop);
                                    tx.AddNewlyCreatedDBObject(connTop, true);
                                }
                                catch (System.Exception connEx)
                                {
                                    ed.WriteMessage(
                                        $"\n  ⚠ Connector error at Sta {cp.Station:F2}: {connEx.Message}");
                                }
                            }

                            // ── Build active-constraints string (post-hoc, no branching) ──
                            var constraintParts = new List<string>();
                            if (!double.IsNaN(surfaceStartY)) constraintParts.Add("SurfaceCover");
                            bool sawAbove = false, sawBelow = false;
                            foreach (var lc2 in otherCrossings)
                            {
                                double dx2 = lc2.DrawingX - startX;
                                double dummy2 = 0, cyI2 = 0, cyC2 = 0;
                                if (!pv.FindXYAtStationAndElevation(lc2.Station, lc2.InvertElev, ref dummy2, ref cyI2)) continue;
                                if (!pv.FindXYAtStationAndElevation(lc2.Station, lc2.InvertElev + lc2.OuterDiam, ref dummy2, ref cyC2)) continue;
                                double lb2  = actualStartY + dx2 * tanAngle;
                                double lt2  = lb2 + verticalOffset;
                                bool isAbove = IsCrossingAboveLateral(lb2, lt2, cyI2, cyC2);
                                // "LatBelowPipe" = lateral passes BELOW the crossing pipe (above-rule).
                                // "LatAbovePipe" = lateral passes ABOVE the crossing pipe (below-rule).
                                if (isAbove  && !sawAbove) { constraintParts.Add("LatBelowPipe"); sawAbove = true; }
                                else if (!isAbove && !sawBelow) { constraintParts.Add("LatAbovePipe"); sawBelow = true; }
                            }

                            reports.Add(new LateralReport
                            {
                                PvName = pv.Name, Station = cp.Station,
                                Status = LateralStatus.Drawn,
                                ActualStartY = actualStartY, MinStartY = minStartY,
                                SurfaceStartY = surfaceStartY, DeltaY = deltaY,
                                ActiveConstraints = string.Join("|", constraintParts),
                                FailReason = "",
                                CrossingsInfo = BuildCrossingsInfo(
                                    pv, otherCrossings, startX, tanAngle,
                                    actualStartY, verticalOffset)
                            });

                            lateralsDrawn++;
                        }
                    }

                    tx.Commit();

                    // ── Print structured report ────────────────────────────────────────
                    int rDrawn   = reports.Count(r => r.Status == LateralStatus.Drawn);
                    int rSkipped = reports.Count - rDrawn;
                    int rSurface = reports.Count(r => r.Status == LateralStatus.Drawn && !double.IsNaN(r.SurfaceStartY));
                    int rShifted = reports.Count(r => r.Status == LateralStatus.Drawn && r.DeltaY > 0.001);

                    ed.WriteMessage("\n");
                    ed.WriteMessage("\n--- LATERAL BEAST REPORT -------------------------------------------");
                    ed.WriteMessage($"\n  Profile views processed : {profileViewIds.Count}");
                    ed.WriteMessage($"\n  Laterals drawn          : {rDrawn}");
                    ed.WriteMessage($"\n  Laterals skipped        : {rSkipped}");
                    ed.WriteMessage($"\n  With surface cover      : {rSurface}");
                    ed.WriteMessage($"\n  Shifted by pipe constr. : {rShifted}");
                    ed.WriteMessage("\n--------------------------------------------------------------------");

                    // Group by PV
                    var pvNames = new Dictionary<string, List<LateralReport>>();
                    foreach (var r in reports)
                    {
                        if (!pvNames.ContainsKey(r.PvName)) pvNames[r.PvName] = new List<LateralReport>();
                        pvNames[r.PvName].Add(r);
                    }

                    foreach (var kvp in pvNames)
                    {
                        string pvN   = kvp.Key;
                        var    rList = kvp.Value;
                        int pvDrawn  = rList.Count(r => r.Status == LateralStatus.Drawn);
                        int pvSkip   = rList.Count - pvDrawn;

                        ed.WriteMessage($"\n\n  PV: {pvN}");
                        ed.WriteMessage($"\n  {rList.Count} crossing(s) — {pvDrawn} drawn, {pvSkip} skipped");
                        ed.WriteMessage("\n  +-----------+--------+-----------+-----------+------------------+----------------------+");
                        ed.WriteMessage("\n  | Station   | Status | ActStartY | DeltaY    | Constraints      | Note                 |");
                        ed.WriteMessage("\n  +-----------+--------+-----------+-----------+------------------+----------------------+");

                        foreach (var r in rList)
                        {
                            string staSt  = double.IsNaN(r.Station)      ? "    N/A  " : $"{r.Station,9:F2}";
                            string stSt   = r.Status switch
                            {
                                LateralStatus.Drawn                  => "DRAWN ",
                                LateralStatus.SkippedNoIntersection  => "SKIP-N",
                                LateralStatus.SkippedInfeasible      => "SKIP-I",
                                LateralStatus.SkippedValidationFailed=> "SKIP-V",
                                LateralStatus.SkippedNoTarget        => "SKIP-T",
                                _                                    => "??????"
                            };
                            string aSt = double.IsNaN(r.ActualStartY) ? "      N/A" : $"{r.ActualStartY,9:F4}";
                            string dSt = double.IsNaN(r.DeltaY)       ? "      N/A" : $"{r.DeltaY,9:F4}";
                            string cSt = string.IsNullOrEmpty(r.ActiveConstraints) ? "none" : r.ActiveConstraints;
                            if (cSt.Length > 16) cSt = cSt.Substring(0, 16);
                            string nSt = r.Status == LateralStatus.Drawn ? "ok" : r.FailReason;
                            if (nSt.Length > 20) nSt = nSt.Substring(0, 20);

                            ed.WriteMessage(
                                $"\n  | {staSt} | {stSt} | {aSt} | {dSt} | {cSt,-16} | {nSt,-20} |");
                            if (!string.IsNullOrEmpty(r.CrossingsInfo))
                            {
                                ed.WriteMessage("\n    crossings (other-network pipes intersecting this lateral):");
                                foreach (var ln in r.CrossingsInfo.Split(
                                             new[] { "\r\n", "\n" }, StringSplitOptions.None))
                                {
                                    ed.WriteMessage("\n" + ln);
                                }
                            }
                        }

                        ed.WriteMessage("\n  +-----------+--------+-----------+-----------+------------------+----------------------+");
                    }

                    ed.WriteMessage("\n--------------------------------------------------------------------");
                    ed.WriteMessage($"\n  Done. {lateralsDrawn} lateral(s) drawn.\n");
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
