using System;
using System.Collections.Generic;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CivilDB  = Autodesk.Civil.DatabaseServices;
using AcApp    = Autodesk.AutoCAD.ApplicationServices.Application;
using AdvancedLandDevTools.UI;

namespace AdvancedLandDevTools.Commands
{
    public class MarkFittingsCommand
    {
        private class FittingInfo
        {
            public ObjectId PvId;
            public CivilDB.ProfileView Pv;
            public CivilDB.Alignment Alignment;
            public string Description;
            public double Station;
        }

        private const string DXF_PRESSURE_PART = "AECC_GRAPH_PROFILE_PRESSURE_PART";

        private static readonly string[] _partIdProps = {
            "ModelPartId", "PartId", "NetworkPartId", "BasePipeId",
            "SourceObjectId", "EntityId", "ComponentObjectId",
            "ReferencedObjectId", "SourceId", "PipeId", "StructureId"
        };
        private static readonly string[] _partIdMethods = {
            "GetPartId", "GetNetworkPartId", "GetSourceId", "GetEntityId"
        };

        [CommandMethod("MARKFITTINGS", CommandFlags.Modal)]
        public void MarkFittings()
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
                ed.WriteMessage("  Advanced Land Development Tools  |  Mark Fittings        \n");
                ed.WriteMessage("═══════════════════════════════════════════════════════════\n");

                // Select profile views
                var pvIds = new List<ObjectId>();
                while (true)
                {
                    string prompt = pvIds.Count == 0
                        ? "\n  Select a profile view (Enter when done): "
                        : $"\n  Select another profile view or Enter to finish [{pvIds.Count} selected]: ";

                    var peo = new PromptEntityOptions(prompt) { AllowNone = true, AllowObjectOnLockedLayer = true };
                    var per = ed.GetEntity(peo);
                    
                    if (per.Status == PromptStatus.None || per.Status == PromptStatus.Cancel) break;
                    if (per.Status != PromptStatus.OK) break;

                    using (var txCheck = db.TransactionManager.StartTransaction())
                    {
                        var ent = txCheck.GetObject(per.ObjectId, OpenMode.ForRead);
                        var pv  = ent as CivilDB.ProfileView ?? FindProfileViewAtPoint(per.PickedPoint, txCheck, db);

                        if (pv != null && !pvIds.Contains(pv.ObjectId))
                        {
                            pvIds.Add(pv.ObjectId);
                            ed.WriteMessage($"\n  Added: '{pv.Name}'");
                        }
                        else if (pv == null)
                            ed.WriteMessage("\n  Not a profile view — try again.");
                        else
                            ed.WriteMessage("\n  Already selected.");

                        txCheck.Abort();
                    }
                }

                if (pvIds.Count == 0)
                {
                    ed.WriteMessage("\n  No profile views selected — cancelled.\n");
                    return;
                }

