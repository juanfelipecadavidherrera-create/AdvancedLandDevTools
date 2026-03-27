using System;
using System.Text.RegularExpressions;

namespace AdvancedLandDevTools.Helpers
{
    /// <summary>
    /// Converts Civil 3D station strings to raw double values (feet).
    /// Accepts:  "12+00"  →  1200.0
    ///           "12+00.50" → 1200.5
    ///           "1200"   →  1200.0
    ///           "1200.5" →  1200.5
    /// Also formats doubles back to station strings: 1200.0 → "12+00.00"
    /// </summary>
    public static class StationParser
    {
        // Matches "12+00" or "12+00.50" – captures hundreds (12) and remainder (00.50)
        private static readonly Regex _stationRegex =
            new Regex(@"^(\d+)\+(\d{2}(?:\.\d+)?)$", RegexOptions.Compiled);

        // ─────────────────────────────────────────────────────────────────────
        //  Parse  –  string → double
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Returns true and sets <paramref name="value"/> if the string is a
        /// valid station in either "12+00" or plain "1200" format.
        /// </summary>
        public static bool TryParse(string input, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(input)) return false;

            input = input.Trim();

            // Format A:  "12+00"  or  "12+00.50"
            var match = _stationRegex.Match(input);
            if (match.Success)
            {
                double hundreds  = double.Parse(match.Groups[1].Value);
                double remainder = double.Parse(match.Groups[2].Value);
                value = hundreds * 100.0 + remainder;
                return true;
            }

            // Format B:  plain decimal  "1200"  or  "1200.5"
            if (double.TryParse(input, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out double plain))
            {
                value = plain;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Parses without the bool; throws <see cref="FormatException"/> on failure.
        /// </summary>
        public static double Parse(string input)
        {
            if (TryParse(input, out double v)) return v;
            throw new FormatException(
                $"'{input}' is not a valid station. Use '12+00' or '1200'.");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Format  –  double → "12+00.00"
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Formats a raw station double as the Civil 3D "12+00.00" string.
        /// </summary>
        public static string Format(double station)
        {
            if (station < 0)
                return $"-{Format(Math.Abs(station))}";

            long hundreds  = (long)(station / 100.0);
            double remainder = station - hundreds * 100.0;
            return $"{hundreds}+{remainder:00.00}";
        }
    }
}
