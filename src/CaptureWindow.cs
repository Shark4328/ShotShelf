using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ScreenLine
{
    internal sealed class CaptureMark
    {
        public string Tool, Text;
        public Point Start, End;
        public Color Color;
        public double Size;
        public readonly List<Point> Points = new List<Point>();
    }

    internal sealed class CaptureSurface : FrameworkElement
    {
        readonly DrawingVisual baseLayer = new DrawingVisual();
        readonly DrawingVisual markLayer = new DrawingVisual();
        readonly DrawingVisual overlayLayer = new DrawingVisual();
        internal readonly List<CaptureMark> Marks = new List<CaptureMark>();
        BitmapSource picture;
        Rect selection = Rect.Empty, hover = Rect.Empty;
        CaptureMark pending;
        double scale = 1;
        Point cursor;
        bool showCursor;
        long staticBuilds;

        internal CaptureSurface()
        {
            AddVisualChild(baseLayer);
            AddVisualChild(markLayer);
            AddVisualChild(overlayLayer);
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
            SyncTransforms();
        }

        protected override int VisualChildrenCount { get { return 3; } }

        protected override Visual GetVisualChild(int index)
        {
            if (index == 0) return baseLayer;
            if (index == 1) return markLayer;
            return overlayLayer;
        }

        internal BitmapSource Picture
        {
            get { return picture; }
            set
            {
                if (picture == value) return;
                picture = value;
                RebuildBase(); RebuildMarks(); DrawOverlay();
            }
        }

        internal Rect Selection
        {
            get { return selection; }
            set
            {
                if (selection == value) return;
                selection = value;
                RebuildMarks(); DrawOverlay();
            }
        }

        internal Rect Hover
        {
            get { return hover; }
            set
            {
                if (hover == value) return;
                hover = value; DrawOverlay();
            }
        }

        internal CaptureMark Pending
        {
            get { return pending; }
            set
            {
                if (pending == value) return;
                pending = value; RebuildMarks(); DrawOverlay();
            }
        }

        internal double Scale
        {
            get { return scale; }
            set
            {
                double next = value > 0 ? value : 1;
                if (scale == next) return;
                scale = next; SyncTransforms(); RebuildMarks(); DrawOverlay();
            }
        }

        // 鼠标移动只重画选框和十字线，底图与标注层保持原样。
        internal long StaticLayerBuilds { get { return staticBuilds; } }

        internal void SetCursor(Point point, bool visible)
        {
            if (cursor == point && showCursor == visible) return;
            cursor = point; showCursor = visible; DrawOverlay();
        }

        internal void SetCursorVisible(bool visible)
        {
            if (showCursor == visible) return;
            showCursor = visible; DrawOverlay();
        }

        internal void Refresh() { RebuildMarks(); DrawOverlay(); InvalidateVisual(); }

        void SyncTransforms()
        {
            var transform = new ScaleTransform(1 / scale, 1 / scale);
            transform.Freeze();
            baseLayer.Transform = transform;
            markLayer.Transform = transform;
            overlayLayer.Transform = transform;
        }

        void RebuildBase()
        {
            using (var drawing = baseLayer.RenderOpen())
            {
                if (picture != null) drawing.DrawImage(picture, new Rect(0, 0, picture.PixelWidth, picture.PixelHeight));
            }
            staticBuilds++;
        }

        internal static Rect Region(Point start, Point end, Rect bounds)
        {
            var region = new Rect(start, end); region.Intersect(bounds); return region;
        }

        internal BitmapSource Export()
        {
            var region = new Int32Rect((int)Selection.X, (int)Selection.Y, (int)Selection.Width, (int)Selection.Height);
            if (region.Width < 1 || region.Height < 1) throw new InvalidOperationException("请先选择截图区域。");
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
            using (var drawing = visual.RenderOpen())
            {
                drawing.PushTransform(new TranslateTransform(-region.X, -region.Y));
                DrawPicture(drawing); drawing.Pop();
            }
            var result = new RenderTargetBitmap(region.Width, region.Height, 96, 96, PixelFormats.Pbgra32);
            result.Render(visual); result.Freeze(); return result;
        }

        void DrawPicture(DrawingContext drawing)
        {
            if (Picture == null) return;
            drawing.DrawImage(Picture, new Rect(0, 0, Picture.PixelWidth, Picture.PixelHeight));
            if (Selection.IsEmpty) return;
            drawing.PushClip(new RectangleGeometry(Selection));
            foreach (var mark in Marks) DrawMark(drawing, mark);
            if (Pending != null) DrawMark(drawing, Pending);
            drawing.Pop();
        }

        void RebuildMarks()
        {
            using (var drawing = markLayer.RenderOpen())
            {
                if (picture == null || selection.IsEmpty) return;
                drawing.PushClip(new RectangleGeometry(selection));
                foreach (var mark in Marks) DrawMark(drawing, mark);
                if (pending != null) DrawMark(drawing, pending);
                drawing.Pop();
            }
        }

        void DrawOverlay()
        {
            using (var drawing = overlayLayer.RenderOpen())
            {
                if (picture == null) return;
                double width = picture.PixelWidth, height = picture.PixelHeight;
                var full = new Rect(0, 0, width, height);
                Rect highlight = selection.IsEmpty ? hover : selection;
                var shade = new SolidColorBrush(Color.FromArgb(125, 0, 0, 0));
                shade.Freeze();
                if (highlight.IsEmpty)
                    drawing.DrawRectangle(shade, null, full);
                else
                {
                    highlight.Intersect(full);
                    drawing.DrawRectangle(shade, null, new Rect(0, 0, width, highlight.Top));
                    drawing.DrawRectangle(shade, null, new Rect(0, highlight.Bottom, width, height - highlight.Bottom));
                    drawing.DrawRectangle(shade, null, new Rect(0, highlight.Top, highlight.Left, highlight.Height));
                    drawing.DrawRectangle(shade, null, new Rect(highlight.Right, highlight.Top, width - highlight.Right, highlight.Height));
                }
                if (selection.IsEmpty && !hover.IsEmpty)
                    drawing.DrawRectangle(null, BorderPen(), hover);
                if (!selection.IsEmpty)
                {
                    drawing.DrawRectangle(null, BorderPen(), selection);
                    foreach (double x in new[] { selection.Left, selection.Right })
                        foreach (double y in new[] { selection.Top, selection.Bottom })
                            drawing.DrawRectangle(Brushes.White, new Pen(Brushes.DodgerBlue, scale), new Rect(x - 3 * scale, y - 3 * scale, 6 * scale, 6 * scale));
                }
                else if (showCursor)
                {
                    var cross = new Pen(new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), Math.Max(0.5, scale));
                    drawing.DrawLine(cross, new Point(0, cursor.Y), new Point(width, cursor.Y));
                    drawing.DrawLine(cross, new Point(cursor.X, 0), new Point(cursor.X, height));
                }
            }
        }

        Pen BorderPen() { return new Pen(new SolidColorBrush(Color.FromRgb(71, 153, 255)), Math.Max(1, scale)); }

        void DrawMark(DrawingContext drawing, CaptureMark mark)
        {
            var pen = new Pen(new SolidColorBrush(mark.Color), mark.Size) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            var region = Region(mark.Start, mark.End, new Rect(0, 0, Picture.PixelWidth, Picture.PixelHeight));
            if (mark.Tool == "矩形") drawing.DrawRectangle(null, pen, region);
            else if (mark.Tool == "圆形") drawing.DrawEllipse(null, pen, new Point(region.X + region.Width / 2, region.Y + region.Height / 2), region.Width / 2, region.Height / 2);
            else if (mark.Tool == "箭头")
            {
                drawing.DrawLine(pen, mark.Start, mark.End);
                Vector direction = mark.End - mark.Start;
                if (direction.Length > 0)
                {
                    direction.Normalize(); Vector normal = new Vector(-direction.Y, direction.X);
                    Point neck = mark.End - direction * Math.Max(12, mark.Size * 4);
                    drawing.DrawLine(pen, neck + normal * mark.Size * 2.5, mark.End);
                    drawing.DrawLine(pen, neck - normal * mark.Size * 2.5, mark.End);
                }
            }
            else if (mark.Tool == "画笔")
            {
                if (mark.Points.Count == 1) drawing.DrawEllipse(pen.Brush, null, mark.Start, mark.Size / 2, mark.Size / 2);
                for (int i = 1; i < mark.Points.Count; i++) drawing.DrawLine(pen, mark.Points[i - 1], mark.Points[i]);
            }
            else if (mark.Tool == "文字")
                drawing.DrawText(new FormattedText(mark.Text ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface("Microsoft YaHei UI"), Math.Max(16, mark.Size * 7), pen.Brush, 1), mark.Start);
            else if (mark.Tool == "马赛克" && !region.IsEmpty && region.Width >= 1 && region.Height >= 1)
            {
                // 缩小选区后用最近邻放大，只遮挡用户划定的部分。
                var cropped = new CroppedBitmap(Picture, new Int32Rect((int)region.X, (int)region.Y, (int)region.Width, (int)region.Height));
                double ratio = Math.Max(1 / Math.Min(cropped.PixelWidth, cropped.PixelHeight), 1 / Math.Max(8, mark.Size * 5));
                var mosaic = new TransformedBitmap(cropped, new ScaleTransform(ratio, ratio));
                drawing.DrawImage(mosaic, region);
            }
        }

    }

    internal sealed class CaptureWindow : Window
    {
        internal readonly CaptureSurface Surface = new CaptureSurface();
        internal readonly Dictionary<string, Button> Tools = new Dictionary<string, Button>();
        readonly TaskCompletionSource<BitmapSource> completion = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly Canvas controls = new Canvas();
        readonly Border toolbar;
        readonly TextBlock dimensions = new TextBlock { Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(8, 4, 8, 4) };
        readonly Border badge;
        readonly TextBox text = new TextBox { MinWidth = 160, MaxWidth = 480, FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 21, Padding = new Thickness(4), Visibility = Visibility.Collapsed };
        readonly Stack<CaptureMark> redo = new Stack<CaptureMark>();
        readonly List<Rect> windows;
        readonly double scale;
        string tool = "选择";
        Color color = Color.FromRgb(232, 69, 69);
        double thickness = 3;
        Point start, textOrigin;
        Rect original;
        Rect hovered = Rect.Empty;
        int edges;
        bool selecting, moving, drawing, finished;
        internal Rect Selection { get { return Surface.Selection; } }

        internal CaptureWindow(Forms.Screen screen, BitmapSource picture = null, List<Rect> windowSnapshot = null)
        {
            scale = WindowsNative.MonitorScale(screen);
            Title = "区域截图 · 图绳";
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
            Topmost = true; Background = Brushes.Black; Cursor = Cursors.Cross;
            Width = screen.Bounds.Width / scale; Height = screen.Bounds.Height / scale;
            Surface.Scale = scale;
            Surface.Picture = picture ?? CaptureShotRules.ReadScreen(screen);
            windows = windowSnapshot ?? CaptureWindows.Snapshot(screen);
            var root = new Grid(); root.Children.Add(Surface); root.Children.Add(controls); Content = root;
            var rows = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = Math.Max(200, Width - 16) };
            var drawingTools = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (string name in new[] { "选择", "矩形", "圆形", "箭头", "画笔", "文字", "马赛克" })
            {
                string current = name;
                var button = AddButton(name, delegate { CommitText(); tool = current; UpdateTools(); });
                drawingTools.Children.Add(button);
            }
            rows.Children.Add(drawingTools);
            var options = new StackPanel { Orientation = Orientation.Horizontal };
            options.Children.Add(Divider());
            var swatches = new List<Border>();
            string[] colorNames = { "红色", "蓝色", "绿色", "黄色", "白色", "黑色" };
            Color[] colors = { Color.FromRgb(232, 69, 69), Colors.DodgerBlue, Colors.ForestGreen, Colors.Gold, Colors.White, Colors.Black };
            for (int i = 0; i < colors.Length; i++)
            {
                Color current = colors[i];
                var ring = new Border { Width = 17, Height = 17, CornerRadius = new CornerRadius(9), BorderBrush = LightTheme.Brush("#2463AE"), BorderThickness = new Thickness(i == 0 ? 1.5 : 0),
                    Child = new Ellipse { Width = 11, Height = 11, Fill = new SolidColorBrush(current), Stroke = LightTheme.Brush("#C8CFD8"), StrokeThickness = 0.6 } };
                swatches.Add(ring);
                var swatch = LightTheme.Button("", delegate { color = current; text.Foreground = new SolidColorBrush(color); foreach (var item in swatches) item.BorderThickness = new Thickness(item == ring ? 1.5 : 0); }, "quiet");
                swatch.Content = ring; swatch.Width = 21; swatch.MinWidth = 0; swatch.Height = 32; swatch.Padding = new Thickness(0); swatch.Focusable = false;
                swatch.ToolTip = colorNames[i]; AutomationProperties.SetName(swatch, colorNames[i]); options.Children.Add(swatch);
            }
            var widths = new List<Button>();
            double[] sizes = { 2, 3, 5 }; string[] sizeNames = { "细线", "中线", "粗线" };
            for (int i = 0; i < sizes.Length; i++)
            {
                double current = sizes[i];
                Button button = null;
                button = LightTheme.Button("", delegate { thickness = current; foreach (var item in widths) item.Tag = item == button ? "primary" : "quiet"; }, i == 1 ? "primary" : "quiet");
                button.Width = 21; button.MinWidth = 0; button.Height = 32; button.Padding = new Thickness(0); button.Focusable = false;
                var dot = new Ellipse { Width = sizes[i] + 3, Height = sizes[i] + 3 };
                dot.SetBinding(Shape.FillProperty, new Binding("Foreground") { Source = button }); button.Content = dot;
                button.ToolTip = sizeNames[i]; AutomationProperties.SetName(button, sizeNames[i]); widths.Add(button); options.Children.Add(button);
            }
            rows.Children.Add(options);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(Divider());
            actions.Children.Add(AddButton("撤销", Undo)); actions.Children.Add(AddButton("重做", Redo));
            actions.Children.Add(AddButton("另存", SaveAs)); actions.Children.Add(AddButton("取消", Close));
            var done = AddButton("完成", Finish); done.Tag = "primary"; actions.Children.Add(done);
            rows.Children.Add(actions);
            toolbar = new Border { Child = rows, Background = Brushes.White, CornerRadius = new CornerRadius(9), Padding = new Thickness(5), Visibility = Visibility.Collapsed,
                BorderBrush = LightTheme.Brush("#DCE2EA"), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { Color = Color.FromRgb(25, 36, 50), BlurRadius = 16, ShadowDepth = 3, Opacity = 0.22 } };
            controls.Children.Add(toolbar);
            badge = new Border { Child = dimensions, Background = LightTheme.Brush("#DD20242C"), CornerRadius = new CornerRadius(4), IsHitTestVisible = false };
            controls.Children.Add(badge); controls.Children.Add(text);
            dimensions.Text = "点击窗口或拖拽选区 · Esc 取消"; Canvas.SetLeft(badge, 20); Canvas.SetTop(badge, 20);
            Surface.MouseLeftButtonDown += Down; Surface.MouseMove += Move; Surface.MouseLeftButtonUp += Up;
            MouseRightButtonUp += delegate { Close(); };
            text.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { CommitText(); e.Handled = true; } };
            PreviewKeyDown += Keys;
            SourceInitialized += delegate
            {
                WindowsNative.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height, 0);
            };
            Loaded += delegate
            {
                // 首次显示先完成窗口合成；激活、工具栏状态和光标命中放到
                // 输入优先级，避免这些工作拖慢全屏遮罩的首帧。
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    Activate(); WindowsNative.ActivateEditor(new WindowInteropHelper(this).Handle); Focus(); UpdateTools();
                    var cursor = WindowsNative.Cursor(); HoverAt(new Point(cursor.X - screen.Bounds.Left, cursor.Y - screen.Bounds.Top));
                }), DispatcherPriority.Input);
            };
            Closed += delegate { Surface.Picture = null; Surface.Marks.Clear(); Surface.Pending = null; redo.Clear(); completion.TrySetResult(null); };
        }

        Button AddButton(string name, Action action)
        {
            var icons = new Dictionary<string, string> {
                { "选择", "M4,2 L4,19 L8,15 L11,21 L14,20 L11,14 L17,14 Z" },
                { "矩形", "M4,5 H20 V19 H4 Z" }, { "圆形", "M12,4 A8,8 0 1 0 12,20 A8,8 0 1 0 12,4" },
                { "箭头", "M4,20 L19,5 M12,5 H19 V12" }, { "画笔", "M5,19 L7,15 L17,5 Q19,3 20,5 Q21,6 19,8 L9,18 Z M5,19 L4,21 L8,20" },
                { "文字", "M4,5 H20 M12,5 V20 M9,20 H15" }, { "马赛克", "M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H20 V20 H14 Z" },
                { "撤销", "M9,8 L5,12 L9,16 M5,12 H15 Q20,12 20,17" }, { "重做", "M15,8 L19,12 L15,16 M19,12 H9 Q4,12 4,17" },
                { "另存", "M12,3 V15 M8,11 L12,15 L16,11 M4,16 V20 H20 V16" },
                { "取消", "M6,6 L18,18 M18,6 L6,18" }, { "完成", "M4,12 L9,17 L20,6" }
            };
            var button = LightTheme.Button("", action, "quiet"); button.MinWidth = 0; button.Width = 32; button.Height = 32; button.Padding = new Thickness(0);
            button.Margin = new Thickness(1); button.Focusable = false; button.ToolTip = name;
            var path = new System.Windows.Shapes.Path { Data = Geometry.Parse(icons[name]), StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
            path.SetBinding(Shape.StrokeProperty, new Binding("Foreground") { Source = button });
            button.Content = new Viewbox { Width = 17, Height = 17, Child = path };
            AutomationProperties.SetName(button, name); Tools.Add(name, button); return button;
        }

        static Border Divider() { return new Border { Width = 1, Height = 20, Background = LightTheme.Brush("#E4E9EF"), Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }; }

        internal Task<BitmapSource> PickAsync() { Show(); return completion.Task; }

        internal static BitmapSource ReadScreen(Forms.Screen display) { return CaptureShotRules.ReadScreen(display); }

        Point Pixel(MouseEventArgs e)
        {
            var point = e.GetPosition(Surface);
            return new Point(Math.Max(0, Math.Min(Surface.Picture.PixelWidth, Math.Round(point.X * scale))), Math.Max(0, Math.Min(Surface.Picture.PixelHeight, Math.Round(point.Y * scale))));
        }

        void Down(object sender, MouseButtonEventArgs e)
        {
            CommitText(); start = Pixel(e); original = Selection;
            hovered = original.IsEmpty || !original.Contains(start) ? CaptureWindows.At(windows, start) : Rect.Empty;
            if (tool == "文字" && !Selection.IsEmpty && Selection.Contains(start))
            {
                textOrigin = start; Canvas.SetLeft(text, start.X / scale); Canvas.SetTop(text, start.Y / scale);
                text.Foreground = new SolidColorBrush(color); text.Visibility = Visibility.Visible; text.Focus(); return;
            }
            if (tool == "选择" || Selection.IsEmpty)
            {
                double grip = 8 * scale;
                edges = Selection.IsEmpty ? 0 : (Math.Abs(start.X - Selection.Left) < grip ? 1 : Math.Abs(start.X - Selection.Right) < grip ? 2 : 0)
                    | (Math.Abs(start.Y - Selection.Top) < grip ? 4 : Math.Abs(start.Y - Selection.Bottom) < grip ? 8 : 0);
                if (!Selection.IsEmpty) { var grips = Selection; grips.Inflate(grip, grip); if (!grips.Contains(start)) edges = 0; }
                moving = !Selection.IsEmpty && edges == 0 && Selection.Contains(start);
                selecting = true; toolbar.Visibility = Visibility.Collapsed;
                if (!moving && edges == 0) Surface.Selection = new Rect(start, start);
            }
            else if (Selection.Contains(start))
            {
                drawing = true; redo.Clear();
                Surface.Pending = new CaptureMark { Tool = tool, Start = start, End = start, Color = color, Size = thickness * scale };
                Surface.Pending.Points.Add(start);
            }
            Surface.CaptureMouse(); e.Handled = true;
        }

        void Move(object sender, MouseEventArgs e)
        {
            var point = Pixel(e);
            if (!selecting && !drawing) { if (Selection.IsEmpty) HoverAt(point); return; }
            Surface.SetCursor(point, false);
            if (selecting)
            {
                var bounds = new Rect(0, 0, Surface.Picture.PixelWidth, Surface.Picture.PixelHeight);
                if (moving)
                    Surface.Selection = new Rect(Math.Max(0, Math.Min(bounds.Width - original.Width, original.X + point.X - start.X)),
                        Math.Max(0, Math.Min(bounds.Height - original.Height, original.Y + point.Y - start.Y)), original.Width, original.Height);
                else if (edges != 0)
                    Surface.Selection = CaptureSurface.Region(new Point((edges & 1) != 0 ? point.X : original.Left, (edges & 4) != 0 ? point.Y : original.Top),
                        new Point((edges & 2) != 0 ? point.X : original.Right, (edges & 8) != 0 ? point.Y : original.Bottom), bounds);
                else Surface.Selection = CaptureSurface.Region(start, point, bounds);
            }
            if (drawing)
            {
                Surface.Pending.End = new Point(Math.Max(Selection.Left, Math.Min(Selection.Right, point.X)), Math.Max(Selection.Top, Math.Min(Selection.Bottom, point.Y)));
                Surface.Pending.Points.Add(Surface.Pending.End);
            }
            UpdateSelection(false);
        }

        void Up(object sender, MouseButtonEventArgs e)
        {
            if (!selecting && !drawing) return;
            Move(sender, e); selecting = moving = drawing = false; Surface.ReleaseMouseCapture();
            if (Surface.Pending != null) { Surface.Marks.Add(Surface.Pending); Surface.Pending = null; }
            if (Selection.Width < 4 || Selection.Height < 4) Surface.Selection = hovered;
            Surface.Hover = Rect.Empty;
            UpdateSelection(true);
        }

        internal void HoverAt(Point point)
        {
            Surface.SetCursor(point, Selection.IsEmpty);
            Rect candidate = CaptureWindows.At(windows, point);
            if (candidate == Surface.Hover) return;
            Surface.Hover = candidate;
        }

        internal void Select(Rect region)
        {
            Surface.Hover = Rect.Empty;
            Surface.Selection = CaptureSurface.Region(region.TopLeft, region.BottomRight, new Rect(0, 0, Surface.Picture.PixelWidth, Surface.Picture.PixelHeight));
            UpdateSelection(true);
        }

        void UpdateSelection(bool show)
        {
            Surface.SetCursorVisible(Selection.IsEmpty);
            Surface.Refresh(); UpdateTools();
            if (Selection.IsEmpty) { toolbar.Visibility = Visibility.Collapsed; return; }
            dimensions.Text = (int)Selection.Width + " × " + (int)Selection.Height;
            Canvas.SetLeft(badge, Math.Max(6, Selection.Left / scale)); Canvas.SetTop(badge, Math.Max(6, Selection.Top / scale - 30));
            if (!show) return;
            toolbar.Visibility = Visibility.Visible;
            toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double width = toolbar.DesiredSize.Width, height = toolbar.DesiredSize.Height;
            Canvas.SetLeft(toolbar, Math.Max(4, Math.Min(Width - width - 4, Selection.Right / scale - width)));
            double top = Selection.Bottom / scale + 10;
            if (top + height > Height - 4) top = Math.Max(4, Selection.Top / scale - height - 10);
            Canvas.SetTop(toolbar, top);
        }

        void UpdateTools()
        {
            foreach (var entry in Tools)
                if (new[] { "选择", "矩形", "圆形", "箭头", "画笔", "文字", "马赛克" }.Contains(entry.Key)) entry.Value.Tag = entry.Key == tool ? "primary" : "quiet";
            Tools["撤销"].IsEnabled = Surface.Marks.Count > 0; Tools["重做"].IsEnabled = redo.Count > 0;
        }

        void CommitText()
        {
            if (text.Visibility != Visibility.Visible) return;
            if (!string.IsNullOrWhiteSpace(text.Text))
            {
                Surface.Marks.Add(new CaptureMark { Tool = "文字", Text = text.Text, Start = textOrigin, End = textOrigin, Color = color, Size = thickness * scale }); redo.Clear();
            }
            text.Clear(); text.Visibility = Visibility.Collapsed; Surface.Refresh(); UpdateTools();
        }

        internal void Undo() { CommitText(); if (Surface.Marks.Count > 0) { redo.Push(Surface.Marks[Surface.Marks.Count - 1]); Surface.Marks.RemoveAt(Surface.Marks.Count - 1); } UpdateSelection(true); }
        internal void Redo() { if (redo.Count > 0) Surface.Marks.Add(redo.Pop()); UpdateSelection(true); }

        void SaveAs()
        {
            CommitText();
            var picker = new SaveFileDialog { Title = "保存截图", Filter = "PNG 图片|*.png", FileName = "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png", AddExtension = true, DefaultExt = ".png" };
            if (picker.ShowDialog(this) != true) return;
            try { ImageEditFiles.Save(picker.FileName, ImageContent.EncodeClipboard(Surface.Export())); }
            catch (Exception error) { MessageBox.Show(this, "无法保存截图：" + error.Message, "图绳", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        void Finish()
        {
            if (finished || Selection.IsEmpty) return;
            CommitText(); var result = Surface.Export(); finished = true; completion.TrySetResult(result); Close();
        }

        void Keys(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (!text.IsKeyboardFocused && e.Key == Key.Enter) { e.Handled = true; Finish(); }
            else if (!text.IsKeyboardFocused && Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { e.Handled = true; Undo(); }
            else if (!text.IsKeyboardFocused && Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) { e.Handled = true; Redo(); }
        }
    }
}
