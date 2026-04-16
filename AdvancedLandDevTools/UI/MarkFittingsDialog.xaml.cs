using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace AdvancedLandDevTools.UI
{
    public class FittingDescriptionItem
    {
        public string Description { get; set; } = "";
        public int Count { get; set; }
        public bool IsSelected { get; set; } = true;
        public string Display => $"{Description} ({Count})";
    }

    public partial class MarkFittingsDialog : Window
    {
        public List<string> SelectedDescriptions { get; private set; } = new();
        private List<FittingDescriptionItem> _items;

        public MarkFittingsDialog(List<FittingDescriptionItem> items)
        {
            InitializeComponent();
            _items = items;
            DescriptionListView.ItemsSource = _items;
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            SelectedDescriptions = _items.Where(i => i.IsSelected).Select(i => i.Description).ToList();
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
