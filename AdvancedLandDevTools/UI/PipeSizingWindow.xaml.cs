using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdvancedLandDevTools.Engine;
using AdvancedLandDevTools.Models;

namespace AdvancedLandDevTools.UI
{
    public partial class PipeSizingWindow : Window
    {
        private static readonly Brush PassBg  = new SolidColorBrush(Color.FromArgb(40, 107, 203, 119));
        private static readonly Brush FailBg  = new SolidColorBrush(Color.FromArgb(40, 255, 107, 107));
        private static readonly Brush PassFg  = new SolidColorBrush(Color.FromRgb(107, 203, 119));
        private static readonly Brush FailFg  = new SolidColorBrush(Color.FromRgb(255, 107, 107));

        private List<AreaEntry> _currentAreas = new();
        private bool _areaMgrExpanded = false;

        // Material → (n value, description)
        private static readonly (string Label, double N, string Hint)[] _materials =
        {
            ("— select material —",  0,      ""),
            ("Concrete (RCP)",       0.013,  "Reinforced concrete pipe, most common for storm drainage."),
            ("Concrete (CIP smooth)",0.012,  "Cast-in-place, smooth form finish."),
            ("HDPE (corrugated)",    0.024,  "Corrugated HDPE, high roughness."),
            ("HDPE (smooth liner)",  0.012,  "HDPE with smooth interior liner."),
            ("PVC (smooth)",         0.009,  "PVC or CPEP smooth wall."),
            ("Ductile Iron",         0.013,  "Ductile iron pipe, lined."),
            ("Cast Iron",            0.014,  "Unlined cast iron."),
            ("Galvanized Steel",     0.016,  "Corrugated galvanized metal pipe."),
            ("Clay (vitrified)",     0.013,  "Vitrified clay sewer pipe."),
            ("Brick / Masonry",      0.015,  "Brick mortar-lined channel or culvert."),
        };

        public PipeSizingWindow()
        {
            InitializeComponent();
            LoadMaterials();
        }

        // ═════════════════════════════════════════════════════════════════
        //  Material Selector
        // ═════════════════════════════════════════════════════════════════

        private void LoadMaterials()
        {
            foreach (var m in _materials)
                CboMaterial.Items.Add(m.Label);
            CboMaterial.SelectedIndex = 0;
        }

        private void CboMaterial_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int idx = CboMaterial.SelectedIndex;
            if (idx <= 0 || idx >= _materials.Length) return;

            var (_, n, hint) = _materials[idx];
            TxtN.Text = n.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
            LblNHint.Text = hint;
        }

        // ═════════════════════════════════════════════════════════════════
        //  Area Manager Integration
        // ═════════════════════════════════════════════════════════════════

        private void BtnToggleAreaMgr_Click(object sender, RoutedEventArgs e)
        {
            _areaMgrExpanded = !_areaMgrExpanded;
            PnlAreaMgr.Visibility  = _areaMgrExpanded ? Visibility.Visible : Visibility.Collapsed;
            TxtToggleArrow.Text    = _areaMgrExpanded ? "▼" : "▶";

            if (_areaMgrExpanded && CboProject.Items.Count == 0)
                LoadProjectList();
        }

        private void BtnReloadProjects_Click(object sender, RoutedEventArgs e)
        {
            LoadProjectList();
        }

        private void LoadProjectList()
        {
            var projects = AreaManagerStore.ListProjects();
            CboProject.Items.Clear();
            PnlAreaList.Children.Clear();
            _currentAreas.Clear();
            UpdateTotal();

            if (projects.Count == 0)
            {
                LblNoProjects.Visibility = Visibility.Visible;
                return;
            }

            LblNoProjects.Visibility = Visibility.Collapsed;
            foreach (var p in projects)
                CboProject.Items.Add(p);

            CboProject.SelectedIndex = 0;
        }