                ed.WriteMessage($"\n  Processing {pvIds.Count} profile view(s)...");

                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    var bt      = tx.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                    var msWrite = tx.GetObject(bt![BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;
                    
                    // Load Linetype DASHED if needed
                    var ltTable = tx.GetObject(db.LinetypeTableId, OpenMode.ForRead) as LinetypeTable;
                    bool hasDashed = ltTable.Has("DASHED");
                    if (!hasDashed)
                    {
                        try 
                        { 
                            db.LoadLineTypeFile("DASHED", "acad.lin"); 
                            hasDashed = ltTable.Has("DASHED");
                        } catch { }
                    }

                    // Collect all pressure part proxies ahead of time
                    var proxyIds = new List<ObjectId>();
                    foreach (ObjectId id in msWrite!)
                    {
                        if (id.ObjectClass.DxfName == DXF_PRESSURE_PART)
                        {
                            proxyIds.Add(id);
                        }
                    }

                    var collectedFittings = new List<FittingInfo>();

                    foreach (ObjectId pvId in pvIds)
                    {
                        var pv = tx.GetObject(pvId, OpenMode.ForRead) as CivilDB.ProfileView;
                        if (pv == null || pv.AlignmentId.IsNull) continue;

                        var alignment = tx.GetObject(pv.AlignmentId, OpenMode.ForRead) as CivilDB.Alignment;
                        if (alignment == null) continue;

                        var pvExt = pv.GeometricExtents;

                        foreach (ObjectId proxyId in proxyIds)
                        {
                            var proxy = tx.GetObject(proxyId, OpenMode.ForRead);
                            bool belongsToPV = false;
                            
                            try
                            {
                                var owner = tx.GetObject(proxy.OwnerId, OpenMode.ForRead) as CivilDB.ProfileView;
                                if (owner != null && owner.ObjectId == pv.ObjectId) belongsToPV = true;
                            } catch { }

                            if (!belongsToPV && proxy is Entity entProxy)
                            {
                                try
                                {
                                    var pExt = entProxy.GeometricExtents;
                                    Point3d center = new Point3d((pExt.MinPoint.X + pExt.MaxPoint.X) / 2, (pExt.MinPoint.Y + pExt.MaxPoint.Y) / 2, 0);
                                    if (center.X >= pvExt.MinPoint.X && center.X <= pvExt.MaxPoint.X &&
                                        center.Y >= pvExt.MinPoint.Y && center.Y <= pvExt.MaxPoint.Y)
                                    {
                                        belongsToPV = true;
                                    }
                                }
                                catch { }
                            }

                            if (belongsToPV)
                            {
                                ObjectId partId = ResolvePartId(proxy, ed);
                                if (!partId.IsNull)
                                {
                                    var fitting = tx.GetObject(partId, OpenMode.ForRead) as CivilDB.PressureFitting;
                                    if (fitting != null)
                                    {
                                        double sta = 0, off = 0;
                                        try
                                        {
                                            alignment.StationOffset(fitting.Position.X, fitting.Position.Y, ref sta, ref off);
                                            
                                            // Check PV limits
                                            if (sta < pv.StationStart - 0.5 || sta > pv.StationEnd + 0.5) continue;
                                            
                                            string desc = "Unknown";
                                            try { desc = !string.IsNullOrEmpty(fitting.PartDescription) ? fitting.PartDescription : fitting.Name; }
                                            catch { try { desc = fitting.Name; } catch { } }

                                            collectedFittings.Add(new FittingInfo
                                            {
                                                 PvId = pvId,
                                                 Pv = pv,
                                                 Alignment = alignment,
                                                 Description = desc,
                                                 Station = sta
                                            });
                                        }
                                        catch { }
                                    }
                                }
                            }
                        }
                    }

                    if (collectedFittings.Count == 0)
                    {
                        ed.WriteMessage("\n  No pressure fittings found in the selected profile views.");
                        tx.Abort();
                        return;
                    }

                    // Group by description
                    var descGroups = new Dictionary<string, int>();
                    foreach(var f in collectedFittings)
                    {
                        if (descGroups.ContainsKey(f.Description)) descGroups[f.Description]++;
                        else descGroups[f.Description] = 1;
                    }

                    var dialogItems = new List<FittingDescriptionItem>();
                    foreach(var kv in descGroups)
                    {
                        dialogItems.Add(new FittingDescriptionItem { Description = kv.Key, Count = kv.Value, IsSelected = true });
                    }
                    dialogItems.Sort((a,b) => a.Description.CompareTo(b.Description));

                    var dlg = new MarkFittingsDialog(dialogItems);
                    bool? dlgRes = AcApp.ShowModalWindow(dlg);
                    if (dlgRes != true)
                    {
                        ed.WriteMessage("\n  Command cancelled.");
                        tx.Abort();
                        return;
                    }

                    var allowedDesc = new HashSet<string>(dlg.SelectedDescriptions);
                    int totalDrawn = 0;

                    // Keep track to avoid duplicating lines in the same PV for the same station
                    var drawnStationsPerPv = new Dictionary<ObjectId, HashSet<string>>();

                    foreach(var f in collectedFittings)
                    {
                        if (!allowedDesc.Contains(f.Description)) continue;

                        if (!drawnStationsPerPv.ContainsKey(f.PvId)) drawnStationsPerPv[f.PvId] = new HashSet<string>();
                        string staKey = f.Station.ToString("F2");
                        if (drawnStationsPerPv[f.PvId].Contains(staKey)) continue;

                        double xBot = 0, yBot = 0, xTop = 0, yTop = 0;
                        if (f.Pv.FindXYAtStationAndElevation(f.Station, f.Pv.ElevationMin, ref xBot, ref yBot) &&
                            f.Pv.FindXYAtStationAndElevation(f.Station, f.Pv.ElevationMax, ref xTop, ref yTop))
                        {
                            var vLine = new Line(new Point3d(xBot, yBot, 0), new Point3d(xTop, yTop, 0));
                            if (hasDashed) vLine.Linetype = "DASHED";
                            msWrite.AppendEntity(vLine);
                            tx.AddNewlyCreatedDBObject(vLine, true);
                            
                            drawnStationsPerPv[f.PvId].Add(staKey);
                            totalDrawn++;
                        }
                    }

                    tx.Commit();
                    ed.WriteMessage($"\n  Total lines drawn: {totalDrawn}");
                    ed.WriteMessage("\n  Mark Fittings complete.");
                    ed.WriteMessage("\n═══════════════════════════════════════════════════════════\n");
                }
            }
            catch (System.Exception ex)
            {
                var d = AcApp.DocumentManager.MdiActiveDocument;
                d?.Editor.WriteMessage($"\n[ALDT ERROR] MARKFITTINGS: {ex.Message}\n");
            }
        }

        private static CivilDB.ProfileView? FindProfileViewAtPoint(Point3d pickPoint, Transaction tx, Database db)
        {
            RXClass pvClass = RXObject.GetClass(typeof(CivilDB.ProfileView));
            var bt = tx.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            var ms = tx.GetObject(bt![BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;

            foreach (ObjectId id in ms!)
            {
                if (!id.ObjectClass.IsDerivedFrom(pvClass)) continue;
                try
                {
                    var pv = tx.GetObject(id, OpenMode.ForRead) as CivilDB.ProfileView;
                    if (pv == null) continue;

                    double sta = 0, elev = 0;
                    if (pv.FindStationAndElevationAtXY(pickPoint.X, pickPoint.Y, ref sta, ref elev))
                        return pv;
                }
                catch { }
            }
            return null;
        }

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
