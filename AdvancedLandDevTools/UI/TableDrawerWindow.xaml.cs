// Advanced Land Development Tools
// Copyright © Juan Felipe Cadavid — All Rights Reserved
// Unauthorized copying or redistribution is prohibited.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AdvancedLandDevTools.Engine;
using AdvancedLandDevTools.Models;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AdvancedLandDevTools.UI
{
    public partial class TableDrawerWindow : Window
    {
        // ── Constants ─────────────────────────────────────────────────────────
        private const double PxPerFt    = 8.0;   // canvas pixels per drawing foot
        private const double CanvasPad  = 12.0;  // padding inside canvas border

        // ── State ─────────────────────────────────────────────────────────────
        private TableDefinition _td;
        private int  _selRow = -1;
        private int  _selCol = -1;
        private bool _suppressEvents;
        private string _textStyleName = "Standard";
        private double _textHeight     = 2.5;
        private readonly Document _doc;

        /// <summary>Set when the user clicks "Draw in Model". Consumed by the command.</summary>
        public TableDefinition? ResultTable { get; private set; }

        // ── DimItem — view model for col-width / row-height lists ─────────────
        private class DimItem
        {
            public int    Index { get; set; }
            public string Label { get; set; } = "";
            public string Value { get; set; } = "";
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Constructor
        // ─────────────────────────────────────────────────────────────────────

        public TableDrawerWindow(Document doc)
        {
            _doc = doc;
            _td  = TableDefinition.CreateDefault(3, 3);
            InitializeComponent();
            TxtTableName.Text = _td.Name;
            RefreshSavedCombo();
            RebuildDimControls();
            LoadTextStyles();
            DrawPreview();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Text style helpers
        // ─────────────────────────────────────────────────────────────────────

        private void LoadTextStyles()
        {
            var names = new List<string>();
            try
            {
                using var tr = _doc.Database.TransactionManager.StartTransaction();
                var tst = (TextStyleTable)tr.GetObject(
                    _doc.Database.TextStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in tst)
                {
                    var rec = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!string.IsNullOrEmpty(rec.Name))
                        names.Add(rec.Name);
                }
                tr.Commit();
            }
            catch { }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            if (!names.Contains("Standard", StringComparer.OrdinalIgnoreCase))
                names.Insert(0, "Standard");

            _suppressEvents = true;
            CmbTextStyle.ItemsSource = names;
            // Select the current style, defaulting to Standard
            int idx = names.FindIndex(n =>
                string.Equals(n, _textStyleName, StringComparison.OrdinalIgnoreCase));
            CmbTextStyle.SelectedIndex = idx >= 0 ? idx : 0;
            _suppressEvents = false;
        }

        private void CmbTextStyle_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            if (CmbTextStyle.SelectedItem is string s)
                _textStyleName = s;
        }

        private void TxtTextHeight_LostFocus(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(TxtTextHeight.Text,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.CurrentCulture, out double h) && h > 0)
                _textHeight = h;
            else
                TxtTextHeight.Text = _textHeight.ToString("F2");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Canvas preview
        // ─────────────────────────────────────────────────────────────────────

        private void DrawPreview()
        {
            PreviewCanvas.Children.Clear();
            if (_td.Rows == 0 || _td.Cols == 0) return;

            // Pixel positions for column left edges
            var colX = new double[_td.Cols + 1];
            colX[0] = CanvasPad;
            for (int c = 0; c < _td.Cols; c++)
                colX[c + 1] = colX[c] + SafeDim(_td.ColWidths, c, 20.0) * PxPerFt;

            // Pixel positions for row top edges (Y increases downward on canvas)
            var rowY = new double[_td.Rows + 1];
            rowY[0] = CanvasPad;
            for (int r = 0; r < _td.Rows; r++)
                rowY[r + 1] = rowY[r] + SafeDim(_td.RowHeights, r, 8.0) * PxPerFt;

            PreviewCanvas.Width  = colX[_td.Cols] + CanvasPad;
            PreviewCanvas.Height = rowY[_td.Rows] + CanvasPad;

            var borderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80));

            // ── Cell backgrounds + content ────────────────────────────────────
            for (int r = 0; r < _td.Rows; r++)
            {
                for (int c = 0; c < _td.Cols; c++)
                {
                    var cell     = _td[r, c];
                    bool isSlave = cell?.IsMergedSlave ?? false;
                    if (isSlave) continue;

                    int spanR = cell != null ? Math.Max(1, Math.Min(cell.RowSpan, _td.Rows - r)) : 1;
                    int spanC = cell != null ? Math.Max(1, Math.Min(cell.ColSpan, _td.Cols - c)) : 1;

                    double cellW = colX[c + spanC] - colX[c];
                    double cellH = rowY[r + spanR] - rowY[r];

                    bool isSelected = (r == _selRow && c == _selCol);
                    bool isLinked   = cell?.ContentType == CellContentType.LinkedProperty;

                    // Background rectangle
                    Brush fillBrush = isSelected
                        ? new SolidColorBrush(Color.FromArgb(140, 255, 140,  0))   // orange
                        : isLinked
                            ? new SolidColorBrush(Color.FromArgb( 55,  0, 120, 212)) // blue tint
                            : new SolidColorBrush(Color.FromArgb( 25, 50,  50,  50)); // subtle dark

                    var rect = new Rectangle
                    {
                        Width           = cellW,
                        Height          = cellH,
                        Fill            = fillBrush,
                        Stroke          = borderBrush,
                        StrokeThickness = 0   // grid lines drawn separately
                    };
                    Canvas.SetLeft(rect, colX[c]);
                    Canvas.SetTop(rect,  rowY[r]);
                    PreviewCanvas.Children.Add(rect);

                    // Cell display text
                    string txt = cell?.DisplayValue ?? "";
                    if (!string.IsNullOrWhiteSpace(txt))
                    {
                        var tb = new TextBlock
                        {
                            Text                = txt,
                            Foreground          = Brushes.White,
                            FontSize            = 11,
                            Width               = cellW - 6,
                            MaxHeight           = cellH - 4,
                            TextWrapping        = TextWrapping.Wrap,
                            TextTrimming        = TextTrimming.CharacterEllipsis,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            TextAlignment       = System.Windows.TextAlignment.Center
                        };
                        Canvas.SetLeft(tb, colX[c] + 3);
                        Canvas.SetTop(tb,  rowY[r] + Math.Max(0, (cellH - 16) / 2.0));
                        PreviewCanvas.Children.Add(tb);
                    }

                    // Link badge (top-right corner)
                    if (isLinked)
                    {
                        var badge = new TextBlock
                        {
                            Text      = "⛓",
                            FontSize  = 9,
                            Foreground = new SolidColorBrush(Color.FromRgb(96, 205, 255))
                        };
                        Canvas.SetLeft(badge, colX[c + spanC] - 15);
                        Canvas.SetTop(badge,  rowY[r] + 2);
                        PreviewCanvas.Children.Add(badge);
                    }
                }
            }

            // ── Grid lines ────────────────────────────────────────────────────
            for (int r = 0; r <= _td.Rows; r++)
            {
                bool isOuter = r == 0 || r == _td.Rows;
                PreviewCanvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = colX[0], Y1 = rowY[r], X2 = colX[_td.Cols], Y2 = rowY[r],
                    Stroke          = isOuter
                        ? new SolidColorBrush(Color.FromRgb(100, 150, 100))
                        : borderBrush,
                    StrokeThickness = isOuter ? 1.5 : 1.0
                });
            }
            for (int c = 0; c <= _td.Cols; c++)
            {
                bool isOuter = c == 0 || c == _td.Cols;
                PreviewCanvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = colX[c], Y1 = rowY[0], X2 = colX[c], Y2 = rowY[_td.Rows],
                    Stroke          = isOuter
                        ? new SolidColorBrush(Color.FromRgb(100, 150, 100))
                        : borderBrush,
                    StrokeThickness = isOuter ? 1.5 : 1.0
                });
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Canvas mouse — cell selection
        // ─────────────────────────────────────────────────────────────────────

        private void PreviewCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_td.Rows == 0 || _td.Cols == 0) return;
            var pt = e.GetPosition(PreviewCanvas);

            var colX = BuildColX();
            var rowY = BuildRowY();

            int clickedRow = -1, clickedCol = -1;
            for (int c = 0; c < _td.Cols; c++)
                if (pt.X >= colX[c] && pt.X < colX[c + 1]) { clickedCol = c; break; }
            for (int r = 0; r < _td.Rows; r++)
                if (pt.Y >= rowY[r] && pt.Y < rowY[r + 1]) { clickedRow = r; break; }

            if (clickedRow < 0 || clickedCol < 0) return;

            // If clicked on a slave cell, redirect to its master.
            var cell = _td.Cells.FirstOrDefault(x => x.Row == clickedRow && x.Col == clickedCol);
            if (cell != null && cell.IsMergedSlave)
            {
                var master = _td.Cells.FirstOrDefault(x =>
                    !x.IsMergedSlave &&
                    x.Row <= clickedRow && x.Row + x.RowSpan > clickedRow &&
                    x.Col <= clickedCol && x.Col + x.ColSpan > clickedCol);
                if (master != null) { clickedRow = master.Row; clickedCol = master.Col; }
            }

            SelectCell(clickedRow, clickedCol);

            // Double-click: focus the text box for immediate typing
            if (e.ClickCount >= 2)
                FocusCellTextBox();
        }

        private void SelectCell(int row, int col)
        {
            _selRow = row;
            _selCol = col;
            DrawPreview();
            ShowCellEditor(row, col);
            FocusCellTextBox();
        }

        private void FocusCellTextBox()
        {
            if (!TxtCellText.IsEnabled) return;
            TxtCellText.Focus();
            TxtCellText.SelectAll();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Keyboard navigation (Excel-like)
        // ─────────────────────────────────────────────────────────────────────

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_td.Rows == 0 || _td.Cols == 0) return;
            if (_selRow < 0 || _selCol < 0) return;

            // Don't intercept keys when typing in non-cell textboxes
            if (e.OriginalSource is TextBox srcBox &&
                srcBox != TxtCellText)
                return;

            bool isCellTextFocused = TxtCellText.IsFocused;

            switch (e.Key)
            {
                case Key.Tab:
                    if (Keyboard.Modifiers == ModifierKeys.Shift)
                        MoveTo(_selRow, _selCol - 1);
                    else
                        MoveTo(_selRow, _selCol + 1);
                    e.Handled = true;
                    break;

                case Key.Enter:
                    MoveTo(_selRow + 1, _selCol);
                    e.Handled = true;
                    break;

                case Key.Up:
                    if (!isCellTextFocused) { MoveTo(_selRow - 1, _selCol); e.Handled = true; }
                    break;

                case Key.Down:
                    if (!isCellTextFocused) { MoveTo(_selRow + 1, _selCol); e.Handled = true; }
                    break;

                case Key.Left:
                    if (!isCellTextFocused) { MoveTo(_selRow, _selCol - 1); e.Handled = true; }
                    break;

                case Key.Right:
                    if (!isCellTextFocused) { MoveTo(_selRow, _selCol + 1); e.Handled = true; }
                    break;

                case Key.Delete:
                    if (!isCellTextFocused)
                    {
                        var cell = _td.GetOrCreate(_selRow, _selCol);
                        if (cell.ContentType == CellContentType.FreeText)
                        {
                            cell.Text = "";
                            _suppressEvents = true;
                            TxtCellText.Text = "";
                            _suppressEvents = false;
                            DrawPreview();
                        }
                        e.Handled = true;
                    }
                    break;

                case Key.Escape:
                    _selRow = -1;
                    _selCol = -1;
                    CellEditorPanel.Visibility = System.Windows.Visibility.Collapsed;
                    DrawPreview();
                    PreviewCanvas.Focus();
                    e.Handled = true;
                    break;

                case Key.F2:
                    FocusCellTextBox();
                    e.Handled = true;
                    break;

                default:
                    // Start typing directly into cell when a printable key is pressed
                    if (!isCellTextFocused && !IsModifierKey(e.Key) &&
                        e.Key != Key.System && Keyboard.Modifiers == ModifierKeys.None)
                    {
                        var activeCell = _td.GetOrCreate(_selRow, _selCol);
                        if (activeCell.ContentType == CellContentType.FreeText &&
                            TxtCellText.IsEnabled)
                        {
                            TxtCellText.Focus();
                            // Let the keypress propagate to the now-focused text box
                        }
                    }
                    break;
            }
        }

        private void MoveTo(int row, int col)
        {
            // Wrap column at row boundaries
            if (col >= _td.Cols) { col = 0; row++; }
            if (col < 0) { col = _td.Cols - 1; row--; }

            if (row < 0 || row >= _td.Rows) return;

            // Skip slave cells (jump to next non-slave)
            var cell = _td.Cells.FirstOrDefault(x => x.Row == row && x.Col == col);
            if (cell != null && cell.IsMergedSlave)
            {
                var master = _td.Cells.FirstOrDefault(x =>
                    !x.IsMergedSlave &&
                    x.Row <= row && x.Row + x.RowSpan > row &&
                    x.Col <= col && x.Col + x.ColSpan > col);
                if (master != null) { row = master.Row; col = master.Col; }
            }

            SelectCell(row, col);
        }

        private static bool IsModifierKey(Key key)
            => key == Key.LeftShift || key == Key.RightShift ||
               key == Key.LeftCtrl  || key == Key.RightCtrl  ||
               key == Key.LeftAlt   || key == Key.RightAlt   ||
               key == Key.LWin      || key == Key.RWin       ||
               key == Key.CapsLock  || key == Key.NumLock;

        // ─────────────────────────────────────────────────────────────────────
        //  Cell editor panel
        // ─────────────────────────────────────────────────────────────────────

        private void ShowCellEditor(int r, int c)
        {
            CellEditorPanel.Visibility = System.Windows.Visibility.Visible;
            TxtCellTitle.Text = $"CELL [{r},{c}]";

            var cell = _td.GetOrCreate(r, c);
            _suppressEvents = true;
            TxtCellText.Text      = cell.ContentType == CellContentType.FreeText ? cell.Text : "";
            TxtCellText.IsEnabled = cell.ContentType == CellContentType.FreeText;
            _suppressEvents = false;

            if (cell.ContentType == CellContentType.LinkedProperty && cell.Link != null)
            {
                LinkInfoPanel.Visibility = System.Windows.Visibility.Visible;
                TxtLinkSummary.Text =
                    $"{cell.Link.EntityKind} · {cell.Link.Property}\n" +
                    $"= {(string.IsNullOrEmpty(cell.Link.CachedValue) ? "(no value)" : cell.Link.CachedValue)}";
            }
            else
            {
                LinkInfoPanel.Visibility = System.Windows.Visibility.Collapsed;
            }
        }

        private void TxtCellText_Changed(object sender, TextChangedEventArgs e)
        {
            if (_suppressEvents || _selRow < 0 || _selCol < 0) return;
            var cell = _td.GetOrCreate(_selRow, _selCol);
            cell.ContentType = CellContentType.FreeText;
            cell.Text = TxtCellText.Text;
            DrawPreview();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Link entity
        // ─────────────────────────────────────────────────────────────────────

        private void BtnLinkEntity_Click(object sender, RoutedEventArgs e)
        {
            if (_selRow < 0 || _selCol < 0) return;

            var ed = _doc.Editor;

            try
            {
                // CRITICAL FIX: Do NOT use this.Hide() / this.Show() for modal WPF windows
                // in AutoCAD. It corrupts the modal message loop and causes fatal crashes
                // (AccessViolations or eInvalidInput) later when creating transactions.
                // Instead, use StartUserInteraction which safely suspends the modal state.
                using (ed.StartUserInteraction(this))
                {
                    var opt = new PromptEntityOptions("\nSelect entity to link to this cell: ")
                    { AllowNone = false };
                    var res = ed.GetEntity(opt);

                    if (res.Status != PromptStatus.OK) return;

                    // Extract available properties.
                    var (kind, opts) = TableDrawerEngine.ExtractProperties(res.ObjectId, _doc.Database);

                    if (opts.Count == 0)
                    {
                        ed.WriteMessage("\n  No readable properties found for this entity.\n");
                        return;
                    }

                    // Display property list in command line.
                    ed.WriteMessage($"\n  Entity type: {kind}");
                    ed.WriteMessage("\n  Available properties:");
                    for (int i = 0; i < opts.Count; i++)
                        ed.WriteMessage($"\n    [{i + 1}] {opts[i].Name} = {opts[i].Value}");

                    var numOpt = new PromptIntegerOptions(
                        $"\nEnter property number [1-{opts.Count}]: ")
                    {
                        AllowNone  = false,
                        LowerLimit = 1,
                        UpperLimit = opts.Count
                    };
                    var numRes = ed.GetInteger(numOpt);
                    if (numRes.Status != PromptStatus.OK) return;

                    var chosen = opts[numRes.Value - 1];

                    // Read the entity's handle safely.
                    string handleStr;
                    using (var tr = _doc.Database.TransactionManager.StartTransaction())
                    {
                        var obj = tr.GetObject(res.ObjectId, OpenMode.ForRead);
                        handleStr = obj.Handle.ToString();
                        tr.Commit();
                    }

                    // Save link.
                    var cell = _td.GetOrCreate(_selRow, _selCol);
                    cell.ContentType = CellContentType.LinkedProperty;
                    cell.Text = "";
                    cell.Link = new LinkedPropertyRef
                    {
                        EntityHandle = handleStr,
                        EntityKind   = kind.ToString(),
                        Property     = chosen.Name,
                        CachedValue  = chosen.Value
                    };

                    ed.WriteMessage(
                        $"\n  Linked: cell [{_selRow},{_selCol}] → {kind}.{chosen.Name} = {chosen.Value}\n");
                }
            }
            finally
            {
                this.Activate();
                if (_selRow >= 0) ShowCellEditor(_selRow, _selCol);
                DrawPreview();
            }
        }

        private void BtnRemoveLink_Click(object sender, RoutedEventArgs e)
        {
            if (_selRow < 0 || _selCol < 0) return;
            var cell = _td.GetOrCreate(_selRow, _selCol);
            cell.ContentType = CellContentType.FreeText;
            cell.Link = null;
            ShowCellEditor(_selRow, _selCol);
            DrawPreview();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Merge / Unmerge
        // ─────────────────────────────────────────────────────────────────────

        private void BtnMergeRight_Click(object sender, RoutedEventArgs e)
        {
            if (_selRow < 0 || _selCol < 0) return;
            var master = _td.GetOrCreate(_selRow, _selCol);
            if (master.IsMergedSlave) return;

            int newSpanC = master.ColSpan + 1;
            if (_selCol + newSpanC > _td.Cols) return;   // out of bounds

            int targetCol = _selCol + master.ColSpan;
            var slave     = _td.GetOrCreate(_selRow, targetCol);
            slave.IsMergedSlave = true;
            slave.ContentType   = CellContentType.FreeText;
            slave.Text = "";
            slave.Link = null;
            master.ColSpan = newSpanC;
            DrawPreview();
        }

        private void BtnMergeDown_Click(object sender, RoutedEventArgs e)
        {
            if (_selRow < 0 || _selCol < 0) return;
            var master = _td.GetOrCreate(_selRow, _selCol);
            if (master.IsMergedSlave) return;

            int newSpanR = master.RowSpan + 1;
            if (_selRow + newSpanR > _td.Rows) return;

            int targetRow = _selRow + master.RowSpan;
            var slave     = _td.GetOrCreate(targetRow, _selCol);
            slave.IsMergedSlave = true;
            slave.ContentType   = CellContentType.FreeText;
            slave.Text = "";
            slave.Link = null;
            master.RowSpan = newSpanR;
            DrawPreview();
        }

        private void BtnUnmerge_Click(object sender, RoutedEventArgs e)
        {
            if (_selRow < 0 || _selCol < 0) return;
            var master = _td.GetOrCreate(_selRow, _selCol);
            if (master.IsMergedSlave) return;

            // Un-mark all slave cells covered by this master.
            for (int r = _selRow; r < _selRow + master.RowSpan; r++)
            {
                for (int c = _selCol; c < _selCol + master.ColSpan; c++)
                {
                    if (r == _selRow && c == _selCol) continue;
                    var slave = _td.Cells.FirstOrDefault(x => x.Row == r && x.Col == c);
                    if (slave != null) slave.IsMergedSlave = false;
                }
            }
            master.RowSpan = 1;
            master.ColSpan = 1;
            DrawPreview();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Table structure — apply size
        // ─────────────────────────────────────────────────────────────────────

        private void BtnApplySize_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtRows.Text, out int rows) || rows < 1 || rows > 50) return;
            if (!int.TryParse(TxtCols.Text, out int cols) || cols < 1 || cols > 50) return;

            // Grow or shrink row heights
            while (_td.RowHeights.Count < rows) _td.RowHeights.Add(8.0);
            while (_td.RowHeights.Count > rows) _td.RowHeights.RemoveAt(_td.RowHeights.Count - 1);

            // Grow or shrink column widths
            while (_td.ColWidths.Count < cols) _td.ColWidths.Add(20.0);
            while (_td.ColWidths.Count > cols) _td.ColWidths.RemoveAt(_td.ColWidths.Count - 1);

            _td.Rows = rows;
            _td.Cols = cols;

            // Trim cells that are now outside bounds
            _td.Cells.RemoveAll(c => c.Row >= rows || c.Col >= cols);

            _selRow = -1;
            _selCol = -1;
            CellEditorPanel.Visibility = System.Windows.Visibility.Collapsed;

            RebuildDimControls();
            DrawPreview();
        }

        private void RebuildDimControls()
        {
            _suppressEvents = true;
            TxtRows.Text = _td.Rows.ToString();
            TxtCols.Text = _td.Cols.ToString();

            var colItems = new List<DimItem>();
            for (int c = 0; c < _td.Cols; c++)
                colItems.Add(new DimItem
                {
                    Index = c,
                    Label = $"C{c + 1}",
                    Value = SafeDim(_td.ColWidths, c, 20.0).ToString("F1")
                });
            IcColWidths.ItemsSource = colItems;

            var rowItems = new List<DimItem>();
            for (int r = 0; r < _td.Rows; r++)
                rowItems.Add(new DimItem
                {
                    Index = r,
                    Label = $"R{r + 1}",
                    Value = SafeDim(_td.RowHeights, r, 8.0).ToString("F1")
                });
            IcRowHeights.ItemsSource = rowItems;

            _suppressEvents = false;
        }

        private void ColWidth_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            if (sender is not TextBox tb) return;
            if (!int.TryParse(tb.Tag?.ToString(), out int idx)) return;
            if (!double.TryParse(tb.Text, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.CurrentCulture, out double val) || val <= 0) return;
            if (idx < _td.ColWidths.Count) _td.ColWidths[idx] = val;
            DrawPreview();
        }

        private void RowHeight_LostFocus(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;
            if (sender is not TextBox tb) return;
            if (!int.TryParse(tb.Tag?.ToString(), out int idx)) return;
            if (!double.TryParse(tb.Text, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.CurrentCulture, out double val) || val <= 0) return;
            if (idx < _td.RowHeights.Count) _td.RowHeights[idx] = val;
            DrawPreview();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Auto-fit column widths
        // ─────────────────────────────────────────────────────────────────────

        private void BtnAutoFitWidths_Click(object sender, RoutedEventArgs e)
        {
            AutoFitColumnWidths();
            RebuildDimControls();
            DrawPreview();
        }

        private void AutoFitColumnWidths()
        {
            // For each column, find the longest text and size the column to fit.
            // Heuristic: width ≈ charCount × charWidth + padding
            // where charWidth ≈ 0.6 × textHeight for most fonts.
            double charWidth = _textHeight * 0.6;
            double padding   = _textHeight * 1.5; // margins on both sides
            double minWidth  = _textHeight * 3.0;  // minimum usable width

            for (int c = 0; c < _td.Cols; c++)
            {
                int maxChars = 0;
                for (int r = 0; r < _td.Rows; r++)
                {
                    var cell = _td.Cells.FirstOrDefault(x => x.Row == r && x.Col == c);
                    if (cell == null || cell.IsMergedSlave) continue;
                    if (cell.ColSpan > 1) continue; // don't size based on merged cells

                    string text = cell.DisplayValue ?? "";
                    // Use the longest single line if multi-line
                    foreach (string line in text.Split('\n'))
                    {
                        if (line.Length > maxChars)
                            maxChars = line.Length;
                    }
                }

                double fitted = maxChars > 0
                    ? maxChars * charWidth + padding
                    : minWidth;
                fitted = Math.Max(minWidth, fitted);

                if (c < _td.ColWidths.Count)
                    _td.ColWidths[c] = Math.Round(fitted, 1);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Save / Load
        // ─────────────────────────────────────────────────────────────────────

        private void RefreshSavedCombo()
        {
            CmbSaved.ItemsSource = TableStorage.LoadAll();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtTableName.Text.Trim();
            if (string.IsNullOrEmpty(name)) name = "Table";
            _td.Name = name;
            TableStorage.Save(_td);
            RefreshSavedCombo();
        }

        private void CmbSaved_Changed(object sender, SelectionChangedEventArgs e)
        {
            // Preview only — Load button confirms.
        }

        private void BtnLoad_Click(object sender, RoutedEventArgs e)
        {
            if (CmbSaved.SelectedItem is not TableDefinition td) return;
            _td = td;
            _selRow = -1; _selCol = -1;
            CellEditorPanel.Visibility = System.Windows.Visibility.Collapsed;
            TxtTableName.Text = td.Name;
            RebuildDimControls();
            DrawPreview();
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (CmbSaved.SelectedItem is not TableDefinition td) return;
            TableStorage.Delete(td.Id);
            RefreshSavedCombo();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Footer actions
        // ─────────────────────────────────────────────────────────────────────

        private void BtnDraw_Click(object sender, RoutedEventArgs e)
        {
            // Stamp user's style / height choices onto the definition
            // so the drawing engine can pick them up.
            _td.TextStyleName = _textStyleName;
            _td.TextHeight    = _textHeight;

            ResultTable  = _td;
            DialogResult = true;
            Close();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────────

        private double[] BuildColX()
        {
            var colX = new double[_td.Cols + 1];
            colX[0] = CanvasPad;
            for (int c = 0; c < _td.Cols; c++)
                colX[c + 1] = colX[c] + SafeDim(_td.ColWidths, c, 20.0) * PxPerFt;
            return colX;
        }

        private double[] BuildRowY()
        {
            var rowY = new double[_td.Rows + 1];
            rowY[0] = CanvasPad;
            for (int r = 0; r < _td.Rows; r++)
                rowY[r + 1] = rowY[r] + SafeDim(_td.RowHeights, r, 8.0) * PxPerFt;
            return rowY;
        }

        private static double SafeDim(List<double> list, int idx, double fallback)
            => idx < list.Count && list[idx] > 0 ? list[idx] : fallback;
    }
}
