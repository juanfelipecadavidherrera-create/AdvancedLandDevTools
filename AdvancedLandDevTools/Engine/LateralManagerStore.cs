using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using AdvancedLandDevTools.Models;

namespace AdvancedLandDevTools.Engine
{
    public static class LateralManagerStore
    {
        private static readonly string StoreDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AdvancedLandDevTools", "LateralManager");

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };

        public static void Save(LateralManagerProject project)
        {
            Directory.CreateDirectory(StoreDir);
            string path = GetFilePath(project.ProjectName);
            string json = JsonConvert.SerializeObject(project, JsonSettings);
            File.WriteAllText(path, json);
        }

        public static LateralManagerProject? Load(string projectName)
        {
            string path = GetFilePath(projectName);
            if (!File.Exists(path)) return null;
            string json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<LateralManagerProject>(json, JsonSettings);
        }

        public static List<string> ListProjects()
        {
            var names = new List<string>();
            if (!Directory.Exists(StoreDir)) return names;
            foreach (string file in Directory.GetFiles(StoreDir, "*.json"))
            {
                try
                {
                    string json = File.ReadAllText(file);
                    var proj = JsonConvert.DeserializeObject<LateralManagerProject>(json, JsonSettings);
                    if (proj != null && !string.IsNullOrEmpty(proj.ProjectName))
                        names.Add(proj.ProjectName);
                }
                catch { /* skip corrupt */ }
            }
            return names;
        }

        public static void Delete(string projectName)
        {
            string path = GetFilePath(projectName);
            if (File.Exists(path))
                File.Delete(path);
        }

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
