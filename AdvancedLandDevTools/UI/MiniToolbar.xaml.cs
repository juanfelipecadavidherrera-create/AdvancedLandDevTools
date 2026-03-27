using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Newtonsoft.Json;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AdvancedLandDevTools.UI
{
    public partial class MiniToolbar : Window
    {
        // ═════════════════════════════════════════════════════════════════════
        //  Command registry — Tag → display label (for settings panel)
        // ═════════════════════════════════════════════════════════════════════
        private static readonly (string Tag, string Label, string Section)[] _commands = new[]
        {
            ("BULKSUR ",         "Bulk Surface Profile",  "PROFILES"),
            ("GETPARENT ",       "Get Parent Alignment",  "PROFILES"),
            ("PIPEMAGIC ",       "Pipe Magic",            "PROFILES"),
            ("MARKLINES ",       "Mark Lines",            "PROFILES"),
            ("ALIGNDEPLOY ",     "Align Deploy",          "ALIGN"),
            ("INVERTPULLUP ",    "Invert Pull Up",        "PIPES"),
            ("CHANGEELEVATION ", "Change Elevation",      "PIPES"),
            ("ELEVSLOPE ",       "Elev Slope",            "SURFACES"),
            ("LOWRIM ",          "Lowest Rim",            "SURFACES"),
            ("FLOODZONE ",       "FEMA Flood Zone",       "INFO"),
            ("FLOODCRITERIA ",   "County Flood",          "INFO"),
            ("SECTIONLOOKUP ",   "PLSS Section",          "INFO"),
            ("GROUNDWATER ",     "Water Table",           "INFO"),
            ("AREAMANAGER ",     "Area Manager",          "AREAS"),
            ("VPCUT ",           "VP Cut",                "VIEWPORT"),
            ("VTPANEL ",         "VT Panel",              "VEHICLE"),
            ("VTDRIVE ",         "Interactive Drive",     "VEHICLE"),
            ("VTSWEEP ",         "Swept Path",            "VEHICLE"),
            ("VTPARK ",          "Parking Layout",        "VEHICLE"),
        };

        private static readonly string _prefsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AdvancedLandDevTools", "toolbar_prefs.json");

        private Dictionary<string, bool> _visibility = new();

        public MiniToolbar()
        {
            InitializeComponent();
            LoadPrefs();
            ApplyVisibility();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Core handlers
        // ═════════════════════════════════════════════════════════════════════

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void ToolButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string cmd)
            {
                var doc = AcadApp.DocumentManager.MdiActiveDocument;
                doc?.SendStringToExecute(cmd, true, false, true);
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            Hide();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Settings popup
        // ═════════════════════════════════════════════════════════════════════

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
            if (SettingsPopup.IsOpen)
                BuildSettingsPanel();
        }

        private void BuildSettingsPanel()
        {
            SettingsPanel.Children.Clear();

            string lastSection = "";
            foreach (var (tag, label, section) in _commands)
            {
                if (section != lastSection)
                {
                    lastSection = section;
                    var sectionLabel = new TextBlock
                    {
                        Text = section,
                        Foreground = new System.Windows.Media.SolidColorBrush(
                            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#60CDFF")),
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, section == "PROFILES" ? 0 : 6, 0, 2),
                        Opacity = 0.7
                    };
                    SettingsPanel.Children.Add(sectionLabel);
                }

                bool isVisible = !_visibility.ContainsKey(tag) || _visibility[tag];

                var cb = new CheckBox
                {
                    Content = label,
                    IsChecked = isVisible,
                    Tag = tag,
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFFFFF")),
                    FontSize = 11,
                    Margin = new Thickness(2, 1, 0, 1),
                    Cursor = Cursors.Hand
                };
                cb.Checked   += SettingsCheckbox_Changed;
                cb.Unchecked += SettingsCheckbox_Changed;
                SettingsPanel.Children.Add(cb);
            }
        }

        private void SettingsCheckbox_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.Tag is string tag)
            {
                _visibility[tag] = cb.IsChecked == true;
                SavePrefs();
                ApplyVisibility();
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Visibility logic
        // ═════════════════════════════════════════════════════════════════════

        private void ApplyVisibility()
        {
            // Find all tool buttons by their Tag and toggle visibility
            var allButtons = FindToolButtons(PanelContent);
            foreach (var btn in allButtons)
            {
                if (btn.Tag is string tag && tag.Length > 0)
                {
                    bool show = !_visibility.ContainsKey(tag) || _visibility[tag];
                    btn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            // Hide section headers + dividers if all commands in that section are hidden
            HideEmptySections();
        }

        private void HideEmptySections()
        {
            // Walk through PanelContent children — section headers are TextBlocks,
            // dividers are Borders, buttons are Buttons with Tags
            var children = PanelContent.Children.Cast<UIElement>().ToList();

            int i = 0;
            while (i < children.Count)
            {
                var el = children[i];

                // Check if this is a section header TextBlock (cyan, small font)
                if (el is TextBlock tb && tb.FontSize <= 8 && tb.Foreground is System.Windows.Media.SolidColorBrush brush)
                {
                    string colorHex = brush.Color.ToString();
                    // Section headers use #60CDFF
                    if (colorHex.Contains("60CDFF") || colorHex.Contains("60cdff"))
                    {
                        // Collect all buttons until next section header or end
                        int start = i;
                        int j = i + 1;
                        bool anyVisible = false;

                        while (j < children.Count)
                        {
                            var next = children[j];
                            // Stop at next section header
                            if (next is TextBlock nextTb && nextTb.FontSize <= 8)
                                break;
                            // Stop at divider before next section
                            if (next is Border bd && bd.Height == 1 && j + 1 < children.Count &&
                                children[j + 1] is TextBlock)
                                break;

                            if (next is Button btn && btn.Visibility == Visibility.Visible)
                                anyVisible = true;
                            j++;
                        }

                        // Also check the divider above (if any)
                        if (start > 0 && children[start - 1] is Border divAbove && divAbove.Height == 1)
                            divAbove.Visibility = anyVisible ? Visibility.Visible : Visibility.Collapsed;

                        tb.Visibility = anyVisible ? Visibility.Visible : Visibility.Collapsed;
                    }
                }
                i++;
            }
        }

        private static List<Button> FindToolButtons(Panel panel)
        {
            var result = new List<Button>();
            foreach (UIElement child in panel.Children)
            {
                if (child is Button btn && btn.Tag is string tag && tag.Trim().Length > 0
                    && btn.Name != "BtnClose" && btn.Name != "BtnSettings")
                    result.Add(btn);
            }
            return result;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Persistence
        // ═════════════════════════════════════════════════════════════════════

        private void LoadPrefs()
        {
            try
            {
                if (File.Exists(_prefsPath))
                {
                    string json = File.ReadAllText(_prefsPath);
                    _visibility = JsonConvert.DeserializeObject<Dictionary<string, bool>>(json)
                                  ?? new Dictionary<string, bool>();
                }
            }
            catch { _visibility = new Dictionary<string, bool>(); }
        }

        private void SavePrefs()
        {
            try
            {
                string dir = Path.GetDirectoryName(_prefsPath)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_prefsPath, JsonConvert.SerializeObject(_visibility, Formatting.Indented));
            }
            catch { /* non-fatal */ }
        }
    }
}
