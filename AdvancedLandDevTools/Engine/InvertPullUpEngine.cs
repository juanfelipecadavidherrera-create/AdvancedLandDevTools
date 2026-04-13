using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivilApp = Autodesk.Civil.ApplicationServices;
using CivilDB  = Autodesk.Civil.DatabaseServices;
using AdvancedLandDevTools.Helpers;

namespace AdvancedLandDevTools.Engine
{
    public class InvertPullUpResult
    {
        public bool    Success           { get; set; }
        public string  PipeName         { get; set; } = "";
        public string  PipeKind         { get; set; } = "";
        public double  StartInvert       { get; set; }
        public double  EndInvert         { get; set; }
        public double  PipeLength2D      { get; set; }
        public double  DistanceAlongPipe { get; set; }
        public double  InvertAtPoint     { get; set; }
        public string  ErrorMessage      { get; set; } = "";
        // WCS pipe endpoints — stored so the deferred handler can compute invert
        // at the label placement point without needing the pipe entity again.
        public Point3d PipeStartWCS      { get; set; }
        public Point3d PipeEndWCS        { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Data carried from QueueLabelCommand() through to InjectInvertElevation().
    // ─────────────────────────────────────────────────────────────────────────
    internal class InvertLabelJob
    {
        public Point3d       PipeStartWCS    { get; set; }
        public Point3d       PipeEndWCS      { get; set; }
        public double        StartInvert     { get; set; }
        public double        EndInvert       { get; set; }
        /// <summary>Invert computed at the user's click point — used directly for the label.</summary>
        public double        InvertAtClick   { get; set; }
        public string        PipeName        { get; set; } = "";
        public string        PipeKind        { get; set; } = "";
        public ObjectId      LabelStyleId    { get; set; }
        public HashSet<long> ExistingHandles { get; set; } = new();
        public Database      Db              { get; set; } = null!;
    }

    public static class InvertPullUpEngine
    {
        // ── Deferred label queue — PipeMagic two-level CommandEnded pattern ──
        private static readonly Queue<InvertLabelJob> _pendingJobs    = new();
        private static bool                           _lvl1Registered = false;
        private static InvertLabelJob?                _currentJob     = null;

        // ─────────────────────────────────────────────────────────────────────
        //  Calculate invert at a clicked point along a host-drawing pipe.
        // ─────────────────────────────────────────────────────────────────────
        public static InvertPullUpResult Calculate(
            ObjectId    pipeId,
            Point3d     clickedPoint,
            Transaction tx)
        {
            var r = new InvertPullUpResult();
            try
            {
                var obj = tx.GetObject(pipeId, OpenMode.ForRead);

                Point3d pipeStart, pipeEnd;
                double  startInvert, endInvert;
                string  pipeName, pipeKind;

                if (obj is CivilDB.Pipe gPipe)
                {
                    pipeStart   = gPipe.StartPoint;
                    pipeEnd     = gPipe.EndPoint;
                    double gr   = gPipe.InnerDiameterOrWidth / 2.0;
                    startInvert = gPipe.StartPoint.Z - gr;
                    endInvert   = gPipe.EndPoint.Z   - gr;
                    pipeName    = gPipe.Name;
                    pipeKind    = "Gravity";
                }
                else if (obj is CivilDB.PressurePipe pPipe)
                {
                    pipeStart   = pPipe.StartPoint;
                    pipeEnd     = pPipe.EndPoint;
                    double pr   = pPipe.InnerDiameter / 2.0;
                    startInvert = pPipe.StartPoint.Z - pr;
                    endInvert   = pPipe.EndPoint.Z   - pr;
                    pipeName    = pPipe.Name;
                    pipeKind    = "Pressure";
                }
                else
                {
                    r.ErrorMessage = "Selected object is not a gravity or pressure pipe.";
                    return r;
                }

                return CalculateFromGeometry(
                    pipeStart, pipeEnd, startInvert, endInvert,
                    pipeName, pipeKind, clickedPoint);
            }
            catch (Exception ex) { r.ErrorMessage = ex.Message; }
            return r;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Calculate invert from pre-resolved WCS pipe geometry.
        //  Also stores PipeStartWCS / PipeEndWCS on the result for later use.
        // ─────────────────────────────────────────────────────────────────────
        public static InvertPullUpResult CalculateFromGeometry(
            Point3d pipeStart,   Point3d pipeEnd,
            double  startInvert, double  endInvert,
            string  pipeName,    string  pipeKind,
            Point3d clickedPoint)
        {
            var r = new InvertPullUpResult();
            try
            {
                var    s2d = new Point2d(pipeStart.X,    pipeStart.Y);
                var    e2d = new Point2d(pipeEnd.X,      pipeEnd.Y);
                var    c2d = new Point2d(clickedPoint.X, clickedPoint.Y);
                double len = s2d.GetDistanceTo(e2d);

                double dist = 0;
                if (len > 0.001)
                {
                    double dx = e2d.X - s2d.X, dy = e2d.Y - s2d.Y;
                    double t  = ((c2d.X - s2d.X) * dx + (c2d.Y - s2d.Y) * dy)
                                / (len * len);
                    t    = Math.Max(0.0, Math.Min(1.0, t));
                    dist = t * len;
                }

                double ratio  = len > 0.001 ? dist / len : 0.0;
                double invert = startInvert + ratio * (endInvert - startInvert);

                r.Success           = true;
                r.PipeName          = pipeName;
                r.PipeKind          = pipeKind;
                r.StartInvert       = startInvert;
                r.EndInvert         = endInvert;
                r.PipeLength2D      = len;
                r.DistanceAlongPipe = dist;
                r.InvertAtPoint     = invert;
                r.PipeStartWCS      = pipeStart;
                r.PipeEndWCS        = pipeEnd;
            }
            catch (Exception ex) { r.ErrorMessage = ex.Message; }
            return r;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Calculate invert at the exact point where a pipe crosses an alignment.
        //  Uses geometric intersection instead of click-point projection.
        // ─────────────────────────────────────────────────────────────────────
        public static InvertPullUpResult CalculateAtAlignmentCrossing(
            ObjectId          pipeId,
            CivilDB.Alignment alignment,
            Transaction       tx,
            ObjectId?         surfaceId = null)
        {
            var r = new InvertPullUpResult();
            try
            {
                var crossings = PipeAlignmentIntersector.FindCrossings(
                    pipeId, alignment, tx, surfaceId);

                if (crossings.Count == 0)
                {
                    r.ErrorMessage = "Pipe does not cross the selected alignment.";
                    return r;
                }

                // Use the first crossing (nearest to alignment start)
                var c = crossings[0];
                r.Success           = true;
                r.PipeName          = c.PipeName;
                r.PipeKind          = c.PipeKind;
                r.InvertAtPoint     = c.InvertElevation;
                r.DistanceAlongPipe = 0; // not applicable in alignment mode

                // Fill pipe endpoints for downstream deferred-label logic
                var obj = tx.GetObject(pipeId, OpenMode.ForRead);
                if (obj is CivilDB.Pipe gp)
                {
                    r.PipeStartWCS = gp.StartPoint;
                    r.PipeEndWCS   = gp.EndPoint;
                    r.StartInvert  = gp.StartPoint.Z - gp.InnerDiameterOrWidth / 2.0;
                    r.EndInvert    = gp.EndPoint.Z   - gp.InnerDiameterOrWidth / 2.0;
                    r.PipeLength2D = new Point2d(gp.StartPoint.X, gp.StartPoint.Y)
                                         .GetDistanceTo(new Point2d(gp.EndPoint.X, gp.EndPoint.Y));
                }
                else if (obj is CivilDB.PressurePipe pp)
                {
                    r.PipeStartWCS = pp.StartPoint;
                    r.PipeEndWCS   = pp.EndPoint;
                    r.StartInvert  = pp.StartPoint.Z - pp.InnerDiameter / 2.0;
                    r.EndInvert    = pp.EndPoint.Z   - pp.InnerDiameter / 2.0;
                    r.PipeLength2D = new Point2d(pp.StartPoint.X, pp.StartPoint.Y)
                                         .GetDistanceTo(new Point2d(pp.EndPoint.X, pp.EndPoint.Y));
                }
            }
            catch (Exception ex) { r.ErrorMessage = ex.Message; }
            return r;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Queue the interactive label operation.
        //
        //  After INVERTPULLUP exits, Level-1 CommandEnded fires _AeccAddAlignOffLbl
        //  with no pre-filled coordinates so the user interacts normally:
        //    1. Select alignment (XREF-aware — Civil 3D ignores layer-off state)
        //    2. Click where to place the label (on the pipe)
        //
        //  Level-2 CommandEnded finds the new StationOffsetLabel, derives its
        //  WCS position from its bounding box, interpolates the invert on the
        //  stored pipe geometry, applies the chosen style, and injects the
        //  invert elevation as a text override.
        // ─────────────────────────────────────────────────────────────────────
        public static string QueueLabelCommand(
            Database db,
            InvertPullUpResult pipeResult,
            ObjectId           labelStyleId)
        {
            var existingHandles = SnapshotLabelHandles(db);

            _pendingJobs.Enqueue(new InvertLabelJob
            {
                PipeStartWCS    = pipeResult.PipeStartWCS,
                PipeEndWCS      = pipeResult.PipeEndWCS,
                StartInvert     = pipeResult.StartInvert,
                EndInvert       = pipeResult.EndInvert,
                InvertAtClick   = pipeResult.InvertAtPoint,
                PipeName        = pipeResult.PipeName,
                PipeKind        = pipeResult.PipeKind,
                LabelStyleId    = labelStyleId,
                ExistingHandles = existingHandles,
                Db              = db
            });

            if (!_lvl1Registered)
            {
                Application.DocumentManager.MdiActiveDocument!
                    .CommandEnded += OnInvertCommandEnded;
                _lvl1Registered = true;
            }

            return "Label command queued — select alignment then click on the pipe to place label.";
        }

        // ── Level-1: fires after INVERTPULLUP exits ───────────────────────────
        private static void OnInvertCommandEnded(object sender, CommandEventArgs e)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            doc.CommandEnded -= OnInvertCommandEnded;
            _lvl1Registered   = false;

            ProcessNextJob(doc);
        }

        // ── Dequeue one job, register Level-2, fire _AeccAddAlignOffLbl ──────
        private static void ProcessNextJob(Document doc)
        {
            if (_pendingJobs.Count == 0) return;

            _currentJob = _pendingJobs.Dequeue();
            doc.CommandEnded += OnLabelCommandEnded;

            // Fire Civil 3D's native label command with NO pre-filled input.
            // The user interacts exactly as they would manually:
            //   click alignment → click position on pipe.
            // The trailing space acts as an initial Enter in case the command
            // needs one before presenting its first prompt.
            doc.SendStringToExecute("_AeccAddAlignOffLbl ", true, false, false);
        }

        // ── Level-2: fires after _AeccAddAlignOffLbl exits ───────────────────
        private static void OnLabelCommandEnded(object sender, CommandEventArgs e)
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            doc.CommandEnded -= OnLabelCommandEnded;

            var job    = _currentJob;
            _currentJob = null;

            if (job != null)
                InjectInvertElevation(doc, job);

            // Chain: handle any further queued jobs
            ProcessNextJob(doc);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Find newly created StationOffsetLabels (not in pre-run snapshot),
        //  compute invert from label bounding-box centre projected onto the pipe,
        //  apply the chosen style, and inject invert as a text override.
        // ─────────────────────────────────────────────────────────────────────
        private static void InjectInvertElevation(Document doc, InvertLabelJob job)
        {
            try
            {
                Database db       = job.Db;
                int      injected = 0;

                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tx.GetObject(
                        bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    foreach (ObjectId id in ms)
                    {
                        // Skip labels that existed before the command ran
                        if (job.ExistingHandles.Contains(id.Handle.Value)) continue;

                        CivilDB.StationOffsetLabel? lbl;
                        try
                        {
                            lbl = tx.GetObject(id, OpenMode.ForRead)
                                  as CivilDB.StationOffsetLabel;
                        }
                        catch { continue; }
                        if (lbl == null) continue;

                        // Interpolate invert at the label's position on the pipe.
                        // Use the bbox CENTER projected onto the pipe axis — this is
                        // more reliable than bbox corners (which shift with text style).
                        double invert = job.InvertAtClick; // fallback
                        try
                        {
                            var ext = lbl.GeometricExtents;
                            double cx = (ext.MinPoint.X + ext.MaxPoint.X) / 2.0;
                            double cy = (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0;

                            // Project bbox center onto the pipe axis
                            double sx = job.PipeStartWCS.X, sy = job.PipeStartWCS.Y;
                            double ex = job.PipeEndWCS.X,   ey = job.PipeEndWCS.Y;
                            double pdx = ex - sx, pdy = ey - sy;
                            double lenSq = pdx * pdx + pdy * pdy;

                            if (lenSq > 1e-6)
                            {
                                double t = ((cx - sx) * pdx + (cy - sy) * pdy) / lenSq;
                                t = Math.Max(0.0, Math.Min(1.0, t));
                                invert = job.StartInvert + t * (job.EndInvert - job.StartInvert);
                            }
                        }
                        catch { /* fallback to InvertAtClick */ }

                        // ── Open label for write ───────────────────────────────
                        lbl = (CivilDB.StationOffsetLabel)
                              tx.GetObject(id, OpenMode.ForWrite);

                        // ── Apply the style chosen in the dialog ───────────────
                        // Changing StyleId before GetTextComponentIds() ensures
                        // the component IDs belong to the chosen style.
                        if (!job.LabelStyleId.IsNull && job.LabelStyleId.IsValid)
                        {
                            try { lbl.StyleId = job.LabelStyleId; }
                            catch { }
                        }

                        // ── Inject invert elevation into first text component ──
                        try
                        {
                            var compIds = lbl.GetTextComponentIds();
                            if (compIds.Count > 0)
                            {
                                string origText = GetStyleFirstText(lbl, tx);
                                string ov = string.IsNullOrEmpty(origText)
                                    ? $"INV. ELEV. = {invert:F3}'"
                                    : origText + $"\\PINV. ELEV. = {invert:F3}'";

                                lbl.SetTextComponentOverride(
                                    compIds[0], ov,
                                    Autodesk.Civil.TextJustificationType.Center);

                                injected++;

                                try
                                {
                                    System.Windows.Clipboard.SetText($"{invert:F3}");
                                }
                                catch { }

                                doc.Editor.WriteMessage(
                                    $"\n  ✓ {job.PipeName} — INV. ELEV. = {invert:F3}'" +
                                    $"  Copied to clipboard.\n");
                            }
                        }
                        catch { }
                    }

                    tx.Commit();
                }

                if (injected == 0)
                    doc.Editor.WriteMessage(
                        "\n  ⚠ _AeccAddAlignOffLbl ended but no new " +
                        "StationOffsetLabel was found. Did you place a label?\n");
            }
            catch (Exception ex)
            {
                doc.Editor.WriteMessage(
                    $"\n  ⚠ Invert injection error: {ex.Message}\n");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Snapshot handles of all current StationOffsetLabels in model space.
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
                            if (tx.GetObject(id, OpenMode.ForRead)
                                is CivilDB.StationOffsetLabel)
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
        //  Read the text-expression string from the label's first style component.
        //  Called AFTER StyleId is set so it reflects the chosen style.
        // ─────────────────────────────────────────────────────────────────────
        private static string GetStyleFirstText(CivilDB.Label label, Transaction tx)
        {
            try
            {
                var style = tx.GetObject(label.StyleId, OpenMode.ForRead)
                            as CivilDB.Styles.LabelStyle;
                if (style == null) return "";

                var comps = style.GetComponents(
                    CivilDB.Styles.LabelStyleComponentType.Text);
                if (comps.Count == 0) return "";

                var tc = tx.GetObject(comps[0], OpenMode.ForRead)
                         as CivilDB.Styles.LabelStyleTextComponent;
                return tc?.Text.Contents.Value ?? "";
            }
            catch { return ""; }
        }
    }
}
