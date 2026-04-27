using System.Windows;

namespace AdvancedLandDevTools.UI
{
    public partial class LateralBeastWindow : Window
    {
        public string TargetLayer { get; private set; } = "P-ROW";
        public bool IsLeft { get; private set; }
        public double AngleDeg { get; private set; }
        public double PipeGap { get; private set; }

        public LateralBeastWindow(System.Collections.Generic.IEnumerable<string> availableLayers)
        {
            InitializeComponent();
            foreach (var layer in availableLayers)
                cmbTargetLayer.Items.Add(layer);

            // When IsEditable="True", WPF updates SelectedItem but doesn't always
            // sync the editable text box visually — force it on every selection change.
            cmbTargetLayer.SelectionChanged += (s, e) =>
            {
                if (cmbTargetLayer.SelectedItem != null)
                    cmbTargetLayer.Text = cmbTargetLayer.SelectedItem.ToString();
            };
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(cmbTargetLayer.Text))
            {
                MessageBox.Show("Target Layer cannot be empty.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (!double.TryParse(txtAngle.Text, out double angle))
            {
                MessageBox.Show("Invalid Angle value.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (!double.TryParse(txtGap.Text, out double gap))
            {
                MessageBox.Show("Invalid Pipe Gap value.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            TargetLayer = cmbTargetLayer.Text.Trim();
            AngleDeg = angle;
            PipeGap = gap;
            IsLeft = cmbDirection.SelectedIndex == 0;

            this.DialogResult = true;
            this.Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}
