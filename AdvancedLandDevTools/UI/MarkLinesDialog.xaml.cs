using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using Autodesk.AutoCAD.DatabaseServices;

namespace AdvancedLandDevTools.UI
{
    /// <summary>
    /// Layer selection dialog for the MARKLINES command.
    /// </summary>
    public partial class MarkLinesDialog : Window
    {
        private List<LayerCheckItem> _allLayers = new();

        /// <summary>Names of the layers the user selected.</summary>
        public List<string> SelectedLayerNames { get; private set; } = new();

        public MarkLinesDialog(Database db)
        {
            InitializeComponent();
            LoadLayers(db);
        }

        // ─────────────────────────────────────────────────────────────────
        private void LoadLayers(Database db)
        {
            using var tx = db.TransactionManager.StartTransaction();

            var lt = tx.GetObject(db.LayerTableId, OpenMode.ForRead) as LayerTable;
            if (lt == null) { tx.Abort(); return; }

            foreach (ObjectId id in lt)
            {
                try
                {
                    var ltr = tx.GetObject(id, OpenMode.ForRead) as LayerTableRecord;
                    if (ltr == null) continue;
                    _allLayers.Add(new LayerCheckItem { Name = ltr.Name, IsSelected = false });
                }
                catch { }
            }

            tx.Abort();

            _allLayers = _allLayers.OrderBy(l => l.Name).ToList();
            LstLayers.ItemsSource = _allLayers;
        }

        // ─────────────────────────────────────────────────────────────────
        private void TxtFilter_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            string filter = TxtFilter.Text.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(filter))
            {
                LstLayers.ItemsSource = _allLayers;
            }
            else
            {
                LstLayers.ItemsSource = _allLayers
                    .Where(l => l.Name.ToUpperInvariant().Contains(filter))
                    .ToList();
            }
        }

        private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _allLayers) item.IsSelected = true;
            LstLayers.Items.Refresh();
        }

        private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _allLayers) item.IsSelected = false;
            LstLayers.Items.Refresh();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            SelectedLayerNames = _allLayers
                .Where(l => l.IsSelected)
                .Select(l => l.Name)
                .ToList();

            if (SelectedLayerNames.Count == 0)
            {
                MessageBox.Show("Please select at least one layer.",
                                "Mark Lines", MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }

    /// <summary>View-model item for the layer checkbox list.</summary>
    public class LayerCheckItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string Name { get; set; } = "";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
