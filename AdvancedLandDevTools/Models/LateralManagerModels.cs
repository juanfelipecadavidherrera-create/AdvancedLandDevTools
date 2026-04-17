using System;
using System.Collections.Generic;

namespace AdvancedLandDevTools.Models
{
    public class LateralManagerProject
    {
        public string ProjectName { get; set; } = "";
        public List<LateralEntry> Laterals { get; set; } = new();
    }

    public class LateralEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Lateral";
        
        // Alignment info
        public string SourceAlignmentName { get; set; } = "";
        public string SourceAlignmentHandle { get; set; } = "";
        public double Station { get; set; } = 0;
        
        // Elevation info
        public double InvertElevation { get; set; } = 0;
        
        // Ellipse info
        public double CenterOffsetX { get; set; } = 0;
        public double CenterOffsetY { get; set; } = 0;
        public double MajorAxisX { get; set; } = 0;
        public double MajorAxisY { get; set; } = 0;
        public double RadiusRatio { get; set; } = 1.0;
        
        public string Layer { get; set; } = "0";
        public short ColorIndex { get; set; } = 256;
        
        // Original Drawing info
        public string SourceDwgName { get; set; } = "";
        
        // CAD entity handle for zooming back
        public string EllipseHandle { get; set; } = "";
    }
}
