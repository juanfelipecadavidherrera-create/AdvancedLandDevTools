using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using AdvancedLandDevTools.VehicleTracking.Data;

namespace AdvancedLandDevTools.UI
{
    // Alias for the tuple returned by VehicleLibrary.GetDisplayList()
    using VehEntry = System.ValueTuple<string, string, string, bool, int>;

    /// <summary>
    /// Modeless floating panel for VTDRIVE interactive drive.
    /// Replaces command-line prompts with a proper UI.
    /// </summary>
    public partial class VtDrivePanel : Window
    {
        // ── Public state read by VtDriveCommand ──────────────────────
        public VehEntry? SelectedVehicle { get; private set; }
        public double SpeedMph => SldSpeed.Value;

        /// <summary>Raised when user clicks Undo.</summary>
        public event Action? UndoRequested;
        /// <summary>Raised when user clicks Finish (end drive, keep path).</summary>
        public event Action? FinishRequested;
        /// <summary>Raised when user clicks Cancel (abort drive entirely).</summary>
        public event Action? CancelRequested;
        /// <summary>Raised when user clicks Accept Path (exit edit mode).</summary>
        public event Action? AcceptRequested;
        /// <summary>Raised when user clicks Place Detail Block.</summary>
        public event Action? PlaceBlockRequested;
        /// <summary>Raised when user clicks Start Run.</summary>
        public event Action? StartRunRequested;
        /// <summary>Raised when user clicks Resume Previous Drive.</summary>
        public event Action? ResumeRequested;

        private readonly List<VehEntry> _vehicles;

        public VtDrivePanel()
        {
            InitializeComponent();

            _vehicles = VehicleLibrary.GetDisplayList();
            int defIdx = 0;
            for (int i = 0; i < _vehicles.Count; i++)
            {
                string fl = _vehicles[i].Item1.Contains("Florida") || _vehicles[i].Item1.Contains("Miami") ? " *FL" : "";
                CboVehicle.Items.Add($"{_vehicles[i].Item2}  —  {_vehicles[i].Item1}{fl}");
                if (_vehicles[i].Item2 == "WB-62FL") defIdx = i;
            }
            CboVehicle.SelectedIndex = defIdx;
        }

        // ── Public methods called by VtDriveCommand ──────────────────

        public void SetStatus(string text) =>
            Dispatcher.Invoke(() => TxtStatus.Text = text);

        public void SetStatus(string text, string color) =>
            Dispatcher.Invoke(() =>
            {
                TxtStatus.Text = text;
                TxtStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
            });

        public void EnableUndo(bool enable) =>
            Dispatcher.Invoke(() => BtnUndo.IsEnabled = enable);

        public void EnableFinish(bool enable) =>
            Dispatcher.Invoke(() => BtnFinish.IsEnabled = enable);

        public void EnterEditMode()
        {
            Dispatcher.Invoke(() =>
            {
                PnlEdit.Visibility = Visibility.Visible;
                PnlActions.Visibility = Visibility.Collapsed;
                TxtStatus.Text = "Click any X to drag-edit. Click Accept when done.";
                TxtStatus.Foreground = new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#FFB74D"));
            });
        }

        public void ExitEditMode()
        {
            Dispatcher.Invoke(() =>
            {
                PnlEdit.Visibility = Visibility.Collapsed;
                PnlActions.Visibility = Visibility.Visible;
            });
        }

        // ── Event handlers ───────────────────────────────────────────

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => DragMove();

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            CancelRequested?.Invoke();
            // Unblock the ed.GetKeywords prompt during setup phase
            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application
                    .DocumentManager.MdiActiveDocument;
                doc?.SendStringToExecute("\x03", true, false, false); // Ctrl+C = cancel prompt
            }
            catch { }
        }

        private void CboVehicle_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            int idx = CboVehicle.SelectedIndex;
            if (idx < 0 || idx >= _vehicles.Count) return;
            SelectedVehicle = _vehicles[idx];
            var entry = SelectedVehicle.Value;

            var v = entry.Item4 // IsArticulated
                ? VehicleLibrary.ArticulatedVehicles[entry.Item5].LeadUnit
                : VehicleLibrary.SingleUnits[entry.Item5];

            TxtVehicleInfo.Text = $"{v.Length:F0}'L  |  WB={v.Wheelbase:F0}'  |  W={v.Width:F1}'  |  MinR={v.EffectiveMinRadius:F1}'";
        }

        private void SldSpeed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (TxtSpeed != null)
                TxtSpeed.Text = $"{SldSpeed.Value:F0} mph";
        }

        private void BtnUndo_Click(object sender, RoutedEventArgs e)
            => UndoRequested?.Invoke();

        private void BtnFinish_Click(object sender, RoutedEventArgs e)
            => FinishRequested?.Invoke();

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
            => CancelRequested?.Invoke();

        private void BtnAccept_Click(object sender, RoutedEventArgs e)
            => AcceptRequested?.Invoke();

        private void BtnStartRun_Click(object sender, RoutedEventArgs e)
        {
            // Switch from setup panel to drive panel
            PnlStart.Visibility = Visibility.Collapsed;
            TxtStatus.Visibility = Visibility.Visible;
            PnlActions.Visibility = Visibility.Visible;
            StartRunRequested?.Invoke();

            // Send keyword to unblock the ed.GetKeywords prompt
            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application
                    .DocumentManager.MdiActiveDocument;
                doc?.SendStringToExecute("StartRun\n", true, false, false);
            }
            catch { }
        }

        private void BtnResume_Click(object sender, RoutedEventArgs e)
        {
            // Switch from setup panel to drive panel
            PnlStart.Visibility = Visibility.Collapsed;
            TxtStatus.Visibility = Visibility.Visible;
            PnlActions.Visibility = Visibility.Visible;
            ResumeRequested?.Invoke();

            // Send keyword to unblock the ed.GetKeywords prompt
            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application
                    .DocumentManager.MdiActiveDocument;
                doc?.SendStringToExecute("Resume\n", true, false, false);
            }
            catch { }
        }

        private void BtnPlaceBlock_Click(object sender, RoutedEventArgs e)
        {
            PlaceBlockRequested?.Invoke();
            // If in setup phase, unblock the ed.GetKeywords prompt
            if (PnlStart.Visibility == Visibility.Visible)
            {
                try
                {
                    var doc = Autodesk.AutoCAD.ApplicationServices.Application
                        .DocumentManager.MdiActiveDocument;
                    doc?.SendStringToExecute("PlaceBlock\n", true, false, false);
                }
                catch { }
            }
        }
    }
}
