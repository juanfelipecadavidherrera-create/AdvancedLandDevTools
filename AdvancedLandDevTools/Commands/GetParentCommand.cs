using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AdvancedLandDevTools.Engine;
using CivilDB = Autodesk.Civil.DatabaseServices;
using AcApp   = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AdvancedLandDevTools.Commands.GetParentCommand))]

namespace AdvancedLandDevTools.Commands
{
    public class GetParentCommand
    {
        [CommandMethod("GETPARENT")]
        public void GetParent()
        {
            try
            {
            if (!Engine.LicenseManager.EnsureLicensed()) return;
            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            Editor   ed = doc.Editor;
            Database db = doc.Database;

            ed.WriteMessage("\n=== GET PARENT ALIGNMENT ===");

            // ── Step 1: Select a profile view ──────────────────────────────
            var peo = new PromptEntityOptions(
                "\nSelect a profile view: ");
            peo.SetRejectMessage("\nMust be a Civil 3D Profile View.");
            peo.AddAllowedClass(typeof(CivilDB.ProfileView), true);

            PromptEntityResult per = ed.GetEntity(peo);
            if (per.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\n⚠ No profile view selected. Command cancelled.");
                return;
            }

            // ── Step 2: Get alignment from profile view ────────────────────
            GetParentResult result;
            using (Transaction tx = db.TransactionManager.StartTransaction())
            {
                result = GetParentEngine.GetAlignmentFromProfileView(
                    per.ObjectId, tx);
                tx.Commit();
            }

            if (!result.Success)
            {
                ed.WriteMessage($"\n❌ {result.ErrorMessage}");
                return;
            }

            // ── Step 3: Select the alignment in the drawing ────────────────
            bool selected = GetParentEngine.SelectAlignment(ed, result.AlignmentId);

            // ── Step 4: Report ─────────────────────────────────────────────
            ed.WriteMessage($"\n  Alignment : {result.AlignmentName}");
            ed.WriteMessage($"\n  Start STA : {result.StartStation:F2}");
            ed.WriteMessage($"\n  End STA   : {result.EndStation:F2}");
            ed.WriteMessage($"\n  Length    : {result.Length:F2}'");

            if (selected)
                ed.WriteMessage("\n  ✓ Alignment selected in drawing.");
            else
                ed.WriteMessage("\n  ⚠ Could not select alignment — locate it manually.");

            ed.WriteMessage("\n=== DONE ===\n");
            }
            catch (System.Exception ex)
            {
                var d = AcApp.DocumentManager.MdiActiveDocument;
                d?.Editor.WriteMessage($"\n[ALDT ERROR] GETPARENT: {ex.Message}\n");
            }
        }
    }
}
