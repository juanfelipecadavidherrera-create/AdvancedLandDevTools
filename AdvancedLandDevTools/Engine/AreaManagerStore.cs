using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using AdvancedLandDevTools.Models;

namespace AdvancedLandDevTools.Engine
{
    public static class AreaManagerStore
    {
        private static readonly string StoreDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AdvancedLandDevTools", "AreaManager");

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };

        public static void Save(AreaManagerProject project)
        {
            Directory.CreateDirectory(StoreDir);
            project.ModifiedUtc = DateTime.UtcNow;
            string path = GetFilePath(project.ProjectName);
            File.WriteAllText(path, JsonConvert.SerializeObject(project, JsonSettings));
        }

        public static AreaManagerProject? Load(string projectName)
        {
            string path = GetFilePath(projectName);
            if (!File.Exists(path)) return null;
            var proj = JsonConvert.DeserializeObject<AreaManagerProject>(
                File.ReadAllText(path), JsonSettings);
            if (proj == null) return null;

            // Normalise fields that may be absent in JSON written by older versions
            proj.SubProjects ??= new List<AreaSubProject>();
            foreach (var sp in proj.SubProjects)
            {
                sp.Categories ??= new List<string>();
                sp.Areas      ??= new List<AreaEntry>();
                if (string.IsNullOrEmpty(sp.Id)) sp.Id = Guid.NewGuid().ToString("N");
            }
            return proj;
        }

        public static List<string> ListProjects()
        {
            var names = new List<string>();
            if (!Directory.Exists(StoreDir)) return names;
            foreach (string file in Directory.GetFiles(StoreDir, "*.json"))
            {
                try
                {
                    var proj = JsonConvert.DeserializeObject<AreaManagerProject>(
                        File.ReadAllText(file), JsonSettings);
                    if (proj != null) names.Add(proj.ProjectName);
                }
                catch { }
            }
            return names;
        }

        public static void Delete(string projectName)
        {
            string path = GetFilePath(projectName);
            if (File.Exists(path)) File.Delete(path);
        }

        private static string GetFilePath(string projectName) =>
            Path.Combine(StoreDir, SanitizeFileName(projectName) + ".json");

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name.Trim();
        }
    }
}
