using System.Windows;
using System.Collections.Generic;

namespace AdvancedLandDevTools.UI
{
    public partial class LayerSelectionWindow : Window
    {
        public string SelectedLayer { get; private set; }

        public LayerSelectionWindow(IEnumerable<string> layers, string currentLayer)
        {
            InitializeComponent();
            
            foreach (var layer in layers)
            {
                cmbLayers.Items.Add(layer);
            }
            
            cmbLayers.Text = currentLayer;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(cmbLayers.Text))
            {
                MessageBox.Show("Layer name cannot be empty.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            SelectedLayer = cmbLayers.Text.Trim();
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
