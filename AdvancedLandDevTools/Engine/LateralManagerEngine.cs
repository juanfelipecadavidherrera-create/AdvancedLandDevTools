using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using AdvancedLandDevTools.Models;

namespace AdvancedLandDevTools.Engine
{
    public static class LateralManagerEngine
    {
        public static LateralEntry? ExtractLateralCrossing(Document doc)
        {
            var ed = doc.Editor;
            
            // 1. Select Profile View
            var peoPV = new PromptEntityOptions("\nSelect the Profile View for the lateral crossing: ");
            peoPV.SetRejectMessage("\nMust be a Profile View.");
            peoPV.AddAllowedClass(typeof(ProfileView), true);
            
            var resPV = ed.GetEntity(peoPV);
            if (resPV.Status != PromptStatus.OK) return null;

            // 2. Select Ellipse
            var peoEll = new PromptEntityOptions("\nSelect the drawn Ellipse representing the pipe: ");
            peoEll.SetRejectMessage("\nMust be an Ellipse.");
            peoEll.AddAllowedClass(typeof(Ellipse), true);
            
            var resEll = ed.GetEntity(peoEll);
            if (resEll.Status != PromptStatus.OK) return null;

            using var tr = doc.TransactionManager.StartTransaction();
            
            var pv = (ProfileView)tr.GetObject(resPV.ObjectId, OpenMode.ForRead);
            var ell = (Ellipse)tr.GetObject(resEll.ObjectId, OpenMode.ForRead);
            var align = (Alignment)tr.GetObject(pv.AlignmentId, OpenMode.ForRead);

            // Calculate bottom of ellipse (Invert)
            // Ellipse center is ell.Center
            // Assuming it's vertically oriented if major axis is vertical, or horizontally oriented.
            // Bounding box gives the lowest point safely.
            var ext = ell.GeometricExtents;
            double bottomY = ext.MinPoint.Y;
            double centerX = ell.Center.X;

            // Get Station and Elevation in Profile View
            double station = 0, invertElev = 0;
            pv.FindStationAndElevationAtXY(centerX, bottomY, ref station, ref invertElev);

            var entry = new LateralEntry
            {
                Name = $"Lateral at {StationToString(station)}",
                SourceAlignmentName = align.Name,
                SourceAlignmentHandle = align.Handle.ToString(),
                Station = station,
                InvertElevation = invertElev,
                CenterOffsetX = ell.Center.X - centerX,
                CenterOffsetY = ell.Center.Y - bottomY,
                MajorAxisX = ell.MajorAxis.X,
                MajorAxisY = ell.MajorAxis.Y,
                RadiusRatio = ell.RadiusRatio,
                Layer = ell.Layer,
                ColorIndex = (short)ell.ColorIndex,
                SourceDwgName = doc.Name,
                EllipseHandle = ell.Handle.ToString()
            };

            tr.Commit();
            return entry;
        }

