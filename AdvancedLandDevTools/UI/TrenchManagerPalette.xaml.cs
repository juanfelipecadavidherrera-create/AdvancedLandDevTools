using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
    public partial class TrenchManagerPalette : UserControl
    {
        private TrenchManagerProject? _project;
        private readonly List<TrenchEntry> _trenches = new();
        private string? _renameTargetId;

        public TrenchManagerPalette()
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
            var proj = TrenchManagerStore.Load(name);
            if (proj == null)
            {
                proj = new TrenchManagerProject { ProjectName = name };
                TrenchManagerStore.Save(proj);
            }
            _project = proj;
            _trenches.Clear();
            _trenches.AddRange(_project.Trenches);
            TxtProjectName.Text = _project.ProjectName;
            RebuildList();
        }

        private void TxtProjectName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                string name = TxtProjectName.Text?.Trim() ?? "";
                if (!string.IsNullOrEmpty(name)) LoadProject(name);
            }
        }

        private void BtnShowProjects_Click(object sender, RoutedEventArgs e)
        {
            var names = TrenchManagerStore.ListProjects();
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
            var result = MessageBox.Show(
                $"Delete project \"{_project.ProjectName}\" and all its saved trenches?",
                "Delete Project", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            TrenchManagerStore.Delete(_project.ProjectName);
            _project = null;
            _trenches.Clear();
            TxtProjectName.Text = "";
            RebuildList();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  ADD TRENCH — select polyline, compute longest segment, store
        // ═════════════════════════════════════════════════════════════════════

        private void BtnAddTrench_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null) { AcadMessage("Load or create a project first."); return; }

            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            while (true)
            {
                var peo = new PromptEntityOptions(
                    "\nSelect a polyline for the trench (ESC to cancel): ");
                peo.SetRejectMessage("\nOnly polylines are accepted. Try again.");
                peo.AddAllowedClass(typeof(Polyline), true);

                var per = ed.GetEntity(peo);
                if (per.Status == PromptStatus.Cancel) return;
                if (per.Status != PromptStatus.OK) continue;

                using (var tr = doc.TransactionManager.StartTransaction())
                {
                    var pline = tr.GetObject(per.ObjectId, OpenMode.ForRead) as Polyline;
                    if (pline == null || pline.NumberOfVertices < 2)
                    {
                        ed.WriteMessage("\nPolyline must have at least 2 vertices. Try again.");
                        tr.Commit();
                        continue;
                    }

                    // Collect vertices
                    var verts = new List<double[]>();
                    for (int i = 0; i < pline.NumberOfVertices; i++)
                    {
                        var pt = pline.GetPoint2dAt(i);
                        verts.Add(new[] { pt.X, pt.Y });
                    }

                    // Compute longest consecutive segment
                    double longest = 0;
                    for (int i = 0; i < pline.NumberOfVertices - 1; i++)
                    {
                        var p1 = pline.GetPoint2dAt(i);
                        var p2 = pline.GetPoint2dAt(i + 1);
                        double d = p1.GetDistanceTo(p2);
                        if (d > longest) longest = d;
                    }
                    // Include closing segment if polyline is closed
                    if (pline.Closed && pline.NumberOfVertices > 1)
                    {
                        var p1 = pline.GetPoint2dAt(pline.NumberOfVertices - 1);
                        var p2 = pline.GetPoint2dAt(0);
                        double d = p1.GetDistanceTo(p2);
                        if (d > longest) longest = d;
                    }

                    int nextNum = _trenches.Count + 1;
                    var entry = new TrenchEntry
                    {
                        Name = $"Trench {nextNum}",
                        LongestSegmentFt = longest,
                        Vertices = verts,
                        Layer = pline.Layer
                    };

                    _project.Trenches.Add(entry);
                    _trenches.Add(entry);
                    SaveProject();
                    RebuildList();

                    ed.WriteMessage($"\nAdded: {entry.Name} — longest segment: {longest:F4} ft");
                    tr.Commit();
                    return;
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  ZOOM TO TRENCH — zoom to bounding box of stored vertices
        // ═════════════════════════════════════════════════════════════════════

        private void ZoomToTrench(TrenchEntry trench)
        {
            if (trench.Vertices.Count < 2) { AcadMessage("No geometry stored for this trench."); return; }

            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            double minX = trench.Vertices.Min(v => v[0]);
            double maxX = trench.Vertices.Max(v => v[0]);
            double minY = trench.Vertices.Min(v => v[1]);
            double maxY = trench.Vertices.Max(v => v[1]);

            double pad = Math.Max(
                (maxX - minX) * 0.20,
                (maxY - minY) * 0.20);
            pad = Math.Max(pad, 5.0);

            string cmd = $"_.ZOOM _W {minX - pad},{minY - pad} {maxX + pad},{maxY + pad} ";

            using (doc.LockDocument())
            {
                doc.SendStringToExecute(cmd, true, false, true);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  RENAME
        // ═════════════════════════════════════════════════════════════════════

        private void OpenRename(string id)
        {
            var t = _trenches.FirstOrDefault(x => x.Id == id);
            if (t == null) return;
            _renameTargetId = id;
            TxtRenameOldName.Text = $"Current: {t.Name}";
            TxtRenameName.Text = t.Name;
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
            var t = _trenches.FirstOrDefault(x => x.Id == _renameTargetId);
            if (t == null) return;
            t.Name = newName;
            SaveProject();
            RebuildList();
        }

        private void BtnCancelRename_Click(object sender, RoutedEventArgs e)
        {
            RenamePopup.IsOpen = false;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  REMOVE
        // ═════════════════════════════════════════════════════════════════════

        private void RemoveTrench(string id)
        {
            var t = _trenches.FirstOrDefault(x => x.Id == id);
            if (t == null) return;
            var result = MessageBox.Show(
                $"Remove \"{t.Name}\" from the list?",
                "Remove Trench", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
            _project!.Trenches.RemoveAll(x => x.Id == id);
            _trenches.RemoveAll(x => x.Id == id);
            SaveProject();
            RebuildList();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  BUILD LIST UI
        // ═════════════════════════════════════════════════════════════════════

        private void RebuildList()
        {
            TrenchListPanel.Children.Clear();

            if (_project == null || _trenches.Count == 0)
            {
                TxtSummary.Text = _project == null
                    ? "No project loaded"
                    : "No trenches — click Add Trench";
                return;
            }

            double total = _trenches.Sum(t => t.LongestSegmentFt);
            TxtSummary.Text = $"{_trenches.Count} trench(es) — Total: {total:F2} ft";

            foreach (var trench in _trenches)
                TrenchListPanel.Children.Add(BuildTrenchCard(trench));
        }

        private Border BuildTrenchCard(TrenchEntry trench)
        {
            var card = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2D2D2D")),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#454545")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 4)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ── Row 0: Name + action buttons ──────────────────────────────
            var row0 = new DockPanel();

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(4, 0, 0, 0)
            };
            DockPanel.SetDock(btnPanel, Dock.Right);

            // Rename
            btnPanel.Children.Add(MakeIconBtn("\uE8AC", "Rename", trench.Id, (s, e) =>
            {
                string id = (string)((Button)s).Tag;
                OpenRename(id);
            }));

            // Zoom to
            btnPanel.Children.Add(MakeIconBtn("\uE71E", "Zoom to trench", trench.Id, (s, e) =>
            {
                string id = (string)((Button)s).Tag;
                var t = _trenches.FirstOrDefault(x => x.Id == id);
                if (t != null) ZoomToTrench(t);
            }));

            // Delete
            btnPanel.Children.Add(MakeIconBtn("\uE74D", "Remove", trench.Id, (s, e) =>
            {
                string id = (string)((Button)s).Tag;
                RemoveTrench(id);
            }, isDelete: true));

            row0.Children.Add(btnPanel);
            row0.Children.Add(new TextBlock
            {
                Text = trench.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetRow(row0, 0);
            grid.Children.Add(row0);

            // ── Row 1: Longest segment length ─────────────────────────────
            var row1 = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            row1.Children.Add(new TextBlock
            {
                Text = $"{trench.LongestSegmentFt:F4} ft",
                FontSize = 12,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF8A65")),
                VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetRow(row1, 1);
            grid.Children.Add(row1);

            card.Child = grid;
            return card;
        }

        private Button MakeIconBtn(string icon, string tooltip, string id,
            RoutedEventHandler click, bool isDelete = false)
        {
            var btn = new Button
            {
                Content = icon,
                Tag = id,
                ToolTip = tooltip,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Width = 26,
                Height = 26,
                Cursor = Cursors.Hand,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9E9E9E"))
            };

            var hoverBg = isDelete ? "#33FF6B6B" : "#383838";
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

            var trigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            trigger.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush((Color)ColorConverter.ConvertFromString(hoverBg)), "Bd"));
            trigger.Setters.Add(new Setter(Button.ForegroundProperty,
                new SolidColorBrush((Color)ColorConverter.ConvertFromString(hoverFg))));
            template.Triggers.Add(trigger);

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
            _project.Trenches = _trenches.ToList();
            TrenchManagerStore.Save(_project);
        }

        private static void AcadMessage(string msg)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            doc?.Editor.WriteMessage($"\n[Trench Manager] {msg}\n");
        }
    }
}
