using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Newtonsoft.Json;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace AdvancedLandDevTools.UI
{
    public partial class MiniToolbar : Window
    {
        // ═════════════════════════════════════════════════════════════════════
        //  Command registry — Tag, Label, Section, Icon (MDL2), Color hex, Short label
        // ═════════════════════════════════════════════════════════════════════
        private static readonly (string Tag, string Label, string Section,
            string Icon, string Color, string Short)[] _commands = new[]
        {
            // PROFILES
            ("BULKSUR ",         "Bulk Surface Profile",  "PROFILES",  "\xE8A5", "#4FC3F7", "Bulk"),
            ("GETPARENT ",       "Get Parent Alignment",  "PROFILES",  "\xE71B", "#4DB6AC", "Parent"),
            ("PIPEMAGIC ",       "Pipe Magic",            "PROFILES",  "\xE945", "#BA68C8", "Magic"),
            ("LLABELGEN ",       "Label Gen",             "PROFILES",  "\xE8F1", "#00E676", "Label"),
            ("MARKLINES ",       "Mark Lines",            "PROFILES",  "\xED63", "#FFB74D", "Mark"),
            ("MARKFITTINGS ",    "Mark Fittings",         "PROFILES",  "\xE81E", "#FF8A65", "MkFit"),
            ("PROFOFF ",         "Profile Off",           "PROFILES",  "\xE738", "#EF5350", "PrOff"),
            ("PVSTYLE ",         "PV Style Override",     "PROFILES",  "\xE771", "#CE93D8", "Style"),
            ("CHOPCHOP ",        "ChopChop",              "PROFILES",  "\xE8C6", "#FF7043", "Chop"),

            // ALIGN
            ("ALIGNDEPLOY ",     "Align Deploy",          "ALIGN",     "\xE8AB", "#81C784", "Deploy"),

            // PIPES
            ("INVERTPULLUP ",    "Invert Pull Up",        "PIPES",     "\xE74A", "#FFD54F", "Invert"),
            ("CHANGEELEVATION ", "Change Elevation",      "PIPES",     "\xE70E", "#FF8A65", "Elev"),
            ("PIPESIZING ",      "Pipe Sizing Calc",      "PIPES",     "\xE81E", "#29B6F6", "Sizing"),
            ("COVERADJUST ",      "Cover Adjust",          "PIPES",     "\xE74B", "#00BCD4", "Cover"),
            ("PRESSCOUNT ",      "Pressure Count",        "PIPES",     "\xE946", "#CE93D8", "PCount"),
            ("RRNETWORKCHECK ",  "Network Check",         "PIPES",     "\xE73E", "#66BB6A", "RRChk"),
            ("LATMANAGER ",      "Lateral Manager",       "PIPES",     "\xE9E9", "#FFB74D", "LatMan"),

            // SURFACES
            ("ELEVSLOPE ",       "Elev Slope",            "SURFACES",  "\xE879", "#66BB6A", "Slope"),
            ("BLOCKTOSURFACE ",  "Block to Surface",      "SURFACES",  "\xE8B7", "#FF7043", "B2S"),
            ("TEXTTOSURFACE ",   "Text to Surface",       "SURFACES",  "\xE8D2", "#FFB74D", "Txt2S"),
            ("LOWRIM ",          "Lowest Rim",            "SURFACES",  "\xE74B", "#EF5350", "LoRim"),

            // INFO
            ("FOLIO ", "Property Appraiser",  "INFO",      "\xE8A5", "#4FC3F7", "Apprs"),
            ("FLOODZONE ",       "FEMA Flood Zone",       "INFO",      "\xE773", "#EF5350", "FEMA"),
            ("FLOODCRITERIA ",   "County Flood",          "INFO",      "\xE81D", "#FF7043", "County"),
            ("SECTIONLOOKUP ",   "PLSS Section",          "INFO",      "\xE909", "#AED581", "PLSS"),
            ("GWMAY ",           "Water Table May",       "INFO",      "\xE787", "#29B6F6", "GWMay"),
            ("GWOCT ",           "Water Table Oct",       "INFO",      "\xE787", "#42A5F5", "GWOct"),

            // AREAS & EXCAVATION
            ("AREAMANAGER ",     "Area Manager",          "AREAS",     "\xE80F", "#66BB6A", "Areas"),
            ("EXF ",             "EXF Trench Manager",    "AREAS",     "\xE81E", "#FF8A65", "Trench"),

            // VIEWPORT
            ("VPCUT ",           "VP Cut",                "VIEWPORT",  "\xE8C6", "#42A5F5", "Cut"),

            // SECTIONS
            ("SECDRAW ",         "Section Drawer",        "SECTIONS",  "\xE8A0", "#00897B", "SecDr"),

            // VEHICLE
            ("VTDRIVE ",         "Interactive Drive",     "VEHICLE",   "\xE7BA", "#66BB6A", "Drive"),
            ("VTPANEL ",         "VT Panel",              "VEHICLE",   "\xE80F", "#4FC3F7", "Panel"),
            ("VTSWEEP ",         "Swept Path",            "VEHICLE",   "\xE81E", "#FFB74D", "Sweep"),
            ("VTPARK ",          "Parking Layout",        "VEHICLE",   "\xE81E", "#BA68C8", "Park"),

            // DISPLAY
            ("LAYOUTDARK ",      "Layout Dark Mode",      "DISPLAY",   "\xE708", "#7E57C2", "Dark"),

            // HELP
            ("ALDTHELP ",        "ALDT Help",             "HELP",      "\xE897", "#60CDFF", "Help"),
        };

        private const int COLUMNS = 4;

        private static readonly string _prefsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AdvancedLandDevTools", "toolbar_prefs.json");

        private Dictionary<string, bool> _visibility = new();

        public MiniToolbar()
        {
            InitializeComponent();
            LoadPrefs();
            BuildButtons();
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
        //  Build the 4-column grid layout from the command registry
        // ═════════════════════════════════════════════════════════════════════

        private void BuildButtons()
        {
            ButtonContainer.Children.Clear();

            // Group commands by section, preserving order
            var sections = new List<(string Section, List<int> Indices)>();
            string lastSection = "";

            for (int i = 0; i < _commands.Length; i++)
            {
                if (_commands[i].Section != lastSection)
                {
                    lastSection = _commands[i].Section;
                    sections.Add((lastSection, new List<int>()));
                }
                sections[^1].Indices.Add(i);
            }

            for (int s = 0; s < sections.Count; s++)
            {
                var (section, indices) = sections[s];

                // Section header
                var header = new TextBlock
                {
                    Text = section,
                    Foreground = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString("#60CDFF")),
                    FontSize = 7.5,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(2, 0, 0, 3),
                    Opacity = 0.6,
                    Tag = $"HEADER_{section}"
                };
                ButtonContainer.Children.Add(header);

                // WrapPanel with 4-column layout
                var wrap = new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    Tag = $"WRAP_{section}"
                };

                foreach (int idx in indices)
                {
                    var cmd = _commands[idx];
                    bool show = !_visibility.ContainsKey(cmd.Tag) || _visibility[cmd.Tag];

                    var btn = CreateToolButton(cmd.Tag, cmd.Icon, cmd.Color,
                        cmd.Short, $"{cmd.Label} ({cmd.Tag.Trim()})");
                    btn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    wrap.Children.Add(btn);
                }

                ButtonContainer.Children.Add(wrap);

                // Divider between sections
                if (s < sections.Count - 1)
                {
                    var divider = new Border
                    {
                        Height = 1,
                        Background = new SolidColorBrush(
                            (Color)ColorConverter.ConvertFromString("#454545")),
                        Margin = new Thickness(2, 4, 2, 4),
                        Tag = $"DIV_{section}"
                    };
                    ButtonContainer.Children.Add(divider);
                }
            }

            HideEmptySections();
        }

        private Button CreateToolButton(string tag, string icon, string colorHex,
            string shortLabel, string tooltip)
        {
            var color = (Color)ColorConverter.ConvertFromString(colorHex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            var hoverBg = new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B));
            hoverBg.Freeze();

            var iconTb = new TextBlock
            {
                Text = icon,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 15,
                Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            var labelTb = new TextBlock
            {
                Text = shortLabel,
                FontSize = 7,
                Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, -1, 0, 0),
                Opacity = 0.7
            };

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(iconTb);
            stack.Children.Add(labelTb);

            var border = new Border
            {
                Width = 44,
                Height = 36,
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
                Child = stack
            };

            var btn = new Button
            {
                Tag = tag,
                ToolTip = tooltip,
                Template = CreateButtonTemplate(hoverBg),
                Content = border
            };
            btn.Click += ToolButton_Click;

            return btn;
        }

        private static ControlTemplate CreateButtonTemplate(Brush hoverBg)
        {
            // Simple template: transparent background, hover shows tint
            var template = new ControlTemplate(typeof(Button));

            var bdFactory = new FrameworkElementFactory(typeof(Border));
            bdFactory.Name = "Bd";
            bdFactory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bdFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));

            var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            cpFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cpFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bdFactory.AppendChild(cpFactory);

            template.VisualTree = bdFactory;

            // Hover trigger
            var hoverTrigger = new Trigger
            {
                Property = UIElement.IsMouseOverProperty,
                Value = true
            };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, hoverBg, "Bd"));
            template.Triggers.Add(hoverTrigger);

            return template;
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
            foreach (var (tag, label, section, _, _, _) in _commands)
            {
                if (section != lastSection)
                {
                    lastSection = section;
                    var sectionLabel = new TextBlock
                    {
                        Text = section,
                        Foreground = new SolidColorBrush(
                            (Color)ColorConverter.ConvertFromString("#60CDFF")),
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
                    Foreground = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString("#FFFFFF")),
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
            // Toggle button visibility inside each WrapPanel
            foreach (UIElement child in ButtonContainer.Children)
            {
                if (child is WrapPanel wrap)
                {
                    foreach (UIElement wc in wrap.Children)
                    {
                        if (wc is Button btn && btn.Tag is string tag && tag.Trim().Length > 0)
                        {
                            bool show = !_visibility.ContainsKey(tag) || _visibility[tag];
                            btn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                        }
                    }
                }
            }

            HideEmptySections();
        }

        private void HideEmptySections()
        {
            // Walk ButtonContainer children in groups of (header, wrap, divider?)
            var children = ButtonContainer.Children.Cast<UIElement>().ToList();

            for (int i = 0; i < children.Count; i++)
            {
                var el = children[i];

                // Find WrapPanel — check if any buttons visible
                if (el is WrapPanel wrap)
                {
                    bool anyVisible = wrap.Children.Cast<UIElement>()
                        .Any(c => c is Button && c.Visibility == Visibility.Visible);

                    wrap.Visibility = anyVisible ? Visibility.Visible : Visibility.Collapsed;

                    // Header is the element before the wrap
                    if (i > 0 && children[i - 1] is TextBlock header)
                        header.Visibility = anyVisible ? Visibility.Visible : Visibility.Collapsed;

                    // Divider is the element after the wrap
                    if (i + 1 < children.Count && children[i + 1] is Border divider && divider.Height == 1)
                        divider.Visibility = anyVisible ? Visibility.Visible : Visibility.Collapsed;
                }
            }
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
