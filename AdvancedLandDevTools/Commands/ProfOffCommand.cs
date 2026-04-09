using System;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApp  = Autodesk.AutoCAD.ApplicationServices.Application;
using CivilDB = Autodesk.Civil.DatabaseServices;

namespace AdvancedLandDevTools.Commands
{
    /// <summary>
    /// PROFOFF — Remove pipes / structures from a Civil 3D profile view.
    ///
    /// Step 1 : click the profile view to lock in pvId.
    /// Step 2 : click each pipe / structure drawn in the profile view.
    ///
    /// Civil 3D returns AECC_GRAPH_PROFILE_NETWORK_PART or
    /// AECC_GRAPH_PROFILE_PRESSURE_PART when you click a part in a profile
    /// view — these are visual proxy entities, not the model-space parts.
    /// We resolve the underlying Pipe / Structure ObjectId via reflection and
    /// then call RemoveFromProfileView on the real part.
    /// </summary>
    public class ProfOffCommand
    {
        // DXF names of the profile-view graph proxy entities
        private const string DXF_NETWORK_PART  = "AECC_GRAPH_PROFILE_NETWORK_PART";
        private const string DXF_PRESSURE_PART = "AECC_GRAPH_PROFILE_PRESSURE_PART";

        // Ordered list of property / method names to try when resolving
        // the underlying part ObjectId from the graph proxy entity
        private static readonly string[] _partIdProps = {
            "ModelPartId",                                          // ProfileViewPressurePart / ProfileViewNetworkPart
            "PartId", "NetworkPartId", "BasePipeId", "SourceObjectId",
            "EntityId", "CrossingPipeId", "ComponentObjectId",
            "ReferencedObjectId", "SourceId", "PipeId", "StructureId"
        };
        private static readonly string[] _partIdMethods = {
            "GetPartId", "GetNetworkPartId", "GetSourceId", "GetEntityId"
        };

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
                ed.WriteMessage("\n  Step 1: click the profile view border or background.");
                ed.WriteMessage("\n  Step 2: click each pipe / structure to hide.");
                ed.WriteMessage("\n          Enter or Escape to finish.");
                ed.WriteMessage("\n═══════════════════════════════════════════════════════════");

                // ── Step 1: pick the profile view ────────────────────────────
                ObjectId pvId = ObjectId.Null;

                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var pvOpt = new PromptEntityOptions(
                        "\n  Select profile view (click border or background): ");
                    pvOpt.SetRejectMessage(
                        "\n  That is not a profile view — click on the view border or grid.");
                    pvOpt.AddAllowedClass(typeof(CivilDB.ProfileView), exactMatch: true);

                    var pvRes = ed.GetEntity(pvOpt);
                    if (pvRes.Status != PromptStatus.OK)
                    {
                        ed.WriteMessage("\n  Cancelled.\n");
                        tx.Abort();
                        return;
                    }

                    var pv = tx.GetObject(pvRes.ObjectId, OpenMode.ForRead)
                             as CivilDB.ProfileView;
                    if (pv == null)
                    {
                        ed.WriteMessage("\n  Could not open profile view.\n");
                        tx.Abort();
                        return;
                    }

                    pvId = pv.ObjectId;
                    ed.WriteMessage("\n  Profile view locked. Now click on parts to hide.\n");
                    tx.Commit();
                }

                // ── Step 2: pick parts ────────────────────────────────────────
                int removed = 0;

                while (true)
                {
                    var peo = new PromptEntityOptions(
                        "\n  Click on a pipe or structure in the profile view <Enter to finish>: ");
                    peo.AllowNone = true;

                    var per = ed.GetEntity(peo);

                    if (per.Status == PromptStatus.None  ||
                        per.Status == PromptStatus.Cancel)
                        break;

                    if (per.Status != PromptStatus.OK) continue;

                    string dxf = per.ObjectId.ObjectClass.DxfName;

                    using (var tx = db.TransactionManager.StartTransaction())
                    {
                        // ── Direct Civil part (fallback if Civil 3D returns the real entity)
                        if (TryRemoveDirect(per.ObjectId, pvId, tx, ed))
                        {
                            removed++;
                            tx.Commit();
                            continue;
                        }

                        // ── Graph proxy entity — resolve to the underlying part
                        if (dxf == DXF_NETWORK_PART || dxf == DXF_PRESSURE_PART)
                        {
                            var proxy = tx.GetObject(per.ObjectId, OpenMode.ForRead);
                            ObjectId partId = ResolvePartId(proxy, ed);

                            if (partId.IsNull)
                            {
                                // ResolvePartId already printed diagnostic output
                                tx.Abort();
                                continue;
                            }

                            if (TryRemoveDirect(partId, pvId, tx, ed))
                            {
                                removed++;
                                tx.Commit();
                            }
                            else
                            {
                                ed.WriteMessage(
                                    "\n  Found the underlying part but could not remove it.");
                                tx.Abort();
                            }
                            continue;
                        }

                        // ── Unrecognised entity type
                        ed.WriteMessage(
                            $"\n  Unrecognised entity type: {dxf} — " +
                            $"click directly on a pipe or structure symbol.");
                        tx.Abort();
                    }
                }

