using System;
using System.IO;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AdvancedLandDevTools.Engine
{
    /// <summary>
    /// Manages licensing via the Autodesk App Store Entitlement API
    /// with a local 14-day free trial fallback.
    ///
    /// Flow:
    ///   1. Check Autodesk entitlement API (online purchase verification)
    ///   2. If entitled → full access, cache result for 3-day offline grace
    ///   3. If not entitled → check local trial (14 days from first use)
    ///   4. If trial expired → locked, show purchase link
    /// </summary>
    public static class LicenseManager
    {
        // ── Replace with your real Autodesk App Store App ID after registration ──
        // You'll get this ID when you submit to apps.autodesk.com
        public const string APP_ID = "REPLACE_WITH_YOUR_APP_ID";

        private const string ENTITLEMENT_URL =
            "https://apps.autodesk.com/webservices/checkentitlement";

        private const int TRIAL_DAYS = 14;
        private const int OFFLINE_GRACE_DAYS = 3;

        // XOR key for simple obfuscation of the trial file
        private static readonly byte[] _obfKey =
            Encoding.UTF8.GetBytes("ALDT2026_LicKey_JFC");

        private static LicenseState _cachedState = LicenseState.Unknown;
        private static DateTime _lastCheck = DateTime.MinValue;

        // Re-check every 4 hours (don't spam the API)
        private static readonly TimeSpan _recheckInterval = TimeSpan.FromHours(4);

        // ─────────────────────────────────────────────────────────────────
        //  Public API
        // ─────────────────────────────────────────────────────────────────

        public enum LicenseState
        {
            Unknown,
            Licensed,    // Paid via App Store
            Trial,       // Within 14-day trial
            Expired      // Trial over, not purchased
        }

        /// <summary>
        /// Call at the top of every command. Returns true if the command
        /// is allowed to run (Licensed or Trial). Shows a message and
        /// returns false if expired.
        /// </summary>
        public static bool EnsureLicensed()
        {
            var state = GetState();

            switch (state)
            {
                case LicenseState.Licensed:
                    return true;

                case LicenseState.Trial:
                    int daysLeft = GetTrialDaysLeft();
                    var doc = AcadApp.DocumentManager.MdiActiveDocument;
                    doc?.Editor.WriteMessage(
                        $"\n[ALDT] Trial mode — {daysLeft} day{(daysLeft == 1 ? "" : "s")} remaining. " +
                        "Purchase at apps.autodesk.com to unlock permanently.\n");
                    return true;

                case LicenseState.Expired:
                default:
                    var doc2 = AcadApp.DocumentManager.MdiActiveDocument;
                    doc2?.Editor.WriteMessage(
                        "\n╔══════════════════════════════════════════════════════════╗" +
                        "\n║  ADVANCED LAND DEVELOPMENT TOOLS — Trial Expired        ║" +
                        "\n╠══════════════════════════════════════════════════════════╣" +
                        "\n║  Your 14-day free trial has ended.                      ║" +
                        "\n║  Purchase from the Autodesk App Store to continue:      ║" +
                        "\n║  https://apps.autodesk.com                              ║" +
                        "\n╚══════════════════════════════════════════════════════════╝\n");
                    return false;
            }
        }

        /// <summary>
        /// Returns the current license state. Caches result to avoid
        /// repeated network calls.
        /// </summary>
        public static LicenseState GetState()
        {
            if (_cachedState != LicenseState.Unknown &&
                DateTime.UtcNow - _lastCheck < _recheckInterval)
            {
                return _cachedState;
            }

            // Step 1: Try Autodesk entitlement check
            bool? entitled = CheckEntitlementOnline();

            if (entitled == true)
            {
                _cachedState = LicenseState.Licensed;
                _lastCheck = DateTime.UtcNow;
                SaveOfflineGrace();
                return _cachedState;
            }

            // Step 2: If offline/failed, check offline grace for paid users
            if (entitled == null && IsWithinOfflineGrace())
            {
                _cachedState = LicenseState.Licensed;
                _lastCheck = DateTime.UtcNow;
                return _cachedState;
            }

            // Step 3: Check trial
            if (IsTrialValid())
            {
                _cachedState = LicenseState.Trial;
                _lastCheck = DateTime.UtcNow;
                return _cachedState;
            }

            _cachedState = LicenseState.Expired;
            _lastCheck = DateTime.UtcNow;
            return _cachedState;
        }

        /// <summary>Returns the startup status line for the banner.</summary>
        public static string GetStatusText()
        {
            var state = GetState();
            return state switch
            {
                LicenseState.Licensed => "Licensed",
                LicenseState.Trial   => $"Trial — {GetTrialDaysLeft()} days left",
                _                    => "Trial Expired"
            };
        }

        // ─────────────────────────────────────────────────────────────────
        //  Autodesk Entitlement API
        // ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Calls the Autodesk App Store entitlement endpoint.
        /// Returns true (entitled), false (not entitled), or null (network error).
        /// </summary>
        private static bool? CheckEntitlementOnline()
        {
            try
            {
                // Get the logged-in Autodesk user ID
                string userId = GetAutodeskUserId();
                if (string.IsNullOrEmpty(userId))
                    return null; // Not logged in — can't check

                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(8);

                string url = $"{ENTITLEMENT_URL}?userid={Uri.EscapeDataString(userId)}" +
                             $"&appid={Uri.EscapeDataString(APP_ID)}";

                var response = client.GetAsync(url).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                    return null; // Server error — treat as offline

                string json = response.Content.ReadAsStringAsync()
                                      .GetAwaiter().GetResult();
                var obj = JObject.Parse(json);

                bool isValid = obj.Value<bool>("IsValid");
                return isValid;
            }
            catch
            {
                // Network failure — treat as offline
                return null;
            }
        }

        private static string GetAutodeskUserId()
        {
            try
            {
                object val = AcadApp.GetSystemVariable("ONLINEUSERID");
                return val?.ToString() ?? "";
            }
            catch
            {
                return "";
            }
        }

        // ─────────────────────────────────────────────────────────────────
        //  Local Trial Management
        // ─────────────────────────────────────────────────────────────────

        private static string GetDataDir()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AdvancedLandDevTools");
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return dir;
        }

        private static string TrialFilePath => Path.Combine(GetDataDir(), "license.dat");
        private static string GraceFilePath => Path.Combine(GetDataDir(), "grace.dat");

        /// <summary>Checks if the trial is still within 14 days.</summary>
        private static bool IsTrialValid()
        {
            DateTime firstUse = GetOrCreateFirstUseDate();
            return (DateTime.UtcNow - firstUse).TotalDays <= TRIAL_DAYS;
        }

        /// <summary>Days remaining in the trial (minimum 0).</summary>
        private static int GetTrialDaysLeft()
        {
            DateTime firstUse = GetOrCreateFirstUseDate();
            double left = TRIAL_DAYS - (DateTime.UtcNow - firstUse).TotalDays;
            return Math.Max(0, (int)Math.Ceiling(left));
        }

        /// <summary>
        /// Reads the first-use date from the trial file, or creates one now.
        /// </summary>
        private static DateTime GetOrCreateFirstUseDate()
        {
            string path = TrialFilePath;

            if (File.Exists(path))
            {
                try
                {
                    byte[] encrypted = File.ReadAllBytes(path);
                    byte[] decrypted = XorBytes(encrypted, _obfKey);
                    string text = Encoding.UTF8.GetString(decrypted);
                    var obj = JObject.Parse(text);
                    return DateTime.Parse(obj.Value<string>("first_use")!,
                        null, System.Globalization.DateTimeStyles.RoundtripKind);
                }
                catch
                {
                    // Corrupted file — treat as new trial
                }
            }

            // First time — create trial file
            DateTime now = DateTime.UtcNow;
            SaveTrialFile(now);
            return now;
        }

        private static void SaveTrialFile(DateTime firstUse)
        {
            try
            {
                var obj = new JObject
                {
                    ["first_use"] = firstUse.ToString("O"),
                    ["product"]   = "AdvancedLandDevTools",
                    ["version"]   = "1.0.0"
                };
                byte[] plain = Encoding.UTF8.GetBytes(obj.ToString());
                byte[] encrypted = XorBytes(plain, _obfKey);
                File.WriteAllBytes(TrialFilePath, encrypted);
            }
            catch { /* Non-fatal */ }
        }

        // ─────────────────────────────────────────────────────────────────
        //  Offline Grace (for paid users when network is unavailable)
        // ─────────────────────────────────────────────────────────────────

        private static void SaveOfflineGrace()
        {
            try
            {
                var obj = new JObject
                {
                    ["last_verified"] = DateTime.UtcNow.ToString("O")
                };
                byte[] plain = Encoding.UTF8.GetBytes(obj.ToString());
                byte[] encrypted = XorBytes(plain, _obfKey);
                File.WriteAllBytes(GraceFilePath, encrypted);
            }
            catch { /* Non-fatal */ }
        }

        private static bool IsWithinOfflineGrace()
        {
            string path = GraceFilePath;
            if (!File.Exists(path)) return false;

            try
            {
                byte[] encrypted = File.ReadAllBytes(path);
                byte[] decrypted = XorBytes(encrypted, _obfKey);
                string text = Encoding.UTF8.GetString(decrypted);
                var obj = JObject.Parse(text);
                var lastVerified = DateTime.Parse(
                    obj.Value<string>("last_verified")!,
                    null, System.Globalization.DateTimeStyles.RoundtripKind);

                return (DateTime.UtcNow - lastVerified).TotalDays <= OFFLINE_GRACE_DAYS;
            }
            catch
            {
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────
        //  Obfuscation helper
        // ─────────────────────────────────────────────────────────────────

        private static byte[] XorBytes(byte[] data, byte[] key)
        {
            byte[] result = new byte[data.Length];
            for (int i = 0; i < data.Length; i++)
                result[i] = (byte)(data[i] ^ key[i % key.Length]);
            return result;
        }
    }
}