        private void CboProject_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CboProject.SelectedItem is not string projectName) return;

            var project = AreaManagerStore.Load(projectName);
            _currentAreas = project?.Areas ?? new List<AreaEntry>();

            PnlAreaList.Children.Clear();
            UpdateTotal();

            if (_currentAreas.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = "No areas in this project.",
                    Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                    FontStyle = FontStyles.Italic,
                    Margin = new Thickness(4, 6, 4, 6),
                    FontSize = 11
                };
                PnlAreaList.Children.Add(empty);
                return;
            }

            foreach (var area in _currentAreas)
            {
                var cb = new CheckBox
                {
                    Tag = area,
                    Margin = new Thickness(2, 3, 2, 3),
                    Foreground = new SolidColorBrush(Colors.White),
                    FontSize = 12
                };

                double acres = area.AreaSqFt / 43560.0;
                cb.Content = $"{area.Name}  —  {area.AreaSqFt:N0} sq ft  ({acres:F3} ac)";
                cb.Checked   += (s, _) => UpdateTotal();
                cb.Unchecked += (s, _) => UpdateTotal();
                PnlAreaList.Children.Add(cb);
            }
        }

        private void UpdateTotal()
        {
            double total = 0;
            foreach (CheckBox cb in PnlAreaList.Children.OfType<CheckBox>())
            {
                if (cb.IsChecked == true && cb.Tag is AreaEntry area)
                    total += area.AreaSqFt;
            }
            double acres = total / 43560.0;
            LblAreaTotal.Text = $"{total:N0} sq ft  ({acres:F3} ac)";
        }

        private void BtnApplyArea_Click(object sender, RoutedEventArgs e)
        {
            double total = 0;
            foreach (CheckBox cb in PnlAreaList.Children.OfType<CheckBox>())
            {
                if (cb.IsChecked == true && cb.Tag is AreaEntry area)
                    total += area.AreaSqFt;
            }

            if (total <= 0)
            {
                ShowError("No areas selected. Check at least one area before applying.");
                return;
            }

            TxtArea.Text = total.ToString("F2", CultureInfo.InvariantCulture);
        }

        // ═════════════════════════════════════════════════════════════════
        //  Calculate
        // ═════════════════════════════════════════════════════════════════
        private void BtnCalculate_Click(object sender, RoutedEventArgs e)
        {
            RunCalculation();
        }

        private void RunCalculation()
        {
            ErrorCard.Visibility   = Visibility.Collapsed;
            ResultsCard.Visibility = Visibility.Collapsed;
            LblSuggestion.Visibility = Visibility.Collapsed;

            var input = ParseInputs();
            if (input == null) return; // error shown by ParseInputs

            var result = PipeSizingEngine.Calculate(input);

            if (!result.IsValid)
            {
                ShowError(result.ErrorMessage);
                return;
            }

            // Hydrology
            LblAcres.Text   = result.AreaAcres.ToString("F4");
            LblQRunoff.Text = result.QRunoff.ToString("F3") + " CFS";

            // Hydraulics
            LblDft.Text   = result.DiameterFt.ToString("F3");
            LblApipe.Text = result.AreaPipe.ToString("F4");
            LblP.Text     = result.WettedP.ToString("F4");
            LblR.Text     = result.HydRadius.ToString("F4");
            LblQCap.Text  = result.QCapacity.ToString("F3") + " CFS";
            LblV.Text     = result.Velocity.ToString("F2") + " ft/s";

            // Validation badges
            CapacityBorder.Background = result.CapacityPass ? PassBg : FailBg;
            LblCapacity.Foreground    = result.CapacityPass ? PassFg : FailFg;
            LblCapacity.Text          = result.CapacityMsg;

            VelocityBorder.Background = result.VelocityPass ? PassBg : FailBg;
            LblVelocity.Foreground    = result.VelocityPass ? PassFg : FailFg;
            LblVelocity.Text          = result.VelocityMsg;

            ResultsCard.Visibility = Visibility.Visible;
        }

        // ═════════════════════════════════════════════════════════════════
        //  Suggest Minimum Diameter
        // ═════════════════════════════════════════════════════════════════
        private void BtnSuggest_Click(object sender, RoutedEventArgs e)
        {
            // First run the calculation with current inputs so user sees results
            RunCalculation();
            if (ResultsCard.Visibility != Visibility.Visible) return;

            var input = ParseInputs();
            if (input == null) return;

            int minDiam = PipeSizingEngine.SuggestMinDiameter(input);

            if (minDiam < 0)
            {
                LblSuggestion.Text = "No standard pipe size (12\"-96\") passes both checks at this slope and demand.";
            }
            else if (minDiam <= input.PipeDiameterIn)
            {
                LblSuggestion.Text = $"Current {input.PipeDiameterIn:F0}\" diameter is adequate. " +
                                     $"Minimum standard size that passes: {minDiam}\".";
            }
            else
            {
                LblSuggestion.Text = $"Minimum standard diameter that passes both checks: {minDiam}\". " +
                                     $"Consider increasing from {input.PipeDiameterIn:F0}\" to {minDiam}\".";
            }

            LblSuggestion.Visibility = Visibility.Visible;
        }

        // ═════════════════════════════════════════════════════════════════
        //  Parse user inputs
        // ═════════════════════════════════════════════════════════════════
        private PipeSizingInput? ParseInputs()
        {
            if (!TryParse(TxtArea.Text,  "Drainage Area",       out double area))  return null;
            if (!TryParse(TxtC.Text,     "Runoff Coefficient",  out double c))     return null;
            if (!TryParse(TxtI.Text,     "Rainfall Intensity",  out double i))     return null;
            if (!TryParse(TxtDiam.Text,  "Pipe Diameter",       out double diam))  return null;
            if (!TryParse(TxtSlope.Text, "Pipe Slope",          out double slope)) return null;
            if (!TryParse(TxtN.Text,     "Manning's n",         out double n))     return null;

            return new PipeSizingInput
            {
                DrainageAreaSqFt  = area,
                RunoffCoefficient = c,
                RainfallIntensity = i,
                PipeDiameterIn    = diam,
                PipeSlope         = slope,
                ManningsN         = n
            };
        }

        private bool TryParse(string text, string fieldName, out double value)
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return true;

            ShowError($"Invalid number for \"{fieldName}\": {text}");
            value = 0;
            return false;
        }

        private void ShowError(string msg)
        {
            LblError.Text          = msg;
            ErrorCard.Visibility   = Visibility.Visible;
            ResultsCard.Visibility = Visibility.Collapsed;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
