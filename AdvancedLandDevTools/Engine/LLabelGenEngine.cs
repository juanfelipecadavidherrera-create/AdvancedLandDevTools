using System;
using System.Collections.Generic;
using System.Linq;
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

    // ─────────────────────────────────────────────────────────────────────────
    //  Data carried from QueueLabelCommand() through to InjectStyles().
    // ─────────────────────────────────────────────────────────────────────────
    internal class LLabelGenJob
    {
        public ObjectId      LabelStyleId    { get; set; }
        public ObjectId      MarkerStyleId   { get; set; }
        public HashSet<long> ExistingHandles { get; set; } = new();
        public Database      Db              { get; set; } = null!;
        /// <summary>
        /// Drag offset applied to each new label text box (in host WCS drawing units).
        /// Positive X = right, positive Y = up.  Zero = no drag.
        /// </summary>
        public Vector3d      DragOffset      { get; set; } = new Vector3d(0, 0, 0);
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
        private static readonly Queue<LLabelGenJob> _pendingJobs = new();
        private static bool _eventRegistered = false;
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
            Document doc, bool isXref = false,
            ObjectId chosenLabelStyleId = default,
            ObjectId chosenMarkerStyleId = default,
            Vector3d dragOffset = default)
        {
            if (crossings.Count == 0) return;

            var existingHandles = SnapshotLabelHandles(doc.Database);
            
            _pendingJobs.Enqueue(new LLabelGenJob
            {
                LabelStyleId    = chosenLabelStyleId,
                MarkerStyleId   = chosenMarkerStyleId,
                ExistingHandles = existingHandles,
                Db              = doc.Database,
                DragOffset      = dragOffset
            });

            if (!_eventRegistered)
            {
                doc.CommandEnded += OnLabelCommandEnded;
                _eventRegistered = true;
            }

            var sb = new StringBuilder();

            sb.Append("(progn ");
            sb.Append($"(setq _aldt_ent (handent \"{hostHandle}\")) ");
            sb.Append($"(if _aldt_ent (command \"ADDPROFILEVIEWSTAELEVLBL\" ");
            
            // For XREFs, _aldt_ent is the BlockReference. Civil 3D rejects BlockReferences 
            // for the ADDPROFILEVIEWSTAELEVLBL command, so we simulate a direct point click.
            // For Native, _aldt_ent is the ProfileView itself, which works perfectly.
            if (isXref)
            {
                sb.Append($"\"_NON\" (trans (list {pickPt.X:F6} {pickPt.Y:F6} {pickPt.Z:F6}) 0 1) ");
            }
            else
            {
                sb.Append($"(list _aldt_ent (trans (list {pickPt.X:F6} {pickPt.Y:F6} {pickPt.Z:F6}) 0 1)) ");
            }

            foreach (var cp in crossings)
            {
                if (isXref)
                {
                    // Pass literal numerical values for Station and Elevation
                    // This bypasses Civil 3D's buggy coordinate projection for XREF profile views.
                    sb.Append($"\"{cp.Station:F6}\" \"{cp.Elevation:F6}\" ");
                }
                else
                {
                    // Click 2: horizontal position — X coord picks the station
                    sb.Append($"\"_NON\" (trans (list {cp.DrawingX:F6} {cp.DrawingY:F6} 0.0) 0 1) ");
                    // Click 3: vertical position   — Y coord picks the elevation
                    sb.Append($"\"_NON\" (trans (list {cp.DrawingX:F6} {cp.DrawingY:F6} 0.0) 0 1) ");
                }
            }

            sb.Append("\"\" ) ");   // exit repeat loop + close info
            sb.Append("(princ \"\\nLLABELGEN: no valid entity found to label.\\n\") ) ) ");
            // outer progn closed ──────────────────────────────────────────────

            doc.SendStringToExecute(sb.ToString(), true, false, false);
        }

        // ── Level-2: fires after ADDPROFILEVIEWSTAELEVLBL exits ──────────────
        private static void OnLabelCommandEnded(object sender, CommandEventArgs e)
        {
            // Log every command that ends while we have pending jobs so we can
            // see the exact GlobalCommandName Civil 3D reports.
            if (_pendingJobs.Count > 0)
            {
                var doc2 = Application.DocumentManager.MdiActiveDocument;
                doc2?.Editor.WriteMessage($"\n  DIAG evt: '{e.GlobalCommandName}'");
            }

            // Accept the command however Civil 3D registers it — upper-case, with
            // or without leading underscore, and regardless of locale suffix.
            string cmd = e.GlobalCommandName.ToUpper().TrimStart('_');
            if (cmd == "ADDPROFILEVIEWSTAELEVLBL" ||
                cmd.Contains("ADDPROFILEVIEWSTAELEVLBL"))
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                if (_pendingJobs.Count > 0)
                {
                    var job = _pendingJobs.Dequeue();
                    InjectStyles(doc, job);
                }

                if (_pendingJobs.Count == 0)
                {
                    doc.CommandEnded -= OnLabelCommandEnded;
                    _eventRegistered = false;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Find newly created Labels (not in pre-run snapshot) and apply styles.
        //  Drag: Civil 3D's managed API has no settable DraggedOffset property.
        //  We read each label's anchor via LabelLocation (readable), collect the
        //  per-label drag targets, commit styles, then fire a single LISP call
        //  that sets LabelLocation on each label via COM (vlax-put-property).
        //  Setting LabelLocation to a position different from the anchor puts
        //  the label into dragged state and Civil 3D draws the leader automatically.
        // ─────────────────────────────────────────────────────────────────────
        private static void InjectStyles(Document doc, LLabelGenJob job)
        {
            bool hasDrag = job.DragOffset.Length > 0;

            doc.Editor.WriteMessage(
                $"\n  DIAG InjectStyles: hasDrag={hasDrag}  offset=({job.DragOffset.X:F3},{job.DragOffset.Y:F3})");

            int totalNew = 0, totalLabels = 0;

            try
            {
                Database db = job.Db;

                using (Transaction dbTx = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)dbTx.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)dbTx.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    foreach (ObjectId id in ms)
                    {
                        if (job.ExistingHandles.Contains(id.Handle.Value)) continue;
                        totalNew++;

                        CivilDB.Label? lbl;
                        try { lbl = dbTx.GetObject(id, OpenMode.ForWrite) as CivilDB.Label; }
                        catch { continue; }
                        if (lbl == null) continue;
                        totalLabels++;

                        // One-time .NET reflection dump
                        if (totalLabels == 1)
                        {
                            doc.Editor.WriteMessage($"\n  DIAG type: {lbl.GetType().FullName}");
                            var dragProps = lbl.GetType()
                                .GetProperties(
                                    System.Reflection.BindingFlags.Public |
                                    System.Reflection.BindingFlags.Instance)
                                .Where(p =>
                                    p.Name.IndexOf("drag",     System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    p.Name.IndexOf("offset",   System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    p.Name.IndexOf("location", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    p.Name.IndexOf("text",     System.StringComparison.OrdinalIgnoreCase) >= 0)
                                .Select(p =>
                                    $"{p.Name}[{(p.CanWrite ? "RW" : "RO")}:{p.PropertyType.Name}]");
                            doc.Editor.WriteMessage(
                                $"\n  DIAG drag-props: {string.Join(", ", dragProps)}");
                        }

                        if (!job.LabelStyleId.IsNull && job.LabelStyleId.IsValid)
                            try { lbl.StyleId = job.LabelStyleId; } catch { }

                        if (!job.MarkerStyleId.IsNull && job.MarkerStyleId.IsValid)
                            try { dynamic d = lbl; d.MarkerStyleId = job.MarkerStyleId; } catch { }

                        if (hasDrag)
                        {
                            try
                            {
                                Point3d anchor = lbl.LabelLocation;
                                double  tx     = anchor.X + job.DragOffset.X;
                                double  ty     = anchor.Y + job.DragOffset.Y;
                                
                                // Direct .NET assignment automatically puts the label into Dragged State
                                lbl.LabelLocation = new Point3d(tx, ty, 0);

                                doc.Editor.WriteMessage(
                                    $"\n  DIAG drag applied: h={id.Handle} anchor({anchor.X:F2},{anchor.Y:F2}) -> target({tx:F2},{ty:F2})");
                            }
                            catch (Exception ex)
                            {
                                doc.Editor.WriteMessage($"\n  DIAG drag err: {ex.Message}");
                            }
                        }
                    }

                    dbTx.Commit();
                }
            }
            catch (Exception ex)
            {
                doc.Editor.WriteMessage($"\n  ⚠ Style injection error: {ex.Message}\n");
            }

            doc.Editor.WriteMessage(
                $"\n  DIAG InjectStyles done: totalNew={totalNew} totalLabels={totalLabels}");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Snapshot handles of all current Labels in model space.
        // ─────────────────────────────────────────────────────────────────────
        private static HashSet<long> SnapshotLabelHandles(Database db)
        {
            var handles = new HashSet<long>();
            try
            {
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tx.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId id in ms)
                    {
                        try
                        {
                            if (tx.GetObject(id, OpenMode.ForRead) is CivilDB.Label)
                                handles.Add(id.Handle.Value);
                        }
                        catch { }
                    }
                    tx.Abort();
                }
            }
            catch { }
            return handles;
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
