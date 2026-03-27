using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CivilApp = Autodesk.Civil.ApplicationServices;
using CivilDB  = Autodesk.Civil.DatabaseServices;

[assembly: CommandClass(typeof(AdvancedLandDevTools.Commands.ElevSlopeCommand))]

namespace AdvancedLandDevTools.Commands
{
    public class ElevSlopeCommand
    {
        [CommandMethod("ELEVSLOPE")]
        public void Execute()
        {
            try
            {
                if (!Engine.LicenseManager.EnsureLicensed()) return;

                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                Editor   ed = doc.Editor;
                Database db = doc.Database;

                ed.WriteMessage("\n═══════════════════════════════════════════════════════\n");
                ed.WriteMessage("  ELEV SLOPE  –  Elevation Sloper (Surface Point)\n");
                ed.WriteMessage("═══════════════════════════════════════════════════════\n");

                // ── Step 1: List TIN surfaces and let user pick one ───
                var surfaces = new List<(ObjectId Id, string Name)>();

                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var civDoc = CivilApp.CivilDocument.GetCivilDocument(db);
                    foreach (ObjectId sid in civDoc.GetSurfaceIds())
                    {
                        var surf = tx.GetObject(sid, OpenMode.ForRead) as CivilDB.TinSurface;
                        if (surf != null)
                            surfaces.Add((sid, surf.Name));
                    }
                    tx.Commit();
                }

                if (surfaces.Count == 0)
                {
                    ed.WriteMessage("\n  No TIN surfaces found in drawing.\n");
                    return;
                }

                ObjectId surfaceId;
                string   surfaceName;

                if (surfaces.Count == 1)
                {
                    surfaceId   = surfaces[0].Id;
                    surfaceName = surfaces[0].Name;
                    ed.WriteMessage($"\n  Using surface: {surfaceName}\n");
                }
                else
                {
                    ed.WriteMessage("\n  Available TIN Surfaces:\n");
                    for (int i = 0; i < surfaces.Count; i++)
                        ed.WriteMessage($"    [{i + 1}]  {surfaces[i].Name}\n");

                    var intOpt = new PromptIntegerOptions(
                        $"\n  Select surface [1-{surfaces.Count}]")
                    {
                        LowerLimit   = 1,
                        UpperLimit   = surfaces.Count,
                        DefaultValue = 1
                    };
                    intOpt.UseDefaultValue = true;

                    var intRes = ed.GetInteger(intOpt);
                    if (intRes.Status != PromptStatus.OK) return;

                    int idx     = intRes.Value - 1;
                    surfaceId   = surfaces[idx].Id;
                    surfaceName = surfaces[idx].Name;
                }

                // ── Step 2: Pick start point on surface ───────────────
                ed.WriteMessage("\n  Pick a point on the surface:\n");

                var startPtRes = ed.GetPoint(new PromptPointOptions("\n  Select start point: "));
                if (startPtRes.Status != PromptStatus.OK) return;

                Point3d pickPt = startPtRes.Value;
                double  startElev;

                using (var tx = db.TransactionManager.StartTransaction())
                {
                    var tinSurf = tx.GetObject(surfaceId, OpenMode.ForRead) as CivilDB.TinSurface;
                    if (tinSurf == null) { ed.WriteMessage("\n  Surface error.\n"); tx.Abort(); return; }

                    try
                    {
                        startElev = tinSurf.FindElevationAtXY(pickPt.X, pickPt.Y);
                    }
                    catch
                    {
                        ed.WriteMessage("\n  Point is outside the surface boundary. Try again.\n");
                        tx.Abort();
                        return;
                    }
                    tx.Commit();
                }

                ed.WriteMessage($"\n  Start point: ({pickPt.X:F3}, {pickPt.Y:F3})");
                ed.WriteMessage($"\n  Surface elevation: {startElev:F3}\n");

                // ── Step 3: Slope + new point location ────────────────
                double slope = 2.0;

                var ptOpt = new PromptPointOptions(
                    $"\n  Pick new point location (slope={slope:F2}%) [Slope]: ");
                ptOpt.Keywords.Add("Slope");
                ptOpt.AppendKeywordsToMessage = true;
                ptOpt.UseBasePoint = true;
                ptOpt.BasePoint    = pickPt;

                while (true)
                {
                    var ptRes = ed.GetPoint(ptOpt);

                    if (ptRes.Status == PromptStatus.Keyword && ptRes.StringResult == "Slope")
                    {
                        var slopeOpt = new PromptDoubleOptions(
                            "\n  Enter slope in % (e.g. 2 for 2%, -1.5 for -1.5%): ")
                        {
                            DefaultValue  = slope,
                            AllowNegative = true,
                            AllowZero     = true
                        };
                        slopeOpt.UseDefaultValue = true;

                        var slopeRes = ed.GetDouble(slopeOpt);
                        if (slopeRes.Status == PromptStatus.OK)
                            slope = slopeRes.Value;

                        ed.WriteMessage($"\n  Slope set to {slope:F2}%\n");

                        // Update prompt text with new slope
                        ptOpt = new PromptPointOptions(
                            $"\n  Pick new point location (slope={slope:F2}%) [Slope]: ");
                        ptOpt.Keywords.Add("Slope");
                        ptOpt.AppendKeywordsToMessage = true;
                        ptOpt.UseBasePoint = true;
                        ptOpt.BasePoint    = pickPt;
                        continue;
                    }

                    if (ptRes.Status != PromptStatus.OK) return;

                    // ── Step 4: Calculate new elevation ───────────────
                    Point3d endPt = ptRes.Value;

                    double dx   = endPt.X - pickPt.X;
                    double dy   = endPt.Y - pickPt.Y;
                    double dist = Math.Sqrt(dx * dx + dy * dy);

                    double rise    = dist * (slope / 100.0);
                    double newElev = startElev + rise;

                    ed.WriteMessage($"\n  Horizontal distance: {dist:F3}");
                    ed.WriteMessage($"\n  Slope: {slope:F2}%  →  Rise: {rise:F3}");
                    ed.WriteMessage($"\n  New elevation: {newElev:F3}\n");

                    // ── Step 5: Add point to the TIN surface ──────────
                    using (var tx = db.TransactionManager.StartTransaction())
                    {
                        var tinSurf = tx.GetObject(surfaceId, OpenMode.ForWrite) as CivilDB.TinSurface;
                        if (tinSurf == null)
                        {
                            ed.WriteMessage("\n  Surface error.\n");
                            tx.Abort();
                            return;
                        }

                        var pts = new Point3dCollection
                        {
                            new Point3d(endPt.X, endPt.Y, newElev)
                        };
                        tinSurf.AddVertices(pts);

                        tx.Commit();
                    }

                    ed.WriteMessage($"\n  ✓  Surface point added at elevation {newElev:F3}\n");
                    ed.WriteMessage("═══════════════════════════════════════════════════════\n");
                    break;
                }
            }
            catch (System.Exception ex)
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage($"\n[ELEVSLOPE ERROR] {ex.Message}\n");
            }
        }
    }
}
