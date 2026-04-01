using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AdvancedLandDevTools.Engine;

namespace AdvancedLandDevTools.UI
{
    public partial class PipeSizingWindow : Window
    {
        private static readonly Brush PassBg  = new SolidColorBrush(Color.FromArgb(40, 107, 203, 119));
        private static readonly Brush FailBg  = new SolidColorBrush(Color.FromArgb(40, 255, 107, 107));
        private static readonly Brush PassFg  = new SolidColorBrush(Color.FromRgb(107, 203, 119));
        private static readonly Brush FailFg  = new SolidColorBrush(Color.FromRgb(255, 107, 107));

        public PipeSizingWindow()
        {
            InitializeComponent();
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
