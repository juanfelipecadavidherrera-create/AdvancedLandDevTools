using System;
using System.Collections.Generic;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApp  = Autodesk.AutoCAD.ApplicationServices.Application;
using CivilDB = Autodesk.Civil.DatabaseServices;

[assembly: CommandClass(typeof(AdvancedLandDevTools.Commands.OffPvCommand))]

namespace AdvancedLandDevTools.Commands
{
    /// <summary>
    /// OFFPV — "Off Profile View".
    ///
    /// Step 1 : User clicks any part (pipe / structure / pressure part) in a
    ///          profile view. We resolve that pick to (a) the underlying Civil
    ///          3D part, (b) the profile view it lives in, and (c) the network
    ///          (gravity Network or PressureNetwork) the part belongs to.
    ///
    /// Step 2 : User selects the parts to KEEP visible. Pressing Enter with
    ///          nothing selected turns OFF every part of that network in this
    ///          single profile view.
    ///
    /// Step 3 : Every part of the resolved network that is NOT in the keep-set
    ///          gets RemoveFromProfileView(pvId). Parts not actually drawn in
    ///          the PV are skipped silently (try/catch tolerates the failure,
    ///          same as PROFOFF).
    ///
    /// Operates on the SINGLE profile view of the initial pick — never
    /// touches other PVs.
    /// </summary>
    public class OffPvCommand
    {
        // DXF names of the profile-view graph proxy entities
        private const string DXF_NETWORK_PART  = "AECC_GRAPH_PROFILE_NETWORK_PART";
        private const string DXF_PRESSURE_PART = "AECC_GRAPH_PROFILE_PRESSURE_PART";

        // Mirror PROFOFF's reflection table for resolving graph-proxy → underlying part.
        private static readonly string[] _partIdProps = {
            "ModelPartId",
            "PartId", "NetworkPartId", "BasePipeId", "SourceObjectId",
            "EntityId", "CrossingPipeId", "ComponentObjectId",
            "ReferencedObjectId", "SourceId", "PipeId", "StructureId"
        };
        private static readonly string[] _partIdMethods = {
            "GetPartId", "GetNetworkPartId", "GetSourceId", "GetEntityId"
        };

        [CommandMethod("OFFPV", CommandFlags.Modal)]
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
                ed.WriteMessage("\n  Advanced Land Development Tools  |  Off Profile View");
                ed.WriteMessage("\n  Click any part in a profile view; then pick parts to KEEP.");
                ed.WriteMessage("\n  Enter with nothing selected turns ALL parts off.");
                ed.WriteMessage("\n═══════════════════════════════════════════════════════════");

                // ── STEP 1 : pick the seed part ─────────────────────────────
                ObjectId pvId          = ObjectId.Null;
                ObjectId networkId     = ObjectId.Null;
                bool     isPressure    = false;
                string   networkName   = "<unknown>";
                string   pvName        = "<unknown>";

                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var peo = new PromptEntityOptions(
                        "\n  Click any part in the profile view to choose its network: ");
                    peo.AllowNone = false;
                    var per = ed.GetEntity(peo);

                    if (per.Status != PromptStatus.OK)
                    {
                        tx.Abort();
                        return;
                    }

                    string dxf = per.ObjectId.ObjectClass.DxfName;
                    ObjectId seedPartId = ObjectId.Null;

                    if (dxf == DXF_NETWORK_PART || dxf == DXF_PRESSURE_PART)
                    {
                        var proxy = tx.GetObject(per.ObjectId, OpenMode.ForRead);
                        seedPartId = ResolvePartId(proxy, ed);
                        pvId       = FindProfileView(proxy, per, db, tx, ed);
                    }
                    else
                    {
                        // Fallback: user clicked the real Civil part directly.
                        seedPartId = per.ObjectId;
                        pvId       = FindProfileViewFromPoint(per, db, tx, ed);
                    }

                    if (seedPartId.IsNull)
                    {
                        ed.WriteMessage("\n  Could not resolve the picked part. Aborting.");
                        tx.Abort();
                        return;
                    }
                    if (pvId.IsNull)
                    {
                        ed.WriteMessage("\n  Could not determine the profile view. Aborting.");
                        tx.Abort();
                        return;
                    }

                    // Resolve the parent network of the seed part.
                    if (!TryResolveNetwork(seedPartId, tx, out networkId, out isPressure, out networkName))
                    {
                        ed.WriteMessage("\n  Could not resolve the parent network of the picked part.");
                        tx.Abort();
                        return;
                    }

                    var pv = tx.GetObject(pvId, OpenMode.ForRead) as CivilDB.ProfileView;
                    if (pv != null) pvName = pv.Name;

                    tx.Commit();
                }

