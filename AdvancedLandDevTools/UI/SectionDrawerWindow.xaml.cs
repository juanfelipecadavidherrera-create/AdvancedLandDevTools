using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AdvancedLandDevTools.Engine;

namespace AdvancedLandDevTools.UI
{
    public partial class SectionDrawerWindow : Window
    {
        private SectionProfile _profile = new();

        /// <summary>Set when user clicks "Draw in Model". The command reads this.</summary>
        public SectionProfile? ResultProfile { get; private set; }

        /// <summary>Whether to draw vertical divider lines at each segment boundary.</summary>
        public bool DrawSegmentLines { get; private set; }


        public SectionDrawerWindow()
        {
            InitializeComponent();
            RefreshSavedList();
        }

        // ── List item display model ──────────────────────────────────

        private class SegItem
        {
            public int Index { get; set; }
            public string Display { get; set; } = "";
            public string RoadLabel { get; set; } = "R";
            public string RoadBg { get; set; } = "#333333";
            public string RoadFg { get; set; } = "#666666";
            public Visibility RoadVisible { get; set; } = Visibility.Visible;
        }

        // ── Add / Delete segments ────────────────────────────────────

        private void BtnAddLeft_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParseSeg(TxtLeftDist, TxtLeftSlope, out var seg)) return;
            _profile.LeftSegments.Add(seg);
            TxtLeftDist.Text = ""; TxtLeftSlope.Text = "";
            RefreshAll();
        }

        private void BtnAddRight_Click(object sender, RoutedEventArgs e)
        {
            if (!TryParseSeg(TxtRightDist, TxtRightSlope, out var seg)) return;
            _profile.RightSegments.Add(seg);
            TxtRightDist.Text = ""; TxtRightSlope.Text = "";
            RefreshAll();
        }

        private void BtnAddLeftTypeF_Click(object sender, RoutedEventArgs e)
        {
            _profile.LeftSegments.Add(new SectionSegment
                { HorizontalDistance = 2.0, Type = SegmentType.TypeF });
            RefreshAll();
        }

        private void BtnAddLeftTypeD_Click(object sender, RoutedEventArgs e)
        {
            _profile.LeftSegments.Add(new SectionSegment
                { HorizontalDistance = 0.5, Type = SegmentType.TypeD });
            RefreshAll();
        }

        private void BtnAddRightTypeF_Click(object sender, RoutedEventArgs e)
        {
            _profile.RightSegments.Add(new SectionSegment
                { HorizontalDistance = 2.0, Type = SegmentType.TypeF });
            RefreshAll();
        }

        private void BtnAddRightTypeD_Click(object sender, RoutedEventArgs e)
        {
            _profile.RightSegments.Add(new SectionSegment
                { HorizontalDistance = 0.5, Type = SegmentType.TypeD });
            RefreshAll();
        }

        private void BtnToggleLeftRoad_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int idx && idx < _profile.LeftSegments.Count)
            {
                _profile.LeftSegments[idx].IsRoad = !_profile.LeftSegments[idx].IsRoad;
                RefreshAll();
            }
        }

        private void BtnToggleRightRoad_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int idx && idx < _profile.RightSegments.Count)
            {
                _profile.RightSegments[idx].IsRoad = !_profile.RightSegments[idx].IsRoad;
                RefreshAll();
            }
        }

        private void BtnAddLeftValley_Click(object sender, RoutedEventArgs e)
        {
            _profile.LeftSegments.Add(new SectionSegment
                { HorizontalDistance = 2.0, Type = SegmentType.ValleyGutter });
            RefreshAll();
        }

        private void BtnAddRightValley_Click(object sender, RoutedEventArgs e)
        {
            _profile.RightSegments.Add(new SectionSegment
                { HorizontalDistance = 2.0, Type = SegmentType.ValleyGutter });
            RefreshAll();
        }

        private void BtnDeleteLeft_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int idx && idx < _profile.LeftSegments.Count)
            {
                _profile.LeftSegments.RemoveAt(idx);
                RefreshAll();
            }
        }

        private void BtnDeleteRight_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int idx && idx < _profile.RightSegments.Count)
            {
                _profile.RightSegments.RemoveAt(idx);
                RefreshAll();
            }
        }

        // ── Save / Load / Delete ─────────────────────────────────────

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtSectionName.Text.Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Enter a section name.", "Save", MessageBoxButton.OK);
                return;
            }
            _profile.Name = name;
            SectionDrawerEngine.Save(_profile);
            RefreshSavedList();
        }

        private void BtnLoad_Click(object sender, RoutedEventArgs e)
        {
            string? name = CmbSaved.SelectedItem as string;
            if (string.IsNullOrEmpty(name)) return;
            var loaded = SectionDrawerEngine.Load(name);
            if (loaded == null) return;
            _profile = loaded;
            TxtSectionName.Text = _profile.Name;
            RefreshAll();
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            string? name = CmbSaved.SelectedItem as string;
            if (string.IsNullOrEmpty(name)) return;
            SectionDrawerEngine.Delete(name);
            RefreshSavedList();
        }

        private void CmbSaved_Changed(object sender, SelectionChangedEventArgs e) { }

        // ── Draw / Close ─────────────────────────────────────────────

        private void BtnDraw_Click(object sender, RoutedEventArgs e)
        {
            if (_profile.LeftSegments.Count == 0 && _profile.RightSegments.Count == 0)
            {
                MessageBox.Show("Add at least one segment before drawing.", "Draw",
                    MessageBoxButton.OK);
                return;
            }
            ResultProfile = _profile;
            DrawSegmentLines = ChkSegmentLines.IsChecked == true;
            DialogResult = true;
            Close();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        // ── Refresh helpers ──────────────────────────────────────────

        private void RefreshAll()
        {
            RebuildList(LstLeft, _profile.LeftSegments);
            RebuildList(LstRight, _profile.RightSegments);
            RefreshPreview();
        }

        private static void RebuildList(ListBox lb, List<SectionSegment> segs)
        {
            lb.ItemsSource = segs.Select((s, i) =>
            {
                bool isSpecial = s.Type != SegmentType.Normal;
                return new SegItem
                {
                    Index = i,
                    Display = s.Type switch
                    {
                        SegmentType.TypeF => $"#{i + 1}  Type F Curb & Gutter (2.0')",
                        SegmentType.TypeD => $"#{i + 1}  Type D Curb (0.5')",
                        SegmentType.ValleyGutter => $"#{i + 1}  Valley Gutter (2.0')",
                        _ => $"#{i + 1}  Dist: {s.HorizontalDistance:F1} ft   Slope: {s.SlopePercent:F1}%"
                    },
                    RoadLabel = "R",
                    RoadBg = s.IsRoad ? "#224422" : "#333333",
                    RoadFg = s.IsRoad ? "#6BCB77" : "#666666",
                    RoadVisible = isSpecial ? Visibility.Collapsed : Visibility.Visible
                };
            }).ToList();
        }

        private void RefreshSavedList()
        {
            var names = SectionDrawerEngine.ListSections();
            CmbSaved.ItemsSource = names;
            if (names.Count > 0) CmbSaved.SelectedIndex = 0;
        }

        // ── Canvas preview ───────────────────────────────────────────

        private void ChkSegmentLines_Changed(object sender, RoutedEventArgs e)
            => RefreshPreview();

        private void PreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
            => RefreshPreview();

        private void RefreshPreview()
        {
            PreviewCanvas.Children.Clear();
            double cw = PreviewCanvas.ActualWidth, ch = PreviewCanvas.ActualHeight;
            if (cw < 10 || ch < 10) return;

            var geo = SectionDrawerEngine.ComputePoints(_profile);

            // CL extends ABOVE the section surface by CenterlineHeight
            double surfaceY = geo.CenterlineTopY;     // where segments start
            double clHeight = _profile.CenterlineHeight;
            double clTopAbove = surfaceY + clHeight;   // top of CL above content

            // Collect all points for bounding box (including block outlines + CL above)
            var all = new List<(double X, double Y)>();
            all.AddRange(geo.LeftPoints);
            all.AddRange(geo.RightPoints);
            all.Add((0, surfaceY));
            all.Add((0, clTopAbove));
            foreach (var block in geo.BlockOutlines)
                all.AddRange(block);

            double minX = all.Min(p => p.X);
            double maxX = all.Max(p => p.X);
            double minY = all.Min(p => p.Y);
            double maxY = all.Max(p => p.Y);

            // Ensure minimum extents so it doesn't collapse
            double rangeX = maxX - minX;
            double rangeY = maxY - minY;
            if (rangeX < 1) { minX -= 5; maxX += 5; rangeX = maxX - minX; }
            if (rangeY < 1) { minY -= 5; maxY += 5; rangeY = maxY - minY; }

            double pad = 30;
            double scaleX = (cw - 2 * pad) / rangeX;
            double scaleY = (ch - 2 * pad) / rangeY;
            double scale = Math.Min(scaleX, scaleY);

            // Center the drawing in the canvas
            double drawW = rangeX * scale, drawH = rangeY * scale;
            double offX = (cw - drawW) / 2.0;
            double offY = (ch - drawH) / 2.0;

            Point ToCanvas((double X, double Y) p)
                => new Point(offX + (p.X - minX) * scale,
                             offY + (maxY - p.Y) * scale); // flip Y

            // Grid lines (subtle)
            DrawGrid(minX, maxX, minY, maxY, scale, ToCanvas);

            // Left polyline (accent blue)
            DrawPolyline(geo.LeftPoints, "#60CDFF", ToCanvas, _profile.LeftSegments);

            // Right polyline (green)
            DrawPolyline(geo.RightPoints, "#6BCB77", ToCanvas, _profile.RightSegments);

            // Block outlines (filled concrete shapes)
            foreach (var block in geo.BlockOutlines)
                DrawBlockOutline(block, ToCanvas);

            // Centerline drawn LAST — from section surface UPWARD
            var clBase = ToCanvas((0, surfaceY));
            var clTop = ToCanvas((0, clTopAbove));
            var centerLine = new Line
            {
                X1 = clBase.X, Y1 = clBase.Y,
                X2 = clTop.X, Y2 = clTop.Y,
                Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0x00)),
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 6, 3 }
            };
            PreviewCanvas.Children.Add(centerLine);

            // CL label (yellow, larger) — at the very top
            AddLabel("CL", clTop.X - 6, clTop.Y - 20, "#FFFF00", 12);

            // Segment divider lines in preview (if checkbox is checked)
            if (ChkSegmentLines.IsChecked == true)
            {
                var divBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
                foreach (var bp in geo.SegmentBoundaries)
                {
                    // Skip divider lines at block boundaries
                    if (bp.Type != SegmentType.Normal) continue;

                    var bpBase = ToCanvas((bp.X, bp.Y));
                    var bpTop = ToCanvas((bp.X, clTopAbove));
                    PreviewCanvas.Children.Add(new Line
                    {
                        X1 = bpBase.X, Y1 = bpBase.Y,
                        X2 = bpTop.X, Y2 = bpTop.Y,
                        Stroke = divBrush,
                        StrokeThickness = 1,
                        StrokeDashArray = new DoubleCollection { 4, 2 }
                    });
                }
            }
        }

        private void DrawPolyline(List<(double X, double Y)> pts, string colorHex,
            Func<(double, double), Point> toCanvas, List<SectionSegment> segs)
        {
            if (pts.Count < 2) return;
            var color = (Color)ColorConverter.ConvertFromString(colorHex);
            var brush = new SolidColorBrush(color);

            var poly = new Polyline
            {
                Stroke = brush,
                StrokeThickness = 2.5,
                StrokeLineJoin = PenLineJoin.Round
            };
            foreach (var p in pts)
                poly.Points.Add(toCanvas(p));
            PreviewCanvas.Children.Add(poly);

            // Dots at every point
            for (int i = 1; i < pts.Count; i++)
            {
                var cp = toCanvas(pts[i]);
                var dot = new Ellipse { Width = 6, Height = 6, Fill = brush };
                Canvas.SetLeft(dot, cp.X - 3);
                Canvas.SetTop(dot, cp.Y - 3);
                PreviewCanvas.Children.Add(dot);
            }

            // Labels: map segment → sub-point range
            int ptIdx = 1; // points index (0 = origin)
            foreach (var seg in segs)
            {
                int subCount = SectionDrawerEngine.SubPointCount(seg);
                if (ptIdx + subCount - 1 >= pts.Count) break;

                // Label positioned at midpoint of the segment's full span
                var spanStart = pts[ptIdx - 1];
                var spanEnd = pts[ptIdx + subCount - 1];
                var midPt = toCanvas((
                    (spanStart.X + spanEnd.X) / 2,
                    (spanStart.Y + spanEnd.Y) / 2));

                string lbl = seg.Type switch
                {
                    SegmentType.TypeF => "Type F  2.0'",
                    SegmentType.TypeD => "Type D  0.5'",
                    SegmentType.ValleyGutter => "Valley  2.0'",
                    _ => $"{seg.SlopePercent:F1}%  {seg.HorizontalDistance:F1}'"
                };
                AddLabel(lbl, midPt.X, midPt.Y - 14, colorHex, 10);

                ptIdx += subCount;
            }
        }

        private void DrawBlockOutline(List<(double X, double Y)> block,
            Func<(double, double), Point> toCanvas)
        {
            if (block.Count < 3) return;

            // Filled polygon with semi-transparent concrete color
            var fill = new SolidColorBrush(Color.FromArgb(0x40, 0x90, 0x90, 0x90));
            var stroke = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));

            var polygon = new Polygon
            {
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = 1.2,
                StrokeLineJoin = PenLineJoin.Miter
            };
            foreach (var pt in block)
                polygon.Points.Add(toCanvas(pt));
            PreviewCanvas.Children.Add(polygon);
        }

        private void DrawGrid(double minX, double maxX, double minY, double maxY,
            double scale, Func<(double, double), Point> toCanvas)
        {
            double range = Math.Max(maxX - minX, maxY - minY);
            double gridStep = 5;
            if (range > 100) gridStep = 20;
            else if (range > 50) gridStep = 10;

            var gridBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D));

            double startX = Math.Floor(minX / gridStep) * gridStep;
            for (double x = startX; x <= maxX; x += gridStep)
            {
                var p1 = toCanvas((x, minY));
                var p2 = toCanvas((x, maxY));
                PreviewCanvas.Children.Add(new Line
                {
                    X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y,
                    Stroke = gridBrush, StrokeThickness = 0.5
                });
            }

            double startY = Math.Floor(minY / gridStep) * gridStep;
            for (double y = startY; y <= maxY; y += gridStep)
            {
                var p1 = toCanvas((minX, y));
                var p2 = toCanvas((maxX, y));
                PreviewCanvas.Children.Add(new Line
                {
                    X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y,
                    Stroke = gridBrush, StrokeThickness = 0.5
                });
            }
        }

        private void AddLabel(string text, double x, double y,
            string colorHex, double fontSize = 9)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(colorHex))
            };
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
            PreviewCanvas.Children.Add(tb);
        }

        // ── Helpers ──────────────────────────────────────────────────

        private static bool TryParseSeg(TextBox distBox, TextBox slopeBox,
            out SectionSegment seg)
        {
            seg = new SectionSegment();
            if (!double.TryParse(distBox.Text, out double dist) || dist <= 0)
            {
                MessageBox.Show("Enter a valid positive distance (ft).", "Input",
                    MessageBoxButton.OK);
                return false;
            }
            if (!double.TryParse(slopeBox.Text, out double slope))
            {
                MessageBox.Show("Enter a valid slope percentage.", "Input",
                    MessageBoxButton.OK);
                return false;
            }
            seg.HorizontalDistance = dist;
            seg.SlopePercent = slope;
            return true;
        }
    }
}
