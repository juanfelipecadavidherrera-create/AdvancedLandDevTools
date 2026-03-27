using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using AdvancedLandDevTools.Models;

namespace AdvancedLandDevTools.Engine
{
    /// <summary>
    /// Persists AreaManager projects as JSON files in %APPDATA%\AdvancedLandDevTools\AreaManager\.
    /// Each project is a separate .json file named by a sanitized project name.
    /// </summary>
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

        // ─────────────────────────────────────────────────────────────────────
        //  Save / Load a single project
        // ─────────────────────────────────────────────────────────────────────
        public static void Save(AreaManagerProject project)
        {
            Directory.CreateDirectory(StoreDir);
            project.ModifiedUtc = DateTime.UtcNow;
            string path = GetFilePath(project.ProjectName);
            string json = JsonConvert.SerializeObject(project, JsonSettings);
            File.WriteAllText(path, json);
        }

        public static AreaManagerProject? Load(string projectName)
        {
            string path = GetFilePath(projectName);
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<AreaManagerProject>(json, JsonSettings);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  List all saved project names
        // ─────────────────────────────────────────────────────────────────────
        public static List<string> ListProjects()
        {
            var names = new List<string>();
            if (!Directory.Exists(StoreDir)) return names;
            foreach (string file in Directory.GetFiles(StoreDir, "*.json"))
            {
                try
                {
                    string json = File.ReadAllText(file);
                    var proj = JsonConvert.DeserializeObject<AreaManagerProject>(json, JsonSettings);
                    if (proj != null)
                        names.Add(proj.ProjectName);
                }
                catch { /* skip corrupt files */ }
            }
            return names;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Delete a project file
        // ─────────────────────────────────────────────────────────────────────
        public static void Delete(string projectName)
        {
            string path = GetFilePath(projectName);
            if (File.Exists(path))
                File.Delete(path);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────────
        private static string GetFilePath(string projectName)
        {
            string safe = SanitizeFileName(projectName);
            return Path.Combine(StoreDir, safe + ".json");
        }

        private static string SanitizeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            string result = name;
            foreach (char c in invalid)
                result = result.Replace(c, '_');
            return result.Trim();
        }
    }
}