                ed.WriteMessage($"\n  Network : {networkName}  ({(isPressure ? "pressure" : "gravity")})");
                ed.WriteMessage($"\n  Profile View : {pvName}");

                // ── STEP 2 : selection set of parts to KEEP ─────────────────
                var keepIds = new HashSet<ObjectId>();

                var pso = new PromptSelectionOptions
                {
                    MessageForAdding =
                        "\n  Select pipes/parts to KEEP visible — Enter to finish, Enter with nothing to keep none and turn ALL off"
                };

                var filter = new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Operator, "<OR"),
                    new TypedValue((int)DxfCode.Start,    DXF_NETWORK_PART),
                    new TypedValue((int)DxfCode.Start,    DXF_PRESSURE_PART),
                    new TypedValue((int)DxfCode.Operator, "OR>")
                });

                var psr = ed.GetSelection(pso, filter);

                if (psr.Status == PromptStatus.OK && psr.Value != null)
                {
                    using var tx = db.TransactionManager.StartTransaction();
                    foreach (SelectedObject sel in psr.Value)
                    {
                        if (sel == null) continue;

                        try
                        {
                            var proxy  = tx.GetObject(sel.ObjectId, OpenMode.ForRead);
                            ObjectId partId = ResolvePartId(proxy, ed);
                            if (partId.IsNull) continue;

                            // Reject parts from a different network.
                            if (TryResolveNetwork(partId, tx,
                                    out ObjectId thisNetId, out _, out string thisNetName))
                            {
                                if (thisNetId != networkId)
                                {
                                    ed.WriteMessage(
                                        $"\n  Skipped — part belongs to network '{thisNetName}', not '{networkName}'.");
                                    continue;
                                }
                            }
                            else
                            {
                                continue;
                            }

                            keepIds.Add(partId);
                        }
                        catch { }
                    }
                    tx.Commit();
                }
                else if (psr.Status != PromptStatus.None && psr.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\n  Cancelled.");
                    return;
                }

                ed.WriteMessage($"\n  Keep-set : {keepIds.Count} part(s).");

                // ── STEP 3 : enumerate the network and remove the rest ──────
                int removed = 0;
                int kept    = 0;

                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var partIds = new List<ObjectId>();

                    if (isPressure)
                    {
                        var pn = tx.GetObject(networkId, OpenMode.ForRead) as CivilDB.PressurePipeNetwork;
                        if (pn != null)
                        {
                            foreach (ObjectId id in pn.GetPipeIds())          partIds.Add(id);
                            foreach (ObjectId id in pn.GetFittingIds())       partIds.Add(id);
                            foreach (ObjectId id in pn.GetAppurtenanceIds())  partIds.Add(id);
                        }
                    }
                    else
                    {
                        var nw = tx.GetObject(networkId, OpenMode.ForRead) as CivilDB.Network;
                        if (nw != null)
                        {
                            foreach (ObjectId id in nw.GetPipeIds())      partIds.Add(id);
                            foreach (ObjectId id in nw.GetStructureIds()) partIds.Add(id);
                        }
                    }

                    foreach (ObjectId pid in partIds)
                    {
                        if (keepIds.Contains(pid))
                        {
                            kept++;
                            continue;
                        }

                        // Try to remove. Tolerate failures (part may not be drawn in this PV).
                        try
                        {
                            if (TryRemoveFromPv(pid, pvId, tx))
                                removed++;
                        }
                        catch { }
                    }

                    tx.Commit();
                }

                ed.WriteMessage($"\n\n  ═══ OFFPV COMPLETE ═══");
                ed.WriteMessage($"\n  Network      : {networkName}");
                ed.WriteMessage($"\n  Profile View : {pvName}");
                ed.WriteMessage($"\n  Removed      : {removed} part(s)");
                ed.WriteMessage($"\n  Kept         : {kept} part(s)\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[OFFPV ERROR] {ex.Message}\n");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Network resolution: from a part ObjectId, find its parent Network /
        //  PressureNetwork ObjectId. We try the strongly-typed properties
        //  first (NetworkId), then fall back to NetworkName lookup.
        // ─────────────────────────────────────────────────────────────────────
        private static bool TryResolveNetwork(
            ObjectId partId, Transaction tx,
            out ObjectId networkId, out bool isPressure, out string networkName)
        {
            networkId   = ObjectId.Null;
            isPressure  = false;
            networkName = "<unknown>";

            try
            {
                var part = tx.GetObject(partId, OpenMode.ForRead);

                if (part is CivilDB.Pipe gp)
                {
                    networkId   = gp.NetworkId;
                    networkName = SafeNetworkName(gp.NetworkId, tx, gp.NetworkName);
                    isPressure  = false;
                    return !networkId.IsNull;
                }
                if (part is CivilDB.Structure gs)
                {
                    networkId   = gs.NetworkId;
                    networkName = SafeNetworkName(gs.NetworkId, tx, gs.NetworkName);
                    isPressure  = false;
                    return !networkId.IsNull;
                }
                if (part is CivilDB.PressurePipe pp)
                {
                    networkId   = TryGetPressureNetworkId(pp, tx, out string nm);
                    networkName = nm;
                    isPressure  = true;
                    return !networkId.IsNull;
                }
                if (part is CivilDB.PressureFitting pf)
                {
                    networkId   = TryGetPressureNetworkId(pf, tx, out string nm);
                    networkName = nm;
                    isPressure  = true;
                    return !networkId.IsNull;
                }
                if (part is CivilDB.PressureAppurtenance pa)
                {
                    networkId   = TryGetPressureNetworkId(pa, tx, out string nm);
                    networkName = nm;
                    isPressure  = true;
                    return !networkId.IsNull;
                }
            }
            catch { }

            return false;
        }

        private static string SafeNetworkName(ObjectId nid, Transaction tx, string fallback)
        {
            try
            {
                if (!nid.IsNull)
                {
                    var nw = tx.GetObject(nid, OpenMode.ForRead);
                    if (nw is CivilDB.Network n)         return n.Name;
                    if (nw is CivilDB.PressurePipeNetwork p) return p.Name;
                }
            }
            catch { }
            return fallback ?? "<unknown>";
        }

        // PressurePipe / PressureFitting / PressureAppurtenance expose the
        // parent network via a "NetworkId" or "NetworkName" property — names
        // vary across releases, so we go through reflection just like PROFOFF.
        private static ObjectId TryGetPressureNetworkId(DBObject part, Transaction tx, out string name)
        {
            name = "<unknown>";
            var type  = part.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            // 1) Try NetworkId-style property
            string[] idProps = { "NetworkId", "PressureNetworkId", "ParentNetworkId" };
            foreach (string n in idProps)
            {
                try
                {
                    var p = type.GetProperty(n, flags);
                    if (p?.PropertyType == typeof(ObjectId))
                    {
                        var val = (ObjectId)p.GetValue(part)!;
                        if (!val.IsNull)
                        {
                            try
                            {
                                if (tx.GetObject(val, OpenMode.ForRead) is CivilDB.PressurePipeNetwork pn)
                                    name = pn.Name;
                            }
                            catch { }
                            return val;
                        }
                    }
                }
                catch { }
            }

            // 2) Fall back: NetworkName + scan model space for matching PressureNetwork
            string netName = null;
            try
            {
                var pName = type.GetProperty("NetworkName", flags);
                if (pName?.PropertyType == typeof(string))
                    netName = pName.GetValue(part) as string;
            }
            catch { }

            if (!string.IsNullOrEmpty(netName))
            {
                name = netName;
                try
                {
                    var db  = part.Database;
                    var btr = tx.GetObject(db.CurrentSpaceId, OpenMode.ForRead) as BlockTableRecord;
                    if (btr != null)
                    {
                        foreach (ObjectId id in btr)
                        {
                            try
                            {
                                if (tx.GetObject(id, OpenMode.ForRead) is CivilDB.PressurePipeNetwork pn
                                    && pn.Name == netName)
                                    return pn.ObjectId;
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            return ObjectId.Null;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Remove a part from the given profile view. Tolerates parts that
        //  are not currently drawn in that PV.
        // ─────────────────────────────────────────────────────────────────────
        private static bool TryRemoveFromPv(ObjectId partId, ObjectId pvId, Transaction tx)
        {
            try
            {
                var ent = tx.GetObject(partId, OpenMode.ForWrite);

                if (ent is CivilDB.Pipe gp)                 { gp.RemoveFromProfileView(pvId); return true; }
                if (ent is CivilDB.Structure gs)            { gs.RemoveFromProfileView(pvId); return true; }
                if (ent is CivilDB.PressurePipe pp)         { pp.RemoveFromProfileView(pvId); return true; }
                if (ent is CivilDB.PressureFitting pf)      { pf.RemoveFromProfileView(pvId); return true; }
                if (ent is CivilDB.PressureAppurtenance pa) { pa.RemoveFromProfileView(pvId); return true; }
            }
            catch { }
            return false;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Profile-view discovery (mirrors PROFOFF).
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
        //  Resolve graph-proxy → underlying part ObjectId (mirror of PROFOFF).
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
                    var method = type.GetMethod(name, flags, null, Type.EmptyTypes, null);
                    if (method?.ReturnType == typeof(ObjectId))
                    {
                        var val = (ObjectId)method.Invoke(proxy, null)!;
                        if (!val.IsNull) return val;
                    }
                }
                catch { }
            }

            return ObjectId.Null;
        }
    }
}
