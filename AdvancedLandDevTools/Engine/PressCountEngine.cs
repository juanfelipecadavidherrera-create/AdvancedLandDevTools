using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using CivilDB = Autodesk.Civil.DatabaseServices;

namespace AdvancedLandDevTools.Engine
{
    /// <summary>Summary info about one pressure network — used to populate the selection dialog.</summary>
    public class PressureNetworkSummary
    {
        public ObjectId NetworkId    { get; set; }
        public string   Name         { get; set; } = "";
        public int      PipeCount    { get; set; }
        public int      FittingCount { get; set; }
    }

    /// <summary>One numbered fitting with its plan-view location.</summary>
    public class FittingInfo
    {
        public int      Number   { get; set; }
        public ObjectId Id       { get; set; }
        public Point3d  Location { get; set; }
    }

    /// <summary>Full result for a single network: total 3D pipe length + numbered fittings.</summary>
    public class PressCountResult
    {
        public PressureNetworkSummary Network      { get; set; } = null!;
        public double                 TotalLength3D { get; set; }
        public List<FittingInfo>      Fittings     { get; set; } = new();
    }

    public static class PressCountEngine
    {
        // ─────────────────────────────────────────────────────────────────────
        //  Scan model space and return one summary row per pressure network.
        // ─────────────────────────────────────────────────────────────────────
        public static List<PressureNetworkSummary> GetPressureNetworks(Database db)
        {
            var networks  = new Dictionary<ObjectId, PressureNetworkSummary>();
            var fitCounts = new Dictionary<ObjectId, int>();

            using var tx = db.TransactionManager.StartTransaction();
            var bt = tx.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            var ms = tx.GetObject(bt![BlockTableRecord.ModelSpace], OpenMode.ForRead)
                     as BlockTableRecord;

            foreach (ObjectId id in ms!)
            {
                try
                {
                    var obj = tx.GetObject(id, OpenMode.ForRead);

                    if (obj is CivilDB.PressurePipe pipe)
                    {
                        var nid = pipe.NetworkId;
                        if (nid.IsNull) continue;

                        if (!networks.TryGetValue(nid, out var summary))
                        {
                            summary = new PressureNetworkSummary
                            {
                                NetworkId = nid,
                                Name      = GetNetworkName(tx, nid)
                            };
                            networks[nid] = summary;
                        }
                        summary.PipeCount++;
                    }
                    else if (obj is CivilDB.PressureFitting fitting)
                    {
                        var nid = fitting.NetworkId;
                        if (nid.IsNull) continue;
                        fitCounts[nid] = fitCounts.TryGetValue(nid, out int c) ? c + 1 : 1;

                        // Ensure there is a slot for networks that only have fittings (no pipes yet)
                        if (!networks.ContainsKey(nid))
                        {
                            networks[nid] = new PressureNetworkSummary
                            {
                                NetworkId = nid,
                                Name      = GetNetworkName(tx, nid)
                            };
                        }
                    }
                }
                catch { }
            }

            // Merge fitting counts into network summaries
            foreach (var kv in fitCounts)
                if (networks.TryGetValue(kv.Key, out var s))
                    s.FittingCount = kv.Value;

            tx.Abort();
            return networks.Values.OrderBy(s => s.Name).ToList();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Compute total 3D length and build a numbered fitting list for one network.
        // ─────────────────────────────────────────────────────────────────────
        public static PressCountResult ComputeNetwork(
            PressureNetworkSummary network, Database db)
        {
            var result = new PressCountResult { Network = network };
            int counter  = 1;
            double total = 0.0;

            using var tx = db.TransactionManager.StartTransaction();
            var bt = tx.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            var ms = tx.GetObject(bt![BlockTableRecord.ModelSpace], OpenMode.ForRead)
                     as BlockTableRecord;

            foreach (ObjectId id in ms!)
            {
                try
                {
                    var obj = tx.GetObject(id, OpenMode.ForRead);

                    if (obj is CivilDB.PressurePipe pipe &&
                        pipe.NetworkId == network.NetworkId)
                    {
                        // 3D Euclidean distance — works for both straight and
                        // gently-curved pressure mains between fitting centers.
                        total += pipe.StartPoint.DistanceTo(pipe.EndPoint);
                    }
                    else if (obj is CivilDB.PressureFitting fitting &&
                             fitting.NetworkId == network.NetworkId)
                    {
                        result.Fittings.Add(new FittingInfo
                        {
                            Number   = counter++,
                            Id       = id,
                            Location = GetFittingLocation(fitting)
                        });
                    }
                }
                catch { }
            }

            result.TotalLength3D = total;
            tx.Abort();
            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Place an MText label at each fitting's plan-view location.
        // ─────────────────────────────────────────────────────────────────────
        public static int PlaceFittingLabels(
            PressCountResult result, Database db, double textHeight)
        {
            int placed = 0;

            using var tx = db.TransactionManager.StartTransaction();
            var bt = tx.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            var ms = tx.GetObject(bt![BlockTableRecord.ModelSpace], OpenMode.ForWrite)
                     as BlockTableRecord;

            foreach (var fi in result.Fittings)
            {
                try
                {
                    var mt = new MText
                    {
                        Contents   = fi.Number.ToString(),
                        Location   = fi.Location,   // preserve the fitting's 3D elevation
                        TextHeight = textHeight,
                        Attachment = AttachmentPoint.MiddleCenter
                    };

                    ms!.AppendEntity(mt);
                    tx.AddNewlyCreatedDBObject(mt, true);
                    placed++;
                }
                catch { }
            }

            tx.Commit();
            return placed;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Retrieve the Name of a pressure network container via reflection so we
        /// don't need to hard-code a specific Civil 3D assembly type name.
        /// </summary>
        private static string GetNetworkName(Transaction tx, ObjectId networkId)
        {
            try
            {
                var obj     = tx.GetObject(networkId, OpenMode.ForRead);
                var nameProp = obj.GetType().GetProperty("Name");
                if (nameProp != null)
                {
                    var val = nameProp.GetValue(obj) as string;
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                }
            }
            catch { }
            return networkId.Handle.ToString();
        }

        /// <summary>
        /// Try Location first (fitting center), fall back to Position if not available.
        /// </summary>
        private static Point3d GetFittingLocation(CivilDB.PressureFitting fitting)
        {
            try { return fitting.Location; }
            catch { }
            try { return fitting.Position; }
            catch { }
            return Point3d.Origin;
        }
    }
}
