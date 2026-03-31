using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using CivilApp = Autodesk.Civil.ApplicationServices;
using CivilDB  = Autodesk.Civil.DatabaseServices;

namespace AdvancedLandDevTools.Commands
{
    public class BlockToSurfaceCommand
    {
        [CommandMethod("BLOCKTOSURFACE", CommandFlags.Modal)]
        public void Execute()
        {
            try
            {
                if (!Engine.LicenseManager.EnsureLicensed()) return;

                Document doc = AcadApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                Editor ed = doc.Editor;
                Database db = doc.Database;

                // ── Step 1: Select TIN Surface ──────────────────────────
                ObjectId surfaceId = ObjectId.Null;
                string surfaceName = "";

                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    CivilApp.CivilDocument civDoc = CivilApp.CivilDocument.GetCivilDocument(db);
                    var surfaces = new List<(string Name, ObjectId Id)>();

                    foreach (ObjectId id in civDoc.GetSurfaceIds())
                    {
                        try
                        {
                            var surf = tx.GetObject(id, OpenMode.ForRead) as CivilDB.TinSurface;
                            if (surf != null)
                                surfaces.Add((surf.Name, id));
                        }
                        catch { }
                    }

                    tx.Commit();

                    if (surfaces.Count == 0)
                    {
                        ed.WriteMessage("\n  No TIN surfaces found in drawing.\n");
                        return;
                    }

                    // Build keyword prompt
                    ed.WriteMessage("\n  Available TIN surfaces:\n");
                    for (int i = 0; i < surfaces.Count; i++)
                        ed.WriteMessage($"    [{i + 1}] {surfaces[i].Name}\n");

                    var pio = new PromptIntegerOptions($"\n  Select surface number [1-{surfaces.Count}]: ")
                    {
                        LowerLimit = 1,
                        UpperLimit = surfaces.Count,
                        AllowNone = false
                    };
                    var pir = ed.GetInteger(pio);
                    if (pir.Status != PromptStatus.OK) return;

                    int idx = pir.Value - 1;
                    surfaceId = surfaces[idx].Id;
                    surfaceName = surfaces[idx].Name;
                    ed.WriteMessage($"\n  Surface selected: {surfaceName}\n");
                }

                // ── Step 2: Select a block reference ────────────────────
                var peo = new PromptEntityOptions("\n  Select a block reference in model space: ");
                peo.SetRejectMessage("\n  Not a block reference. Try again.");
                peo.AddAllowedClass(typeof(BlockReference), true);
                var per = ed.GetEntity(peo);
                if (per.Status != PromptStatus.OK) return;

                string blockName;
                int totalCount = 0;

                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    var br = tx.GetObject(per.ObjectId, OpenMode.ForRead) as BlockReference;
                    if (br == null) { ed.WriteMessage("\n  Invalid selection.\n"); return; }

                    // Get the block definition name (handle dynamic blocks)
                    var btr = tx.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                    if (btr == null) { ed.WriteMessage("\n  Invalid block.\n"); return; }
                    blockName = btr.Name;

                    // If dynamic block, get the real name
                    if (br.IsDynamicBlock)
                    {
                        var dynBtr = tx.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead)
                            as BlockTableRecord;
                        if (dynBtr != null) blockName = dynBtr.Name;
                    }

                    // Count all references of this block in model space
                    var ms = tx.GetObject(
                        ((BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead))[BlockTableRecord.ModelSpace],
                        OpenMode.ForRead) as BlockTableRecord;

                    foreach (ObjectId entId in ms!)
                    {
                        try
                        {
                            var ent = tx.GetObject(entId, OpenMode.ForRead) as BlockReference;
                            if (ent == null) continue;

                            string entBlockName;
                            if (ent.IsDynamicBlock)
                            {
                                var dynBtr = tx.GetObject(ent.DynamicBlockTableRecord, OpenMode.ForRead)
                                    as BlockTableRecord;
                                entBlockName = dynBtr?.Name ?? "";
                            }
                            else
                            {
                                var entBtr = tx.GetObject(ent.BlockTableRecord, OpenMode.ForRead)
                                    as BlockTableRecord;
                                entBlockName = entBtr?.Name ?? "";
                            }

                            if (string.Equals(entBlockName, blockName, StringComparison.OrdinalIgnoreCase))
                                totalCount++;
                        }
                        catch { }
                    }

                    tx.Commit();
                }

                ed.WriteMessage($"\n  Block: \"{blockName}\"  —  Count: {totalCount}\n");

