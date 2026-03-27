using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using CivilDB = Autodesk.Civil.DatabaseServices;
using AdvancedLandDevTools.Engine;

[assembly: CommandClass(typeof(AdvancedLandDevTools.Commands.AlignDeployCommand))]

namespace AdvancedLandDevTools.Commands
{
    public class AlignDeployCommand
    {
        [CommandMethod("ALIGNDEPLOY")]
        public void Execute()
        {
            try
            {
            if (!Engine.LicenseManager.EnsureLicensed()) return;
            Document doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            Editor    ed = doc.Editor;
            Database  db = doc.Database;

            ed.WriteMessage("\n═══════════════════════════════════════════════════════\n");
            ed.WriteMessage("  ALIGN DEPLOY  –  Cross Alignment Deployer\n");
            ed.WriteMessage("═══════════════════════════════════════════════════════\n");

            // ── 1. Select main (long) alignment ──────────────────────────────
            var peoMain = new PromptEntityOptions(
                "\nSelect the MAIN alignment (long, follows the road): ");
            peoMain.SetRejectMessage("\n  ✗  Not valid – must be a Civil 3D Alignment.");
            peoMain.AddAllowedClass(typeof(CivilDB.Alignment), true);

            PromptEntityResult perMain = ed.GetEntity(peoMain);
            if (perMain.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\n  Cancelled.\n");
                return;
            }

            // Validate it really is an Alignment
            ObjectId mainAlignId;
            using (Transaction tv = db.TransactionManager.StartTransaction())
            {
                var obj = tv.GetObject(perMain.ObjectId, OpenMode.ForRead);
                if (obj is not CivilDB.Alignment)
                {
                    ed.WriteMessage("\n  ✗  Selected object is not a valid Civil 3D Alignment.\n");
                    tv.Abort();
                    return;
                }
                mainAlignId = perMain.ObjectId;
                tv.Abort();
            }
            ed.WriteMessage("\n  ✓  Main alignment selected.\n");

            // ── 2. Select cross (short) alignment ────────────────────────────
            var peoCross = new PromptEntityOptions(
                "\nSelect the CROSS alignment (short, intersecting): ");
            peoCross.SetRejectMessage("\n  ✗  Not valid – must be a Civil 3D Alignment.");
            peoCross.AddAllowedClass(typeof(CivilDB.Alignment), true);

            PromptEntityResult perCross = ed.GetEntity(peoCross);
            if (perCross.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\n  Cancelled.\n");
                return;
            }

            ObjectId crossAlignId;
            using (Transaction tv = db.TransactionManager.StartTransaction())
            {
                var obj = tv.GetObject(perCross.ObjectId, OpenMode.ForRead);
                if (obj is not CivilDB.Alignment)
                {
                    ed.WriteMessage("\n  ✗  Selected object is not a valid Civil 3D Alignment.\n");
                    tv.Abort();
                    return;
                }
                if (perCross.ObjectId == mainAlignId)
                {
                    ed.WriteMessage("\n  ✗  Cross alignment must be different from the main alignment.\n");
                    tv.Abort();
                    return;
                }
                crossAlignId = perCross.ObjectId;
                tv.Abort();
            }
            ed.WriteMessage("\n  ✓  Cross alignment selected.\n");

            // ── 3. Get offset value ───────────────────────────────────────────
            var pdo = new PromptDistanceOptions(
                "\nEnter interval offset (must be positive, e.g. 50): ");
            pdo.AllowNegative  = false;
            pdo.AllowZero      = false;
            pdo.DefaultValue   = 50.0;
            pdo.UseDefaultValue = true;

            PromptDoubleResult pdr = ed.GetDistance(pdo);
            if (pdr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\n  Cancelled.\n");
                return;
            }

            double offset = pdr.Value;
            if (offset <= 0)
            {
                ed.WriteMessage("\n  ✗  Offset must be a positive value.\n");
                return;
            }

            ed.WriteMessage($"\n  ✓  Offset: {offset:F2} ft\n");
            ed.WriteMessage("\n  Creating cross alignments…\n");

            // ── 4. Run engine ─────────────────────────────────────────────────
            AlignDeployResult result = AlignDeployEngine.Run(
                mainAlignId, crossAlignId, offset);

            // ── 5. Write full log to command line ─────────────────────────────
            ed.WriteMessage("\n  ─── ALIGN DEPLOY  –  RESULTS ──────────────────────\n");
            foreach (string line in result.Log)
                ed.WriteMessage($"  {line}\n");
            ed.WriteMessage("  ────────────────────────────────────────────────────\n");
            ed.WriteMessage($"  Completed:  {result.CreatedCount} alignment(s) created.\n");
            ed.WriteMessage("═══════════════════════════════════════════════════════\n");

            // ── 6. Popup summary ──────────────────────────────────────────────
            string summary =
                $"{result.CreatedCount} cross alignment(s) created.\n\n" +
                $"Interval: {offset:F2} ft\n\n" +
                "── Log ──\n" +
                string.Join("\n", result.Log);

            MessageBox.Show(
                summary,
                result.CreatedCount > 0
                    ? "Align Deploy – Complete"
                    : "Align Deploy – No alignments created",
                MessageBoxButton.OK,
                result.CreatedCount > 0
                    ? MessageBoxImage.Information
                    : MessageBoxImage.Warning);
            }
            catch (System.Exception ex)
            {
                var d = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                d?.Editor.WriteMessage($"\n[ALDT ERROR] ALIGNDEPLOY: {ex.Message}\n");
            }
        }
    }
}
