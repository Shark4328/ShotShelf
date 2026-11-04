using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace ScreenLine
{
    // One CompositionTarget.Rendering subscription drives every flight. Each flight
    // owns a fixed-size layered window and two frozen bitmaps, so a animation frame
    // only moves the window and updates transform/opacity values.
    internal static class ShotFlightAnimator
    {
        static readonly List<ShotFlight> active = new List<ShotFlight>();
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static bool listening;
        static int hooks;

        public static int Count { get { return active.Count; } }
        public static bool Running { get { return listening; } }
        // Number of Rendering subscriptions; always 0 or 1 since flights share one loop.
        public static int Hooks { get { return hooks; } }
        public static double Now { get { return clock.Elapsed.TotalSeconds; } }

        public static void Add(ShotFlight flight)
        {
            if (active.Contains(flight)) return;
            active.Add(flight);
            if (listening) return;
            listening = true;
            CompositionTarget.Rendering += Tick;
            hooks++;
        }

        public static void Remove(ShotFlight flight)
        {
            if (!active.Remove(flight)) return;
            if (!listening || active.Count > 0) return;
            listening = false;
            CompositionTarget.Rendering -= Tick;
            hooks--;
        }

        static void Tick(object sender, EventArgs args)
        {
            double now = clock.Elapsed.TotalSeconds;
            ShotFlight[] snapshot = active.ToArray();
            for (int i = 0; i < snapshot.Length; i++) snapshot[i].Step(now);
        }
    }

    internal sealed class ShotFlight : Window
    {
        const double DesignLimit = 1024;
        const double BitmapMargin = 22;
        const double SideMargin = 24;
        const double TopMargin = 34;
        const double BottomMargin = 34;

        readonly ShotCard card;
        readonly Canvas stage = new Canvas(), frame = new Canvas();
        readonly Image photoLayer = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
        readonly Image chromeLayer = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
        readonly ClothesClip clip = new ClothesClip();
        readonly ScaleTransform zoom = new ScaleTransform(1, 1);
        readonly RotateTransform turn = new RotateTransform();
        readonly RectangleGeometry crop = new RectangleGeometry();
        readonly Action landed;
        readonly Func<Rect> basketTarget;
        readonly Rect screenBounds;
        readonly double scale, designWidth, designHeight, windowWidth, windowHeight;
        double startedAt;
        Rect start, target;
        BitmapSource chromeSource;
        IntPtr handle;
        bool registered, completed, arrived, placed;

        public bool Falling { get; private set; }
        public bool Collecting { get { return basketTarget != null; } }
        // Width the picture currently occupies on screen, in DIPs.
        public double VisualWidth { get { return designWidth * zoom.ScaleX; } }
        public double VisualHeight { get { return designHeight * zoom.ScaleY; } }

        public ShotFlight(Forms.Screen screen, ShotCard card, Rect origin, bool falling, Action landed, BitmapSource image = null, Func<Rect> basketTarget = null)
        {
            this.card = card;
            this.landed = landed;
            this.basketTarget = basketTarget;
            Falling = falling;
            ApplyTheme(card.Appearance);

            scale = WindowsNative.MonitorScale(screen);
            screenBounds = new Rect(screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height);
            start = Local(origin);
            target = start;
            double widest = start.Width, tallest = start.Height;
            if (!falling)
            {
                Rect now = Target();
                target = now;
                widest = Math.Max(widest, now.Width);
                tallest = Math.Max(tallest, now.Height);
            }
            widest = Math.Max(1, widest); tallest = Math.Max(1, tallest);
            double memory = Math.Min(1, DesignLimit / Math.Max(widest, tallest));
            designWidth = widest * memory; designHeight = tallest * memory;
            windowWidth = widest + SideMargin * 2; windowHeight = tallest + TopMargin + BottomMargin;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowActivated = false;
            ShowInTaskbar = false;
            Topmost = true;
            IsHitTestVisible = false;
            Width = windowWidth; Height = windowHeight;
            Content = stage;
            stage.Width = windowWidth; stage.Height = windowHeight;
            frame.Width = windowWidth; frame.Height = windowHeight;
            Canvas.SetLeft(frame, 0); Canvas.SetTop(frame, 0);
            var group = new TransformGroup();
            group.Children.Add(zoom); group.Children.Add(turn);
            zoom.CenterX = windowWidth / 2; zoom.CenterY = windowHeight / 2;
            turn.CenterX = windowWidth / 2; turn.CenterY = windowHeight / 2;
            frame.RenderTransform = group;
            stage.Children.Add(frame);

            BitmapSource picture = image ?? card.Item.Image.Thumbnail;
            double bitmapWidth = designWidth + BitmapMargin * 2, bitmapHeight = designHeight + BitmapMargin * 2;
            photoLayer.Source = RenderPhoto(picture, designWidth, designHeight);
            chromeSource = RenderChrome(picture, designWidth, designHeight);
            chromeLayer.Source = chromeSource;
            Canvas.SetLeft(photoLayer, (windowWidth - bitmapWidth) / 2);
            Canvas.SetTop(photoLayer, (windowHeight - bitmapHeight) / 2);
            Canvas.SetLeft(chromeLayer, (windowWidth - bitmapWidth) / 2);
            Canvas.SetTop(chromeLayer, (windowHeight - bitmapHeight) / 2);
            frame.Children.Add(photoLayer);
            frame.Children.Add(chromeLayer);
            frame.Children.Add(clip);
            Canvas.SetLeft(clip, windowWidth / 2 - 8);
            Canvas.SetTop(clip, (windowHeight - designHeight) / 2 - 20);

            Opacity = 0;
            SourceInitialized += delegate
            {
                if (completed) return;
                handle = new WindowInteropHelper(this).Handle;
                WindowsNative.PrepareOverlay(handle);
                WindowsNative.PassMouse(handle, true);
                startedAt = ShotFlightAnimator.Now;
                registered = true;
                ShotFlightAnimator.Add(this);
                Step(ShotFlightAnimator.Now);
            };
            Closed += delegate
            {
                if (registered) { registered = false; ShotFlightAnimator.Remove(this); }
                photoLayer.Source = null; chromeLayer.Source = null; chromeSource = null;
                Complete();
            };
        }

        Rect Local(Rect rectangle)
        {
            return new Rect((rectangle.X - screenBounds.X) / scale, (rectangle.Y - screenBounds.Y) / scale,
                rectangle.Width / scale, rectangle.Height / scale);
        }

        Rect Target()
        {
            if (Falling) return start;
            try { return Local(card.PhotoRect()); }
            catch (Exception) { return target; }
        }

        public void ApplyTheme(LineTheme theme) { clip.Apply(theme, card.ColorIndex); }

        void Complete()
        {
            if (completed) return;
            completed = true;
            if (Collecting && !arrived) return;
            if (landed != null) landed();
        }

        internal void Step(double now)
        {
            if (handle == IntPtr.Zero) return;
            double elapsed = Math.Max(0, now - startedAt);
            if (Collecting) { RenderCollection(elapsed); return; }

            double duration = Falling ? 0.55 : 0.65;
            double progress = Math.Min(1, elapsed / duration);
            double ease = progress * progress * progress * (10 + progress * (-15 + 6 * progress));
            if (progress < 1)
            {
                if (Falling)
                {
                    double width = start.Width, height = start.Height;
                    double cx = start.X + width / 2;
                    double cy = start.Y + height / 2 + 520 * ease;
                    double angle = card.Tilt + (card.Tilt < 0 ? -32 : 32) * ease;
                    chromeLayer.Opacity = 1; photoLayer.Opacity = 0; clip.Opacity = 1;
                    SetVisual(cx, cy, width, height, angle, 1 - ease);
                    return;
                }
                Rect ends = Target();
                double w = start.Width + (ends.Width - start.Width) * ease;
                double h = start.Height + (ends.Height - start.Height) * ease;
                double x = start.X + start.Width / 2 + (ends.X + ends.Width / 2 - start.X - start.Width / 2) * ease;
                double top = start.Y + (ends.Y - start.Y) * ease - 30 * Math.Sin(Math.PI * ease);
                double chrome = Math.Max(0, Math.Min(1, (ease - 0.3) / 0.6));
                chrome = chrome * chrome * (3 - 2 * chrome);
                chromeLayer.Opacity = chrome; photoLayer.Opacity = 1; clip.Opacity = chrome;
                SetVisual(x, top + h / 2, w, h, card.Tilt * ease, 1);
                return;
            }
            Complete();
            if (Falling) { Close(); return; }
            Opacity = Math.Max(0, 1 - (elapsed - duration) / 0.16);
            if (elapsed >= duration + 0.16) Close();
        }

        void RenderCollection(double elapsed)
        {
            double progress = Math.Min(1, elapsed / 0.64);
            double ease = progress * progress * progress * (10 + progress * (-15 + 6 * progress));
            Rect mouth;
            try { mouth = Local(basketTarget()); }
            catch (Exception) { mouth = target; }
            double smallWidth = Math.Min(22, Math.Max(10, start.Width * 0.15));
            double smallHeight = smallWidth * start.Height / start.Width;
            double width = start.Width + (smallWidth - start.Width) * ease;
            double height = start.Height + (smallHeight - start.Height) * ease;
            double startX = start.X + start.Width / 2, endX = mouth.X + mouth.Width / 2;
            double x = startX + (endX - startX) * ease;
            double lift = Math.Min(38, Math.Max(10, start.Y - 10));
            double startY = start.Y + start.Height / 2;
            double endY = mouth.Y + smallHeight / 2 + 4;
            double y = startY + (endY - startY) * ease - lift * Math.Sin(Math.PI * ease);
            double angle = card.Tilt * (1 - ease) + Math.Sign(endX - startX) * 10 * Math.Sin(Math.PI * ease);
            chromeLayer.Opacity = 1; photoLayer.Opacity = 0; clip.Opacity = 0;
            SetVisual(x, y, width, height, angle, 1 - 0.8 * Math.Max(0, (progress - 0.82) / 0.18));
            // Once the picture crosses the basket mouth, crop it instead of fading in front of the basket.
            double scaleY = height / designHeight;
            if (progress > 0.68 && Math.Abs(x - endX) < mouth.Width * 0.6 && scaleY > 0.0001)
            {
                double local = (mouth.Y - y) / scaleY + windowHeight / 2;
                crop.Rect = new Rect(0, 0, windowWidth, Math.Max(0, Math.Min(windowHeight, local)));
                frame.Clip = crop;
            }
            else frame.Clip = null;
            if (progress < 1) return;
            arrived = true; Complete(); Close();
        }

        void SetVisual(double cx, double cy, double width, double height, double angle, double opacity)
        {
            if (width < 1) width = 1;
            if (height < 1) height = 1;
            zoom.ScaleX = width / designWidth; zoom.ScaleY = height / designHeight; turn.Angle = angle;
            int widthPx = (int)Math.Round(windowWidth * scale), heightPx = (int)Math.Round(windowHeight * scale);
            int left = (int)Math.Round(screenBounds.X + (cx - windowWidth / 2) * scale);
            int top = (int)Math.Round(screenBounds.Y + (cy - windowHeight / 2) * scale);
            // SWP_NOSENDCHANGING suppresses WM_WINDOWPOSCHANGING, which otherwise costs
            // roughly 15 ms of UI-thread time on every animation frame.
            WindowsNative.SetWindowPos(handle, new IntPtr(-1), left, top, widthPx, heightPx, 0x10 | 0x4 | 0x1 | 0x400);
            if (!placed) { placed = true; }
            if (Math.Abs(Opacity - opacity) > 0.001) Opacity = opacity;
        }

        static BitmapSource RenderPhoto(BitmapSource image, double width, double height)
        {
            var visual = new DrawingVisual();
            using (DrawingContext painter = visual.RenderOpen())
            {
                Rect box = new Rect(BitmapMargin, BitmapMargin, width, height);
                painter.PushClip(new RectangleGeometry(box, 2, 2));
                painter.DrawImage(image, box);
                painter.Pop();
            }
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width + BitmapMargin * 2),
                (int)Math.Ceiling(height + BitmapMargin * 2), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        // Bake the white frame, rounded photo and soft shadow once. Animation then only
        // cross-fades this frozen layer over the plain picture.
        static BitmapSource RenderChrome(BitmapSource image, double width, double height)
        {
            double totalWidth = width + BitmapMargin * 2, totalHeight = height + BitmapMargin * 2;
            var root = new Grid { Width = totalWidth, Height = totalHeight, Background = Brushes.Transparent };
            var border = new Border
            {
                Width = width, Height = height, Margin = new Thickness(BitmapMargin),
                Background = Brushes.White, CornerRadius = new CornerRadius(12),
                BorderBrush = new SolidColorBrush(Color.FromRgb(220, 225, 233)), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 4, Opacity = 0.2, Color = Colors.Black }
            };
            var photo = new Image { Source = image, Stretch = Stretch.Fill, Margin = new Thickness(6) };
            double innerWidth = Math.Max(1, width - 14), innerHeight = Math.Max(1, height - 14);
            photo.Clip = new RectangleGeometry(new Rect(0, 0, innerWidth, innerHeight), 7, 7);
            border.Child = photo;
            root.Children.Add(border);
            root.Measure(new Size(totalWidth, totalHeight));
            root.Arrange(new Rect(0, 0, totalWidth, totalHeight));
            root.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(totalWidth), (int)Math.Ceiling(totalHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