                ed.WriteMessage($"\n\n  ═══ PROFOFF COMPLETE ═══");
                ed.WriteMessage($"\n  {removed} part(s) removed from profile view.\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[PROFOFF ERROR] {ex.Message}\n");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Try to open objectId directly as a Civil part and remove from view.
        // ─────────────────────────────────────────────────────────────────────
        private static bool TryRemoveDirect(
            ObjectId id, ObjectId pvId, Transaction tx, Editor ed)
        {
            try
            {
                var ent = tx.GetObject(id, OpenMode.ForWrite);
                string name = "";

                if (ent is CivilDB.Pipe gp)
                {
                    name = gp.Name; gp.RemoveFromProfileView(pvId);
                    ed.WriteMessage($"\n  ✓ Removed gravity pipe '{name}'.");
                    return true;
                }
                if (ent is CivilDB.Structure gs)
                {
                    name = gs.Name; gs.RemoveFromProfileView(pvId);
                    ed.WriteMessage($"\n  ✓ Removed structure '{name}'.");
                    return true;
                }
                if (ent is CivilDB.PressurePipe pp)
                {
                    name = pp.Name; pp.RemoveFromProfileView(pvId);
                    ed.WriteMessage($"\n  ✓ Removed pressure pipe '{name}'.");
                    return true;
                }
                if (ent is CivilDB.PressureFitting pf)
                {
                    name = pf.Name; pf.RemoveFromProfileView(pvId);
                    ed.WriteMessage($"\n  ✓ Removed pressure fitting '{name}'.");
                    return true;
                }
                if (ent is CivilDB.PressureAppurtenance pa)
                {
                    name = pa.Name; pa.RemoveFromProfileView(pvId);
                    ed.WriteMessage($"\n  ✓ Removed pressure appurtenance '{name}'.");
                    return true;
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n  RemoveFromProfileView failed: {ex.Message}");
            }
            return false;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Resolve the underlying network-part ObjectId from a graph proxy.
        //  Tries common property/method names via reflection.
        //  If none work, prints all ObjectId-typed members for diagnosis.
        // ─────────────────────────────────────────────────────────────────────
        private static ObjectId ResolvePartId(DBObject proxy, Editor ed)
        {
            var type  = proxy.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            // Try properties
            foreach (string name in _partIdProps)
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

            // Try methods with no parameters returning ObjectId
            foreach (string name in _partIdMethods)
            {
                try
                {
                    var method = type.GetMethod(name, flags, null, Type.EmptyTypes, null);
                    if (method?.ReturnType == typeof(ObjectId))
                    {
                        var val = (ObjectId)method.Invoke(proxy, null)!;
                        if (!val.IsNull) return val;
                    }
                }
                catch { }
            }

            // ── Diagnostic: print all ObjectId-typed members so we can find the right name
            ed.WriteMessage($"\n  [DIAG] Graph proxy type: {type.FullName}");
            ed.WriteMessage($"\n  [DIAG] ObjectId properties found:");

            foreach (var prop in type.GetProperties(flags))
            {
                if (prop.PropertyType != typeof(ObjectId)) continue;
                try
                {
                    var val = prop.GetValue(proxy);
                    ed.WriteMessage($"\n    .{prop.Name} = {val}");
                }
                catch (System.Exception ex)
                {
                    ed.WriteMessage($"\n    .{prop.Name} → threw {ex.GetType().Name}");
                }
            }

            return ObjectId.Null;
        }
    }
}
