using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.AutoCAD.EditorInput;
using AdvancedLandDevTools.VehicleTracking.Core;
using AdvancedLandDevTools.VehicleTracking.Data;
using AdvancedLandDevTools.VehicleTracking.Commands;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcDb = Autodesk.AutoCAD.DatabaseServices;
using AcGeo = Autodesk.AutoCAD.Geometry;
using WpfShapes = System.Windows.Shapes;

namespace AdvancedLandDevTools.VehicleTracking.UI
{
    public partial class VtMainPanel : UserControl
    {
        private AcDb.ObjectId _pathId = AcDb.ObjectId.Null;
        private List<Vec2>? _pathPoints;

        // ── Display data class for ListBox binding ────────────────
        private class VehicleItem
        {
            public string Symbol { get; set; } = "";
            public string Name { get; set; } = "";
            public string Category { get; set; } = "";
            public bool IsArticulated { get; set; }
            public int Index { get; set; }
        }

        private List<VehicleItem> _allVehicles = new();

        public VtMainPanel()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadVehicleLists();
        }

        private void LoadVehicleLists()
        {
            var display = VehicleLibrary.GetDisplayList();
            _allVehicles = display.Select(d => new VehicleItem
            {
                Symbol = d.Symbol,
                Name = d.Name,
                Category = d.Category,
                IsArticulated = d.IsArticulated,
                Index = d.Index
            }).ToList();

            // Populate swept path combo
            CmbVehicle.Items.Clear();
            foreach (var v in _allVehicles)
                CmbVehicle.Items.Add($"{v.Symbol} — {v.Name}");

            // Default to WB-62FL
            int flIdx = _allVehicles.FindIndex(v => v.Symbol == "WB-62FL");
            CmbVehicle.SelectedIndex = flIdx >= 0 ? flIdx : 0;

            // Populate library list
            LstVehicles.ItemsSource = _allVehicles;

            CmbVehicle.SelectionChanged += CmbVehicle_SelectionChanged;
            UpdateVehicleInfo();
        }

        // ── Vehicle info display ──────────────────────────────────

        private void CmbVehicle_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => UpdateVehicleInfo();

        private void UpdateVehicleInfo()
        {
            int idx = CmbVehicle.SelectedIndex;
            if (idx < 0 || idx >= _allVehicles.Count) return;

            var item = _allVehicles[idx];
            TxtCategory.Text = item.Category;

            if (item.IsArticulated)
            {
                var av = VehicleLibrary.ArticulatedVehicles[item.Index];
                TxtDimensions.Text = $"{av.TotalLength:F0}' L x {av.LeadUnit.Width:F0}' W";
                TxtMinRadius.Text = $"{av.LeadUnit.EffectiveMinRadius:F1}' (outside front tire)";
                DrawVehiclePreview(VehiclePreview, av);
            }
            else
            {
                var vu = VehicleLibrary.SingleUnits[item.Index];
                TxtDimensions.Text = $"{vu.Length:F0}' L x {vu.Width:F0}' W, WB={vu.Wheelbase:F0}'";
                TxtMinRadius.Text = $"{vu.EffectiveMinRadius:F1}' (outside front tire)";
                DrawVehiclePreview(VehiclePreview, vu);
            }
        }

        // ── Swept Path tab ────────────────────────────────────────

        private void BtnPickPath_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                var ed = doc.Editor;

                var opt = new PromptEntityOptions("\nSelect path polyline: ");
                opt.SetRejectMessage("\nMust be a Polyline.");
                opt.AddAllowedClass(typeof(AcDb.Polyline), true);
                opt.AddAllowedClass(typeof(AcDb.Polyline2d), true);
                opt.AddAllowedClass(typeof(AcDb.Polyline3d), true);

                var res = ed.GetEntity(opt);
                if (res.Status != PromptStatus.OK) return;

                _pathId = res.ObjectId;

