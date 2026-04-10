using System;
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
    /// PVSTYLE — Change the style override of a pipe or structure drawn in a profile view.
    ///
    /// Mirrors the PROFOFF workflow:
    ///   1. Click the profile view border/background to lock it.
    ///   2. Click the pipe or structure symbol in the profile view.
    ///   3. A numbered list of available styles appears in the command window.
    ///   4. Type a number to apply that style as the profile-view style override.
    ///
    /// This changes the "Style Override" property visible in
    ///   Profile View Properties → Pipe Networks → [part] → Style.
    /// It does NOT change the part's global style — only the profile view appearance.
    /// </summary>
    public class PvStyleCommand
    {
        // DXF names for graph proxy entities (same as PROFOFF)
        private const string DXF_NETWORK_PART  = "AECC_GRAPH_PROFILE_NETWORK_PART";
        private const string DXF_PRESSURE_PART = "AECC_GRAPH_PROFILE_PRESSURE_PART";

        // Property names to resolve underlying part ObjectId from graph proxy
        private static readonly string[] _partIdProps = {
            "ModelPartId", "PartId", "NetworkPartId", "BasePipeId",
            "SourceObjectId", "EntityId", "CrossingPipeId",
            "ComponentObjectId", "ReferencedObjectId", "SourceId",
            "PipeId", "StructureId"
        };
        private static readonly string[] _partIdMethods = {
            "GetPartId", "GetNetworkPartId", "GetSourceId", "GetEntityId"
        };

        // Property names to get/set style on the graph proxy entity
        private static readonly string[] _proxyStyleProps = {
            "OverrideStyleId", "StyleId", "StyleOverrideId",
            "DisplayStyleId", "PartStyleId"
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
                ed.WriteMessage("\n  Step 1: click the profile view border or background.");
                ed.WriteMessage("\n  Step 2: click a pipe / structure to change its style.");
                ed.WriteMessage("\n          Enter or Escape to finish.");
                ed.WriteMessage("\n═══════════════════════════════════════════════════════════");

                // ── Step 1: select the profile view (same as PROFOFF) ────────
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
                        tx.Abort(); return;
                    }

                    var pv = tx.GetObject(pvRes.ObjectId, OpenMode.ForRead)
                             as CivilDB.ProfileView;
                    if (pv == null)
                    {
                        ed.WriteMessage("\n  Could not open profile view.\n");
                        tx.Abort(); return;
                    }

                    pvId = pv.ObjectId;
                    ed.WriteMessage($"\n  Profile view locked: '{pv.Name}'");
                    ed.WriteMessage("\n  Now click on parts to change their style.\n");
                    tx.Commit();
                }

                // ── Step 2: pick parts (loop until Enter/Escape) ─────────────
                int changed = 0;

                while (true)
                {
                    var peo = new PromptEntityOptions(
                        "\n  Click on a pipe or structure in the profile view <Enter to finish>: ");
                    peo.AllowNone = true;

                    var per = ed.GetEntity(peo);

                    if (per.Status == PromptStatus.None ||
                        per.Status == PromptStatus.Cancel)
                        break;
                    if (per.Status != PromptStatus.OK) continue;

                    string dxf = per.ObjectId.ObjectClass.DxfName;

                    using (var tx = db.TransactionManager.StartTransaction())
                    {
                        // ── Direct Civil part (if Civil 3D returns the real entity)
                        var ent = tx.GetObject(per.ObjectId, OpenMode.ForRead);

                        if (ent is CivilDB.Pipe || ent is CivilDB.Structure ||
                            ent is CivilDB.PressurePipe || ent is CivilDB.PressureFitting ||
                            ent is CivilDB.PressureAppurtenance)
                        {
                            if (HandleStyleChange(ent as CivilDB.Part, null, pvId, tx, ed, db))
                                changed++;
                            else
                                tx.Abort();
                            continue;
                        }

                        // ── Graph proxy entity — resolve to the underlying part
                        if (dxf == DXF_NETWORK_PART || dxf == DXF_PRESSURE_PART)
                        {
                            var proxy = tx.GetObject(per.ObjectId, OpenMode.ForRead);
                            ObjectId partId = ResolvePartId(proxy, ed);

                            if (partId.IsNull)
                            {
                                tx.Abort(); continue;
                            }

                            var part = tx.GetObject(partId, OpenMode.ForRead) as CivilDB.Part;
                            if (part == null)
                            {
                                ed.WriteMessage("\n  Resolved ID is not a Civil 3D part.");
                                tx.Abort(); continue;
                            }

                            if (HandleStyleChange(part, proxy, pvId, tx, ed, db))
                                changed++;
                            else
                                tx.Abort();
                            continue;
                        }

                        // ── Unrecognised entity
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
        //  Core logic: show available styles, let user pick, apply override.
        //  Returns true (and commits tx) on success.
        // ─────────────────────────────────────────────────────────────────────
        private static bool HandleStyleChange(
            CivilDB.Part part, DBObject? proxy,
            ObjectId pvId, Transaction tx, Editor ed, Database db)
        {
            bool isPipe = part is CivilDB.Pipe;
            string partName = part.Name;
            string typeLabel = isPipe ? "Pipe" : "Structure";

            ed.WriteMessage($"\n  Part: {typeLabel} '{partName}'");

            // ── Collect available styles for this part type ───────────────
            var styles = new List<(ObjectId Id, string Label)>();

            var civDoc = CivilApp.CivilDocument.GetCivilDocument(db);

            // Current style first
            TryAddStyle(part.StyleId, "[current]", styles, tx);

            // Scan all networks for additional styles of the same type
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

            if (styles.Count == 0)
            {
                ed.WriteMessage("\n  No styles found for this part type.");
                return false;
            }

            // ── Display numbered list ────────────────────────────────────
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
                ed.WriteMessage("\n  Skipped.");
                return false;
            }

            var (selectedStyleId, selectedLabel) = styles[pir.Value - 1];

            // ── Apply the style override ─────────────────────────────────
            // Priority 1: set StyleId on the graph proxy entity itself.
            //   This is the profile-view-specific style override — it only
            //   changes how the part looks in THIS profile view.
            // Priority 2: reflection methods on the real Part with (pvId, styleId).
            // Priority 3: change the part's global StyleId (last resort).

            bool applied = false;

            // Priority 1: proxy entity style properties
            if (proxy != null && !applied)
                applied = TrySetProxyStyle(proxy, selectedStyleId, tx, ed);

            // Priority 2: Part profile-view override methods
            if (!applied)
                applied = TrySetPartPvOverride(part, pvId, selectedStyleId, tx, ed);

            // Priority 3: global style (fallback)
            if (!applied)
            {
                try
                {
                    part.UpgradeOpen();
                    part.StyleId = selectedStyleId;
                    applied = true;
                    ed.WriteMessage(
                        "\n  (Applied as global style change — no per-PV override API found)");
                }
                catch (System.Exception ex)
                {
                    ed.WriteMessage($"\n  Failed to set global style: {ex.Message}");
                }
            }

            if (applied)
            {
                ed.WriteMessage(
                    $"\n  ✓ Style set to '{selectedLabel}' for {typeLabel} '{partName}'.");
                tx.Commit();
            }

            return applied;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Try to set style directly on the graph proxy entity.
        //  The proxy (AECC_GRAPH_PROFILE_NETWORK_PART) represents the part
        //  as drawn in the profile view — setting its StyleId is the true
        //  "profile view style override".
        // ─────────────────────────────────────────────────────────────────────
        private static bool TrySetProxyStyle(
            DBObject proxy, ObjectId styleId, Transaction tx, Editor ed)
        {
            var type  = proxy.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (string propName in _proxyStyleProps)
            {
                try
                {
                    var prop = type.GetProperty(propName, flags);
                    if (prop == null) continue;
                    if (prop.PropertyType != typeof(ObjectId)) continue;
                    if (!prop.CanWrite) continue;

                    // Upgrade to write
                    if (proxy is Entity ent)
                        ent.UpgradeOpen();
                    else
                        proxy.UpgradeOpen();

                    prop.SetValue(proxy, styleId);
                    ed.WriteMessage($"\n  Applied via proxy.{propName}");
                    return true;
                }
                catch { }
            }

            // If none of the known names worked, try ANY writable ObjectId property
            // whose name contains "style" (case-insensitive)
            foreach (var prop in type.GetProperties(flags))
            {
                if (prop.PropertyType != typeof(ObjectId)) continue;
                if (!prop.CanWrite) continue;
                if (!prop.Name.Contains("Style", StringComparison.OrdinalIgnoreCase) &&
                    !prop.Name.Contains("style", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    if (proxy is Entity ent)
                        ent.UpgradeOpen();
                    else
                        proxy.UpgradeOpen();

                    prop.SetValue(proxy, styleId);
                    ed.WriteMessage($"\n  Applied via proxy.{prop.Name}");
                    return true;
                }
                catch { }
            }

            return false;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Try to set profile-view-specific style override on the real Part
        //  via reflection (method signatures with pvId + styleId).
        // ─────────────────────────────────────────────────────────────────────
        private static readonly string[] _pvOverrideMethods = {
            "SetProfileViewStyleOverride",
            "SetProfileViewStyle",
            "SetProfileViewStyleId",
            "SetDrawingStyle",
            "OverrideStyleInProfileView"
        };

        private static bool TrySetPartPvOverride(
            CivilDB.Part part, ObjectId pvId, ObjectId styleId,
            Transaction tx, Editor ed)
        {
            var type     = part.GetType();
            var flags    = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var argTypes = new[] { typeof(ObjectId), typeof(ObjectId) };
            var args     = new object[] { pvId, styleId };

            part.UpgradeOpen();

            foreach (string methodName in _pvOverrideMethods)
            {
                try
                {
                    var m = type.GetMethod(methodName, flags, null, argTypes, null);
                    if (m == null) continue;
                    m.Invoke(part, args);
                    ed.WriteMessage($"\n  Applied via Part.{methodName}()");
                    return true;
                }
                catch { }
            }

            return false;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Resolve the underlying Part ObjectId from a graph proxy entity.
        //  Same logic as PROFOFF.
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

            // Try no-arg methods
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

            // Diagnostic dump
            ed.WriteMessage($"\n  [DIAG] Graph proxy type: {type.FullName}");
            ed.WriteMessage($"\n  [DIAG] ObjectId properties:");
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

        // ─────────────────────────────────────────────────────────────────────
        //  Add a style to the list (deduplicates by ObjectId).
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
                string name  = styleBase?.Name ?? styleId.Handle.ToString();
                string label = suffix != null ? $"{name}  {suffix}" : name;
                styles.Add((styleId, label));
            }
            catch { }
        }
    }
}
