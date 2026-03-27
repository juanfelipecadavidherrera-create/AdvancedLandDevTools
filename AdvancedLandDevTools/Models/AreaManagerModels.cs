using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace AdvancedLandDevTools.Models
{
    // ─────────────────────────────────────────────────────────────────────────
    //  AreaEntry — one stored area (hatch or closed boundary)
    // ─────────────────────────────────────────────────────────────────────────
    public class AreaEntry : INotifyPropertyChanged
    {
        private string _name = "Unnamed Area";
        private string _category = "";

        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(nameof(Name)); } }
        }

        public string Category
        {
            get => _category;
            set { if (_category != value) { _category = value; OnPropertyChanged(nameof(Category)); } }
        }

        /// <summary>Area in square feet (always stored in sq ft).</summary>
        public double AreaSqFt { get; set; }

        /// <summary>Layer the original entity was on.</summary>
        public string Layer { get; set; } = "";

        /// <summary>Hatch pattern name (e.g. "SOLID", "ANSI31"). Empty for boundary-only.</summary>
        public string HatchPattern { get; set; } = "";

        /// <summary>Hatch pattern scale.</summary>
        public double HatchScale { get; set; } = 1.0;

        /// <summary>AutoCAD color index of the hatch.</summary>
        public int ColorIndex { get; set; } = 7;

        /// <summary>
        /// Boundary loop vertices for redraw. Each outer list = one loop.
        /// Each inner list = sequence of [x,y] coordinate pairs.
        /// </summary>
        public List<List<double[]>> BoundaryLoops { get; set; } = new();

        /// <summary>True if the original entity was a Hatch; false if a closed polyline/boundary.</summary>
        public bool IsHatch { get; set; }

        /// <summary>Timestamp when the area was added.</summary>
        public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  AreaManagerProject — top-level project container
    // ─────────────────────────────────────────────────────────────────────────
    public class AreaManagerProject
    {
        public string ProjectName { get; set; } = "Untitled Project";
        public List<string> Categories { get; set; } = new();
        public List<AreaEntry> Areas { get; set; } = new();
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    }
}
