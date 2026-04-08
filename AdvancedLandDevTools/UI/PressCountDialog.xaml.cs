using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using AdvancedLandDevTools.Engine;

namespace AdvancedLandDevTools.UI
{
    public partial class PressCountDialog : Window
    {
        public PressureNetworkSummary? SelectedNetwork { get; private set; }

        public PressCountDialog(List<PressureNetworkSummary> networks)
        {
            InitializeComponent();
            LstNetworks.ItemsSource = networks;
        }

        private void LstNetworks_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            BtnOk.IsEnabled = LstNetworks.SelectedItem != null;
        }

        private void LstNetworks_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LstNetworks.SelectedItem != null)
                Accept();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e) => Accept();

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Accept()
        {
            SelectedNetwork = LstNetworks.SelectedItem as PressureNetworkSummary;
            DialogResult    = true;
            Close();
        }
    }
}
