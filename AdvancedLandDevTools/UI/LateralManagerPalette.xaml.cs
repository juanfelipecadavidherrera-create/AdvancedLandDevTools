using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using AdvancedLandDevTools.Engine;
using AdvancedLandDevTools.Models;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AdvancedLandDevTools.UI
{
    public partial class LateralManagerPalette : UserControl
    {
        private LateralManagerProject? _project;
        private readonly List<LateralEntry> _laterals = new();

        public LateralManagerPalette()
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
            var proj = LateralManagerStore.Load(name);
            if (proj == null)
            {
                proj = new LateralManagerProject { ProjectName = name };
                LateralManagerStore.Save(proj);
            }
            _project = proj;
            _laterals.Clear();
            _laterals.AddRange(_project.Laterals);
            TxtProjectName.Text = _project.ProjectName;
            RebuildList();
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
            var names = LateralManagerStore.ListProjects();
            if (names.Count == 0) { AcadMessage("No saved lateral projects yet."); return; }
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
                $"Delete project \"{_project.ProjectName}\" and all its saved laterals?",
                "Delete Project", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            LateralManagerStore.Delete(_project.ProjectName);
            _project = null;
            _laterals.Clear();
            TxtProjectName.Text = "";
            RebuildList();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  ACTIONS
        // ═════════════════════════════════════════════════════════════════════

        private void BtnAddCrossing_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null) { AcadMessage("Load or create a project first."); return; }

            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            // Hide the palette temporarily if needed, though dockable usually stays out of way.
            // Using Application.MainWindow focus might be required.
            
            var entry = LateralManagerEngine.ExtractLateralCrossing(doc);
            if (entry != null)
            {
                _project.Laterals.Add(entry);
                _laterals.Add(entry);
                SaveProject();
                RebuildList();
                AcadMessage($"Added: {entry.Name} at Station {entry.Station:N2}");
            }
        }

        private void BtnProjectCrossings_Click(object sender, RoutedEventArgs e)
        {
            if (_project == null || _laterals.Count == 0)
            {
                AcadMessage("Load a project with saved laterals first."); 
                return; 
            }

            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            LateralManagerEngine.ProjectLaterals(doc, _project);
        }

        private void BtnZoom_Click(object sender, RoutedEventArgs e)
        {
            string id = (string)((Button)sender).Tag;
            var lat = _laterals.FirstOrDefault(l => l.Id == id);
            if (lat == null) return;

            Document doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            if (doc.Name != lat.SourceDwgName)
            {
                AcadMessage($"This lateral was saved in drawing: {System.IO.Path.GetFileName(lat.SourceDwgName)}");
                return;
            }

            LateralManagerEngine.ZoomToEllipse(doc, lat.EllipseHandle);
        }

        private void BtnRemove_Click(object sender, RoutedEventArgs e)
        {
            string id = (string)((Button)sender).Tag;
            var lat = _laterals.FirstOrDefault(l => l.Id == id);
            if (lat == null) return;

            var result = System.Windows.MessageBox.Show(
                $"Remove \"{lat.Name}\" from the list?",
                "Remove Lateral", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            _project!.Laterals.RemoveAll(l => l.Id == id);
            _laterals.RemoveAll(l => l.Id == id);
            SaveProject();
            RebuildList();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  RENAME
        // ═════════════════════════════════════════════════════════════════════

        private string? _renameTargetId;

        private void BtnRename_Click(object sender, RoutedEventArgs e)
        {
            string id = (string)((Button)sender).Tag;
            var lat = _laterals.FirstOrDefault(l => l.Id == id);
            if (lat == null) return;

            _renameTargetId = id;
            TxtRenameOldName.Text = $"Current: {lat.Name}";
            TxtRenameName.Text = lat.Name;
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

            var lat = _laterals.FirstOrDefault(l => l.Id == _renameTargetId);
            if (lat == null) return;

            lat.Name = newName;
            SaveProject();
            RebuildList();
        }

        private void BtnCancelRename_Click(object sender, RoutedEventArgs e)
        {
            RenamePopup.IsOpen = false;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  UI BUILDING
        // ═════════════════════════════════════════════════════════════════════

        private void RebuildList()
        {
            LateralListPanel.Children.Clear();

            if (_project == null || _laterals.Count == 0)
            {
                TxtSummary.Text = _project == null ? "No project loaded" : "No laterals — click Add Crossing Info";
                return;
            }

            TxtSummary.Text = $"{_laterals.Count} lateral(s) stored.";

            foreach (var lat in _laterals)
            {
                LateralListPanel.Children.Add(BuildCard(lat));
            }
        }

        private Border BuildCard(LateralEntry lat)
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

            // Row 0: Name + Buttons
            var row0 = new DockPanel();

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 0) };
            DockPanel.SetDock(btnPanel, Dock.Right);

            btnPanel.Children.Add(MakeIconBtn("\uE8AC", "Rename", lat.Id, BtnRename_Click));
            btnPanel.Children.Add(MakeIconBtn("\uE8A3", "Zoom to Original", lat.Id, BtnZoom_Click));
            btnPanel.Children.Add(MakeIconBtn("\uE74D", "Remove", lat.Id, BtnRemove_Click, true));

            row0.Children.Add(btnPanel);
            row0.Children.Add(new TextBlock
            {
                Text = lat.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetRow(row0, 0);
            grid.Children.Add(row0);

            // Row 1: Details
            var row1 = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

            row1.Children.Add(new TextBlock
            {
                Text = $"Align: {lat.SourceAlignmentName}",
                FontSize = 11,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFB74D"))
            });

            row1.Children.Add(new TextBlock
            {
                Text = $"Station: {StationToString(lat.Station)}  |  Invert: {lat.InvertElevation:N2}",
                FontSize = 11,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#60CDFF")),
                Margin = new Thickness(0, 2, 0, 0)
            });

            Grid.SetRow(row1, 1);
            grid.Children.Add(row1);

            card.Child = grid;
            return card;
        }

        private Button MakeIconBtn(string icon, string tooltip, string latId,
            RoutedEventHandler click, bool isDelete = false)
        {
            var btn = new Button
            {
                Content = icon,
                Tag = latId,
                ToolTip = tooltip,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Width = 26,
                Height = 26,
                Cursor = Cursors.Hand,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9E9E9E"))
            };

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

        private static string StationToString(double station)
        {
            int hundreds = (int)(station / 100);
            double remainder = station - (hundreds * 100);
            return $"{hundreds}+{remainder:00.00}";
        }

        private void SaveProject()
        {
            if (_project == null) return;
            _project.Laterals = _laterals.ToList();
            LateralManagerStore.Save(_project);
        }

        private static void AcadMessage(string msg)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            doc?.Editor.WriteMessage($"\n[Lateral Manager] {msg}\n");
        }
    }
}
