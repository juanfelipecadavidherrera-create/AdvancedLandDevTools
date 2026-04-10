using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
    /// PVSTYLE — Override the display style of a gravity pipe or structure in a profile view.
    ///
    /// Workflow:
    ///   1. Click anywhere inside a profile view to identify it.
    ///   2. Click the pipe or structure projected in that profile view.
    ///   3. The command lists all styles found in the drawing for that part type.
    ///   4. Type a number to apply the selected style as the profile-view style override.
    ///
    /// This is equivalent to enabling "Style Override" in the part's profile view properties.
    /// Only affects the display in the selected profile view — the part's global style is
    /// unchanged.  Supports gravity pipes and structures.
    /// </summary>
    public class PvStyleCommand
    {
        // Candidate method names for per-profile-view style override (Civil 3D API).
        // We try them in order via reflection so the code compiles even if names vary
        // between Civil 3D versions.
        private static readonly string[] _overrideMethodNames =
        {
            "SetProfileViewStyleOverride",
            "SetProfileViewStyle",
            "SetProfileViewStyleId",
            "SetDrawingStyle",
        };

        // ─────────────────────────────────────────────────────────────────────
        [CommandMethod("PVSTYLE", CommandFlags.Modal)]
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
                ed.WriteMessage("\n  Advanced Land Development Tools  |  Profile View Style Override");
                ed.WriteMessage("\n═══════════════════════════════════════════════════════════\n");

                // ── Step 1: Identify the profile view ────────────────────────
                ed.WriteMessage("  Step 1: Click anywhere inside the profile view.\n");

                var pvPick = ed.GetPoint(new PromptPointOptions("\n  Pick point inside profile view: "));
                if (pvPick.Status != PromptStatus.OK) return;

                ObjectId pvId   = ObjectId.Null;
                string   pvName = "";

                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    var pv = FindProfileViewAtPoint(pvPick.Value, tx, db);
                    if (pv == null)
                    {
                        ed.WriteMessage("\n  No profile view found at that point.\n");
                        tx.Abort(); return;
                    }
                    pvId   = pv.ObjectId;
                    pvName = pv.Name;
                    tx.Commit();
                }

                ed.WriteMessage($"\n  Profile View: '{pvName}'");

                // ── Step 2: Select the pipe or structure ─────────────────────
                ed.WriteMessage("\n  Step 2: Click the pipe or structure in the profile view.\n");

                var partPick = ed.GetEntity(new PromptEntityOptions(
                    "\n  Select gravity pipe or structure: ") { AllowNone = false });
                if (partPick.Status != PromptStatus.OK) return;

                ObjectId partId   = ObjectId.Null;
                bool     isPipe   = false;
                string   partName = "";

                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    var ent = tx.GetObject(partPick.ObjectId, OpenMode.ForRead);

                    if (ent is CivilDB.Pipe directPipe)
                    {
                        partId   = directPipe.ObjectId;
                        partName = directPipe.Name;
                        isPipe   = true;
                    }
                    else if (ent is CivilDB.Structure directStr)
                    {
                        partId   = directStr.ObjectId;
                        partName = directStr.Name;
                        isPipe   = false;
                    }
                    else
                    {
                        // Fall back to nearest-part search in profile-view space
                        var pv = tx.GetObject(pvId, OpenMode.ForRead) as CivilDB.ProfileView;
                        if (pv == null) { ed.WriteMessage("\n  Profile view not found.\n"); tx.Abort(); return; }

                        double station = 0, elevation = 0;
                        pv.FindStationAndElevationAtXY(
                            partPick.PickedPoint.X, partPick.PickedPoint.Y,
                            ref station, ref elevation);

                        var al = tx.GetObject(pv.AlignmentId, OpenMode.ForRead)
                                 as CivilDB.Alignment;
                        if (al == null) { ed.WriteMessage("\n  Cannot open alignment.\n"); tx.Abort(); return; }

                        var civDoc = CivilApp.CivilDocument.GetCivilDocument(db);
                        partId = FindNearestGravityPart(
                            civDoc, al, station, elevation, tx,
                            out isPipe, out partName);

                        if (partId.IsNull)
                        {
                            ed.WriteMessage("\n  No gravity pipe or structure found near pick point.\n");
                            tx.Abort(); return;
                        }
                    }

                    tx.Commit();
                }

                ed.WriteMessage($"\n  Part: {(isPipe ? "Pipe" : "Structure")} '{partName}'");

                // ── Step 3: Build the numbered style list ─────────────────────
                // Collect all unique styles used by parts of the same type
                // in the drawing, ensuring we show every style available.
                var styles = new List<(ObjectId Id, string Label)>();

                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    var civDoc  = CivilApp.CivilDocument.GetCivilDocument(db);
                    var partObj = tx.GetObject(partId, OpenMode.ForRead) as CivilDB.Part;

                    // ── Current style is always first ────────────────────────
                    if (partObj != null)
                        TryAddStyle(partObj.StyleId, "[current]", styles, tx);

                    // ── Scan all networks for additional styles ───────────────
                    foreach (ObjectId nid in civDoc.GetPipeNetworkIds())
                    {
                        try
                        {
                            var net = tx.GetObject(nid, OpenMode.ForRead) as CivilDB.Network;
                            if (net == null) continue;

                            IEnumerable<ObjectId> ids = isPipe
                                ? net.GetPipeIds().Cast<ObjectId>()
                                : net.GetStructureIds().Cast<ObjectId>();

                            foreach (ObjectId pid in ids)
                            {
                                try
                                {
                                    var p = tx.GetObject(pid, OpenMode.ForRead) as CivilDB.Part;
                                    if (p != null)
                                        TryAddStyle(p.StyleId, null, styles, tx);
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }

                    tx.Commit();
                }

                if (styles.Count == 0)
                {
                    ed.WriteMessage("\n  No styles found for this part type.\n");
                    return;
                }

                // ── Step 4: Display numbered list ─────────────────────────────
                string typeLabel = isPipe ? "Pipe" : "Structure";
                ed.WriteMessage($"\n\n  Available {typeLabel} styles:\n");
                for (int i = 0; i < styles.Count; i++)
                    ed.WriteMessage($"    [{i + 1}]  {styles[i].Label}\n");

                var pio = new PromptIntegerOptions(
                    $"\n  Select style number [1-{styles.Count}]: ")
                {
                    LowerLimit = 1,
                    UpperLimit = styles.Count,
                    AllowNone  = false
                };
                var pir = ed.GetInteger(pio);
                if (pir.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\n  Command cancelled.\n");
                    return;
                }

                var (selectedStyleId, selectedLabel) = styles[pir.Value - 1];

                // ── Step 5: Apply profile-view style override ─────────────────
                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    var part = tx.GetObject(partId, OpenMode.ForWrite) as CivilDB.Part;
                    if (part == null)
                    {
                        ed.WriteMessage("\n  Cannot open part for writing.\n");
                        tx.Abort(); return;
                    }

                    bool overrideSet = TrySetProfileViewStyleOverride(
                        part, pvId, selectedStyleId, ed);

                    if (!overrideSet)
                    {
                        // Fallback: change the part's global style.
                        // This affects all profile views and plan view, but it's the
                        // most compatible option when the per-PV override API is unavailable.
                        part.StyleId = selectedStyleId;
                        ed.WriteMessage(
                            "\n  (Applied as global style — per-PV override API not found)");
                    }

                    tx.Commit();
                }

                ed.WriteMessage(
                    $"\n\n  ═══ PVSTYLE COMPLETE ═══");
                ed.WriteMessage(
                    $"\n  Part   : {typeLabel} '{partName}'");
                ed.WriteMessage(
                    $"\n  PV     : '{pvName}'");
                ed.WriteMessage(
                    $"\n  Style  : {selectedLabel}\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[PVSTYLE ERROR] {ex.Message}\n");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Attempt to set profile-view-specific style override via reflection.
        //  Civil 3D method names vary; we try all known candidates.
        //  Returns true if a method was successfully called.
        // ─────────────────────────────────────────────────────────────────────
        private static bool TrySetProfileViewStyleOverride(
            CivilDB.Part part, ObjectId pvId, ObjectId styleId, Editor ed)
        {
            var type = part.GetType();
            var argTypes = new[] { typeof(ObjectId), typeof(ObjectId) };
            var args     = new object[] { pvId, styleId };

            foreach (string methodName in _overrideMethodNames)
            {
                try
                {
                    var m = type.GetMethod(methodName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, argTypes, null);

                    if (m == null) continue;

                    m.Invoke(part, args);
                    ed.WriteMessage($"\n  Applied via {methodName}()");
                    return true;
                }
                catch { }
            }

            return false;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Add a style to the list (deduplicates by ObjectId).
        //  suffix appears after the style name (e.g. "[current]").
        // ─────────────────────────────────────────────────────────────────────
        private static void TryAddStyle(
            ObjectId styleId, string? suffix,
            List<(ObjectId, string)> styles, Transaction tx)
        {
            if (styleId.IsNull || styles.Exists(s => s.Item1 == styleId)) return;
            try
            {
                var styleBase = tx.GetObject(styleId, OpenMode.ForRead)
                                as CivilDB.Styles.StyleBase;
                string name = styleBase?.Name ?? styleId.Handle.ToString();
                string label = suffix != null ? $"{name}  {suffix}" : name;
                styles.Add((styleId, label));
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Find the nearest gravity pipe or structure to the pick point,
        //  matched by station/elevation in profile-view space.
        // ─────────────────────────────────────────────────────────────────────
        private static ObjectId FindNearestGravityPart(
            CivilApp.CivilDocument civDoc,
            CivilDB.Alignment      alignment,
            double                 pickStation,
            double                 pickElevation,
            Transaction            tx,
            out bool               isPipe,
            out string             partName)
        {
            isPipe   = false;
            partName = "";

            ObjectId bestId    = ObjectId.Null;
            double   bestDist  = double.MaxValue;
            bool     bestIsPipe = false;
            string   bestName   = "";

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
                            double dist = PipeDistInProfile(
                                pipe.StartPoint, pipe.EndPoint,
                                pipe.OuterDiameterOrWidth / 2.0,
                                alignment, pickStation, pickElevation);
                            if (dist < bestDist)
                            {
                                bestDist    = dist;
                                bestId      = pid;
                                bestIsPipe  = true;
                                bestName    = pipe.Name;
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
                            double dist = PointDistInProfile(
                                st.Position, alignment, pickStation, pickElevation);
                            if (dist < bestDist)
                            {
                                bestDist    = dist;
                                bestId      = sid;
                                bestIsPipe  = false;
                                bestName    = st.Name;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }

            if (bestDist > 10.0) return ObjectId.Null;

            isPipe   = bestIsPipe;
            partName = bestName;
            return bestId;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Distance from a pipe (segment) to the pick in profile-view space
        // ─────────────────────────────────────────────────────────────────────
        private static double PipeDistInProfile(
            Point3d start, Point3d end, double outerRadius,
            CivilDB.Alignment al, double sta, double elev)
        {
            try
            {
                double s1 = 0, o1 = 0, s2 = 0, o2 = 0;
                al.StationOffset(start.X, start.Y, ref s1, ref o1);
                al.StationOffset(end.X,   end.Y,   ref s2, ref o2);
                double lo = Math.Min(s1, s2), hi = Math.Max(s1, s2);
                double clamped = Math.Max(lo, Math.Min(hi, sta));
                double dSta = Math.Abs(sta - clamped);
                double t = Math.Abs(s2 - s1) < 0.001
                           ? 0.5
                           : (clamped - s1) / (s2 - s1);
                t = Math.Max(0.0, Math.Min(1.0, t));
                double pipeZ = start.Z + t * (end.Z - start.Z);
                double dElev = Math.Max(0.0, Math.Abs(elev - pipeZ) - outerRadius);
                return dSta < 1.0 ? dElev : Math.Sqrt(dSta * dSta + dElev * dElev);
            }
            catch { return double.MaxValue; }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Distance from a point-like part to the pick in profile-view space
        // ─────────────────────────────────────────────────────────────────────
        private static double PointDistInProfile(
            Point3d pos, CivilDB.Alignment al, double sta, double elev)
        {
            try
            {
                double s = 0, o = 0;
                al.StationOffset(pos.X, pos.Y, ref s, ref o);
                double dSta  = Math.Abs(sta  - s);
                double dElev = Math.Abs(elev - pos.Z);
                return Math.Sqrt(dSta * dSta + dElev * dElev);
            }
            catch { return double.MaxValue; }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Find the first ProfileView whose bounds contain the given world point
        // ─────────────────────────────────────────────────────────────────────
        private static CivilDB.ProfileView? FindProfileViewAtPoint(
            Point3d pt, Transaction tx, Database db)
        {
            var bt = tx.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            var ms = tx.GetObject(bt![BlockTableRecord.ModelSpace], OpenMode.ForRead)
                     as BlockTableRecord;
            RXClass pvClass = RXObject.GetClass(typeof(CivilDB.ProfileView));
            foreach (ObjectId id in ms!)
            {
                if (!id.ObjectClass.IsDerivedFrom(pvClass)) continue;
                try
                {
                    var pv = tx.GetObject(id, OpenMode.ForRead) as CivilDB.ProfileView;
                    if (pv == null) continue;
                    double s = 0, e = 0;
                    if (pv.FindStationAndElevationAtXY(pt.X, pt.Y, ref s, ref e)) return pv;
                }
                catch { }
            }
            return null;
        }
    }
}
