using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
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
    public class TextToSurfaceCommand
    {
        [CommandMethod("TEXTTOSURFACE", CommandFlags.Modal)]
        public void Execute()
        {
            try
            {
                if (!Engine.LicenseManager.EnsureLicensed()) return;

                Document doc = AcadApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                Editor ed = doc.Editor;
                Database db = doc.Database;

                ed.WriteMessage("\n");
                ed.WriteMessage("========================================================\n");
                ed.WriteMessage("  Advanced Land Development Tools  |  Text to Surface   \n");
                ed.WriteMessage("========================================================\n");

                // ── Step 1: Select TIN Surface ──────────────────────────
                ObjectId surfaceId = ObjectId.Null;
                string surfaceName = "";

                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var civDoc = CivilApp.CivilDocument.GetCivilDocument(db);
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

                    surfaceId = surfaces[pir.Value - 1].Id;
                    surfaceName = surfaces[pir.Value - 1].Name;
                    ed.WriteMessage($"\n  Surface selected: {surfaceName}\n");
                }

                // ── Step 2: Select MTexts and/or MLeaders ───────────────
                ed.WriteMessage("\n  Select MTexts and/or MLeaders containing elevations:\n");

                var filter = new SelectionFilter(new[]
                {
                    new TypedValue(-4, "<OR"),
                    new TypedValue(0, "MTEXT"),
                    new TypedValue(0, "MULTILEADER"),
                    new TypedValue(-4, "OR>"),
                });

                var psr = ed.GetSelection(filter);
                if (psr.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\n  No selection — cancelled.\n");
                    return;
                }

                // ── Step 3: Parse text and collect points ───────────────
                var pointsToAdd = new List<(Point3d Position, double Elevation, string Info)>();
                int skipped = 0;

                using (var tx = db.TransactionManager.StartTransaction())
                {
                    foreach (SelectedObject so in psr.Value)
                    {
                        try
                        {
                            var entity = tx.GetObject(so.ObjectId, OpenMode.ForRead) as Entity;
                            if (entity == null) { skipped++; continue; }

                            string rawText;
                            Point3d insertPt;

                            if (entity is MText mtext)
                            {
                                rawText  = mtext.Contents;
                                insertPt = mtext.Location;
                            }
                            else if (entity is MLeader ml)
                            {
                                // Text from the leader's MText content
                                var mlMText = ml.MText;
                                if (mlMText == null) { skipped++; continue; }
                                rawText = mlMText.Contents;

                                // Arrow tip = first point of the first leader line
                                Point3d? arrowTip = GetMLeaderArrowTip(ml);
                                if (arrowTip == null) { skipped++; continue; }
                                insertPt = arrowTip.Value;
                            }
                            else
                            {
                                skipped++;
                                continue;
                            }

                            if (!TryParseElevation(rawText, out double elev))
                            {
                                skipped++;
                                continue;
                            }

                            pointsToAdd.Add((
                                new Point3d(insertPt.X, insertPt.Y, elev),
                                elev,
                                $"({insertPt.X:F2}, {insertPt.Y:F2}) elev={elev:F3}'"
                            ));
                        }
                        catch { skipped++; }
                    }

                    tx.Commit();
                }

                if (pointsToAdd.Count == 0)
                {
                    ed.WriteMessage($"\n  No entities with valid elevation numbers found.");
                    if (skipped > 0)
                        ed.WriteMessage($" ({skipped} skipped)");
                    ed.WriteMessage("\n");
                    return;
                }

                ed.WriteMessage($"\n  Found {pointsToAdd.Count} elevation(s) ready to add.");
                if (skipped > 0)
                    ed.WriteMessage($" ({skipped} skipped — no number found)");
                ed.WriteMessage("\n");

                // ── Step 4: Confirm ─────────────────────────────────────
                var pko = new PromptKeywordOptions(
                    $"\n  Add {pointsToAdd.Count} elevation point(s) to surface \"{surfaceName}\"? [Yes/No]",
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

                // ── Step 5: Add to TIN surface ──────────────────────────
                int added = 0;
                int failed = 0;

                using (var tx = db.TransactionManager.StartTransaction())
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
                            tinSurf.AddVertices(new Point3dCollection { pt.Position });
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
                ed.WriteMessage($"\n\n  ═══ TEXT TO SURFACE COMPLETE ═══");
                ed.WriteMessage($"\n  Surface : {surfaceName}");
                ed.WriteMessage($"\n  Added   : {added} elevation point(s)");
                if (failed > 0)
                    ed.WriteMessage($"\n  Failed  : {failed} (outside surface boundary?)");
                ed.WriteMessage("\n");
            }
            catch (System.Exception ex)
            {
                AcadApp.DocumentManager.MdiActiveDocument?.Editor
                    .WriteMessage($"\n[TEXTTOSURFACE ERROR] {ex.Message}\n");
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Returns the arrowhead tip of the first leader line.
        /// This is where the MLeader is "pointing at".
        /// Uses GetFirstVertex(lineIndex) — the arrowhead end of each leader line.
        /// GetLeaderLineIndexes returns ArrayList; LeaderCount is the root count.
        /// </summary>
        private static Point3d? GetMLeaderArrowTip(MLeader ml)
        {
            try
            {
                for (int li = 0; li < ml.LeaderCount; li++)
                {
                    var lineIdxs = ml.GetLeaderLineIndexes(li);
                    if (lineIdxs == null || lineIdxs.Count == 0) continue;
                    int lineIdx = (int)lineIdxs[0];
                    return ml.GetFirstVertex(lineIdx);
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Strips MTEXT inline formatting codes and extracts the first number.
        /// Handles: "7.62'", "7.62", "EL=7.62'", "\fArial;7.62'", etc.
        /// If the number is followed by a foot marker ('), the marker is ignored
        /// and the numeric value is used directly as the elevation.
        /// Returns false if no number is found.
        /// </summary>
        private static bool TryParseElevation(string raw, out double elev)
        {
            elev = double.NaN;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            // Strip MTEXT formatting codes: \code; or \code<space> patterns, plus braces
            string s = Regex.Replace(raw, @"\\[A-Za-z0-9\|\.\-,;:]+;?", " ");
            s = Regex.Replace(s, @"[{}\\]", " ");

            // First: look for number followed by ' (foot marker attached or with a space)
            var m = Regex.Match(s, @"(-?\d+(?:\.\d+)?)\s*'");
            if (m.Success && double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out elev))
                return true;

            // Fallback: any standalone number
            m = Regex.Match(s, @"-?\d+(?:\.\d+)?");
            if (m.Success && double.TryParse(m.Value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out elev))
                return true;

            return false;
        }
    }
}
