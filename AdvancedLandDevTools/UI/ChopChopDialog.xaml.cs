using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AdvancedLandDevTools.Engine;
using AdvancedLandDevTools.Helpers;

namespace AdvancedLandDevTools.UI
{
    // ─────────────────────────────────────────────────────────────────────────
    //  IntervalRow — one editable row in the Custom Intervals list
    // ─────────────────────────────────────────────────────────────────────────
    public class IntervalRow : INotifyPropertyChanged
    {
        private string _startText = "";
        private string _endText   = "";

        public string StartText
        {
            get => _startText;
            set { _startText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StartText))); }
        }

        public string EndText
        {
            get => _endText;
            set { _endText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EndText))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  ChopChopDialog
    // ─────────────────────────────────────────────────────────────────────────
    public partial class ChopChopDialog : Window
    {
        // ── Public result — read by command after ShowDialog ─────────────────
        public ChopChopSettings? Result { get; private set; }

        // ── Source PV info — set by command before showing ───────────────────
        public string SourcePvName   { get; set; } = "";
        public double SourceStaStart { get; set; }
        public double SourceStaEnd   { get; set; }

        private readonly ObservableCollection<IntervalRow> _intervals = new();

        // ─────────────────────────────────────────────────────────────────────
        public ChopChopDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Populate source PV info
            double totalLen = SourceStaEnd - SourceStaStart;
            TxtPvInfo.Text =
                $"Name:  {SourcePvName}\n" +
                $"Station Range:  {StationParser.Format(SourceStaStart)}  to  " +
                $"{StationParser.Format(SourceStaEnd)}\n" +
                $"Total Length:  {totalLen:F2} ft";

            // Seed the custom intervals list with one full-range row
            _intervals.Add(new IntervalRow
            {
                StartText = StationParser.Format(SourceStaStart),
                EndText   = StationParser.Format(SourceStaEnd)
            });
            IntervalItems.ItemsSource = _intervals;

            // Trigger initial preview
            UpdateEqualPreview();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Mode toggle
        // ─────────────────────────────────────────────────────────────────────
        private void RbMode_Checked(object sender, RoutedEventArgs e)
        {
            if (PnlEqual == null || PnlCustom == null) return;

            bool isEqual = RbEqual.IsChecked == true;
            PnlEqual.Visibility  = isEqual ? Visibility.Visible : Visibility.Collapsed;
            PnlCustom.Visibility = isEqual ? Visibility.Collapsed : Visibility.Visible;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Equal-mode preview
        // ─────────────────────────────────────────────────────────────────────
        private void TxtSegmentLength_TextChanged(object sender, TextChangedEventArgs e)
            => UpdateEqualPreview();

        private void UpdateEqualPreview()
        {
            if (TxtPreview == null) return;

            if (!double.TryParse(TxtSegmentLength.Text.Trim(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double segLen) || segLen <= 0)
            {
                TxtPreview.Text = "Enter a positive segment length.";
                return;
            }

            var intervals = ComputeEqualIntervals(segLen);
            var lines = new List<string> { $"Preview: {intervals.Count} view(s)" };
            for (int i = 0; i < intervals.Count; i++)
            {
                var (s, e2) = intervals[i];
                lines.Add($"  {i + 1}.  {StationParser.Format(s)}  →  {StationParser.Format(e2)}");
            }
            TxtPreview.Text = string.Join("\n", lines);
        }

        private List<(double Start, double End)> ComputeEqualIntervals(double segLen)
        {
            var result = new List<(double, double)>();
            double total = SourceStaEnd - SourceStaStart;
            int n = Math.Max(1, (int)(total / segLen));

            for (int i = 0; i < n; i++)
            {
                double s = SourceStaStart + i * segLen;
                double e = (i == n - 1) ? SourceStaEnd : SourceStaStart + (i + 1) * segLen;
                result.Add((s, e));
            }
            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Custom-mode interval management
        // ─────────────────────────────────────────────────────────────────────
        private void BtnAddInterval_Click(object sender, RoutedEventArgs e)
        {
            // Default new row: start from last row's end, end at PV end
            double newStart = SourceStaStart;
            if (_intervals.Count > 0)
            {
                var last = _intervals[^1];
                if (StationParser.TryParse(last.EndText, out double lastEnd))
                    newStart = lastEnd;
            }

            _intervals.Add(new IntervalRow
            {
                StartText = StationParser.Format(newStart),
                EndText   = StationParser.Format(SourceStaEnd)
            });
        }

        private void BtnRemoveInterval_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is IntervalRow row)
                _intervals.Remove(row);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Validation
        // ─────────────────────────────────────────────────────────────────────
        private void ShowValidationError(string msg)
        {
            TxtValidation.Text       = "⚠  " + msg;
            TxtValidation.Visibility = Visibility.Visible;
        }

        private void ClearValidationError()
        {
            TxtValidation.Text       = string.Empty;
            TxtValidation.Visibility = Visibility.Collapsed;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  OK
        // ─────────────────────────────────────────────────────────────────────
        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            ClearValidationError();

            // Parse layout values
            if (!double.TryParse(TxtVerticalOffset.Text.Trim(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double vertOffset) || vertOffset <= 0)
            {
                ShowValidationError("Vertical Offset must be a positive number.");
                return;
            }

            if (!double.TryParse(TxtHorizontalGap.Text.Trim(),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double hGap) || hGap < 0)
            {
                ShowValidationError("Horizontal Gap must be zero or a positive number.");
                return;
            }

            // Build intervals based on mode
            List<(double Start, double End)> intervals;

            if (RbEqual.IsChecked == true)
            {
                if (!double.TryParse(TxtSegmentLength.Text.Trim(),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double segLen) || segLen <= 0)
                {
                    ShowValidationError("Segment Length must be a positive number.");
                    return;
                }
                intervals = ComputeEqualIntervals(segLen);
            }
            else
            {
                // Parse custom intervals
                intervals = new List<(double, double)>();

                if (_intervals.Count == 0)
                {
                    ShowValidationError("Add at least one interval.");
                    return;
                }

                for (int i = 0; i < _intervals.Count; i++)
                {
                    var row = _intervals[i];
                    if (!StationParser.TryParse(row.StartText, out double s))
                    {
                        ShowValidationError($"Interval {i + 1}: invalid Start Station '{row.StartText}'.");
                        return;
                    }
                    if (!StationParser.TryParse(row.EndText, out double en))
                    {
                        ShowValidationError($"Interval {i + 1}: invalid End Station '{row.EndText}'.");
                        return;
                    }
                    if (s >= en)
                    {
                        ShowValidationError($"Interval {i + 1}: Start must be less than End.");
                        return;
                    }
                    intervals.Add((s, en));
                }

                // Sort by start station
                intervals = intervals.OrderBy(x => x.Start).ToList();

                // Check for overlaps
                for (int i = 1; i < intervals.Count; i++)
                {
                    if (intervals[i].Start < intervals[i - 1].End)
                    {
                        ShowValidationError(
                            $"Intervals {i} and {i + 1} overlap at " +
                            $"station {StationParser.Format(intervals[i].Start)}.");
                        return;
                    }
                }
            }

            if (intervals.Count == 0)
            {
                ShowValidationError("No intervals were generated.");
                return;
            }

            // Build result (AlignmentId, StyleId, etc. are set by the command)
            Result = new ChopChopSettings
            {
                Intervals      = intervals,
                VerticalOffset = vertOffset,
                HorizontalGap  = hGap
            };

            DialogResult = true;
            Close();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Cancel
        // ─────────────────────────────────────────────────────────────────────
        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Result       = null;
            DialogResult = false;
            Close();
        }
    }
}
