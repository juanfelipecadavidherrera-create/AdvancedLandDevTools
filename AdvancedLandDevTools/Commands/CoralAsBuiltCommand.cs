// Advanced Land Development Tools
// Copyright © Juan Felipe Cadavid — All Rights Reserved
// Unauthorized copying or redistribution is prohibited.

using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AdvancedLandDevTools.Engine;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AdvancedLandDevTools.Commands.CoralAsBuiltCommand))]

namespace AdvancedLandDevTools.Commands
{
    /// <summary>
    /// CORALASBUILT — Queries the Coral Gables Sewer GIS within 1000 ft of a
    /// picked point and draws gravity mains, force mains, manholes, and laterals
    /// directly into model space.
    /// </summary>
    public class CoralAsBuiltCommand
    {
        [CommandMethod("CORALASBUILT", CommandFlags.Modal)]
        public void CoralAsBuilt()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;

            try
            {
                ed.WriteMessage("\n");
                ed.WriteMessage("═══════════════════════════════════════════════════════════\n");
                ed.WriteMessage("  Advanced Land Development Tools  |  Coral As-Builts      \n");
                ed.WriteMessage("═══════════════════════════════════════════════════════════\n");

                // Prompt for center point
                var ppo = new PromptPointOptions("\n  Pick center point for as-built query: ")
                {
                    AllowNone = false
                };
                PromptPointResult ppr = ed.GetPoint(ppo);
                if (ppr.Status != PromptStatus.OK)
                {
                    ed.WriteMessage("\n  Command cancelled.\n");
                    return;
                }

                Point3d center = ppr.Value;

                // Optionally prompt for radius (default 1000 ft)
                var pro = new PromptDistanceOptions("\n  Search radius [ft] <1000>: ")
                {
                    AllowNone        = true,
                    AllowZero        = false,
                    AllowNegative    = false,
                    DefaultValue     = 1000.0,
                    UseDefaultValue  = true
                };
                PromptDoubleResult pdr = ed.GetDistance(pro);
                double radius = (pdr.Status == PromptStatus.OK) ? pdr.Value : 1000.0;

                // Run engine
                CoralAsBuiltSummary result = CoralAsBuiltEngine.FetchAndDraw(doc, center, radius);

                if (!result.Success)
                {
                    ed.WriteMessage($"\n  ** Query failed: {result.ErrorMessage}\n");
                    return;
                }

                ed.WriteMessage("\n");
                ed.WriteMessage("\n  ╔══════════════════════════════════════════════════╗");
                ed.WriteMessage("\n  ║      CORAL GABLES SEWER AS-BUILT RESULTS         ║");
                ed.WriteMessage("\n  ╠══════════════════════════════════════════════════╣");
                ed.WriteMessage($"\n  ║  Gravity Mains:   {result.GravityCount,-30} ║");
                ed.WriteMessage($"\n  ║  Force Mains:     {result.ForceCount,-30} ║");
                ed.WriteMessage($"\n  ║  Manholes:        {result.ManholeCount,-30} ║");
                ed.WriteMessage($"\n  ║  Laterals:        {result.LateralCount,-30} ║");
                ed.WriteMessage("\n  ╠══════════════════════════════════════════════════╣");
                ed.WriteMessage("\n  ║  Layers:  ALDT-CG-GRAVITY-MAIN  (cyan)           ║");
                ed.WriteMessage("\n  ║           ALDT-CG-FORCE-MAIN    (red)            ║");
                ed.WriteMessage("\n  ║           ALDT-CG-MANHOLE       (yellow)         ║");
                ed.WriteMessage("\n  ║           ALDT-CG-LATERAL       (magenta)        ║");
                ed.WriteMessage("\n  ╚══════════════════════════════════════════════════╝");
                ed.WriteMessage("\n═══════════════════════════════════════════════════════════\n");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[ALDT ERROR] CORALASBUILT: {ex.Message}\n");
            }
        }
    }
}
