using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using CivilApp = Autodesk.Civil.ApplicationServices;
using CivilDB  = Autodesk.Civil.DatabaseServices;

namespace AdvancedLandDevTools.Engine
{
    public static class LowRimEngine
    {
        public struct RimResult
        {
            public bool   Found;
            public string StructureName;
            public double Elevation;
            public int    TotalStructures;
        }

        /// <summary>
        /// Returns all gravity pipe network names + ObjectIds in the drawing.
        /// </summary>
        public static List<(ObjectId Id, string Name)> GetNetworks()
        {
            var list = new List<(ObjectId, string)>();

            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return list;
            Database db = doc.Database;

            using var tx = db.TransactionManager.StartTransaction();
            try
            {
                var civDoc = CivilApp.CivilDocument.GetCivilDocument(db);
                foreach (ObjectId nid in civDoc.GetPipeNetworkIds())
                {
                    var net = tx.GetObject(nid, OpenMode.ForRead) as CivilDB.Network;
                    if (net != null)
                        list.Add((nid, net.Name));
                }
            }
            catch { }
            tx.Abort();

            return list;
        }

        /// <summary>
        /// Scans all structures in the given network and returns
        /// the one with the lowest "Surface Elevation At Insertion Point".
        /// </summary>
        public static RimResult FindLowest(ObjectId networkId)
        {
            var result = new RimResult { Found = false };

            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return result;
            Database db = doc.Database;

            double lowestElev  = double.MaxValue;
            string lowestName  = "";
            int    count       = 0;

            using var tx = db.TransactionManager.StartTransaction();
            try
            {
                var net = tx.GetObject(networkId, OpenMode.ForRead) as CivilDB.Network;
                if (net == null) { tx.Abort(); return result; }

                foreach (ObjectId sid in net.GetStructureIds())
                {
                    try
                    {
                        var structure = tx.GetObject(sid, OpenMode.ForRead) as CivilDB.Structure;
                        if (structure == null) continue;

                        count++;

                        double elev = structure.SurfaceElevationAtInsertionPoint;
                        if (elev < lowestElev)
                        {
                            lowestElev = elev;
                            lowestName = structure.Name;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            tx.Abort();

            if (count > 0 && lowestElev < double.MaxValue)
            {
                result.Found           = true;
                result.StructureName   = lowestName;
                result.Elevation       = lowestElev;
                result.TotalStructures = count;
            }

            return result;
        }
    }
}
