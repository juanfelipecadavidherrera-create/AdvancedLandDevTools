using System;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CivilDB = Autodesk.Civil.DatabaseServices;
using AcApp  = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AdvancedLandDevTools.Commands
{
    public class ChangeElevationCommand
    {
        // Profile-view graph proxy DXF names (same as PROFOFF)
        private const string DXF_NETWORK_PART  = "AECC_GRAPH_PROFILE_NETWORK_PART";
        private const string DXF_PRESSURE_PART = "AECC_GRAPH_PROFILE_PRESSURE_PART";

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

                // ── Step 1: Select a pipe (plan or profile view) ──────────────
                var peo = new PromptEntityOptions(
                    "\n  Select a pipe (plan or profile view): ");
                peo.AllowObjectOnLockedLayer = true;

                PromptEntityResult per = ed.GetEntity(peo);
                if (per.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\n  Command cancelled.\n");
                    return;
                }

                // ── Step 2: Resolve the underlying pipe ObjectId ──────────────
                ObjectId pipeId     = ObjectId.Null;
                bool     isPressure = false;

                using (var tx = db.TransactionManager.StartTransaction())
                {
                    string dxf = per.ObjectId.ObjectClass.DxfName;
                    var    ent = tx.GetObject(per.ObjectId, OpenMode.ForRead);

                    if (ent is CivilDB.Pipe)
                    {
                        pipeId = per.ObjectId;
                    }
                    else if (ent is CivilDB.PressurePipe)
                    {
                        pipeId     = per.ObjectId;
                        isPressure = true;
                    }
                    else if (dxf == DXF_NETWORK_PART || dxf == DXF_PRESSURE_PART)
                    {
                        // Profile-view graph proxy — resolve via ModelPartId reflection
                        ObjectId partId = ResolveModelPartId(ent);

                        if (partId.IsNull)
                        {
                            ed.WriteMessage(
                                "\n  Could not resolve underlying pipe from profile view proxy.\n");
                            tx.Abort();
                            return;
                        }

                        var part = tx.GetObject(partId, OpenMode.ForRead);
                        if (part is CivilDB.Pipe)
                        {
                            pipeId = partId;
                        }
                        else if (part is CivilDB.PressurePipe)
                        {
                            pipeId     = partId;
                            isPressure = true;
                        }
                        else
                        {
                            ed.WriteMessage(
                                $"\n  Resolved part is not a pipe ({part.GetType().Name}) — " +
                                "only pipes support elevation change.\n");
                            tx.Abort();
                            return;
                        }
                    }
                    else
                    {
                        ed.WriteMessage(
                            $"\n  Not a pipe (entity type: {dxf}) — " +
                            "select a gravity or pressure pipe.\n");
                        tx.Abort();
                        return;
                    }

                    tx.Abort();
                }

                if (pipeId.IsNull)
                {
                    ed.WriteMessage("\n  No pipe found.\n");
                    return;
                }

                // ── Step 3: Read, display, prompt, modify ─────────────────────
                using (var tx = db.TransactionManager.StartTransaction())
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
                                ed.WriteMessage(
                                    "\n  Both ends already at the same elevation. No change needed.\n");
                                tx.Abort();
                                return;
                            }

                            int choice = PromptChoice(ed, startCrown, endCrown);
                            if (choice < 0)
                            {
                                ed.WriteMessage("\n  Command cancelled.\n");
                                tx.Abort();
                                return;
                            }

                            double targetCrown   = (choice == 1) ? startCrown : endCrown;
                            double targetCenterZ = targetCrown - outerR;

                            if (choice == 1)
                                gp.EndPoint = new Point3d(
                                    gp.EndPoint.X, gp.EndPoint.Y, targetCenterZ);
                            else
                                gp.StartPoint = new Point3d(
                                    gp.StartPoint.X, gp.StartPoint.Y, targetCenterZ);

                            ed.WriteMessage(
                                $"\n  Both ends set to outside crown elevation: {targetCrown:F3}'");
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
                                ed.WriteMessage(
                                    "\n  Both ends already at the same elevation. No change needed.\n");
                                tx.Abort();
                                return;
                            }

                            int choice = PromptChoice(ed, startCrown, endCrown);
                            if (choice < 0)
                            {
                                ed.WriteMessage("\n  Command cancelled.\n");
                                tx.Abort();
                                return;
                            }

                            double targetCrown   = (choice == 1) ? startCrown : endCrown;
                            double targetCenterZ = targetCrown - outerR;

                            if (choice == 1)
                                pp.EndPoint = new Point3d(
                                    pp.EndPoint.X, pp.EndPoint.Y, targetCenterZ);
                            else
                                pp.StartPoint = new Point3d(
                                    pp.StartPoint.X, pp.StartPoint.Y, targetCenterZ);

                            ed.WriteMessage(
                                $"\n  Both ends set to outside crown elevation: {targetCrown:F3}'");
                        }
                        else
                        {
                            ed.WriteMessage("\n  Could not open pipe for editing.\n");
                            tx.Abort();
                            return;
                        }

                        tx.Commit();
                        ed.WriteMessage("\n  Pipe elevation updated successfully.");
                        ed.WriteMessage(
                            "\n═══════════════════════════════════════════════════════════\n");
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
        //  Resolve the underlying pipe ObjectId from a profile-view graph proxy.
        //  Uses ModelPartId (confirmed working in PROFOFF) with fallbacks.
        // ─────────────────────────────────────────────────────────────────────
        private static ObjectId ResolveModelPartId(DBObject proxy)
        {
            var    type  = proxy.GetType();
            var    flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            string[] candidates = {
                "ModelPartId",
                "PartId", "NetworkPartId", "BasePipeId", "SourceObjectId",
                "EntityId", "CrossingPipeId", "ComponentObjectId",
                "ReferencedObjectId", "SourceId", "PipeId", "StructureId"
            };

            foreach (string name in candidates)
            {
                try
                {
                    var prop = type.GetProperty(name, flags);
                    if (prop?.PropertyType == typeof(ObjectId))
                    {
                        var val = (ObjectId)prop.GetValue(proxy)!;
                        if (!val.IsNull) return val;
                    }
                }
                catch { }
            }

            return ObjectId.Null;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Prompt user to pick option 1 (start) or 2 (end)
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
            if (pr.Status != PromptStatus.OK) return -1;

            return pr.StringResult == "1" ? 1 : 2;
        }
    }
}
