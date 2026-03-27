using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CivilDB  = Autodesk.Civil.DatabaseServices;
using CivilApp = Autodesk.Civil.ApplicationServices;
using AcApp    = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AdvancedLandDevTools.Commands
{
    public class ChangeElevationCommand
    {
        [CommandMethod("CHANGEELEVATION", CommandFlags.Modal)]
        public void ChangeElevation()
        {
            try
            {
            if (!Engine.LicenseManager.EnsureLicensed()) return;
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            Editor   ed = doc.Editor;
            Database db = doc.Database;

            ed.WriteMessage("\n");
            ed.WriteMessage("═══════════════════════════════════════════════════════════\n");
            ed.WriteMessage("  Advanced Land Development Tools  |  Change Elevation     \n");
            ed.WriteMessage("═══════════════════════════════════════════════════════════\n");

            // ── Step 1: Select pipe or profile view ─────────────────────────────
            var peo = new PromptEntityOptions(
                "\n  Select a pipe (plan or profile view): ");
            peo.AllowObjectOnLockedLayer = true;

            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\n  Command cancelled.\n");
                return;
            }

            // ── Step 2: Resolve pipe ────────────────────────────────────────────
            ObjectId pipeId = ObjectId.Null;
            bool     isPressure = false;

            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                try
                {
                    var ent = tx.GetObject(per.ObjectId, OpenMode.ForRead);

                    if (ent is CivilDB.Pipe)
                    {
                        pipeId = per.ObjectId;
                    }
                    else if (ent is CivilDB.PressurePipe)
                    {
                        pipeId     = per.ObjectId;
                        isPressure = true;
                    }
                    else
                    {
                        // Entity is a ProfileView directly, or a projected pipe
                        // part inside a profile view — resolve the profile view.
                        CivilDB.ProfileView pv = ent as CivilDB.ProfileView;

                        if (pv == null)
                        {
                            // Clicked on something inside a profile view (e.g. projected pipe).
                            // Search all profile views to find which one contains the pick point.
                            string dxfName = ent.GetRXClass().DxfName ?? "(null)";
                            ed.WriteMessage($"\n  Entity type: {dxfName} — searching for enclosing profile view...");
                            pv = FindProfileViewAtPoint(per.PickedPoint, tx, db);
                        }

                        if (pv == null)
                        {
                            ed.WriteMessage("\n  Could not find a profile view at the pick point.\n");
                            tx.Abort();
                            return;
                        }

                        ed.WriteMessage("\n  Profile view detected — locating pipe at pick point...");
                        pipeId = FindPipeInProfileView(pv, per.PickedPoint, tx, ed, db, out isPressure);
                    }
                }
                catch (System.Exception ex)
                {
                    ed.WriteMessage($"\n  Error resolving selection: {ex.Message}\n");
                    tx.Abort();
                    return;
                }
                tx.Abort();
            }

            if (pipeId.IsNull)
            {
                ed.WriteMessage("\n  No pipe found at that location.\n");
                return;
            }

            // ── Step 3: Read pipe data, display, prompt, and modify ─────────────
            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                try
                {
                    var ent = tx.GetObject(pipeId, OpenMode.ForWrite);

                    if (!isPressure && ent is CivilDB.Pipe gp)
                    {
                        double outerR     = gp.OuterDiameterOrWidth / 2.0;
                        double startCrown = gp.StartPoint.Z + outerR;
                        double endCrown   = gp.EndPoint.Z   + outerR;

                        ed.WriteMessage($"\n  Pipe:  {gp.Name}  (Gravity)");
                        ed.WriteMessage($"\n  Start Outside Crown Elev:  {startCrown:F3}'");
                        ed.WriteMessage($"\n  End Outside Crown Elev:    {endCrown:F3}'");

                        if (Math.Abs(startCrown - endCrown) < 0.001)
                        {
                            ed.WriteMessage("\n  Both ends already at the same elevation. No change needed.\n");
                            tx.Abort();
                            return;
                        }

                        int choice = PromptChoice(ed, startCrown, endCrown);
                        if (choice < 0) { ed.WriteMessage("\n  Command cancelled.\n"); tx.Abort(); return; }

                        double targetCrown  = (choice == 1) ? startCrown : endCrown;
                        double targetCenterZ = targetCrown - outerR;

                        if (choice == 1)
                            gp.EndPoint = new Point3d(gp.EndPoint.X, gp.EndPoint.Y, targetCenterZ);
                        else
                            gp.StartPoint = new Point3d(gp.StartPoint.X, gp.StartPoint.Y, targetCenterZ);

                        ed.WriteMessage($"\n  Both ends set to outside crown elevation: {targetCrown:F3}'");
                    }
                    else if (isPressure && ent is CivilDB.PressurePipe pp)
                    {
                        double outerR     = pp.OuterDiameter / 2.0;
                        double startCrown = pp.StartPoint.Z + outerR;
                        double endCrown   = pp.EndPoint.Z   + outerR;

                        ed.WriteMessage($"\n  Pipe:  {pp.Name}  (Pressure)");
                        ed.WriteMessage($"\n  Start Outside Crown Elev:  {startCrown:F3}'");
                        ed.WriteMessage($"\n  End Outside Crown Elev:    {endCrown:F3}'");

                        if (Math.Abs(startCrown - endCrown) < 0.001)
                        {
                            ed.WriteMessage("\n  Both ends already at the same elevation. No change needed.\n");
                            tx.Abort();
                            return;
                        }

                        int choice = PromptChoice(ed, startCrown, endCrown);
                        if (choice < 0) { ed.WriteMessage("\n  Command cancelled.\n"); tx.Abort(); return; }

                        double targetCrown  = (choice == 1) ? startCrown : endCrown;
                        double targetCenterZ = targetCrown - outerR;

                        if (choice == 1)
                            pp.EndPoint = new Point3d(pp.EndPoint.X, pp.EndPoint.Y, targetCenterZ);
                        else
                            pp.StartPoint = new Point3d(pp.StartPoint.X, pp.StartPoint.Y, targetCenterZ);

                        ed.WriteMessage($"\n  Both ends set to outside crown elevation: {targetCrown:F3}'");
                    }
                    else
                    {
                        ed.WriteMessage("\n  Could not open pipe for editing.\n");
                        tx.Abort();
                        return;
                    }

                    tx.Commit();
                    ed.WriteMessage("\n  Pipe elevation updated successfully.");
                    ed.WriteMessage("\n═══════════════════════════════════════════════════════════\n");
                }
                catch (System.Exception ex)
                {
                    ed.WriteMessage($"\n  Error: {ex.Message}\n");
                    tx.Abort();
                }
            }
            }
            catch (System.Exception ex)
            {
                var d = AcApp.DocumentManager.MdiActiveDocument;
                d?.Editor.WriteMessage($"\n[ALDT ERROR] CHANGEELEVATION: {ex.Message}\n");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Find the profile view that contains the given WCS point.
        // ─────────────────────────────────────────────────────────────────────
        private static CivilDB.ProfileView? FindProfileViewAtPoint(
            Point3d pickPoint, Transaction tx, Database db)
        {
            RXClass pvClass = RXObject.GetClass(typeof(CivilDB.ProfileView));

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
                    if (pv.FindStationAndElevationAtXY(
                            pickPoint.X, pickPoint.Y, ref sta, ref elev))
                    {
                        return pv;
                    }
                }
                catch { }
            }

            return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Find the nearest pipe to the pick point inside a profile view.
        //  Converts XY → station/elevation, then searches all pipe networks.
        // ─────────────────────────────────────────────────────────────────────
        private ObjectId FindPipeInProfileView(
            CivilDB.ProfileView pv,
            Point3d             pickPoint,
            Transaction         tx,
            Editor              ed,
            Database            db,
            out bool            isPressure)
        {
            isPressure = false;

            // Convert screen pick to station / elevation in the profile view
            double station = 0, elevation = 0;
            bool inBounds = pv.FindStationAndElevationAtXY(
                pickPoint.X, pickPoint.Y, ref station, ref elevation);

            if (!inBounds)
            {
                ed.WriteMessage("\n  Pick point is outside the profile view bounds.");
                return ObjectId.Null;
            }

            ed.WriteMessage($"\n  Profile View: '{pv.Name}'");
            ed.WriteMessage($"\n  Pick → Station: {station:F2}, Elevation: {elevation:F3}");

            // Get alignment
            if (pv.AlignmentId.IsNull)
            {
                ed.WriteMessage("\n  Profile view has no alignment.");
                return ObjectId.Null;
            }
            var alignment = tx.GetObject(pv.AlignmentId, OpenMode.ForRead) as CivilDB.Alignment;
            if (alignment == null)
            {
                ed.WriteMessage("\n  Cannot open alignment.");
                return ObjectId.Null;
            }

            var civDoc = CivilApp.CivilDocument.GetCivilDocument(db);

            ObjectId bestId   = ObjectId.Null;
            double   bestDist = double.MaxValue;
            bool     bestIsPressure = false;
            int      pipesChecked = 0;

            // ── Search gravity pipe networks ────────────────────────────────────
            foreach (ObjectId nid in civDoc.GetPipeNetworkIds())
            {
                try
                {
                    var net = tx.GetObject(nid, OpenMode.ForRead) as CivilDB.Network;
                    if (net == null) continue;

                    foreach (ObjectId pid in net.GetPipeIds())
                    {
                        try
                        {
                            var pipe = tx.GetObject(pid, OpenMode.ForRead) as CivilDB.Pipe;
                            if (pipe == null) continue;

                            pipesChecked++;

                            double dist = PipeDistanceToPickInProfile(
                                pipe.StartPoint, pipe.EndPoint,
                                pipe.OuterDiameterOrWidth / 2.0,
                                alignment, station, elevation);

                            if (dist < 50.0)
                                ed.WriteMessage($"\n    '{pipe.Name}'  dist={dist:F2}");

                            if (dist < bestDist)
                            {
                                bestDist       = dist;
                                bestId         = pid;
                                bestIsPressure = false;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // ── Search pressure pipes ───────────────────────────────────────────
            var bt = tx.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            var ms = tx.GetObject(
                     bt![BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;

            foreach (ObjectId id in ms!)
            {
                try
                {
                    if (tx.GetObject(id, OpenMode.ForRead) is CivilDB.PressurePipe pp)
                    {
                        pipesChecked++;

                        double dist = PipeDistanceToPickInProfile(
                            pp.StartPoint, pp.EndPoint,
                            pp.OuterDiameter / 2.0,
                            alignment, station, elevation);

                        if (dist < 50.0)
                            ed.WriteMessage($"\n    '{pp.Name}'  dist={dist:F2}");

                        if (dist < bestDist)
                        {
                            bestDist       = dist;
                            bestId         = id;
                            bestIsPressure = true;
                        }
                    }
                }
                catch { }
            }

            ed.WriteMessage($"\n  Total pipes checked: {pipesChecked}, best dist: {bestDist:F2}");

            // Tolerance: pipe must be within 10 ft of picked elevation
            if (bestDist > 10.0)
            {
                ed.WriteMessage($"\n  No pipe found near Station {station:F2}, Elev {elevation:F3}.");
                return ObjectId.Null;
            }

            isPressure = bestIsPressure;

            // Report which pipe was found
            try
            {
                var found = tx.GetObject(bestId, OpenMode.ForRead);
                string name = found is CivilDB.Pipe gf ? gf.Name :
                              found is CivilDB.PressurePipe pf ? pf.Name : "?";
                ed.WriteMessage($"\n  Found pipe: '{name}' (dist={bestDist:F2})");
            }
            catch { }

            return bestId;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Compute distance from a pipe to the picked station/elevation
        //  in profile-view space.  Projects both pipe endpoints onto the
        //  alignment (station/offset), then interpolates pipe elevation
        //  at the picked station.  NO offset filter — pipes that run
        //  along the alignment (same side) are valid candidates.
        // ─────────────────────────────────────────────────────────────────────
        private static double PipeDistanceToPickInProfile(
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

                // ── Clamp pick station into the pipe's station range ──────
                double clampedSta = Math.Max(minSta, Math.Min(maxSta, pickStation));
                double dSta = Math.Abs(pickStation - clampedSta);

                // ── Interpolate pipe center elevation at the clamped station
                double t = (Math.Abs(sta2 - sta1) < 0.001)
                           ? 0.5
                           : (clampedSta - sta1) / (sta2 - sta1);
                t = Math.Max(0.0, Math.Min(1.0, t));

                double pipeCenterZ = startPt.Z + t * (endPt.Z - startPt.Z);

                // ── Elevation distance — zero if pick is inside pipe ──────
                double dElev = Math.Abs(pickElevation - pipeCenterZ);
                if (dElev <= outerRadius) dElev = 0;

                // Combined distance in profile-view space.
                // Station and elevation axes may have very different scales,
                // so use pure elevation distance when station is within range.
                return (dSta < 1.0)
                       ? dElev                                  // within station range
                       : Math.Sqrt(dSta * dSta + dElev * dElev);
            }
            catch
            {
                return double.MaxValue;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Prompt user to choose option 1 (start) or 2 (end)
        // ─────────────────────────────────────────────────────────────────────
        private static int PromptChoice(Editor ed, double startCrown, double endCrown)
        {
            ed.WriteMessage("\n");
            ed.WriteMessage("\n  ╔══════════════════════════════════════════════════╗");
            ed.WriteMessage("\n  ║  Set both ends to which elevation?              ║");
            ed.WriteMessage("\n  ╠══════════════════════════════════════════════════╣");
            ed.WriteMessage($"\n  ║  [1]  Start Crown:  {startCrown:F3}'                ║");
            ed.WriteMessage($"\n  ║  [2]  End Crown:    {endCrown:F3}'                ║");
            ed.WriteMessage("\n  ╚══════════════════════════════════════════════════╝");

            var pko = new PromptKeywordOptions("\n  Type 1 or 2 [1/2]: ");
            pko.Keywords.Add("1");
            pko.Keywords.Add("2");
            pko.AllowNone = false;

            PromptResult pr = ed.GetKeywords(pko);
            if (pr.Status != PromptStatus.OK)
                return -1;

            return pr.StringResult == "1" ? 1 : 2;
        }
    }
}
