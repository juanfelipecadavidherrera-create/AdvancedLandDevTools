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
                ed.WriteMessage("\n  Click each pipe / structure to hide from its profile view.");
                ed.WriteMessage("\n  Enter or Escape to finish.");
                ed.WriteMessage("\n═══════════════════════════════════════════════════════════");

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
                        // ── Graph proxy entity — resolve to the underlying part + its PV ──
                        if (dxf == DXF_NETWORK_PART || dxf == DXF_PRESSURE_PART)
                        {
                            var proxy  = tx.GetObject(per.ObjectId, OpenMode.ForRead);
                            ObjectId partId = ResolvePartId(proxy, ed);
                            ObjectId pvId   = FindProfileView(proxy, per, db, tx, ed);

                            if (partId.IsNull)
                            {
                                tx.Abort();
                                continue;
                            }
                            if (pvId.IsNull)
                            {
                                ed.WriteMessage("\n  Could not determine which profile view this part belongs to.");
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

                        // ── Direct Civil part (fallback if Civil 3D returns the real entity)
                        // In this path we still need a PV — find it from the pick point only.
                        ObjectId pvIdDirect = FindProfileViewFromPoint(per, db, tx, ed);
                        if (!pvIdDirect.IsNull && TryRemoveDirect(per.ObjectId, pvIdDirect, tx, ed))
                        {
                            removed++;
                            tx.Commit();
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
        //  Resolve profile view for a graph proxy:
        //  1. Try proxy.OwnerId (sometimes the PV owns the proxy directly).
        //  2. Fall back to scanning all PVs in model space by pick-point extents.
        // ─────────────────────────────────────────────────────────────────────
        private static ObjectId FindProfileView(
            DBObject proxy, PromptEntityResult per,
            Database db, Transaction tx, Editor ed)
        {
            // Tier 1 — owner chain
            try
            {
                var owner = tx.GetObject(proxy.OwnerId, OpenMode.ForRead) as CivilDB.ProfileView;
                if (owner != null) return owner.ObjectId;
            }
            catch { }

            // Tier 2 — pick point inside PV extents
            return FindProfileViewFromPoint(per, db, tx, ed);
        }

        private static ObjectId FindProfileViewFromPoint(
            PromptEntityResult per, Database db, Transaction tx, Editor ed)
        {
            try
            {
                var pt  = per.PickedPoint;
                var btr = tx.GetObject(db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                if (btr == null) return ObjectId.Null;

                foreach (ObjectId id in btr)
                {
                    CivilDB.ProfileView pv;
                    try { pv = tx.GetObject(id, OpenMode.ForRead) as CivilDB.ProfileView; }
                    catch { continue; }
                    if (pv == null) continue;

                    var ext = pv.GeometricExtents;
                    if (pt.X >= ext.MinPoint.X && pt.X <= ext.MaxPoint.X &&
                        pt.Y >= ext.MinPoint.Y && pt.Y <= ext.MaxPoint.Y)
                        return pv.ObjectId;
                }
            }
            catch { }

            return ObjectId.Null;
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
