using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using AdvancedLandDevTools.Models;

namespace AdvancedLandDevTools.Engine
{
    /// <summary>
    /// Persists TrenchManager projects as JSON files in
    /// %APPDATA%\AdvancedLandDevTools\TrenchManager\.
    /// </summary>
    public static class TrenchManagerStore
    {
        private static readonly string StoreDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AdvancedLandDevTools", "TrenchManager");

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };

        public static void Save(TrenchManagerProject project)
        {
            Directory.CreateDirectory(StoreDir);
            project.ModifiedUtc = DateTime.UtcNow;
            File.WriteAllText(GetFilePath(project.ProjectName),
                JsonConvert.SerializeObject(project, JsonSettings));
        }

        public static TrenchManagerProject? Load(string projectName)
        {
            string path = GetFilePath(projectName);
            if (!File.Exists(path)) return null;
            return JsonConvert.DeserializeObject<TrenchManagerProject>(
                File.ReadAllText(path), JsonSettings);
        }

        public static List<string> ListProjects()
        {
            var names = new List<string>();
            if (!Directory.Exists(StoreDir)) return names;
            foreach (string file in Directory.GetFiles(StoreDir, "*.json"))
            {
                try
                {
                    var proj = JsonConvert.DeserializeObject<TrenchManagerProject>(
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

        private static string GetFilePath(string projectName)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            string safe = projectName;
            foreach (char c in invalid) safe = safe.Replace(c, '_');
            return Path.Combine(StoreDir, safe.Trim() + ".json");
        }
    }
}