                // ── Step 3: Confirm ─────────────────────────────────────
                var pko = new PromptKeywordOptions(
                    $"\n  Add elevations from {totalCount} \"{blockName}\" block(s) to surface \"{surfaceName}\"? [Yes/No]",
                    "Yes No")
                {
                    AllowNone = false
                };
                var pkr = ed.GetKeywords(pko);
                if (pkr.Status != PromptStatus.OK || pkr.StringResult != "Yes")
                {
                    ed.WriteMessage("\n  Command cancelled.\n");
                    return;
                }

                // ── Step 4: Collect ELEV2 attributes + positions ────────
                var pointsToAdd = new List<(Point3d Position, double Elevation, string Info)>();
                int skipped = 0;

                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    var ms = tx.GetObject(
                        ((BlockTable)tx.GetObject(db.BlockTableId, OpenMode.ForRead))[BlockTableRecord.ModelSpace],
                        OpenMode.ForRead) as BlockTableRecord;

                    foreach (ObjectId entId in ms!)
                    {
                        try
                        {
                            var br = tx.GetObject(entId, OpenMode.ForRead) as BlockReference;
                            if (br == null) continue;

                            string entBlockName;
                            if (br.IsDynamicBlock)
                            {
                                var dynBtr = tx.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead)
                                    as BlockTableRecord;
                                entBlockName = dynBtr?.Name ?? "";
                            }
                            else
                            {
                                var btr = tx.GetObject(br.BlockTableRecord, OpenMode.ForRead)
                                    as BlockTableRecord;
                                entBlockName = btr?.Name ?? "";
                            }

                            if (!string.Equals(entBlockName, blockName, StringComparison.OrdinalIgnoreCase))
                                continue;

                            // Search for ELEV2 attribute
                            double elev = double.NaN;
                            foreach (ObjectId attId in br.AttributeCollection)
                            {
                                var att = tx.GetObject(attId, OpenMode.ForRead) as AttributeReference;
                                if (att != null &&
                                    string.Equals(att.Tag, "ELEV2", StringComparison.OrdinalIgnoreCase))
                                {
                                    if (double.TryParse(att.TextString, out double parsed))
                                        elev = parsed;
                                    break;
                                }
                            }

                            if (double.IsNaN(elev))
                            {
                                skipped++;
                                continue;
                            }

                            // Use block's insertion point as coordinates
                            var pos = br.Position;
                            pointsToAdd.Add((
                                new Point3d(pos.X, pos.Y, elev),
                                elev,
                                $"({pos.X:F2}, {pos.Y:F2}) elev={elev:F3}'"
                            ));
                        }
                        catch { skipped++; }
                    }

                    tx.Commit();
                }

                if (pointsToAdd.Count == 0)
                {
                    ed.WriteMessage($"\n  No blocks with valid ELEV2 attribute found. Skipped: {skipped}\n");
                    return;
                }

                ed.WriteMessage($"\n  Found {pointsToAdd.Count} block(s) with ELEV2 attribute.");
                if (skipped > 0)
                    ed.WriteMessage($" ({skipped} skipped — no ELEV2 or invalid value)");
                ed.WriteMessage("\n");

                // ── Step 5: Add elevation points to TIN surface ─────────
                int added = 0;
                int failed = 0;

                using (Transaction tx = db.TransactionManager.StartTransaction())
                {
                    var tinSurf = tx.GetObject(surfaceId, OpenMode.ForWrite) as CivilDB.TinSurface;
                    if (tinSurf == null)
                    {
                        ed.WriteMessage("\n  Could not open surface for writing.\n");
                        tx.Abort();
                        return;
                    }

                    foreach (var pt in pointsToAdd)
                    {
                        try
                        {
                            var pts = new Point3dCollection { pt.Position };
                            tinSurf.AddVertices(pts);
                            added++;
                        }
                        catch (System.Exception ex)
                        {
                            failed++;
                            ed.WriteMessage($"\n    Failed: {pt.Info} — {ex.Message}");
                        }
                    }

                    tx.Commit();
                }

                // ── Report ──────────────────────────────────────────────
                ed.WriteMessage($"\n\n  ═══ BLOCK TO SURFACE COMPLETE ═══");
                ed.WriteMessage($"\n  Surface : {surfaceName}");
                ed.WriteMessage($"\n  Block   : {blockName}");
                ed.WriteMessage($"\n  Added   : {added} elevation point(s)");
                if (failed > 0)
                    ed.WriteMessage($"\n  Failed  : {failed} (outside surface boundary?)");
                ed.WriteMessage("\n");
            }
            catch (System.Exception ex)
            {
                AcadApp.DocumentManager.MdiActiveDocument?.Editor
                    .WriteMessage($"\n[BLOCKTOSURFACE ERROR] {ex.Message}\n");
            }
        }
    }
}