        public static void ProjectLaterals(Document doc, LateralManagerProject project)
        {
            if (project == null || project.Laterals.Count == 0)
            {
                doc.Editor.WriteMessage("\n[Lateral Manager] No laterals saved in this project.");
                return;
            }

            var ed = doc.Editor;
            
            // 1. Select Target Profile View
            var peoPV = new PromptEntityOptions("\nSelect the TARGET Profile View (e.g. Water Main) to project into: ");
            peoPV.SetRejectMessage("\nMust be a Profile View.");
            peoPV.AddAllowedClass(typeof(ProfileView), true);
            
            var resPV = ed.GetEntity(peoPV);
            if (resPV.Status != PromptStatus.OK) return;

            using var tr = doc.TransactionManager.StartTransaction();
            var targetPv = (ProfileView)tr.GetObject(resPV.ObjectId, OpenMode.ForRead);
            var targetAlign = (Alignment)tr.GetObject(targetPv.AlignmentId, OpenMode.ForRead);
            
            int projectedCount = 0;

            foreach (var lateral in project.Laterals)
            {
                // Attempt to find the source alignment
                var db = doc.Database;
                if (!db.TryGetObjectId(new Handle(Convert.ToInt64(lateral.SourceAlignmentHandle, 16)), out ObjectId sourceAlignId))
                {
                    // Try by name as fallback
                    sourceAlignId = FindAlignmentByName(tr, db, lateral.SourceAlignmentName);
                }

                if (sourceAlignId.IsNull) continue;

                var sourceAlign = (Alignment)tr.GetObject(sourceAlignId, OpenMode.ForRead);
                
                // Find geometric intersections between source alignment and target alignment
                var intersections = new Point3dCollection();
                sourceAlign.IntersectWith(targetAlign, Intersect.ExtendBoth, intersections, IntPtr.Zero, IntPtr.Zero);
                
                foreach (Point3d pt in intersections)
                {
                    // For each intersection, calculate the station on the target alignment
                    double targetStation = 0, offset = 0;
                    try
                    {
                        targetAlign.StationOffset(pt.X, pt.Y, ref targetStation, ref offset);
                    }
                    catch
                    {
                        continue; // Point might be off alignment somehow
                    }

                    // Check if targetStation is within Profile View range
                    if (targetStation < targetPv.StationStart || targetStation > targetPv.StationEnd)
                        continue;

                    // Calculate X,Y in the Profile View for this station and the saved Invert Elevation
                    double x = 0, y = 0;
                    targetPv.FindXYAtStationAndElevation(targetStation, lateral.InvertElevation, ref x, ref y);
                    
                    // Draw the Ellipse
                    var btr = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
                    
                    // Reconstruct center point based on saved offsets
                    Point3d center = new Point3d(x + lateral.CenterOffsetX, y + lateral.CenterOffsetY, 0);
                    Vector3d majorAxis = new Vector3d(lateral.MajorAxisX, lateral.MajorAxisY, 0);
                    
                    using var newEll = new Ellipse(center, Vector3d.ZAxis, majorAxis, lateral.RadiusRatio, 0.0, 2 * Math.PI);
                    
                    // Try to match layer/color if it exists
                    EnsureLayer(tr, db, lateral.Layer);
                    newEll.Layer = lateral.Layer;
                    newEll.ColorIndex = lateral.ColorIndex;

                    btr.AppendEntity(newEll);
                    tr.AddNewlyCreatedDBObject(newEll, true);
                    
                    projectedCount++;
                }
            }
            
            tr.Commit();
            ed.WriteMessage($"\n[Lateral Manager] Projected {projectedCount} lateral crossing(s) into the profile view.");
        }

        public static void ZoomToEllipse(Document doc, string handleStr)
        {
            if (string.IsNullOrEmpty(handleStr)) return;
            
            using var tr = doc.TransactionManager.StartTransaction();
            try
            {
                if (doc.Database.TryGetObjectId(new Handle(Convert.ToInt64(handleStr, 16)), out ObjectId id))
                {
                    var ent = (Autodesk.AutoCAD.DatabaseServices.Entity)tr.GetObject(id, OpenMode.ForRead);
                    var ext = ent.GeometricExtents;
                    
                    // Pad by 20 feet
                    var min = new Point3d(ext.MinPoint.X - 20, ext.MinPoint.Y - 20, 0);
                    var max = new Point3d(ext.MaxPoint.X + 20, ext.MaxPoint.Y + 20, 0);
                    
                    string cmd = $"_.ZOOM _W {min.X},{min.Y} {max.X},{max.Y} ";
                    doc.SendStringToExecute(cmd, true, false, true);
                }
            }
            catch { /* Not found */ }
            tr.Commit();
        }

        private static string StationToString(double station)
        {
            int hundreds = (int)(station / 100);
            double remainder = station - (hundreds * 100);
            return $"{hundreds}+{remainder:00.00}";
        }

        private static ObjectId FindAlignmentByName(Transaction tr, Database db, string name)
        {
            var alignId = ObjectId.Null;
            var doc = CivilApplication.ActiveDocument;
            if (doc != null)
            {
                foreach (ObjectId id in doc.GetAlignmentIds())
                {
                    var align = (Alignment)tr.GetObject(id, OpenMode.ForRead);
                    if (align.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        alignId = id;
                        break;
                    }
                }
            }
            return alignId;
        }

        private static void EnsureLayer(Transaction tr, Database db, string layerName)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(layerName))
            {
                lt.UpgradeOpen();
                var lr = new LayerTableRecord { Name = layerName };
                lt.Add(lr);
                tr.AddNewlyCreatedDBObject(lr, true);
            }
        }
    }
}
