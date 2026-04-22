using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AdvancedLandDevTools.Engine;
using AdvancedLandDevTools.Models;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AdvancedLandDevTools.UI
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Value converter: non-empty string → Visible
    // ─────────────────────────────────────────────────────────────────────────
    public class NonEmptyToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) =>
            string.IsNullOrWhiteSpace(value as string)
                ? System.Windows.Visibility.Collapsed
                : System.Windows.Visibility.Visible;
        public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
            throw new NotSupportedException();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  AreaManagerPalette — WPF UserControl hosted in a PaletteSet
    // ─────────────────────────────────────────────────────────────────────────
    public partial class AreaManagerPalette : UserControl
    {
        private AreaManagerProject? _project;
        private string? _activeSubProjectId; // null = root scope
        private bool _suppressSubProjectEvents;

        // ── Scope helpers ──
        private bool IsRootScope => _activeSubProjectId == null;
        private AreaSubProject? ActiveSubProject =>
            IsRootScope ? null : _project?.FindSubProjectById(_activeSubProjectId!);
        private List<AreaEntry> ActiveAreas =>
            IsRootScope ? (_project?.Areas ?? new List<AreaEntry>()) : (ActiveSubProject?.Areas ?? new List<AreaEntry>());
        private List<string> ActiveCategories =>
            IsRootScope ? (_project?.Categories ?? new List<string>()) : (ActiveSubProject?.Categories ?? new List<string>());

        public AreaManagerPalette()
        {
            InitializeComponent();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  PROJECT MANAGEMENT
        // ═════════════════════════════════════════════════════════════════════

        private void LoadProject(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            var proj = AreaManagerStore.Load(name);
            if (proj == null)
            {
                proj = new AreaManagerProject { ProjectName = name };
                AreaManagerStore.Save(proj);
            }
            _project = proj;
            _activeSubProjectId = null;
            TxtProjectName.Text = _project.ProjectName;
            BuildSubProjectSelector();
            RebuildGroupedList();
        }

        private void TxtProjectName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                string name = TxtProjectName.Text?.Trim() ?? "";
                if (!string.IsNullOrEmpty(name))
                    LoadProject(name);
            }
        }

        private void BtnShowProjects_Click(object sender, RoutedEventArgs e)
        {
            var names = AreaManagerStore.ListProjects();
            if (names.Count == 0) { AcadMessage("No saved projects yet."); return; }
            ProjectList.ItemsSource = names;
            ProjectPopup.IsOpen = true;
        }

        private void ProjectItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is string name)
            {
                ProjectPopup.IsOpen = false;
                LoadProject(name);
            }
        }

        private void BtnLoadProject_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtProjectName.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(name)) { AcadMessage("Enter a project name first."); return; }
            LoadProject(name);
        }

        private void BtnDeleteProject_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null) return;
            var result = System.Windows.MessageBox.Show(
                $"Delete project \"{_project.ProjectName}\" and all its saved areas?",
                "Delete Project", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            AreaManagerStore.Delete(_project.ProjectName);
            _project = null;
            _activeSubProjectId = null;
            TxtProjectName.Text = "";
            BuildSubProjectSelector();
            RebuildGroupedList();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  SUBPROJECT MANAGEMENT
        // ═════════════════════════════════════════════════════════════════════

        private void BuildSubProjectSelector()
        {
            _suppressSubProjectEvents = true;
            CboSubProject.Items.Clear();
            CboSubProject.Items.Add("<Root>");
            if (_project != null)
                foreach (var sp in _project.SubProjects)
                    CboSubProject.Items.Add(sp.Name);
            // Select current
            if (_activeSubProjectId == null)
                CboSubProject.SelectedIndex = 0;
            else
            {
                var sp = _project?.FindSubProjectById(_activeSubProjectId);
                CboSubProject.SelectedItem = sp?.Name ?? "<Root>";
                if (CboSubProject.SelectedIndex < 0) CboSubProject.SelectedIndex = 0;
            }
            _suppressSubProjectEvents = false;
        }

        private void CboSubProject_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSubProjectEvents || _project == null) return;
            string? selected = CboSubProject.SelectedItem as string;
            if (selected == null || selected == "<Root>")
                _activeSubProjectId = null;
            else
            {
                var sp = _project.FindSubProjectByName(selected);
                _activeSubProjectId = sp?.Id;
            }
            RebuildGroupedList();
            UpdateSummary();
        }

        private void BtnNewSubProject_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null) { AcadMessage("Load or create a project first."); return; }
            TxtSubProjectName.Text = "";
            SubProjectPopup.IsOpen = true;
            TxtSubProjectName.Focus();
        }

        private void BtnConfirmSubProject_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtSubProjectName.Text?.Trim() ?? "";
            SubProjectPopup.IsOpen = false;
            if (string.IsNullOrEmpty(name) || _project == null) return;
            if (_project.FindSubProjectByName(name) != null)
            { AcadMessage($"Subproject \"{name}\" already exists."); return; }
            var sp = new AreaSubProject { Name = name };
            _project.SubProjects.Add(sp);
            SaveProject();
            _activeSubProjectId = sp.Id;
            BuildSubProjectSelector();
            RebuildGroupedList();
            UpdateSummary();
        }

        private void BtnCancelSubProject_Click(object sender, RoutedEventArgs e)
        {
            SubProjectPopup.IsOpen = false;
        }

        private void TxtSubProjectName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnConfirmSubProject_Click(sender, e);
            else if (e.Key == Key.Escape) SubProjectPopup.IsOpen = false;
        }

        private void BtnRenameSubProject_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null || IsRootScope) return;
            var sp = ActiveSubProject;
            if (sp == null) return;
            TxtSubProjectRename.Text = sp.Name;
            SubProjectRenamePopup.IsOpen = true;
            TxtSubProjectRename.Focus();
            TxtSubProjectRename.SelectAll();
        }

        private void BtnConfirmSubProjectRename_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtSubProjectRename.Text?.Trim() ?? "";
            SubProjectRenamePopup.IsOpen = false;
            if (string.IsNullOrEmpty(name) || _project == null) return;
            var sp = ActiveSubProject;
            if (sp == null) return;
            sp.Name = name;
            sp.ModifiedUtc = DateTime.UtcNow;
            SaveProject();
            BuildSubProjectSelector();
        }

        private void BtnCancelSubProjectRename_Click(object sender, RoutedEventArgs e)
        {
            SubProjectRenamePopup.IsOpen = false;
        }

        private void TxtSubProjectRename_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnConfirmSubProjectRename_Click(sender, e);
            else if (e.Key == Key.Escape) SubProjectRenamePopup.IsOpen = false;
        }

        private void BtnDeleteSubProject_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null || IsRootScope) return;
            var sp = ActiveSubProject;
            if (sp == null) return;
            var res = System.Windows.MessageBox.Show(
                $"Delete subproject \"{sp.Name}\" and all its areas?",
                "Delete SubProject", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;
            _project.SubProjects.RemoveAll(s => s.Id == sp.Id);
            _activeSubProjectId = null;
            SaveProject();
            BuildSubProjectSelector();
            RebuildGroupedList();
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            if (_project == null)
            {
                TxtSummary.Text = "No project loaded";
                TxtGrandTotal.Visibility = System.Windows.Visibility.Collapsed;
                return;
            }
            var active = ActiveAreas;
            double scopeTotal = active.Sum(a => a.AreaSqFt);
            string scopeName = IsRootScope ? "Root" : (ActiveSubProject?.Name ?? "Subproject");
            TxtSummary.Text = $"{scopeName}: {active.Count} area(s) — {scopeTotal:N2} sq ft";

            double grandTotal = _project.AllAreas.Sum(a => a.AreaSqFt);
            int grandCount = _project.AllAreas.Count();
            TxtGrandTotal.Text = $"Project total: {grandCount} area(s) — {grandTotal:N2} sq ft";
            TxtGrandTotal.Visibility = System.Windows.Visibility.Visible;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  ADD AREA
        // ═════════════════════════════════════════════════════════════════════

        private void BtnAddArea_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null) { AcadMessage("Load or create a project first."); return; }

            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            while (true)
            {
                var peo = new PromptEntityOptions(
                    "\nSelect a hatch or closed polyline (ESC to cancel): ");
                peo.SetRejectMessage("\nNot a valid hatch or closed polyline.");
                peo.AddAllowedClass(typeof(Hatch), true);
                peo.AddAllowedClass(typeof(Polyline), true);
                peo.AddAllowedClass(typeof(Polyline2d), true);
                peo.AddAllowedClass(typeof(Polyline3d), true);
                peo.AddAllowedClass(typeof(Circle), true);

                PromptEntityResult per = ed.GetEntity(peo);
                if (per.Status == PromptStatus.Cancel) return;
                if (per.Status != PromptStatus.OK) continue;

                using (var tr = doc.TransactionManager.StartTransaction())
                {
                    Entity ent = (Entity)tr.GetObject(per.ObjectId, OpenMode.ForRead);
                    AreaEntry? entry = ExtractAreaEntry(ent);

                    if (entry == null)
                    {
                        ed.WriteMessage("\nNot a valid closed boundary or hatch. Try again.");
                        tr.Commit();
                        continue;
                    }

                    ActiveAreas.Add(entry);
                    if (!IsRootScope && ActiveSubProject != null)
                        ActiveSubProject.ModifiedUtc = DateTime.UtcNow;
                    SaveProject();
                    RebuildGroupedList();

                    ed.WriteMessage($"\nAdded: {entry.Name} — {entry.AreaSqFt:N2} sq ft");
                    tr.Commit();
                    return;
                }
            }
        }

        private AreaEntry? ExtractAreaEntry(Entity ent)
        {
            var entry = new AreaEntry
            {
                Layer = ent.Layer,
                ColorIndex = ent.ColorIndex
            };

            if (ent is Hatch hatch)
            {
                if (hatch.NumberOfLoops == 0) return null;
                entry.IsHatch = true;
                entry.AreaSqFt = Math.Abs(hatch.Area);
                entry.HatchPattern = hatch.PatternName ?? "SOLID";
                entry.HatchScale = hatch.PatternScale;
                entry.Name = $"Hatch ({entry.HatchPattern})";

                for (int i = 0; i < hatch.NumberOfLoops; i++)
                {
                    var loop = hatch.GetLoopAt(i);
                    var pts = new List<double[]>();

                    if (loop.Polyline != null && loop.Polyline.Count > 0)
                    {
                        for (int j = 0; j < loop.Polyline.Count; j++)
                        {
                            var bv = loop.Polyline[j];
                            pts.Add(new[] { bv.Vertex.X, bv.Vertex.Y });
                        }
                    }
                    else if (loop.Curves != null)
                    {
                        foreach (Curve2d curve in loop.Curves)
                        {
                            var interval = curve.GetInterval();
                            int samples = 20;
                            double step = (interval.UpperBound - interval.LowerBound) / samples;
                            for (int s = 0; s <= samples; s++)
                            {
                                var pt = curve.EvaluatePoint(interval.LowerBound + s * step);
                                pts.Add(new[] { pt.X, pt.Y });
                            }
                        }
                    }

                    if (pts.Count > 0)
                        entry.BoundaryLoops.Add(pts);
                }
                return entry;
            }

            if (ent is Polyline pline)
            {
                if (!pline.Closed) return null;
                entry.IsHatch = false;
                entry.AreaSqFt = Math.Abs(pline.Area);
                entry.Name = $"Boundary ({ent.Layer})";
                entry.HatchPattern = "SOLID";

                var pts = new List<double[]>();
                for (int i = 0; i < pline.NumberOfVertices; i++)
                {
                    var pt = pline.GetPoint2dAt(i);
                    pts.Add(new[] { pt.X, pt.Y });
                }
                if (pts.Count > 0) entry.BoundaryLoops.Add(pts);
                return entry;
            }

            if (ent is Circle circle)
            {
                entry.IsHatch = false;
                entry.AreaSqFt = Math.Abs(circle.Area);
                entry.Name = $"Circle ({ent.Layer})";
                entry.HatchPattern = "SOLID";

                var pts = new List<double[]>();
                for (int i = 0; i < 64; i++)
                {
                    double angle = 2 * Math.PI * i / 64;
                    pts.Add(new[] {
                        circle.Center.X + circle.Radius * Math.Cos(angle),
                        circle.Center.Y + circle.Radius * Math.Sin(angle)
                    });
                }
                entry.BoundaryLoops.Add(pts);
                return entry;
            }

            return null;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  CATEGORY — popup dialog instead of command line
        // ═════════════════════════════════════════════════════════════════════

        private void BtnAddCategory_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null) { AcadMessage("Load or create a project first."); return; }
            TxtCategoryName.Text = "";
            CategoryPopup.IsOpen = true;
            TxtCategoryName.Focus();
        }

        private void TxtCategoryName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnConfirmCategory_Click(sender, e);
            else if (e.Key == Key.Escape) CategoryPopup.IsOpen = false;
        }

        private void BtnConfirmCategory_Click(object sender, RoutedEventArgs e)
        {
            string cat = TxtCategoryName.Text?.Trim() ?? "";
            CategoryPopup.IsOpen = false;
            if (string.IsNullOrEmpty(cat) || _project == null) return;

            var cats = ActiveCategories;
            if (cats.Contains(cat))
            {
                AcadMessage($"Category \"{cat}\" already exists.");
                return;
            }

            cats.Add(cat);
            SaveProject();
            RebuildGroupedList();
            AcadMessage($"Category added: {cat}");
        }

        private void BtnCancelCategory_Click(object sender, RoutedEventArgs e)
        {
            CategoryPopup.IsOpen = false;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  RENAME — popup dialog
        // ═════════════════════════════════════════════════════════════════════

        private string? _renameTargetId;

        private void BtnRename_Click(object sender, RoutedEventArgs e)
        {
            string id = (string)((Button)sender).Tag;
            var area = ActiveAreas.FirstOrDefault(a => a.Id == id);
            if (area == null) return;

            _renameTargetId = id;
            TxtRenameOldName.Text = $"Current: {area.Name}";
            TxtRenameName.Text = area.Name;
            RenamePopup.IsOpen = true;
            TxtRenameName.Focus();
            TxtRenameName.SelectAll();
        }

        private void TxtRenameName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnConfirmRename_Click(sender, e);
            else if (e.Key == Key.Escape) RenamePopup.IsOpen = false;
        }

        private void BtnConfirmRename_Click(object sender, RoutedEventArgs e)
        {
            string newName = TxtRenameName.Text?.Trim() ?? "";
            RenamePopup.IsOpen = false;
            if (string.IsNullOrEmpty(newName) || _renameTargetId == null) return;

            var area = ActiveAreas.FirstOrDefault(a => a.Id == _renameTargetId);
            if (area == null) return;

            area.Name = newName;
            SaveProject();
            RebuildGroupedList();
        }

        private void BtnCancelRename_Click(object sender, RoutedEventArgs e)
        {
            RenamePopup.IsOpen = false;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  CATEGORY CHANGE — dropdown per area card
        // ═════════════════════════════════════════════════════════════════════

        private void CatDropdown_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ComboBox cmb) return;
            string id = cmb.Tag as string ?? "";
            var area = ActiveAreas.FirstOrDefault(a => a.Id == id);
            if (area == null) return;

            string selected = cmb.SelectedItem as string ?? "";
            // "(None)" maps to empty
            string newCat = selected == "(None)" ? "" : selected;
            if (area.Category == newCat) return;

            area.Category = newCat;
            SaveProject();
            // Defer rebuild to avoid modifying visual tree during event
            Dispatcher.BeginInvoke(new Action(RebuildGroupedList));
        }

        // ═════════════════════════════════════════════════════════════════════
        //  REDRAW
        // ═════════════════════════════════════════════════════════════════════

        private void BtnRedraw_Click(object sender, RoutedEventArgs e)
        {
            string id = (string)((Button)sender).Tag;
            var area = ActiveAreas.FirstOrDefault(a => a.Id == id);
            if (area == null || area.BoundaryLoops.Count == 0) return;

            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            using (var docLock = doc.LockDocument())
            using (var tr = doc.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var firstLoop = area.BoundaryLoops[0];
                if (firstLoop.Count < 3) { tr.Commit(); return; }

                var pline = new Polyline();
                for (int i = 0; i < firstLoop.Count; i++)
                    pline.AddVertexAt(i, new Point2d(firstLoop[i][0], firstLoop[i][1]), 0, 0, 0);
                pline.Closed = true;

                EnsureLayer(tr, doc.Database, area.Layer);
                pline.Layer = area.Layer;

                ms.AppendEntity(pline);
                tr.AddNewlyCreatedDBObject(pline, true);

                var hatch = new Hatch();
                hatch.Layer = area.Layer;
                hatch.SetHatchPattern(HatchPatternType.PreDefined,
                    string.IsNullOrEmpty(area.HatchPattern) ? "SOLID" : area.HatchPattern);
                hatch.PatternScale = area.HatchScale > 0 ? area.HatchScale : 1.0;
                hatch.ColorIndex = area.ColorIndex;

                ms.AppendEntity(hatch);
                tr.AddNewlyCreatedDBObject(hatch, true);

                var ids = new ObjectIdCollection { pline.ObjectId };
                hatch.Associative = true;
                hatch.AppendLoop(HatchLoopTypes.Outermost, ids);
                hatch.EvaluateHatch(true);

                var ext = pline.GeometricExtents;
                double pad = Math.Max(
                    (ext.MaxPoint.X - ext.MinPoint.X) * 0.15,
                    (ext.MaxPoint.Y - ext.MinPoint.Y) * 0.15);
                pad = Math.Max(pad, 10);
                var min = new Point3d(ext.MinPoint.X - pad, ext.MinPoint.Y - pad, 0);
                var max = new Point3d(ext.MaxPoint.X + pad, ext.MaxPoint.Y + pad, 0);

                doc.Editor.WriteMessage($"\nRedrawn: {area.Name}");
                tr.Commit();

                string cmd = $"_.ZOOM _W {min.X},{min.Y} {max.X},{max.Y} ";
                doc.SendStringToExecute(cmd, true, false, true);
            }
        }

        private void EnsureLayer(Transaction tr, Database db, string layerName)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt.Has(layerName))
            {
                lt.UpgradeOpen();
                var lr = new LayerTableRecord { Name = layerName };
                lt.Add(lr);
                tr.AddNewlyCreatedDBObject(lr, true);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  REMOVE AREA
        // ═════════════════════════════════════════════════════════════════════

        private void BtnRemoveArea_Click(object sender, RoutedEventArgs e)
        {
            string id = (string)((Button)sender).Tag;
            var area = ActiveAreas.FirstOrDefault(a => a.Id == id);
            if (area == null) return;

            var result = System.Windows.MessageBox.Show(
                $"Remove \"{area.Name}\" from the area list?",
                "Remove Area", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            ActiveAreas.RemoveAll(a => a.Id == id);
            if (!IsRootScope && ActiveSubProject != null)
                ActiveSubProject.ModifiedUtc = DateTime.UtcNow;
            SaveProject();
            RebuildGroupedList();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  EXPORT TO EXCEL (.xlsx)
        // ═════════════════════════════════════════════════════════════════════

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null || _project.Areas.Count == 0)
            {
                AcadMessage("No areas to export."); return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export Area Report",
                Filter = "Excel files (*.xlsx)|*.xlsx",
                FileName = $"{_project.ProjectName} - Area Report.xlsx",
                DefaultExt = ".xlsx"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                ExcelExporter.Export(dlg.FileName, _project);
                AcadMessage($"Report exported to: {dlg.FileName}");
            }
            catch (Exception ex) { AcadMessage($"Export failed: {ex.Message}"); }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  BUILD GROUPED LIST UI
        //  Renders category headers with totals + area cards with dropdowns
        // ═════════════════════════════════════════════════════════════════════

        private void RebuildGroupedList()
        {
            GroupedAreaPanel.Children.Clear();

            var areas = ActiveAreas;

            if (_project == null || areas.Count == 0)
            {
                UpdateSummary();
                return;
            }

            // Build category options for dropdowns
            var catOptions = new List<string> { "(None)" };
            catOptions.AddRange(ActiveCategories);

            // Group areas by category
            var groups = areas
                .GroupBy(a => string.IsNullOrEmpty(a.Category) ? "" : a.Category)
                .OrderBy(g => g.Key == "" ? "zzz" : g.Key); // uncategorized last

            foreach (var group in groups)
            {
                string catName = string.IsNullOrEmpty(group.Key) ? "Uncategorized" : group.Key;
                double catTotal = group.Sum(a => a.AreaSqFt);

                // ── Category Header ──
                var header = new Border
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#252526")),
                    BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#454545")),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(8, 6, 8, 6),
                    Margin = new Thickness(0, 4, 0, 2),
                    CornerRadius = new CornerRadius(6, 6, 0, 0)
                };

                var headerPanel = new DockPanel();

                var totalText = new TextBlock
                {
                    Text = $"{catTotal:N2} sq ft",
                    FontSize = 11,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#60CDFF")),
                    VerticalAlignment = VerticalAlignment.Center
                };
                DockPanel.SetDock(totalText, Dock.Right);
                headerPanel.Children.Add(totalText);

                var catNameText = new TextBlock
                {
                    Text = catName,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFB74D")),
                    VerticalAlignment = VerticalAlignment.Center
                };
                headerPanel.Children.Add(catNameText);

                header.Child = headerPanel;
                GroupedAreaPanel.Children.Add(header);

                // ── Area Cards in this group ──
                foreach (var area in group)
                {
                    GroupedAreaPanel.Children.Add(BuildAreaCard(area, catOptions));
                }
            }

            UpdateSummary();
        }

        private Border BuildAreaCard(AreaEntry area, List<string> catOptions)
        {
            var card = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D2D")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#454545")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 2)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ── Row 0: Name + buttons ──
            var row0 = new DockPanel();

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
            DockPanel.SetDock(btnPanel, Dock.Right);

            btnPanel.Children.Add(MakeIconBtn("\uE8AC", "Rename", area.Id, BtnRename_Click));
            btnPanel.Children.Add(MakeIconBtn("\uE8B3", "Redraw & zoom", area.Id, BtnRedraw_Click));
            btnPanel.Children.Add(MakeIconBtn("\uE74D", "Remove", area.Id, BtnRemoveArea_Click, true));

            row0.Children.Add(btnPanel);
            row0.Children.Add(new TextBlock
            {
                Text = area.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetRow(row0, 0);
            grid.Children.Add(row0);

            // ── Row 1: Area value + category dropdown ──
            var row1 = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };

            // Category dropdown docked right — apply CatDropdown style for dark theme
            var catCombo = new ComboBox
            {
                Tag = area.Id,
                MinWidth = 90,
                Cursor = Cursors.Hand
            };
            if (this.Resources.Contains("CatDropdown"))
                catCombo.Style = (Style)this.Resources["CatDropdown"];

            foreach (string opt in catOptions)
                catCombo.Items.Add(opt);

            // Select current category
            string current = string.IsNullOrEmpty(area.Category) ? "(None)" : area.Category;
            catCombo.SelectedItem = catOptions.Contains(current) ? current : "(None)";

            catCombo.SelectionChanged += CatDropdown_SelectionChanged;
            DockPanel.SetDock(catCombo, Dock.Right);
            row1.Children.Add(catCombo);

            row1.Children.Add(new TextBlock
            {
                Text = $"{area.AreaSqFt:N2} sq ft",
                FontSize = 11,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#60CDFF")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });

            Grid.SetRow(row1, 1);
            grid.Children.Add(row1);

            card.Child = grid;
            return card;
        }

        private Button MakeIconBtn(string icon, string tooltip, string areaId,
            RoutedEventHandler click, bool isDelete = false)
        {
            var btn = new Button
            {
                Content = icon,
                Tag = areaId,
                ToolTip = tooltip,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Width = 26,
                Height = 26,
                Cursor = Cursors.Hand,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9E9E9E"))
            };

            // Simple transparent-bg template with hover
            var hoverColor = isDelete ? "#33FF6B6B" : "#383838";
            var hoverFg = isDelete ? "#FF6B6B" : "#FFFFFF";

            var template = new ControlTemplate(typeof(Button));
            var bdFactory = new FrameworkElementFactory(typeof(Border));
            bdFactory.Name = "Bd";
            bdFactory.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bdFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));

            var cpFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            cpFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cpFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bdFactory.AppendChild(cpFactory);

            template.VisualTree = bdFactory;

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush((Color)ColorConverter.ConvertFromString(hoverColor)), "Bd"));
            hoverTrigger.Setters.Add(new Setter(Button.ForegroundProperty,
                new SolidColorBrush((Color)ColorConverter.ConvertFromString(hoverFg))));
            template.Triggers.Add(hoverTrigger);

            btn.Template = template;
            btn.Click += click;
            return btn;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  HELPERS
        // ═════════════════════════════════════════════════════════════════════

        private void SaveProject()
        {
            if (_project == null) return;
            _project.ModifiedUtc = DateTime.UtcNow;
            if (!IsRootScope && ActiveSubProject != null)
                ActiveSubProject.ModifiedUtc = DateTime.UtcNow;
            AreaManagerStore.Save(_project);
        }

        private static void AcadMessage(string msg)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            doc?.Editor.WriteMessage($"\n[Area Manager] {msg}\n");
        }
    }
}
