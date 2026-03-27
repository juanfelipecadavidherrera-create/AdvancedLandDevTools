using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.AutoCAD.DatabaseServices;
using CivilApp = Autodesk.Civil.ApplicationServices;
using CivilDB  = Autodesk.Civil.DatabaseServices;
using AdvancedLandDevTools.Helpers;
using AdvancedLandDevTools.Models;

// Alias to resolve 'Application' clash between
//   Autodesk.AutoCAD.ApplicationServices.Application
//   System.Windows.Application
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AdvancedLandDevTools.UI
{
    public partial class BulkSurfaceProfileDialog : Window
    {
        // ── Public result – read by the command after ShowDialog() ────────────
        public BulkProfileSettings? Result { get; private set; }

        private List<NamedItem> _surfaceItems      = new();
        private List<NamedItem> _profileStyleItems = new();
        private List<NamedItem> _pvStyleItems      = new();
        private List<AlignmentItem> _allAlignmentItems = new(); // master list – never filtered

        // ─────────────────────────────────────────────────────────────────────
        public BulkSurfaceProfileDialog()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                LoadDrawingData();
                UpdateSelectionCount();
            }
            catch (System.Exception ex)
            {
                ShowValidationError($"Could not load drawing data: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Load Civil 3D data from the active drawing
        // ─────────────────────────────────────────────────────────────────────
        private void LoadDrawingData()
        {
            // Use the alias – no more ambiguity
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                ShowValidationError("No active document.");
                return;
            }

            var db = doc.Database;

            using (var trans = db.TransactionManager.StartTransaction())
            {
                CivilApp.CivilDocument civDoc =
                    CivilApp.CivilDocument.GetCivilDocument(db);

                // ── Alignments (all types) ───────────────────────────────────
                var alignmentItems = new List<AlignmentItem>();
                foreach (ObjectId id in civDoc.GetAlignmentIds())
                {
                    try
                    {
                        var al = trans.GetObject(id, OpenMode.ForRead)
                                 as CivilDB.Alignment;
                        if (al == null) continue;

                        string typeTag = al.AlignmentType.ToString();

                        alignmentItems.Add(new AlignmentItem
                        {
                            Id       = id,
                            Name     = al.Name,
                            Display  = $"{al.Name}  [{typeTag}]  " +
                                       $"Sta {StationParser.Format(al.StartingStation)} " +
                                       $"to {StationParser.Format(al.EndingStation)}",
                            IsSelected = false
                        });
                    }
                    catch { }
                }

                _allAlignmentItems = alignmentItems
                    .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                AlignmentListView.ItemsSource = _allAlignmentItems;

                // ── Surfaces ──────────────────────────────────────────────────
                _surfaceItems.Clear();
                foreach (ObjectId id in civDoc.GetSurfaceIds())
                {
                    try
                    {
                        var s = trans.GetObject(id, OpenMode.ForRead)
                                as CivilDB.Surface;
                        if (s != null)
                            _surfaceItems.Add(new NamedItem { Id = id, Name = s.Name });
                    }
                    catch { }
                }
                CmbSurface.ItemsSource        = _surfaceItems;
                CmbSurface.DisplayMemberPath  = "Name";
                if (_surfaceItems.Count > 0) CmbSurface.SelectedIndex = 0;

                // ── Profile Styles ────────────────────────────────────────────
                _profileStyleItems.Clear();
                foreach (ObjectId id in civDoc.Styles.ProfileStyles)
                {
                    try
                    {
                        var s = trans.GetObject(id, OpenMode.ForRead)
                                as CivilDB.Styles.ProfileStyle;
                        if (s != null)
                            _profileStyleItems.Add(new NamedItem { Id = id, Name = s.Name });
                    }
                    catch { }
                }
                CmbProfileStyle.ItemsSource       = _profileStyleItems;
                CmbProfileStyle.DisplayMemberPath = "Name";
                if (_profileStyleItems.Count > 0) CmbProfileStyle.SelectedIndex = 0;

                // ── Profile View Styles ───────────────────────────────────────
                _pvStyleItems.Clear();
                foreach (ObjectId id in civDoc.Styles.ProfileViewStyles)
                {
                    try
                    {
                        var s = trans.GetObject(id, OpenMode.ForRead)
                                as CivilDB.Styles.ProfileViewStyle;
                        if (s != null)
                            _pvStyleItems.Add(new NamedItem { Id = id, Name = s.Name });
                    }
                    catch { }
                }
                CmbProfileViewStyle.ItemsSource       = _pvStyleItems;
                CmbProfileViewStyle.DisplayMemberPath = "Name";
                if (_pvStyleItems.Count > 0) CmbProfileViewStyle.SelectedIndex = 0;

                trans.Commit();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Alignment checkbox events
        // ─────────────────────────────────────────────────────────────────────
        private void AlignmentCheckBox_Changed(object sender, RoutedEventArgs e)
            => UpdateSelectionCount();

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in GetAlignmentItems()) item.IsSelected = true;
            UpdateSelectionCount();
        }

        private void BtnClearAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in GetAlignmentItems()) item.IsSelected = false;
            UpdateSelectionCount();
        }

        private void UpdateSelectionCount()
        {
            int n = GetAlignmentItems().Count(a => a.IsSelected);
            TxtSelCount.Text = n == 0 ? "none selected"
                             : n == 1 ? "1 selected"
                             : $"{n} selected";
            TxtSelCount.Foreground = n == 0
                ? new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47))   // bright red on dark bg
                : new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));  // teal green on dark bg
        }

        private IEnumerable<AlignmentItem> GetAlignmentItems()
            => _allAlignmentItems; // always the full list – selections preserved when filtered

        // ─────────────────────────────────────────────────────────────────────
        //  Search / filter
        // ─────────────────────────────────────────────────────────────────────
        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Show/hide placeholder
            TxtSearchPlaceholder.Visibility =
                string.IsNullOrEmpty(TxtSearch.Text)
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;

            string filter = TxtSearch.Text.Trim();

            AlignmentListView.ItemsSource = string.IsNullOrEmpty(filter)
                ? _allAlignmentItems
                : _allAlignmentItems
                    .Where(a => a.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            UpdateSelectionCount();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Placeholder TextBox behaviour
        // ─────────────────────────────────────────────────────────────────────
        private void PlaceholderBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb &&
                tb.Text == (string)tb.Tag &&
                tb.Foreground is SolidColorBrush scb &&
                scb.Color != Color.FromRgb(0xE0, 0xE0, 0xE0))
            {
                tb.Text       = string.Empty;
                tb.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));  // light text on dark
            }
        }

        private void PlaceholderBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb && string.IsNullOrWhiteSpace(tb.Text))
            {
                tb.Text       = (string)tb.Tag;
                tb.Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A));  // placeholder gray
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Validation
        // ─────────────────────────────────────────────────────────────────────
        private void ShowValidationError(string msg)
        {
            TxtValidation.Text       = "⚠  " + msg;
            // Use type-qualified Visibility – fixes "instance reference" error
            TxtValidation.Visibility = System.Windows.Visibility.Visible;
        }

        private void ClearValidationError()
        {
            TxtValidation.Text       = string.Empty;
            TxtValidation.Visibility = System.Windows.Visibility.Collapsed;
        }

        private bool TryGetOptionalDouble(TextBox tb, string fieldName, out double? value)
        {
            value = null;
            string raw = tb.Text.Trim();
            if (string.IsNullOrWhiteSpace(raw) || raw == (string)tb.Tag) return true;

            if (double.TryParse(raw,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double d))
            {
                value = d;
                return true;
            }

            ShowValidationError($"{fieldName}: '{raw}' is not a valid number.");
            return false;
        }

        private bool TryGetOptionalStation(TextBox tb, string fieldName, out double? value)
        {
            value = null;
            string raw = tb.Text.Trim();
            if (string.IsNullOrWhiteSpace(raw) || raw == (string)tb.Tag) return true;

            if (StationParser.TryParse(raw, out double station))
            {
                value = station;
                return true;
            }

            ShowValidationError(
                $"{fieldName}: '{raw}' is not a valid station. Use '12+00' or '1200'.");
            return false;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  OK
        // ─────────────────────────────────────────────────────────────────────
        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            ClearValidationError();

            var selected = GetAlignmentItems().Where(a => a.IsSelected).ToList();
            if (selected.Count == 0)
            {
                ShowValidationError("Please select at least one alignment.");
                return;
            }

            if (CmbSurface.SelectedItem is not NamedItem surfItem)
            {
                ShowValidationError("Please select a surface.");
                return;
            }

            if (CmbProfileStyle.SelectedItem is not NamedItem profileStyleItem)
            {
                ShowValidationError("Please select a Profile Style.");
                return;
            }

            if (CmbProfileViewStyle.SelectedItem is not NamedItem pvStyleItem)
            {
                ShowValidationError("Please select a Profile View Style.");
                return;
            }

            if (!TryGetOptionalStation(TxtStationStart, "Start Station", out double? stStart)) return;
            if (!TryGetOptionalStation(TxtStationEnd,   "End Station",   out double? stEnd  )) return;

            if (stStart.HasValue && stEnd.HasValue && stStart.Value >= stEnd.Value)
            {
                ShowValidationError("Start Station must be less than End Station.");
                return;
            }

            if (!TryGetOptionalDouble(TxtElevMin, "Min Elevation", out double? elevMin)) return;
            if (!TryGetOptionalDouble(TxtElevMax, "Max Elevation", out double? elevMax)) return;

            if (elevMin.HasValue && elevMax.HasValue && elevMin.Value >= elevMax.Value)
            {
                ShowValidationError("Min Elevation must be less than Max Elevation.");
                return;
            }

            if (!double.TryParse(TxtViewSpacing.Text,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double spacing) || spacing <= 0)
            {
                ShowValidationError("Vertical Spacing must be a positive number.");
                return;
            }

            Result = new BulkProfileSettings
            {
                SelectedAlignments    = selected,
                SurfaceId             = surfItem.Id,
                ProfileStyleId        = profileStyleItem.Id,
                ProfileViewStyleId    = pvStyleItem.Id,
                ProfileNameSuffix     = TxtProfileSuffix.Text.TrimEnd(),
                ProfileViewNameSuffix = TxtPVSuffix.Text.TrimEnd(),
                StationStart          = stStart,
                StationEnd            = stEnd,
                ElevationMin          = elevMin,
                ElevationMax          = elevMax,
                ViewSpacing           = spacing
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
