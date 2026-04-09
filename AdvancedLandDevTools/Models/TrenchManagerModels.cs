using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace AdvancedLandDevTools.Models
{
    // ─────────────────────────────────────────────────────────────────────────
    //  TrenchEntry — one stored trench picked from a polyline
    // ─────────────────────────────────────────────────────────────────────────
    public class TrenchEntry : INotifyPropertyChanged
    {
        private string _name = "Unnamed Trench";

        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(nameof(Name)); } }
        }

        /// <summary>
        /// Length of the longest consecutive segment in the polyline (feet).
        /// For a 5×10 rectangle this stores 10.
        /// </summary>
        public double LongestSegmentFt { get; set; }

        /// <summary>All polyline vertices as [x, y] pairs — used for zoom-to.</summary>
        public List<double[]> Vertices { get; set; } = new();

        /// <summary>Layer the source polyline was on.</summary>
        public string Layer { get; set; } = "";

        public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  TrenchManagerProject — top-level project container
    // ─────────────────────────────────────────────────────────────────────────
    public class TrenchManagerProject
    {
        public string ProjectName { get; set; } = "Untitled Project";
        public List<TrenchEntry> Trenches { get; set; } = new();
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    }
}