                using (var tx = doc.Database.TransactionManager.StartTransaction())
                {
                    var ent = tx.GetObject(_pathId, AcDb.OpenMode.ForRead);
                    _pathPoints = ExtractPoints(ent);
                    tx.Commit();
                }

                double len = 0;
                for (int i = 1; i < _pathPoints.Count; i++)
                    len += _pathPoints[i - 1].DistanceTo(_pathPoints[i]);

                TxtPathInfo.Text = $"Path: {_pathPoints.Count} vertices, {len:F1}' length";
            }
            catch (Exception ex)
            {
                TxtPathInfo.Text = $"Error: {ex.Message}";
            }
        }

        private void BtnRunSweep_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_pathPoints == null || _pathPoints.Count < 2)
                {
                    MessageBox.Show("Pick a path polyline first.", "Vehicle Tracking",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                int vIdx = CmbVehicle.SelectedIndex;
                if (vIdx < 0) return;
                var item = _allVehicles[vIdx];

                double speed = 15;
                double.TryParse(TxtSpeed.Text, out speed);
                double speedFps = speed * 5280.0 / 3600.0;

                var solver = new SweptPathSolver
                {
                    Speed = speedFps,
                    Reverse = RdReverse.IsChecked == true,
                    SnapshotInterval = 100
                };

                SimulationResult result;
                if (item.IsArticulated)
                    result = solver.Solve(VehicleLibrary.ArticulatedVehicles[item.Index], _pathPoints);
                else
                    result = solver.Solve(VehicleLibrary.SingleUnits[item.Index], _pathPoints);

                // Draw to AutoCAD
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                using (doc.LockDocument())
                using (var tx = doc.Database.TransactionManager.StartTransaction())
                {
                    var bt = (AcDb.BlockTable)tx.GetObject(doc.Database.BlockTableId, AcDb.OpenMode.ForRead);
                    var btr = (AcDb.BlockTableRecord)tx.GetObject(
                        bt[AcDb.BlockTableRecord.ModelSpace], AcDb.OpenMode.ForWrite);
                    VtDrawingWriter.DrawResult(doc.Database, tx, btr, result);
                    tx.Commit();
                }

                // Show results
                TxtResultsHeader.Visibility = System.Windows.Visibility.Visible;
                PnlResults.Visibility = System.Windows.Visibility.Visible;
                TxtResultPath.Text = $"Path Length: {result.PathLength:F1} ft";
                TxtResultWidth.Text = $"Max Swept Width: {result.MaxSweptWidth:F1} ft";
                TxtResultOT.Text = $"Max Offtracking: {result.MaxOfftracking:F1} ft";
                TxtResultClamped.Text = $"Steering Clamped: {(result.SteeringClamped ? "YES" : "No")}";
                TxtResultCollisions.Text = $"Collisions: {result.Collisions.Count}";

                doc.Editor.Regen();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Simulation error: {ex.Message}", "Vehicle Tracking",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Parking tab ───────────────────────────────────────────

        private AcGeo.Point3d _parkCorner1, _parkCorner2;
        private bool _hasBoundary;

        private void BtnPickBoundary_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                var ed = doc.Editor;

                var p1 = ed.GetPoint(new PromptPointOptions("\nPick first corner: "));
                if (p1.Status != PromptStatus.OK) return;

                var p2 = ed.GetCorner(new PromptCornerOptions("\nPick opposite corner: ", p1.Value));
                if (p2.Status != PromptStatus.OK) return;

                _parkCorner1 = p1.Value;
                _parkCorner2 = p2.Value;
                _hasBoundary = true;

                double w = Math.Abs(_parkCorner2.X - _parkCorner1.X);
                double h = Math.Abs(_parkCorner2.Y - _parkCorner1.Y);
                TxtBoundaryInfo.Text = $"Boundary: {w:F0}' x {h:F0}' ({w * h:F0} sq ft)";
            }
            catch (Exception ex)
            {
                TxtBoundaryInfo.Text = $"Error: {ex.Message}";
            }
        }

        private void CmbAngle_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbAngle.SelectedIndex < 0) return;
            var dims = FloridaParkingDefaults.GetByAngle(GetSelectedAngle());
            if (TxtStallWidth != null) TxtStallWidth.Text = dims.StallWidth.ToString("F1");
            if (TxtStallDepth != null) TxtStallDepth.Text = dims.StallDepth.ToString("F1");
            if (TxtAisleWidth != null) TxtAisleWidth.Text = dims.AisleWidthTwoWay.ToString("F1");
        }

        private ParkingAngle GetSelectedAngle() => CmbAngle.SelectedIndex switch
        {
            0 => ParkingAngle.Perpendicular,
            1 => ParkingAngle.Angle60,
            2 => ParkingAngle.Angle45,
            3 => ParkingAngle.Angle30,
            4 => ParkingAngle.Parallel,
            _ => ParkingAngle.Perpendicular
        };

        private void BtnGenerateParking_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_hasBoundary)
                {
                    MessageBox.Show("Pick a boundary first.", "Vehicle Tracking",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                double w = Math.Abs(_parkCorner2.X - _parkCorner1.X);
                double h = Math.Abs(_parkCorner2.Y - _parkCorner1.Y);
                var origin = new Vec2(
                    Math.Min(_parkCorner1.X, _parkCorner2.X),
                    Math.Min(_parkCorner1.Y, _parkCorner2.Y));

                double.TryParse(TxtStallWidth.Text, out double sw);
                double.TryParse(TxtStallDepth.Text, out double sd);
                double.TryParse(TxtAisleWidth.Text, out double aw);
                int.TryParse(TxtAdaCount.Text, out int adaCount);

                var dims = new ParkingDimensions
                {
                    Angle = GetSelectedAngle(),
                    StallWidth = sw > 0 ? sw : 9,
                    StallDepth = sd > 0 ? sd : 18.5,
                    AisleWidthTwoWay = aw > 0 ? aw : 24,
                    AisleWidthOneWay = aw > 0 ? aw : 24
                };

                var gen = new ParkingLayoutGenerator
                {
                    Dimensions = dims,
                    Ada = FloridaParkingDefaults.GetAdaRequirements(),
                    AdaSpacesRequired = adaCount > 0 ? adaCount : 1,
                    TwoWayAisle = true
                };

                var layout = gen.Generate(origin, w, h);

                var doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                using (doc.LockDocument())
                using (var tx = doc.Database.TransactionManager.StartTransaction())
                {
                    var bt = (AcDb.BlockTable)tx.GetObject(doc.Database.BlockTableId, AcDb.OpenMode.ForRead);
                    var btr = (AcDb.BlockTableRecord)tx.GetObject(
                        bt[AcDb.BlockTableRecord.ModelSpace], AcDb.OpenMode.ForWrite);
                    VtDrawingWriter.DrawParking(doc.Database, tx, btr, layout);
                    tx.Commit();
                }

                TxtParkingResult.Text = $"Generated {layout.Stalls.Count} stalls " +
                    $"({layout.TotalRegularSpaces} regular, {layout.TotalAccessibleSpaces} ADA)";

                doc.Editor.Regen();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Vehicle Tracking",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Library tab ───────────────────────────────────────────

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            string q = TxtSearch.Text.Trim().ToLower();
            TxtSearchPlaceholder.Visibility = string.IsNullOrEmpty(q)
                ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

            if (string.IsNullOrEmpty(q))
                LstVehicles.ItemsSource = _allVehicles;
            else
                LstVehicles.ItemsSource = _allVehicles
                    .Where(v => v.Name.ToLower().Contains(q) ||
                                v.Symbol.ToLower().Contains(q) ||
                                v.Category.ToLower().Contains(q))
                    .ToList();
        }

        private void LstVehicles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LstVehicles.SelectedItem is not VehicleItem item) return;

            if (item.IsArticulated)
            {
                var av = VehicleLibrary.ArticulatedVehicles[item.Index];
                var lead = av.LeadUnit;
                TxtLibDetails.Text =
                    $"Name: {av.Name}\n" +
                    $"Symbol: {av.Symbol}\n" +
                    $"Category: {av.Category}\n" +
                    $"Total Length: {av.TotalLength:F1}'\n" +
                    $"Width: {lead.Width:F1}'\n" +
                    $"Tractor WB: {lead.Wheelbase:F1}'\n" +
                    $"Min Turn Radius: {lead.EffectiveMinRadius:F1}'\n" +
                    $"Trailers: {av.Trailers.Length}\n" +
                    $"{(av.IsFloridaVehicle ? "FLORIDA DESIGN VEHICLE" : "")}";
                DrawArticulatedPreview(LibraryPreview, av);
            }
            else
            {
                var vu = VehicleLibrary.SingleUnits[item.Index];
                TxtLibDetails.Text =
                    $"Name: {vu.Name}\n" +
                    $"Symbol: {vu.Symbol}\n" +
                    $"Category: {vu.Category}\n" +
                    $"Length: {vu.Length:F1}'\n" +
                    $"Width: {vu.Width:F1}'\n" +
                    $"Wheelbase: {vu.Wheelbase:F1}'\n" +
                    $"Front Overhang: {vu.FrontOverhang:F1}'\n" +
                    $"Rear Overhang: {vu.RearOverhang:F1}'\n" +
                    $"Track Width: {vu.TrackWidth:F1}'\n" +
                    $"Min Turn Radius: {vu.EffectiveMinRadius:F1}'\n" +
                    $"Max Steer: {vu.MaxSteeringAngle * 180 / Math.PI:F1} deg";
                DrawVehiclePreview(LibraryPreview, vu);
            }
        }

        // ── Vehicle preview drawing ───────────────────────────────

        private void DrawVehiclePreview(Canvas canvas, VehicleUnit vu)
        {
            canvas.Children.Clear();
            double cw = canvas.ActualWidth > 0 ? canvas.ActualWidth : 340;
            double ch = canvas.ActualHeight > 0 ? canvas.ActualHeight : 100;

            double scale = Math.Min((cw - 20) / vu.Length, (ch - 20) / vu.Width);
            double ox = (cw - vu.Length * scale) / 2;
            double oy = (ch - vu.Width * scale) / 2;

            // Body rectangle
            var body = new WpfShapes.Rectangle
            {
                Width = vu.Length * scale,
                Height = vu.Width * scale,
                Stroke = new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF)),
                StrokeThickness = 1.5,
                Fill = new SolidColorBrush(Color.FromArgb(40, 0x60, 0xCD, 0xFF))
            };
            Canvas.SetLeft(body, ox);
            Canvas.SetTop(body, oy);
            canvas.Children.Add(body);

            // Front axle line
            double fax = ox + (vu.Length - vu.FrontOverhang) * scale;
            var fLine = new WpfShapes.Line
            {
                X1 = fax, Y1 = oy,
                X2 = fax, Y2 = oy + vu.Width * scale,
                Stroke = Brushes.Yellow, StrokeThickness = 1,
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 3, 2 }
            };
            canvas.Children.Add(fLine);

            // Rear axle line
            double rax = ox + vu.RearOverhang * scale;
            var rLine = new WpfShapes.Line
            {
                X1 = rax, Y1 = oy,
                X2 = rax, Y2 = oy + vu.Width * scale,
                Stroke = Brushes.Yellow, StrokeThickness = 1,
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 3, 2 }
            };
            canvas.Children.Add(rLine);

            // Wheelbase label
            var wbLabel = new TextBlock
            {
                Text = $"WB={vu.Wheelbase:F0}'",
                Foreground = Brushes.Yellow, FontSize = 9,
                Opacity = 0.7
            };
            Canvas.SetLeft(wbLabel, (rax + fax) / 2 - 15);
            Canvas.SetTop(wbLabel, oy + vu.Width * scale + 2);
            canvas.Children.Add(wbLabel);
        }

        private void DrawVehiclePreview(Canvas canvas, ArticulatedVehicle av)
            => DrawArticulatedPreview(canvas, av);

        private void DrawArticulatedPreview(Canvas canvas, ArticulatedVehicle av)
        {
            canvas.Children.Clear();
            double cw = canvas.ActualWidth > 0 ? canvas.ActualWidth : 340;
            double ch = canvas.ActualHeight > 0 ? canvas.ActualHeight : 120;

            double totalLen = av.TotalLength > 0 ? av.TotalLength
                : av.LeadUnit.Length + av.Trailers.Sum(t => t.Unit.Length);
            double maxW = Math.Max(av.LeadUnit.Width,
                av.Trailers.Max(t => t.Unit.Width));

            double scale = Math.Min((cw - 20) / totalLen, (ch - 20) / maxW);
            double ox = (cw - totalLen * scale) / 2;
            double oy = (ch - maxW * scale) / 2;

            // Draw lead unit
            var leadRect = new WpfShapes.Rectangle
            {
                Width = av.LeadUnit.Length * scale,
                Height = av.LeadUnit.Width * scale,
                Stroke = new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF)),
                StrokeThickness = 1.5,
                Fill = new SolidColorBrush(Color.FromArgb(40, 0x60, 0xCD, 0xFF))
            };
            double leadX = ox + totalLen * scale - av.LeadUnit.Length * scale;
            Canvas.SetLeft(leadRect, leadX);
            Canvas.SetTop(leadRect, oy + (maxW - av.LeadUnit.Width) * scale / 2);
            canvas.Children.Add(leadRect);

            // Draw trailers
            double trailerX = ox;
            foreach (var td in av.Trailers)
            {
                var tRect = new WpfShapes.Rectangle
                {
                    Width = td.Unit.Length * scale,
                    Height = td.Unit.Width * scale,
                    Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D)),
                    StrokeThickness = 1.5,
                    Fill = new SolidColorBrush(Color.FromArgb(30, 0xFF, 0xB7, 0x4D))
                };
                Canvas.SetLeft(tRect, trailerX);
                Canvas.SetTop(tRect, oy + (maxW - td.Unit.Width) * scale / 2);
                canvas.Children.Add(tRect);
                trailerX += td.Unit.Length * scale + 2;
            }

            // Label
            var label = new TextBlock
            {
                Text = $"{av.Symbol} — {av.TotalLength:F0}' total",
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                FontSize = 9
            };
            Canvas.SetLeft(label, ox);
            Canvas.SetTop(label, oy + maxW * scale + 2);
            canvas.Children.Add(label);
        }

        // ── Path extraction helper ────────────────────────────────

        private static List<Vec2> ExtractPoints(AcDb.DBObject ent)
        {
            var pts = new List<Vec2>();
            if (ent is AcDb.Polyline pl)
            {
                for (int i = 0; i < pl.NumberOfVertices; i++)
                {
                    var p = pl.GetPoint2dAt(i);
                    pts.Add(new Vec2(p.X, p.Y));
                }
            }
            else if (ent is AcDb.Polyline2d pl2d)
            {
                foreach (AcDb.ObjectId vid in pl2d)
                {
                    if (vid.GetObject(AcDb.OpenMode.ForRead) is AcDb.Vertex2d v)
                        pts.Add(new Vec2(v.Position.X, v.Position.Y));
                }
            }
            else if (ent is AcDb.Polyline3d pl3d)
            {
                foreach (AcDb.ObjectId vid in pl3d)
                {
                    if (vid.GetObject(AcDb.OpenMode.ForRead) is AcDb.PolylineVertex3d v)
                        pts.Add(new Vec2(v.Position.X, v.Position.Y));
                }
            }
            return pts;
        }
    }
}
