using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivilDB = Autodesk.Civil.DatabaseServices;
using AdvancedLandDevTools.Helpers;

namespace AdvancedLandDevTools.Engine
{
    // ─────────────────────────────────────────────────────────────────────────
    //  One crossing point to label inside a profile view.
    // ─────────────────────────────────────────────────────────────────────────
    public class CrossingLabelPoint
    {
        public double   Station      { get; set; }
        public double   Elevation    { get; set; }   // true invert from 3D model
        public double   DrawingX     { get; set; }   // label insertion X in HOST WCS
        public double   DrawingY     { get; set; }   // label insertion Y in HOST WCS
        public ObjectId NetworkId    { get; set; }   // gravity Network or PressurePipeNetwork
        public string   PipeName     { get; set; } = "";
        public string   NetworkName  { get; set; } = "";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    //  LLabelGenEngine
    //
    //  Finds crossing pipe inverts using PipeAlignmentIntersector (same as
    //  RrNetworkCheckCommand) and queues native Civil 3D label placement via
    //  LISP (command ... pause ...) so the user clicks the PV once, then all
    //  label coordinates are fed automatically.
    // ═══════════════════════════════════════════════════════════════════════════
    public static class LLabelGenEngine
    {
        // ─────────────────────────────────────────────────────────────────────
        //  NATIVE: profile view is in the current (host) database.
        // ─────────────────────────────────────────────────────────────────────
        public static List<CrossingLabelPoint> FindCrossingPoints(
            ObjectId pvId, Database db)
        {
            var points = new List<CrossingLabelPoint>();

            using (var tx = db.TransactionManager.StartTransaction())
            {
                var pv = tx.GetObject(pvId, OpenMode.ForRead) as CivilDB.ProfileView;
                if (pv == null) { tx.Abort(); return points; }

                var align = tx.GetObject(pv.AlignmentId, OpenMode.ForRead) as CivilDB.Alignment;
                if (align == null) { tx.Abort(); return points; }

                CollectFromDatabase(pv, align, db, tx, Matrix3d.Identity, points);
                tx.Abort();
            }
            return points;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  XREF: profile view is inside an XREF block reference.
        // ─────────────────────────────────────────────────────────────────────
        public static List<CrossingLabelPoint> FindCrossingPointsInXref(
            Database xrefDb, Matrix3d xrefToHost, string pvName)
        {
            var points = new List<CrossingLabelPoint>();

            try
            {
                using (var xTx = xrefDb.TransactionManager.StartTransaction())
                {
                    var xMs = xTx.GetObject(xrefDb.CurrentSpaceId, OpenMode.ForRead)
                              as BlockTableRecord;
                    if (xMs == null) { xTx.Abort(); return points; }

                    // Find the exact ProfileView in the XREF DB by name
                    CivilDB.ProfileView? targetPv = null;
                    foreach (ObjectId xId in xMs)
                    {
                        CivilDB.ProfileView xPv;
                        try { xPv = xTx.GetObject(xId, OpenMode.ForRead) as CivilDB.ProfileView; }
                        catch { continue; }
                        if (xPv == null) continue;

                        if (xPv.Name.Equals(pvName, StringComparison.OrdinalIgnoreCase))
                        {
                            targetPv = xPv;
                            break;
                        }
                    }

                    if (targetPv == null) { xTx.Abort(); return points; }

                    var align = xTx.GetObject(targetPv.AlignmentId, OpenMode.ForRead)
                                as CivilDB.Alignment;
                    if (align == null) { xTx.Abort(); return points; }

                    CollectFromDatabase(targetPv, align, xrefDb, xTx, xrefToHost, points);
                    xTx.Abort();
                }
            }
            catch { }

            return points;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Core crossing collector — works on any database (host or XREF).
        //
        //  Phase 1: proxy scan to restrict to pipes drawn in this profile view.
        //  Phase 2: PipeAlignmentIntersector for true 3D invert elevation.
        //  Populates network info (NetworkId, PipeName, NetworkName) on each point.
        // ─────────────────────────────────────────────────────────────────────

        private static void CollectFromDatabase(
            CivilDB.ProfileView pv,
            CivilDB.Alignment   align,
            Database            db,
            Transaction         tx,
            Matrix3d            entityToHostWCS,
            List<CrossingLabelPoint> points)
        {
            double pvStaStart = pv.StationStart;
            double pvStaEnd   = pv.StationEnd;

            var btr = tx.GetObject(db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
            if (btr == null) return;

            // Direct scan — mirrors RrNetworkCheckCommand.CollectCrossings exactly.
            // Cast every entity to CivilDB.Pipe / CivilDB.PressurePipe; no proxy phase needed.
            // This finds ALL pipes regardless of their proxy representation.
            foreach (ObjectId id in btr)
            {
                try
                {
                    var obj = tx.GetObject(id, OpenMode.ForRead);

                    ObjectId netId    = ObjectId.Null;
                    string   pipeName = "";
                    string   netName  = "";
                    bool     isPipe   = false;

                    if (obj is CivilDB.Pipe gp)
                    {
                        netId    = gp.NetworkId;
                        pipeName = gp.Name;
                        isPipe   = true;
                        try
                        {
                            var net = tx.GetObject(netId, OpenMode.ForRead) as CivilDB.Network;
                            if (net != null) netName = net.Name;
                        }
                        catch { }
                    }
                    else if (obj is CivilDB.PressurePipe pp)
                    {
                        netId    = pp.NetworkId;
                        pipeName = pp.Name;
                        isPipe   = true;
                        try
                        {
                            var net = tx.GetObject(netId, OpenMode.ForRead) as CivilDB.PressurePipeNetwork;
                            if (net != null) netName = net.Name;
                        }
                        catch { }
                    }

                    if (!isPipe) continue;

                    foreach (var c in PipeAlignmentIntersector.FindCrossings(id, align, tx))
                    {
                        if (c.Station < pvStaStart - 0.5 || c.Station > pvStaEnd + 0.5)
                            continue;

                        double cx = 0, cy = 0;
                        if (!pv.FindXYAtStationAndElevation(
                                c.Station, c.InvertElevation, ref cx, ref cy))
                            continue;

                        var hostPt = new Point3d(cx, cy, 0).TransformBy(entityToHostWCS);

                        points.Add(new CrossingLabelPoint
                        {
                            Station     = c.Station,
                            Elevation   = c.InvertElevation,
                            DrawingX    = hostPt.X,
                            DrawingY    = hostPt.Y,
                            NetworkId   = netId,
                            PipeName    = pipeName,
                            NetworkName = netName
                        });
                    }
                }
                catch { }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Queue native Civil 3D label placement via LISP.
        //
        //  ADDPROFILEVIEWSTAELEVLBL needs 3 POINT CLICKS per label:
        //    Click 1 — "Select a profile view:"
        //        → (nentselp point) drills through any BlockReference (including
        //          XREF) and returns the actual ProfileView entity underneath —
        //          identical to what Civil 3D receives on an interactive click.
        //          pvNearX/Y must be a point visually inside the PV grid (we use
        //          5 % inset from the bottom-left corner of its host-WCS extents).
        //    Click 2 — "Specify horizontal position:" (sets station)
        //        → (list DrawingX DrawingY 0.0)  — X coordinate determines station.
        //    Click 3 — "Specify vertical position:"   (sets elevation)
        //        → (list DrawingX DrawingY 0.0)  — Y coordinate determines elevation.
        //
        //  Providing the same drawing point for clicks 2 and 3 is correct:
        //  DrawingX/Y was produced by ProfileView.FindXYAtStationAndElevation so it
        //  encodes the exact invert location.  Civil 3D reads X for station and Y
        //  for elevation independently.
        //
        //  After click 3 the label is placed; the command repeats clicks 2+3
        //  for each additional label, then "" exits.
        //
        //  Works identically for native PVs (nentselp returns the PV directly)
        //  and XREF PVs (nentselp drills through the host BlockReference).
        // ─────────────────────────────────────────────────────────────────────
        public static void QueueLabelCommand(
            List<CrossingLabelPoint> crossings,
            string hostHandle, Point3d pickPt,
            Document doc)
        {
            if (crossings.Count == 0) return;

            var sb = new StringBuilder();

            sb.Append("(progn ");
            sb.Append($"(setq _aldt_ent (handent \"{hostHandle}\")) ");
            sb.Append($"(if _aldt_ent (command \"ADDPROFILEVIEWSTAELEVLBL\" ");
            
            // Use entsel list format: (list ENAME (list X Y Z))
            // This robustly selects XREF BlockReferences at the correct pick point.
            // AutoCAD expects coordinate points relative to current UCS.
            // Using (trans pt 0 1) converts our WCS programmatic coordinates correctly into UCS.
            sb.Append($"(list _aldt_ent (trans (list {pickPt.X:F6} {pickPt.Y:F6} {pickPt.Z:F6}) 0 1)) ");

            foreach (var cp in crossings)
            {
                // Click 2: horizontal position — X coord picks the station
                sb.Append($"\"_NON\" (trans (list {cp.DrawingX:F6} {cp.DrawingY:F6} 0.0) 0 1) ");
                // Click 3: vertical position   — Y coord picks the elevation
                sb.Append($"\"_NON\" (trans (list {cp.DrawingX:F6} {cp.DrawingY:F6} 0.0) 0 1) ");
            }

            sb.Append("\"\" ) ");   // exit repeat loop + close info
            sb.Append("(princ \"\\nLLABELGEN: no valid entity found to label.\\n\") ) ) ");
            // outer progn closed ──────────────────────────────────────────────

            doc.SendStringToExecute(sb.ToString() + "\n", true, false, false);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Format station as N+NN.NN (e.g. 1+23.45)
        // ─────────────────────────────────────────────────────────────────────
        public static string FormatStation(double station)
        {
            int    full = (int)(station / 100.0);
            double rem  = station - full * 100.0;
            return $"{full}+{rem:00.00}";
        }
    }
}
