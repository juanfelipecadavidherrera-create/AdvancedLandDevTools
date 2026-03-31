using System.Collections.Generic;
using System.ComponentModel;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AdvancedLandDevTools.Models
{
    // ─────────────────────────────────────────────────────────────────────────
    //  AlignmentItem  –  one row in the alignment checklist
    // ─────────────────────────────────────────────────────────────────────────
    public class AlignmentItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public ObjectId Id       { get; set; }
        public string   Name     { get; set; } = string.Empty;
        /// <summary>Display string: "Name  [Start – End]"</summary>
        public string   Display  { get; set; } = string.Empty;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  NamedItem  –  generic ObjectId + Name (surfaces, styles)
    // ─────────────────────────────────────────────────────────────────────────
    public class NamedItem
    {
        public ObjectId Id   { get; set; }
        public string   Name { get; set; } = string.Empty;
        public override string ToString() => Name;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  BulkProfileSettings  –  validated settings handed to the engine
    // ─────────────────────────────────────────────────────────────────────────
    public class BulkProfileSettings
    {
        // ── Selections ────────────────────────────────────────────────────────
        public List<AlignmentItem> SelectedAlignments { get; set; } = new();
        public ObjectId            SurfaceId          { get; set; }
        public ObjectId            ProfileStyleId     { get; set; }
        public ObjectId            ProfileViewStyleId { get; set; }
        public ObjectId            LabelSetStyleId    { get; set; }  // auto-resolved
        public ObjectId            BandSetStyleId     { get; set; }  // auto-resolved

        // ── Naming ────────────────────────────────────────────────────────────
        /// <summary>Appended to alignment name for the Profile, e.g. " - EG"</summary>
        public string ProfileNameSuffix { get; set; } = " - EG";
        /// <summary>Appended to alignment name for the Profile View, e.g. " - PV"</summary>
        public string ProfileViewNameSuffix { get; set; } = " - PV";

        // ── Station range (null = use full alignment extents) ─────────────────
        public double? StationStart { get; set; }
        public double? StationEnd   { get; set; }

        // ── Elevation range (null = Auto) ─────────────────────────────────────
        public double? ElevationMin { get; set; }
        public double? ElevationMax { get; set; }

        // ── Layout ────────────────────────────────────────────────────────────
        /// <summary>Model-space Y offset between successive profile views (default 250 ft)</summary>
        public double ViewSpacing { get; set; } = 250.0;

        /// <summary>Set by the command after the user picks the insertion point.</summary>
        public Point3d BaseInsertionPoint { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  FloodZoneResult  –  data returned from FEMA NFHL query
    // ─────────────────────────────────────────────────────────────────────────
    public class FloodZoneResult
    {
        public bool   Success             { get; set; }
        public string ErrorMessage        { get; set; } = string.Empty;
        public string FloodZone           { get; set; } = "N/A";   // AE, AH, VE, X, etc.
        public string ZoneSubtype         { get; set; } = "N/A";   // FLOODWAY, COASTAL A, etc.
        public string IsSFHA              { get; set; } = "N/A";   // Special Flood Hazard Area
        public string BaseFloodElevation  { get; set; } = "N/A";   // BFE in feet (NAVD88)
        public string VerticalDatum       { get; set; } = "NAVD88";
        public string Floodway            { get; set; } = "N/A";
        public string Depth               { get; set; } = "N/A";   // Flood depth in feet
        public string FirmPanel           { get; set; } = "N/A";   // FIRM panel reference
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  ContourLineData  –  one clipped contour segment in drawing coordinates
    // ─────────────────────────────────────────────────────────────────────────
    public class ContourLineData
    {
        public double         Elevation { get; set; }
        public List<Point2d>  Points    { get; set; } = new();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  FloodCriteriaResult  –  data returned from MDC Flood Criteria 2022
    // ─────────────────────────────────────────────────────────────────────────
    public class FloodCriteriaResult
    {
        public bool   Success      { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public string Elevation    { get; set; } = "N/A";   // Flood criteria elevation (ft NGVD)
        public string Distance     { get; set; } = "N/A";   // Distance to nearest contour line
        public List<ContourLineData> Contours { get; set; } = new();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  SectionLookupResult  –  Township/Range/Section from SFWMD PLSS
    // ─────────────────────────────────────────────────────────────────────────
    public class SectionLookupResult
    {
        public bool   Success      { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public string SSTTRR       { get; set; } = "N/A";
        public int    Section      { get; set; }
        public int    Township     { get; set; }
        public int    Range        { get; set; }
    }
}
