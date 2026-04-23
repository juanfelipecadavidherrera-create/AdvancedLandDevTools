using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApp    = Autodesk.AutoCAD.ApplicationServices.Application;
using CivilApp = Autodesk.Civil.ApplicationServices;
using CivilDB  = Autodesk.Civil.DatabaseServices;

namespace AdvancedLandDevTools.Commands
{
    /// <summary>
    /// PVSTYLE — Change the per-profile-view style override of a pipe or structure.
    /// Step 1: click the pipe/structure symbol inside the profile view.
    /// Step 2: pick a style from the numbered list.
    /// Works for gravity pipes, gravity structures, and pressure pipes/fittings/appurtenances.
    /// </summary>
    public class PvStyleCommand
    {
        private const string DXF_NETWORK_PART  = "AECC_GRAPH_PROFILE_NETWORK_PART";
        private const string DXF_PRESSURE_PART = "AECC_GRAPH_PROFILE_PRESSURE_PART";

        private static readonly string[] _partIdProps = {
            "ModelPartId", "PartId", "NetworkPartId", "BasePipeId",
            "SourceObjectId", "EntityId", "CrossingPipeId",
            "ComponentObjectId", "ReferencedObjectId", "SourceId",
            "PipeId", "StructureId"
        };
        private static readonly string[] _partIdMethods = {
            "GetPartId", "GetNetworkPartId", "GetSourceId", "GetEntityId"
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
                ed.WriteMessage("\n  Advanced Land Development Tools  |  PV Style Override");
                ed.WriteMessage("\n  Click a pipe / structure to change its style override.");
                ed.WriteMessage("\n  Enter or Escape to finish.");
                ed.WriteMessage("\n═══════════════════════════════════════════════════════════");

                int changed = 0;
                while (true)
                {
                    var peo = new PromptEntityOptions(
                        "\n  Click on a pipe or structure in the profile view <Enter to finish>: ");
                    peo.AllowNone = true;

                    var per = ed.GetEntity(peo);
                    if (per.Status == PromptStatus.None ||
                        per.Status == PromptStatus.Cancel) break;
                    if (per.Status != PromptStatus.OK) continue;

                    string dxf = per.ObjectId.ObjectClass.DxfName;

                    using (var tx = db.TransactionManager.StartTransaction())
                    {
                        var ent = tx.GetObject(per.ObjectId, OpenMode.ForRead);

                        // ── Graph proxy entity — resolve to the underlying part + PV ──
                        if (dxf == DXF_NETWORK_PART || dxf == DXF_PRESSURE_PART)
                        {
                            ObjectId partId = ResolvePartId(ent, ed);
                            if (partId.IsNull) { tx.Abort(); continue; }

                            ObjectId pvId = FindProfileView(ent, per, db, tx, ed);
                            if (pvId.IsNull)
                            {
                                ed.WriteMessage("\n  Could not determine which profile view this part belongs to.");
                                tx.Abort(); continue;
                            }

                            // Resolve the actual Civil 3D part object (gravity OR pressure)
                            var actualObj = tx.GetObject(partId, OpenMode.ForRead);
                            string? partName = GetPartName(actualObj);
                            if (partName == null)
                            {
                                ed.WriteMessage(
                                    $"\n  Resolved ID is not a recognised Civil 3D part " +
                                    $"(type: {actualObj.GetType().Name}).");
                                tx.Abort(); continue;
                            }

                            bool isPipe     = IsPipePart(actualObj);
                            bool isPressure = IsPressurePart(actualObj);

                            if (HandleStyleChange(actualObj, partName, isPipe, isPressure, pvId, tx, ed, db))
                                changed++;
                            else
                                tx.Abort();
                            continue;
                        }

                        // ── Direct Civil part (unusual but handle it) ────────────────
                        if (ent is CivilDB.Pipe || ent is CivilDB.Structure ||
                            ent is CivilDB.PressurePipe || ent is CivilDB.PressureFitting ||
                            ent is CivilDB.PressureAppurtenance)
                        {
                            ObjectId pvId = FindProfileViewFromPoint(per, db, tx, ed);
                            if (pvId.IsNull)
                            {
                                ed.WriteMessage("\n  Could not determine which profile view this part belongs to.");
                                tx.Abort(); continue;
                            }

                            string? partName = GetPartName(ent);
                            if (partName == null) { tx.Abort(); continue; }

                            bool isPipe     = IsPipePart(ent);
                            bool isPressure = IsPressurePart(ent);

                            if (HandleStyleChange(ent, partName, isPipe, isPressure, pvId, tx, ed, db))
                                changed++;
                            else
                                tx.Abort();
                            continue;
                        }

                        ed.WriteMessage(
                            $"\n  Unrecognised entity type: {dxf} — " +
                            "click directly on a pipe or structure symbol.");
                        tx.Abort();
                    }
                }

                ed.WriteMessage($"\n\n  ═══ PVSTYLE COMPLETE ═══");
                ed.WriteMessage($"\n  {changed} part(s) style-overridden.\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[PVSTYLE ERROR] {ex.Message}\n");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Part-type helpers — work for both gravity and pressure hierarchies
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Returns the part name regardless of gravity/pressure hierarchy.</summary>
        private static string? GetPartName(DBObject obj)
        {
            if (obj is CivilDB.Part p)                    return p.Name;
            if (obj is CivilDB.PressurePipe pp)           return pp.Name;
            if (obj is CivilDB.PressureFitting pf)        return pf.Name;
            if (obj is CivilDB.PressureAppurtenance pa)   return pa.Name;
            return null;
        }

        /// <summary>True for gravity pipes and pressure pipes (not structures/fittings).</summary>
        private static bool IsPipePart(DBObject obj) =>
            obj is CivilDB.Pipe || obj is CivilDB.PressurePipe;

        /// <summary>True for pressure-network parts (PressurePipe, PressureFitting, PressureAppurtenance).</summary>
        private static bool IsPressurePart(DBObject obj) =>
            obj is CivilDB.PressurePipe ||
            obj is CivilDB.PressureFitting ||
            obj is CivilDB.PressureAppurtenance;

        // ─────────────────────────────────────────────────────────────────────
        //  Resolve profile view for a graph proxy:
        //  1. Try proxy.OwnerId (sometimes the PV owns the proxy directly).
        //  2. Fall back to scanning all PVs in model space by pick-point extents.
        // ─────────────────────────────────────────────────────────────────────
        private static ObjectId FindProfileView(
            DBObject proxy, PromptEntityResult per,
            Database db, Transaction tx, Editor ed)
        {
            try
            {
                var owner = tx.GetObject(proxy.OwnerId, OpenMode.ForRead) as CivilDB.ProfileView;
                if (owner != null) return owner.ObjectId;
            }
            catch { }

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
        //  Core: collect styles, prompt user, apply override
        //  Works for gravity pipes, gravity structures, and pressure parts.
        // ─────────────────────────────────────────────────────────────────────
        private static bool HandleStyleChange(
            DBObject part, string partName,
            bool isPipe, bool isPressure,
            ObjectId pvId,
            Transaction tx, Editor ed, Database db)
        {
            string typeLabel = isPressure
                ? (isPipe ? "Pressure Pipe" : "Pressure Fitting/Appurtenance")
                : (isPipe ? "Pipe" : "Structure");

            ed.WriteMessage($"\n  Part: {typeLabel} '{partName}'");

            // ── Get current global StyleId via typed access or reflection ────
            var styles = new List<(ObjectId Id, string Label)>();
            ObjectId currentStyleId = GetStyleIdReflection(part);
            if (!currentStyleId.IsNull)
                TryAddStyle(currentStyleId, "[current global]", styles, tx);

            CollectStylesFromManager(isPipe, isPressure, styles, tx, db);

            if (styles.Count == 0)
            {
                ed.WriteMessage("\n  No styles found for this part type.");
                return false;
            }

            // ── Display list ──────────────────────────────────────────────
            ed.WriteMessage($"\n\n  Available {typeLabel} styles:\n");
            for (int i = 0; i < styles.Count; i++)
                ed.WriteMessage($"    [{i + 1}]  {styles[i].Label}\n");

            var pio = new PromptIntegerOptions(
                $"\n  Select style number [1-{styles.Count}]: ")
            { LowerLimit = 1, UpperLimit = styles.Count, AllowNone = false };
            var pir = ed.GetInteger(pio);
            if (pir.Status != PromptStatus.OK)
            { ed.WriteMessage("\n  Skipped."); return false; }

            var (selectedStyleId, selectedLabel) = styles[pir.Value - 1];

            bool applied = TrySetViaOverridesCollection(
                pvId, part.ObjectId, isPipe, isPressure, partName, selectedStyleId, tx, ed);

            if (!applied)
            {
                ed.WriteMessage(
                    "\n  Could not set per-PV style override — no matching API found.");
            }
            else
            {
                ed.WriteMessage(
                    $"\n  ✓ Style override set to '{selectedLabel}' for {typeLabel} '{partName}'.");
                tx.Commit();
            }

            return applied;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  PRIMARY: PipeOverrides / StructureOverrides / PressurePipeOverrides
        //  collection on ProfileView — tries multiple collection names in order.
        // ─────────────────────────────────────────────────────────────────────
        private static bool TrySetViaOverridesCollection(
            ObjectId pvId, ObjectId partId, bool isPipe, bool isPressure, string partName,
            ObjectId styleId, Transaction tx, Editor ed)
        {
            // Try collection names in priority order
            var collNames = new List<string>();
            if (isPressure)
            {
                collNames.Add("PressurePipeOverrides");
                collNames.Add("CrossingPressurePipeOverrides");
                collNames.Add("PipeOverrides");
                collNames.Add("CrossingPipeOverrides");
            }
            else
            {
                if (isPipe)
                {
                    collNames.Add("PipeOverrides");
                    collNames.Add("CrossingPipeOverrides");
                }
                else
                {
                    collNames.Add("StructureOverrides");
                    collNames.Add("CrossingStructureOverrides");
                }
            }

            foreach (string collName in collNames)
            {
                if (TryOverrideOneCollection(pvId, partId, partName, collName, styleId, tx, ed))
                    return true;
            }

            ed.WriteMessage(
                $"\n  [DIAG] No override collection entry found for '{partName}' " +
                $"(handle {partId.Handle}) across [{string.Join(", ", collNames)}].");
            return false;
        }

        /// <summary>
        /// Attempts to find and update one named override collection on the ProfileView.
        /// Returns true if the override was applied.
        /// </summary>
        private static bool TryOverrideOneCollection(
            ObjectId pvId, ObjectId partId, string partName, string collPropName,
            ObjectId styleId, Transaction tx, Editor ed)
        {
            try
            {
                var pv     = tx.GetObject(pvId, OpenMode.ForWrite);
                var pvType = pv.GetType();
                var flags  = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                var coll = pvType.GetProperty(collPropName, flags)?.GetValue(pv);
                if (coll == null) return false;

                var collType = coll.GetType();

                // ID property names to try for matching
                var idProps   = new[] { "PipeId", "PartId", "EntityId", "StructureId", "CrossingPipeId", "SourcePipeId", "CrossingId" };
                // Name property names for fallback match
                var nameProps = new[] { "PipeName", "Name", "StructureName" };

                object? entry = null;

                var getEnum    = collType.GetMethod("GetObjectEnumerator", flags, null, Type.EmptyTypes, null);
                var enumerator = getEnum?.Invoke(coll, null) as IEnumerator;

                while (enumerator != null && enumerator.MoveNext())
                {
                    var item = enumerator.Current;
                    if (item == null) continue;
                    var itemType = item.GetType();

                    // Tier 1: ObjectId match
                    foreach (string idp in idProps)
                    {
                        var prop = itemType.GetProperty(idp, flags);
                        if (prop?.PropertyType != typeof(ObjectId)) continue;
                        try
                        {
                            if ((ObjectId)prop.GetValue(item)! == partId)
                            { entry = item; break; }
                        }
                        catch { }
                    }
                    if (entry != null) break;

                    // Tier 2: name match (handles proxy-ID mismatch)
                    foreach (string np in nameProps)
                    {
                        var prop = itemType.GetProperty(np, flags);
                        if (prop?.PropertyType != typeof(string)) continue;
                        try
                        {
                            if ((string?)prop.GetValue(item) == partName)
                            { entry = item; break; }
                        }
                        catch { }
                    }
                    if (entry != null) break;
                }

                if (entry == null) return false;

                var entryType = entry.GetType();
                var oidProp   = entryType.GetProperty("OverrideStyleId",  flags);
                var useProp   = entryType.GetProperty("UseOverrideStyle", flags);

                if (oidProp?.CanWrite != true)
                {
                    ed.WriteMessage($"\n  [DIAG] OverrideStyleId not writable on {entryType.Name}.");
                    return false;
                }

                oidProp.SetValue(entry, styleId);
                if (useProp?.CanWrite == true)
                    useProp.SetValue(entry, true);

                ed.WriteMessage($"\n  Applied via PV.{collPropName}[..].OverrideStyleId");
                return true;
            }
            catch (System.Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                ed.WriteMessage(
                    $"\n  [DIAG] {collPropName}: {inner.GetType().Name}: {inner.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Style enumeration — works for gravity and pressure networks
        // ─────────────────────────────────────────────────────────────────────
        private static void CollectStylesFromManager(
            bool isPipe, bool isPressure,
            List<(ObjectId, string)> styles, Transaction tx, Database db)
        {
            var civDoc = CivilApp.CivilDocument.GetCivilDocument(db);
            bool gotFromManager = false;

            try
            {
                var flags      = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var stylesRoot = civDoc.GetType().GetProperty("Styles", flags)?.GetValue(civDoc);
                if (stylesRoot != null)
                {
                    // Pressure pipes use a separate style category from gravity pipes
                    string[] collNames = isPressure
                        ? new[] { "PressurePipeStyles", "PressurePartStyles" }
                        : isPipe
                            ? new[] { "PipeStyles" }
                            : new[] { "StructureStyles" };

                    foreach (string collName in collNames)
                    {
                        var coll = stylesRoot.GetType().GetProperty(collName, flags)?.GetValue(stylesRoot);
                        if (coll is IEnumerable enumerable)
                        {
                            foreach (var item in enumerable)
                                if (item is ObjectId oid)
                                    TryAddStyle(oid, null, styles, tx);
                            if (styles.Count > 1) { gotFromManager = true; break; }
                        }
                    }
                }
            }
            catch { }

            if (gotFromManager) return;

            // Fallback: scan networks for styles currently in use
            if (isPressure)
            {
                // Pressure networks — use reflection because GetPressureNetworkIds may vary
                try
                {
                    var flags   = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    var civType = civDoc.GetType();
                    var getNets = civType.GetMethod("GetPressureNetworkIds", flags, null, Type.EmptyTypes, null);
                    var nids    = getNets?.Invoke(civDoc, null) as IEnumerable;
                    if (nids != null)
                    {
                        foreach (var item in nids)
                        {
                            if (item is not ObjectId nid) continue;
                            try
                            {
                                var net     = tx.GetObject(nid, OpenMode.ForRead);
                                var netType = net.GetType();
                                foreach (string mName in new[] {
                                    "GetPressurePipeIds", "GetFittingIds", "GetAppurtenanceIds" })
                                {
                                    var getIds = netType.GetMethod(mName, flags, null, Type.EmptyTypes, null);
                                    if (getIds?.Invoke(net, null) is IEnumerable ids)
                                        foreach (var pidItem in ids)
                                            if (pidItem is ObjectId pid)
                                                TryAddStyleReflection(pid, styles, tx);
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            else
            {
                // Gravity networks
                foreach (ObjectId nid in civDoc.GetPipeNetworkIds())
                {
                    try
                    {
                        var net = tx.GetObject(nid, OpenMode.ForRead) as CivilDB.Network;
                        if (net == null) continue;
                        var ids = isPipe
                            ? net.GetPipeIds().Cast<ObjectId>()
                            : net.GetStructureIds().Cast<ObjectId>();
                        foreach (ObjectId pid in ids)
                        {
                            try
                            {
                                var p = tx.GetObject(pid, OpenMode.ForRead) as CivilDB.Part;
                                if (p != null) TryAddStyle(p.StyleId, null, styles, tx);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Resolve the underlying Part ObjectId from a graph proxy entity
        // ─────────────────────────────────────────────────────────────────────
        private static ObjectId ResolvePartId(DBObject proxy, Editor ed)
        {
            var type  = proxy.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

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

            foreach (string name in _partIdMethods)
            {
                try
                {
                    var m = type.GetMethod(name, flags, null, Type.EmptyTypes, null);
                    if (m?.ReturnType == typeof(ObjectId))
                    {
                        var val = (ObjectId)m.Invoke(proxy, null)!;
                        if (!val.IsNull) return val;
                    }
                }
                catch { }
            }

            ed.WriteMessage($"\n  [DIAG] Could not resolve part ID from proxy {proxy.GetType().Name}");
            return ObjectId.Null;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Gets StyleId via reflection — works for both CivilDB.Part and PressurePipe hierarchies.</summary>
        private static ObjectId GetStyleIdReflection(DBObject obj)
        {
            // Fast path for gravity parts
            if (obj is CivilDB.Part p) return p.StyleId;

            // Reflection path for pressure parts
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var prop  = obj.GetType().GetProperty("StyleId", flags);
            if (prop?.PropertyType == typeof(ObjectId))
            {
                try { return (ObjectId)prop.GetValue(obj)!; }
                catch { }
            }
            return ObjectId.Null;
        }

        /// <summary>Gets StyleId via reflection from a part loaded by ObjectId.</summary>
        private static void TryAddStyleReflection(
            ObjectId partId, List<(ObjectId, string)> styles, Transaction tx)
        {
            try
            {
                var obj = tx.GetObject(partId, OpenMode.ForRead);
                var sid = GetStyleIdReflection(obj);
                TryAddStyle(sid, null, styles, tx);
            }
            catch { }
        }

        /// <summary>Add a style to the list, deduplicating by ObjectId.</summary>
        private static void TryAddStyle(
            ObjectId styleId, string? suffix,
            List<(ObjectId, string)> styles, Transaction tx)
        {
            if (styleId.IsNull || styles.Exists(s => s.Item1 == styleId)) return;
            try
            {
                var styleBase = tx.GetObject(styleId, OpenMode.ForRead) as CivilDB.Styles.StyleBase;
                string name  = styleBase?.Name ?? styleId.Handle.ToString();
                string label = suffix != null ? $"{name}  {suffix}" : name;
                styles.Add((styleId, label));
            }
            catch { }
        }
    }
}
