using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp    = Autodesk.AutoCAD.ApplicationServices.Application;
using CivilApp = Autodesk.Civil.ApplicationServices;
using CivilDB  = Autodesk.Civil.DatabaseServices;

namespace AdvancedLandDevTools.Commands
{
    /// <summary>
    /// PROFOFF — Remove pipes, structures, or pressure parts from a profile view.
    /// Equivalent to unticking "Draw in profile view" in the part properties.
    /// Supports gravity and pressure networks.  Loops until the user presses Escape.
    /// </summary>
    public class ProfOffCommand
    {
        [CommandMethod("PROFOFF", CommandFlags.Modal)]
        public void Execute()
        {
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor   ed = doc.Editor;
            Database db = doc.Database;

            try
            {
                if (!Engine.LicenseManager.EnsureLicensed()) return;

                ed.WriteMessage("\n═══════════════════════════════════════════════════════════");
                ed.WriteMessage("\n  Advanced Land Development Tools  |  Profile Off");
                ed.WriteMessage("\n  Select pipes/structures in a profile view to hide them.");
                ed.WriteMessage("\n  Press Escape or Enter to finish.");
                ed.WriteMessage("\n═══════════════════════════════════════════════════════════");

                int removed = 0;

                while (true)
                {
                    // ── Pick entity in profile view ──────────────────────────
                    var peo = new PromptEntityOptions(
                        "\n  Select pipe or structure in profile view: ");
                    peo.AllowNone = true;
                    var per = ed.GetEntity(peo);

                    if (per.Status == PromptStatus.Cancel ||
                        per.Status == PromptStatus.None)
                        break;

                    if (per.Status != PromptStatus.OK) continue;

                    Point3d pickPt = per.PickedPoint;

                    using (Transaction tx = db.TransactionManager.StartTransaction())
                    {
                        // ── Find the profile view at the pick point ──────────
                        CivilDB.ProfileView? pv = null;
                        var ent = tx.GetObject(per.ObjectId, OpenMode.ForRead);

                        pv = ent as CivilDB.ProfileView
                             ?? FindProfileViewAtPoint(pickPt, tx, db);

                        if (pv == null)
                        {
                            ed.WriteMessage(
                                "\n  Cannot find profile view at pick location.");
                            tx.Abort();
                            continue;
                        }

                        ObjectId pvId = pv.ObjectId;

                        // ── Convert pick to station/elevation ────────────────
                        double station = 0, elevation = 0;
                        if (!pv.FindStationAndElevationAtXY(
                                pickPt.X, pickPt.Y, ref station, ref elevation))
                        {
                            ed.WriteMessage(
                                "\n  Pick point is outside profile view bounds.");
                            tx.Abort();
                            continue;
                        }

                        if (pv.AlignmentId.IsNull)
                        {
                            ed.WriteMessage("\n  Profile view has no alignment.");
                            tx.Abort();
                            continue;
                        }

                        var alignment = tx.GetObject(pv.AlignmentId, OpenMode.ForRead)
                                        as CivilDB.Alignment;
                        if (alignment == null)
                        {
                            ed.WriteMessage("\n  Cannot open alignment.");
                            tx.Abort();
                            continue;
                        }

                        // ── First: check if the clicked entity is directly a
                        //    pipe/structure (user clicked on the projected part) ─
                        bool handled = TryRemoveEntity(ent, pvId, tx, ed);

                        // ── If not, search all networks for the nearest part ─
                        if (!handled)
                        {
                            var civDoc = CivilApp.CivilDocument.GetCivilDocument(db);
                            handled = FindAndRemoveNearestPart(
                                civDoc, alignment, pvId, station, elevation,
                                tx, ed, db);
                        }

                        if (handled)
                        {
                            removed++;
                            tx.Commit();
                        }
                        else
                        {
                            ed.WriteMessage(
                                "\n  No pipe or structure found near pick point.");
                            tx.Abort();
                        }
                    }
                }

                // ── Summary ──────────────────────────────────────────────────
                ed.WriteMessage($"\n\n  ═══ PROFOFF COMPLETE ═══");
                ed.WriteMessage($"\n  Removed {removed} part(s) from profile view(s).\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[PROFOFF ERROR] {ex.Message}\n");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Try to remove a directly-clicked entity from the profile view
        // ─────────────────────────────────────────────────────────────────────
        private static bool TryRemoveEntity(
            DBObject ent, ObjectId pvId, Transaction tx, Editor ed)
        {
            try
            {
                // Gravity pipe
                if (ent is CivilDB.Pipe pipe)
                {
                    pipe.UpgradeOpen();
                    pipe.RemoveFromProfileView(pvId);
                    ed.WriteMessage($"\n  ✓ Removed gravity pipe '{pipe.Name}' from profile view.");
                    return true;
                }

                // Gravity structure
                if (ent is CivilDB.Structure structure)
                {
                    structure.UpgradeOpen();
                    structure.RemoveFromProfileView(pvId);
                    ed.WriteMessage($"\n  ✓ Removed structure '{structure.Name}' from profile view.");
                    return true;
                }

                // Pressure pipe
                if (ent is CivilDB.PressurePipe pp)
                {
                    pp.UpgradeOpen();
                    pp.RemoveFromProfileView(pvId);
                    ed.WriteMessage($"\n  ✓ Removed pressure pipe '{pp.Name}' from profile view.");
                    return true;
                }

                // Pressure fitting
                if (ent is CivilDB.PressureFitting pf)
                {
                    pf.UpgradeOpen();
                    pf.RemoveFromProfileView(pvId);
                    ed.WriteMessage($"\n  ✓ Removed pressure fitting '{pf.Name}' from profile view.");
                    return true;
                }

                // Pressure appurtenance
                if (ent is CivilDB.PressureAppurtenance pa)
                {
                    pa.UpgradeOpen();
                    pa.RemoveFromProfileView(pvId);
                    ed.WriteMessage($"\n  ✓ Removed pressure appurtenance '{pa.Name}' from profile view.");
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n  Failed to remove part: {ex.Message}");
            }

            return false;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Search all pipe networks for the nearest part to the pick point
        //  in profile-view space (station / elevation).
        // ─────────────────────────────────────────────────────────────────────
        private static bool FindAndRemoveNearestPart(
            CivilApp.CivilDocument civDoc,
            CivilDB.Alignment      alignment,
            ObjectId               pvId,
            double                 pickStation,
            double                 pickElevation,
            Transaction            tx,
            Editor                 ed,
            Database               db)
        {
            ObjectId bestId       = ObjectId.Null;
            double   bestDist     = double.MaxValue;
            string   bestType     = "";
            string   bestName     = "";

            // ── Gravity networks: pipes + structures ─────────────────────────
            foreach (ObjectId nid in civDoc.GetPipeNetworkIds())
            {
                try
                {
                    var net = tx.GetObject(nid, OpenMode.ForRead) as CivilDB.Network;
                    if (net == null) continue;

                    // Pipes
                    foreach (ObjectId pid in net.GetPipeIds())
                    {
                        try
                        {
                            var pipe = tx.GetObject(pid, OpenMode.ForRead) as CivilDB.Pipe;
                            if (pipe == null) continue;

                            double dist = PipeDistanceInProfile(
                                pipe.StartPoint, pipe.EndPoint,
                                pipe.OuterDiameterOrWidth / 2.0,
                                alignment, pickStation, pickElevation);

                            if (dist < bestDist)
                            {
                                bestDist = dist;
                                bestId   = pid;
                                bestType = "gravity pipe";
                                bestName = pipe.Name;
                            }
                        }
                        catch { }
                    }

                    // Structures
                    foreach (ObjectId sid in net.GetStructureIds())
                    {
                        try
                        {
                            var st = tx.GetObject(sid, OpenMode.ForRead) as CivilDB.Structure;
                            if (st == null) continue;

                            double dist = StructureDistanceInProfile(
                                st.Position, alignment, pickStation, pickElevation);

                            if (dist < bestDist)
                            {
                                bestDist = dist;
                                bestId   = sid;
                                bestType = "structure";
                                bestName = st.Name;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // ── Pressure pipes + fittings + appurtenances ────────────────────
            var bt = tx.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            var ms = tx.GetObject(
                     bt![BlockTableRecord.ModelSpace], OpenMode.ForRead)
                     as BlockTableRecord;

            foreach (ObjectId id in ms!)
            {
                try
                {
                    var obj = tx.GetObject(id, OpenMode.ForRead);

                    if (obj is CivilDB.PressurePipe pp)
                    {
                        double dist = PipeDistanceInProfile(
                            pp.StartPoint, pp.EndPoint,
                            pp.OuterDiameter / 2.0,
                            alignment, pickStation, pickElevation);

                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestId   = id;
                            bestType = "pressure pipe";
                            bestName = pp.Name;
                        }
                    }
                    else if (obj is CivilDB.PressureFitting pf)
                    {
                        double dist = StructureDistanceInProfile(
                            pf.Position, alignment, pickStation, pickElevation);

                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestId   = id;
                            bestType = "pressure fitting";
                            bestName = pf.Name;
                        }
                    }
                    else if (obj is CivilDB.PressureAppurtenance pa)
                    {
                        double dist = StructureDistanceInProfile(
                            pa.Position, alignment, pickStation, pickElevation);

                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestId   = id;
                            bestType = "pressure appurtenance";
                            bestName = pa.Name;
                        }
                    }
                }
                catch { }
            }

            // ── Tolerance check ──────────────────────────────────────────────
            if (bestDist > 10.0 || bestId.IsNull)
                return false;

            // ── Remove from profile view ─────────────────────────────────────
            try
            {
                var found = tx.GetObject(bestId, OpenMode.ForWrite);

                if (found is CivilDB.Pipe gp)
                    gp.RemoveFromProfileView(pvId);
                else if (found is CivilDB.Structure gs)
                    gs.RemoveFromProfileView(pvId);
                else if (found is CivilDB.PressurePipe rpp)
                    rpp.RemoveFromProfileView(pvId);
                else if (found is CivilDB.PressureFitting rpf)
                    rpf.RemoveFromProfileView(pvId);
                else if (found is CivilDB.PressureAppurtenance rpa)
                    rpa.RemoveFromProfileView(pvId);
                else
                    return false;

                ed.WriteMessage(
                    $"\n  ✓ Removed {bestType} '{bestName}' from profile view. (dist={bestDist:F2})");
                return true;
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n  Failed to remove {bestType} '{bestName}': {ex.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Distance from a pipe (line segment) to pick in profile-view space
        // ─────────────────────────────────────────────────────────────────────
        private static double PipeDistanceInProfile(
            Point3d startPt, Point3d endPt, double outerRadius,
            CivilDB.Alignment alignment,
            double pickStation, double pickElevation)
        {
            try
            {
                double sta1 = 0, off1 = 0, sta2 = 0, off2 = 0;
                alignment.StationOffset(startPt.X, startPt.Y, ref sta1, ref off1);
                alignment.StationOffset(endPt.X,   endPt.Y,   ref sta2, ref off2);

                double minSta = Math.Min(sta1, sta2);
                double maxSta = Math.Max(sta1, sta2);

                double clampedSta = Math.Max(minSta, Math.Min(maxSta, pickStation));
                double dSta = Math.Abs(pickStation - clampedSta);

                double t = (Math.Abs(sta2 - sta1) < 0.001)
                           ? 0.5
                           : (clampedSta - sta1) / (sta2 - sta1);
                t = Math.Max(0.0, Math.Min(1.0, t));

                double pipeCenterZ = startPt.Z + t * (endPt.Z - startPt.Z);

                double dElev = Math.Abs(pickElevation - pipeCenterZ);
                if (dElev <= outerRadius) dElev = 0;

                return (dSta < 1.0) ? dElev
                                    : Math.Sqrt(dSta * dSta + dElev * dElev);
            }
            catch { return double.MaxValue; }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Distance from a point-like part (structure/fitting) to pick
        // ─────────────────────────────────────────────────────────────────────
        private static double StructureDistanceInProfile(
            Point3d partPosition,
            CivilDB.Alignment alignment,
            double pickStation, double pickElevation)
        {
            try
            {
                double sta = 0, off = 0;
                alignment.StationOffset(
                    partPosition.X, partPosition.Y, ref sta, ref off);

                double dSta  = Math.Abs(pickStation   - sta);
                double dElev = Math.Abs(pickElevation - partPosition.Z);

                return Math.Sqrt(dSta * dSta + dElev * dElev);
            }
            catch { return double.MaxValue; }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Find profile view whose bounds contain the given world point
        // ─────────────────────────────────────────────────────────────────────
        private static CivilDB.ProfileView? FindProfileViewAtPoint(
            Point3d pickPoint, Transaction tx, Database db)
        {
            RXClass pvClass = RXObject.GetClass(typeof(CivilDB.ProfileView));

            var bt = tx.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            var ms = tx.GetObject(
                     bt![BlockTableRecord.ModelSpace], OpenMode.ForRead)
                     as BlockTableRecord;

            foreach (ObjectId id in ms!)
            {
                if (!id.ObjectClass.IsDerivedFrom(pvClass)) continue;
                try
                {
                    var pv = tx.GetObject(id, OpenMode.ForRead) as CivilDB.ProfileView;
                    if (pv == null) continue;

                    double sta = 0, elev = 0;
                    if (pv.FindStationAndElevationAtXY(
                            pickPoint.X, pickPoint.Y, ref sta, ref elev))
                        return pv;
                }
                catch { }
            }
            return null;
        }
    }
}
