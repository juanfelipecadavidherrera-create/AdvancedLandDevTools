using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using CivilDB = Autodesk.Civil.DatabaseServices;

namespace AdvancedLandDevTools.Engine
{
    public class GetParentResult
    {
        public bool     Success        { get; set; }
        public ObjectId AlignmentId    { get; set; }
        public string   AlignmentName  { get; set; } = "";
        public double   StartStation   { get; set; }
        public double   EndStation     { get; set; }
        public double   Length         { get; set; }
        public string   ErrorMessage   { get; set; } = "";
    }

    public static class GetParentEngine
    {
        // ─────────────────────────────────────────────────────────────────────
        //  Given a ProfileView ObjectId, find and return its parent alignment
        // ─────────────────────────────────────────────────────────────────────
        public static GetParentResult GetAlignmentFromProfileView(
            ObjectId    profileViewId,
            Transaction tx)
        {
            var r = new GetParentResult();
            try
            {
                var pv = tx.GetObject(profileViewId, OpenMode.ForRead)
                         as CivilDB.ProfileView;
                if (pv == null)
                {
                    r.ErrorMessage = "Selected object is not a profile view.";
                    return r;
                }

                ObjectId alignId = pv.AlignmentId;
                if (alignId.IsNull || !alignId.IsValid)
                {
                    r.ErrorMessage = "Profile view has no associated alignment.";
                    return r;
                }

                var al = tx.GetObject(alignId, OpenMode.ForRead)
                         as CivilDB.Alignment;
                if (al == null)
                {
                    r.ErrorMessage = "Could not open parent alignment.";
                    return r;
                }

                r.Success       = true;
                r.AlignmentId   = alignId;
                r.AlignmentName = al.Name;
                r.StartStation  = al.StartingStation;
                r.EndStation    = al.EndingStation;
                r.Length         = al.Length;
            }
            catch (System.Exception ex)
            {
                r.ErrorMessage = ex.Message;
            }
            return r;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Select the alignment in the drawing (gripped, as if user clicked it)
        // ─────────────────────────────────────────────────────────────────────
        public static bool SelectAlignment(Editor ed, ObjectId alignmentId)
        {
            try
            {
                ed.SetImpliedSelection(new[] { alignmentId });
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }
    }
}
