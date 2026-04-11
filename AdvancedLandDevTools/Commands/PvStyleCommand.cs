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
    /// Step 1: click the profile view border.
    /// Step 2: click the pipe/structure symbol inside the profile view.
    /// Step 3: pick a style from the numbered list.
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

                            var part = tx.GetObject(partId, OpenMode.ForRead) as CivilDB.Part;
                            if (part == null)
                            {
                                ed.WriteMessage("\n  Resolved ID is not a Civil 3D part.");
                                tx.Abort(); continue;
                            }

                            bool isPipeP = part is CivilDB.Pipe || part is CivilDB.PressurePipe;
                            if (HandleStyleChange(part, ent, pvId, isPipeP, tx, ed, db))
                                changed++;
                            else
                                tx.Abort();
                            continue;
                        }

                        // ── Direct Civil part (unusual but handle it)
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
                            bool isPipeD = ent is CivilDB.Pipe || ent is CivilDB.PressurePipe;
                            if (HandleStyleChange(ent as CivilDB.Part, null, pvId, isPipeD, tx, ed, db))
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
        // ─────────────────────────────────────────────────────────────────────
        private static bool HandleStyleChange(
            CivilDB.Part? part, DBObject? proxy,
            ObjectId pvId, bool isPipe,
            Transaction tx, Editor ed, Database db)
        {
            if (part == null) return false;

            string partName  = part.Name;
            string typeLabel = isPipe ? "Pipe" : "Structure";

            ed.WriteMessage($"\n  Part: {typeLabel} '{partName}'");

            // ── Collect available styles ──────────────────────────────────
            var styles = new List<(ObjectId Id, string Label)>();
            TryAddStyle(part.StyleId, "[current global]", styles, tx);
            CollectStylesFromManager(isPipe, styles, tx, db);

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
            bool applied = false;

            // ── Tier 1: PipeOverrides / StructureOverrides on the ProfileView ─
            //   This is the authoritative per-PV style override (the "Style Override"
            //   column in Profile View Properties → Pipe Networks tab).
            applied = TrySetViaOverridesCollection(pvId, part.ObjectId, isPipe, partName, selectedStyleId, tx, ed);

            // (Tier 2 removed — ProfileViewPart proxy does not have OverrideStyleId.
            //  The per-PV override lives in PipeOverride inside PipeOverrideCollection.)

            // ── Nothing worked: diagnostic dump ──────────────────────────────
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
        //  PRIMARY: PipeOverrides / StructureOverrides collection on ProfileView
        //
        //  From diagnostic:
        //    pv.PipeOverrides → PipeOverrideCollection (Count = 14)
        //    PipeOverride properties:
        //      .PipeId          [ObjectId] rw=False  ← pipe to match
        //      .OverrideStyleId [ObjectId] rw=True   ← set this
        //      .UseOverrideStyle[Boolean]  rw=True   ← set to true
        //    Collection methods: GetObjectEnumerator() → IEnumerator
        // ─────────────────────────────────────────────────────────────────────
        private static bool TrySetViaOverridesCollection(
            ObjectId pvId, ObjectId partId, bool isPipe, string partName,
            ObjectId styleId, Transaction tx, Editor ed)
        {
            try
            {
                var pv     = tx.GetObject(pvId, OpenMode.ForWrite);
                var pvType = pv.GetType();
                var flags  = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                string collPropName = isPipe ? "PipeOverrides" : "StructureOverrides";
                var coll = pvType.GetProperty(collPropName, flags)?.GetValue(pv);
                if (coll == null)
                {
                    ed.WriteMessage($"\n  [DIAG] {collPropName} not found on ProfileView.");
                    return false;
                }

                var collType = coll.GetType();

                // ── Find the PipeOverride entry for our part ──────────────
                //
                // Use GetObjectEnumerator() directly (the IEnumerable cast path
                // previously failed to match entries).
                object? entry = null;

                // Enumerate via GetObjectEnumerator() (most reliable for Civil 3D collections).
                // Match strategy:
                //   1. PipeId == partId (direct ObjectId match — works when IDs are consistent)
                //   2. PipeName == partName (name match — fallback when proxy ID ≠ real pipe ID)
                // The name-based fallback is needed because ModelPartId on the proxy returns
                // the proxy's own ObjectId, not the model-space pipe's ObjectId stored in
                // PipeOverride.PipeId.
                var nameProps = isPipe
                    ? new[] { "PipeName", "Name" }
                    : new[] { "StructureName", "Name" };
                var idProps = isPipe
                    ? new[] { "PipeId", "PartId", "EntityId" }
                    : new[] { "StructureId", "PartId", "EntityId" };

                var getEnum = collType.GetMethod("GetObjectEnumerator", flags, null, Type.EmptyTypes, null);
                var enumerator = getEnum?.Invoke(coll, null) as IEnumerator;

                while (enumerator != null && enumerator.MoveNext())
                {
                    var item = enumerator.Current;
                    if (item == null) continue;
                    var itemType = item.GetType();

                    // Try ObjectId match first
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

                    // Name match fallback
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

                if (entry == null)
                {
                    ed.WriteMessage(
                        $"\n  [DIAG] No {collPropName} entry found for '{partName}' " +
                        $"(handle {partId.Handle}).");
                    return false;
                }

                // ── Apply the override ────────────────────────────────────
                var entryType  = entry.GetType();
                var oidProp    = entryType.GetProperty("OverrideStyleId",  flags);
                var useProp    = entryType.GetProperty("UseOverrideStyle", flags);

                if (oidProp?.CanWrite != true)
                {
                    ed.WriteMessage($"\n  [DIAG] OverrideStyleId not writable on {entryType.Name}.");
                    return false;
                }

                oidProp.SetValue(entry, styleId);
                if (useProp?.CanWrite == true)
                    useProp.SetValue(entry, true);

                ed.WriteMessage($"\n  Applied via PV.{collPropName}[PipeId].OverrideStyleId");
                return true;
            }
            catch (System.Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                ed.WriteMessage(
                    $"\n  [DIAG] TrySetViaOverridesCollection: {inner.GetType().Name}: {inner.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  FALLBACK: set override properties directly on the visual proxy entity.
        //
        //  From diagnostic, ProfileViewPart (proxy) has:
        //    .OverrideStyleId  [ObjectId] rw=True   ← per-PV style override
        //    .UseOverrideStyle [Boolean]  rw=True   ← the "Style Override" checkbox
        //
        //  These mirror PipeOverride in the PipeOverrides collection — the proxy
        //  IS the override object.
        // ─────────────────────────────────────────────────────────────────────
        private static bool TrySetProxyOverrideStyle(DBObject proxy, ObjectId styleId, Editor ed)
        {
            var flags    = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type     = proxy.GetType();
            var oidProp  = type.GetProperty("OverrideStyleId", flags);
            var useProp  = type.GetProperty("UseOverrideStyle", flags);

            if (oidProp?.CanWrite != true) return false;

            try
            {
                proxy.UpgradeOpen();
                oidProp.SetValue(proxy, styleId);
                if (useProp?.CanWrite == true)
                    useProp.SetValue(proxy, true);
                ed.WriteMessage("\n  Applied via proxy.OverrideStyleId + UseOverrideStyle");
                return true;
            }
            catch (System.Exception ex)
            {
                // Unwrap TargetInvocationException to get the real Civil 3D error
                var inner = ex.InnerException ?? ex;
                ed.WriteMessage(
                    $"\n  [DIAG] proxy.OverrideStyleId threw: " +
                    $"{inner.GetType().Name}: {inner.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Style enumeration — style manager (all styles) with network fallback
        // ─────────────────────────────────────────────────────────────────────
        private static void CollectStylesFromManager(
            bool isPipe, List<(ObjectId, string)> styles, Transaction tx, Database db)
        {
            var civDoc = CivilApp.CivilDocument.GetCivilDocument(db);
            bool gotFromManager = false;

            try
            {
                var flags      = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var stylesRoot = civDoc.GetType().GetProperty("Styles", flags)?.GetValue(civDoc);
                if (stylesRoot != null)
                {
                    string collName = isPipe ? "PipeStyles" : "StructureStyles";
                    var coll = stylesRoot.GetType().GetProperty(collName, flags)?.GetValue(stylesRoot);
                    if (coll is IEnumerable enumerable)
                    {
                        foreach (var item in enumerable)
                            if (item is ObjectId oid)
                                TryAddStyle(oid, null, styles, tx);
                        gotFromManager = styles.Count > 1;
                    }
                }
            }
            catch { }

            // Fallback: scan all networks for styles currently in use
            if (!gotFromManager)
            {
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
        //  Diagnostic helpers
        // ─────────────────────────────────────────────────────────────────────
        private static void DumpCollectionDiag(
            string collName, object coll, Type collType, BindingFlags flags, Editor ed)
        {
            ed.WriteMessage($"\n  [DIAG] {collName} ({collType.Name}) — no entry found for part.");

            var countProp = collType.GetProperty("Count", flags);
            if (countProp != null)
            {
                try { ed.WriteMessage($"\n  [DIAG] Count = {countProp.GetValue(coll)}"); } catch { }
            }

            ed.WriteMessage($"\n  [DIAG] Methods:");
            foreach (var m in collType.GetMethods(flags)
                .Where(m => !m.Name.StartsWith("get_") && !m.Name.StartsWith("set_"))
                .OrderBy(m => m.Name))
            {
                var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
                ed.WriteMessage($"\n    {m.Name}({ps}) -> {m.ReturnType.Name}");
            }

            // Dump first existing entry if any
            if (coll is IEnumerable en)
            {
                var first = en.Cast<object>().FirstOrDefault();
                if (first != null)
                {
                    ed.WriteMessage($"\n  [DIAG] First entry type: {first.GetType().Name}");
                    DumpEntryDiag(collName, first, first.GetType(), flags, ed);
                }
            }
        }

        private static void DumpEntryDiag(
            string collName, object entry, Type entryType, BindingFlags flags, Editor ed)
        {
            ed.WriteMessage($"\n  [DIAG] {collName} entry ({entryType.Name}) properties:");
            foreach (var p in entryType.GetProperties(flags).OrderBy(p => p.Name))
            {
                string val = "?";
                try { val = p.GetValue(entry)?.ToString() ?? "null"; } catch { val = "threw"; }
                ed.WriteMessage($"\n    .{p.Name} [{p.PropertyType.Name}] rw={p.CanWrite} = {val}");
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
        //  Add a style to the list, deduplicating by ObjectId
        // ─────────────────────────────────────────────────────────────────────
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
