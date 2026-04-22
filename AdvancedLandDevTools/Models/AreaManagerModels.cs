using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Newtonsoft.Json;

namespace AdvancedLandDevTools.Models
{
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

        public double AreaSqFt { get; set; }
        public string Layer { get; set; } = "";
        public string HatchPattern { get; set; } = "";
        public double HatchScale { get; set; } = 1.0;
        public int ColorIndex { get; set; } = 7;
        public List<List<double[]>> BoundaryLoops { get; set; } = new();
        public bool IsHatch { get; set; }
        public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>A named subproject inside a project — 1-level deep, same area schema.</summary>
    public class AreaSubProject
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Untitled SubProject";
        public List<string> Categories { get; set; } = new();
        public List<AreaEntry> Areas { get; set; } = new();
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;
    }

    public class AreaManagerProject
    {
        public string ProjectName { get; set; } = "Untitled Project";
        public List<string> Categories { get; set; } = new();
        public List<AreaEntry> Areas { get; set; } = new();
        public List<AreaSubProject> SubProjects { get; set; } = new();
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime ModifiedUtc { get; set; } = DateTime.UtcNow;

        [JsonIgnore]
        public IEnumerable<AreaEntry> AllAreas =>
            Areas.Concat(SubProjects.SelectMany(sp => sp.Areas));

        public AreaSubProject? FindSubProjectById(string id) =>
            SubProjects.FirstOrDefault(s => s.Id == id);

        public AreaSubProject? FindSubProjectByName(string name) =>
            SubProjects.FirstOrDefault(s =>
                string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
