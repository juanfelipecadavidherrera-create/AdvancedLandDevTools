using System.Collections.Generic;
using System.Windows;
using Autodesk.AutoCAD.DatabaseServices;

namespace AdvancedLandDevTools.UI
{
    public class StyleItem
    {
        public string   Name { get; set; } = "";
        public ObjectId Id   { get; set; }
    }

    public partial class InvertPullUpDialog : Window
    {
        // Results read by the command after OK
        public ObjectId SelectedLabelStyleId { get; private set; }
        public ObjectId SelectedMarkerStyleId{ get; private set; }

        public InvertPullUpDialog(
            List<StyleItem> labelStyles,
            List<StyleItem> markerStyles)
        {
            InitializeComponent();

            CmbLabelStyle.ItemsSource  = labelStyles;
            CmbMarkerStyle.ItemsSource = markerStyles;

            if (labelStyles.Count  > 0) CmbLabelStyle.SelectedIndex  = 0;
            if (markerStyles.Count > 0) CmbMarkerStyle.SelectedIndex = 0;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (CmbLabelStyle.SelectedItem == null)
            {
                MessageBox.Show(
                    "Please select a label style.",
                    "Invert Pull Up",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SelectedLabelStyleId  = ((StyleItem)CmbLabelStyle.SelectedItem).Id;
            SelectedMarkerStyleId = CmbMarkerStyle.SelectedItem != null
                ? ((StyleItem)CmbMarkerStyle.SelectedItem).Id
                : ObjectId.Null;

            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
