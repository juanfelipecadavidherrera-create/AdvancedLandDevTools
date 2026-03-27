using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AdvancedLandDevTools.Engine
{
    public class VpCutResult
    {
        public int ViewportsCreated { get; set; }
        public List<string> Log { get; } = new();
        public void Info(string m) => Log.Add($"  ℹ  {m}");
        public void Ok(string m)   => Log.Add($"  ✓  {m}");
        public void Fail(string m) => Log.Add($"  ✗  {m}");
    }

    public static class VpCutEngine
    {
        private struct VpProps
        {
            public double Scale;
            public Point2d ViewCenter;   // DCS center (relative to ViewTarget)
            public Point3d ViewTarget;   // WCS target point
            public Point3d CenterPoint;  // paper-space center
            public double TwistAngle;
            public bool Locked;
            public ObjectId VisStyleId;
            public ObjectId LayerId;
            public double ViewHeight;
            public double Width;
            public double Height;
            public ObjectIdCollection FrozenLayers;

            public double WcsViewX => ViewTarget.X + ViewCenter.X;
            public double WcsViewY => ViewTarget.Y + ViewCenter.Y;
        }

        private struct ClipInfo
        {
            public List<Point2d> PsClipPts;   // clip boundary in paper space
            public string Handle;
            public int Verts;
        }

        public static VpCutResult Run(ObjectId sourceVpId, IList<ObjectId> shapeIds)
        {
            var result = new VpCutResult();
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) { result.Fail("No active document."); return result; }
            Database db = doc.Database;
            Editor ed = doc.Editor;

            VpProps src;
            ObjectId layoutBtrId;
            var clips = new List<ClipInfo>();

            // ══════════════════════════════════════════════════════════════
            //  TX 1: Read source VP properties, compute clip boundaries
            // ══════════════════════════════════════════════════════════════
            using (var tx = db.TransactionManager.StartTransaction())
            {
                var srcVp = tx.GetObject(sourceVpId, OpenMode.ForRead) as Viewport;
                if (srcVp == null)
                {
                    result.Fail("Not a viewport."); tx.Abort(); return result;
                }

                src = new VpProps
                {
                    Scale       = srcVp.CustomScale,
                    ViewCenter  = srcVp.ViewCenter,
                    ViewTarget  = srcVp.ViewTarget,
                    CenterPoint = srcVp.CenterPoint,
                    TwistAngle  = srcVp.TwistAngle,
                    Locked      = srcVp.Locked,
                    VisStyleId  = srcVp.VisualStyleId,
                    LayerId     = srcVp.LayerId,
                    ViewHeight  = srcVp.ViewHeight,
                    Width       = srcVp.Width,
                    Height      = srcVp.Height,
                    FrozenLayers = new ObjectIdCollection()
                };

                try
                {
                    var fl = srcVp.GetFrozenLayers();
                    if (fl != null) foreach (ObjectId id in fl) src.FrozenLayers.Add(id);
                }
                catch { }

                layoutBtrId = srcVp.OwnerId;

                result.Info($"Source VP: scale={src.Scale:F6}");
                result.Info($"  ViewCenter(DCS)=({src.ViewCenter.X:F2},{src.ViewCenter.Y:F2})");
                result.Info($"  ViewTarget(WCS)=({src.ViewTarget.X:F2},{src.ViewTarget.Y:F2},{src.ViewTarget.Z:F2})");
                result.Info($"  PS center=({src.CenterPoint.X:F2},{src.CenterPoint.Y:F2})  size={src.Width:F2}x{src.Height:F2}");

                foreach (ObjectId shapeId in shapeIds)
                {
                    try
                    {
                        var curve = tx.GetObject(shapeId, OpenMode.ForRead) as Curve;
                        if (curve == null || !curve.Closed)
                        {
                            result.Fail($"{shapeId.Handle}: not a closed curve — skipped.");
                            continue;
                        }

                        var msVerts = GetCurveVertices(curve);
                        if (msVerts.Count < 3)
                        {
                            result.Fail($"{shapeId.Handle}: too few vertices — skipped.");
                            continue;
                        }

                        // Transform each vertex from model space (WCS) to paper space
                        var psPts = new List<Point2d>();
                        foreach (var ms in msVerts)
                            psPts.Add(MsToPsPoint(ms, src));

                        clips.Add(new ClipInfo
                        {
                            PsClipPts = psPts,
                            Handle    = shapeId.Handle.ToString(),
                            Verts     = msVerts.Count
                        });

                        result.Info($"  Shape {shapeId.Handle}: {msVerts.Count} verts → clip ready");
                    }
                    catch (Exception ex)
                    {
                        result.Fail($"{shapeId.Handle}: {ex.Message}");
                    }
                }
                tx.Commit();
            }

            if (clips.Count == 0)
            {
                result.Info("No valid shapes — original kept.");
                return result;
            }

            // ══════════════════════════════════════════════════════════════
            //  TX 2: Create ALL viewports (OFF) + erase source in one TX.
            //  Each VP is an EXACT clone of the source. Only the clip
            //  polyline differs. VPs are created OFF — turned ON in TX 3.
            // ══════════════════════════════════════════════════════════════
            var vpIds = new List<ObjectId>();

            using (var tx = db.TransactionManager.StartTransaction())
            {
                var btr = tx.GetObject(layoutBtrId, OpenMode.ForWrite) as BlockTableRecord;
                if (btr == null) { result.Fail("Layout BTR error."); tx.Abort(); return result; }

                foreach (var c in clips)
                {
                    try
                    {
                        var vp = new Viewport();
                        btr.AppendEntity(vp);
                        tx.AddNewlyCreatedDBObject(vp, true);

                        // Clone ALL properties from source — identical viewport
                        vp.CenterPoint = src.CenterPoint;
                        vp.Width       = src.Width;
                        vp.Height      = src.Height;
                        vp.LayerId     = src.LayerId;
                        // Do NOT set On here — must be in separate TX

                        // Exact same view as original
                        vp.ViewTarget  = src.ViewTarget;
                        vp.ViewCenter  = src.ViewCenter;
                        vp.ViewHeight  = src.ViewHeight;
                        vp.CustomScale = src.Scale;
                        vp.TwistAngle  = src.TwistAngle;

                        try
                        {
                            if (src.VisStyleId != ObjectId.Null && src.VisStyleId.IsValid)
                                vp.VisualStyleId = src.VisStyleId;
                        }
                        catch { }

                        if (src.FrozenLayers.Count > 0)
                        {
                            try { vp.FreezeLayersInViewport(src.FrozenLayers.GetEnumerator()); }
                            catch { }
                        }

                        // Clip polyline in paper space
                        var pl = new Polyline();
                        for (int i = 0; i < c.PsClipPts.Count; i++)
                            pl.AddVertexAt(i, c.PsClipPts[i], 0, 0, 0);
                        pl.Closed  = true;
                        pl.LayerId = src.LayerId;
                        btr.AppendEntity(pl);
                        tx.AddNewlyCreatedDBObject(pl, true);

                        vp.NonRectClipEntityId = pl.ObjectId;
                        vp.NonRectClipOn       = true;
                        vp.Locked              = src.Locked;

                        vpIds.Add(vp.ObjectId);
                        result.Ok($"Created VP from shape {c.Handle} ({c.Verts} verts)");
                        result.ViewportsCreated++;
                    }
                    catch (Exception ex)
                    {
                        result.Fail($"VP {c.Handle}: {ex.Message}");
                    }
                }

                // Erase the original viewport
                try
                {
                    var srcVp = tx.GetObject(sourceVpId, OpenMode.ForWrite) as Viewport;
                    if (srcVp != null)
                    {
                        srcVp.Erase();
                        result.Info("Original viewport erased.");
                    }
                }
                catch { }

                tx.Commit();
            }

            // ══════════════════════════════════════════════════════════════
            //  TX 3: Turn ON each VP individually (one TX per VP).
            //  AutoCAD requires ON in a separate TX from creation, and
            //  turning on multiple VPs in one TX can silently fail.
            // ══════════════════════════════════════════════════════════════
            foreach (var id in vpIds)
            {
                try
                {
                    using var tx = db.TransactionManager.StartTransaction();
                    var vp = tx.GetObject(id, OpenMode.ForWrite) as Viewport;
                    if (vp != null) vp.On = true;
                    tx.Commit();
                }
                catch { }
            }

            try { ed.Regen(); } catch { }
            return result;
        }

        // ═════════════════════════════════════════════════════════════════
        //  Model Space (WCS) → Paper Space transform
        // ═════════════════════════════════════════════════════════════════

        private static Point2d MsToPsPoint(Point3d ms, VpProps vp)
        {
            double wcx = vp.WcsViewX;
            double wcy = vp.WcsViewY;

            double dx = ms.X - wcx;
            double dy = ms.Y - wcy;

            if (Math.Abs(vp.TwistAngle) > 1e-6)
            {
                double cos = Math.Cos(-vp.TwistAngle);
                double sin = Math.Sin(-vp.TwistAngle);
                double rx = dx * cos - dy * sin;
                double ry = dx * sin + dy * cos;
                dx = rx; dy = ry;
            }

            return new Point2d(
                vp.CenterPoint.X + dx * vp.Scale,
                vp.CenterPoint.Y + dy * vp.Scale);
        }

        // ═════════════════════════════════════════════════════════════════

        private static List<Point3d> GetCurveVertices(Curve curve)
        {
            var pts = new List<Point3d>();

            if (curve is Polyline pl)
            {
                for (int i = 0; i < pl.NumberOfVertices; i++)
                    pts.Add(pl.GetPoint3dAt(i));
            }
            else if (curve is Polyline2d p2)
            {
                foreach (ObjectId vid in p2)
                    using (var v = vid.GetObject(OpenMode.ForRead) as Vertex2d)
                        if (v != null) pts.Add(v.Position);
            }
            else if (curve is Polyline3d p3)
            {
                foreach (ObjectId vid in p3)
                    using (var v = vid.GetObject(OpenMode.ForRead) as PolylineVertex3d)
                        if (v != null) pts.Add(v.Position);
            }
            else
            {
                double s = curve.StartParam, e = curve.EndParam;
                double step = (e - s) / 72;
                for (int i = 0; i < 72; i++)
                    pts.Add(curve.GetPointAtParameter(s + i * step));
            }

            return pts;
        }
    }
}
