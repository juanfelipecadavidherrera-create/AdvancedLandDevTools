using System;

namespace AdvancedLandDevTools.Models
{
    public class PropertyAppraisalResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = "";
        
        public string Folio { get; set; } = "";
        public string OwnerName { get; set; } = "";
        public string SiteAddress { get; set; } = "";
        public string SiteCity { get; set; } = "";
        public string SiteZipCode { get; set; } = "";
    }
}
