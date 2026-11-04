using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace ScreenLine
{
    internal static class PictureBasket
    {
        static readonly Dictionary<string, BitmapSource> pictures = new Dictionary<string, BitmapSource>();
        // 原图中的裁切边界与穿绳支点，保证换皮肤后挂钩仍贴住绳子。
        static readonly Int32Rect[] bounds = {
            new Int32Rect(40, 90, 1170, 1045), new Int32Rect(50, 105, 1150, 1000),
            new Int32Rect(110, 100, 1050, 975), new Int32Rect(20, 115, 1220, 1005),
            new Int32Rect(50, 95, 1160, 1020)
        };
        static readonly Point[] hooks = { new Point(592, 155), new Point(584, 165), new Point(565, 178), new Point(599, 175), new Point(573, 155) };
        static readonly double[] rims = { 735, 710, 740, 710, 700 };

        public static BitmapSource Source(LineTheme theme)
        {
            BitmapSource picture;
            if (pictures.TryGetValue(theme.Id, out picture)) return picture;
            picture = SkinImages.Crop("ShotShelf.Basket." + theme.Id, bounds[Array.IndexOf(LineTheme.All, theme)]);
            pictures.Add(theme.Id, picture);
            return picture;
        }

        public static Point Hook(LineTheme theme)
        {
            int index = Array.IndexOf(LineTheme.All, theme);
            return new Point((hooks[index].X - bounds[index].X) * 112 / bounds[index].Width,
                (hooks[index].Y - bounds[index].Y) * 112 / bounds[index].Width);
        }

        public static Point Mouth(LineTheme theme)
        {
            int index = Array.IndexOf(LineTheme.All, theme);
            return new Point(56, (rims[index] - bounds[index].Y) * 112 / bounds[index].Width);
        }
    }

    internal sealed class BasketWindow : Window
    {
        static readonly ControlTemplate choiceTemplate = (ControlTemplate)XamlReader.Parse(@"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>
          <Border x:Name='Surface' Background='Transparent' BorderBrush='Transparent' BorderThickness='1' CornerRadius='8'><ContentPresenter HorizontalAlignment='Stretch'/></Border>
          <ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Background' Value='#F7F9FC'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");
        readonly Gallery gallery;
        readonly Action<bool> setAutoHang;
        readonly Action<CaptureItem> hang;
        readonly Action<ShotAction, CaptureItem> act;
        readonly HashSet<string> selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly UniformGrid thumbnails = new UniformGrid { Columns = 3 };
        readonly ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        readonly TextBlock empty = new TextBlock { TextAlignment = TextAlignment.Center, Foreground = LightTheme.Brush("#65717F"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        readonly TextBlock status = new TextBlock { FontSize = 14, Foreground = LightTheme.Brush("#283343"), VerticalAlignment = VerticalAlignment.Center };
        readonly Image preview = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(18, 10, 18, 12) };
        readonly TextBlock previewHint = new TextBlock { Text = "选择图片，查看大图", FontSize = 14, Foreground = LightTheme.Brush("#65717F"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock metadata = new TextBlock { FontSize = 12, Foreground = LightTheme.Brush("#65717F"), Margin = new Thickness(16, 0, 16, 14) };
        readonly Dictionary<string, Button> choices = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<CaptureItem, Image> pictures = new Dictionary<CaptureItem, Image>();
        readonly HashSet<Image> visiblePictures = new HashSet<Image>();
        readonly HashSet<Image> loadingPictures = new HashSet<Image>();
        static readonly SemaphoreSlim thumbnailSlots = new SemaphoreSlim(2, 2);
        static readonly SemaphoreSlim previewSlot = new SemaphoreSlim(1, 1);
        internal readonly Button PendingTab, AllTab, HangButton, DeleteButton, SelectAllButton, CopyButton, ExportButton, OpenExportButton, ShowFailuresButton;
        internal readonly CheckBox AutoHang;
        readonly TextBlock exportStatus;
        internal readonly ExportMenu ExportMenu;
        string lastExportOutput;
        ExportResult lastExportResult;
        string lastExportDestination;
        ExportScope lastExportScope;
        ExportFormat lastExportFormat;
        ExportFailuresWindow exportFailuresWindow;
        bool exporting, copying;
        bool pendingOnly = true, refreshing, mounting, closed;
        CaptureItem previewItem;
        ImageContent previewContent;
        int previewVersion;
        double thumbnailHeight = 140;
        internal int SelectedCount { get { return selected.Count; } }
        internal IEnumerable<string> InUsePaths
        {
            get
            {
                if (previewItem != null) yield return previewItem.Path;
                foreach (string path in selected) yield return path;
            }
        }
        internal Image Preview { get { return preview; } }
        internal int VisibleCount { get { return choices.Count; } }
        internal Button ChoiceFor(string path) { return choices[path]; }

        public BasketWindow(Gallery gallery, Action<bool> setAutoHang, Action<CaptureItem> hang, Action<ShotAction, CaptureItem> act)
        {
            this.gallery = gallery; this.setAutoHang = setAutoHang; this.hang = hang; this.act = act;
            Title = "图片篮子 · 图架";
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; Topmost = true;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            AutomationProperties.SetAutomationId(this, "PictureBasket");
            var screen = WindowsNative.ScreenAtPointer();
            double dpi = WindowsNative.MonitorScale(screen);
            Width = Math.Min(1040, screen.WorkingArea.Width / dpi - 32);
            Height = Math.Min(660, screen.WorkingArea.Height / dpi - 48);
            MinWidth = Math.Min(720, Width); MinHeight = Math.Min(420, Height);

            var body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(62) });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = 48 });
            body.RowDefinitions.Add(new RowDefinition());
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(68) });
            var card = LightTheme.Card(body); card.Padding = new Thickness(0); Content = card;

            var heading = new Grid { Margin = new Thickness(22, 0, 18, 0) };
            heading.ColumnDefinitions.Add(new ColumnDefinition());
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new TextBlock { Text = "图片篮子", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = LightTheme.Brush("#283343"), VerticalAlignment = VerticalAlignment.Center });
            heading.Children.Add(title);
            var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            tools.Children.Add(new TextBlock { Text = "新图片自动上绳", FontSize = 12, Foreground = LightTheme.Brush("#65717F"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            AutoHang = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = "关闭后，新图片仍收进图片篮子" };
            AutoHang.Template = (ControlTemplate)XamlReader.Parse(@"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='CheckBox'>
              <Border x:Name='Track' Width='38' Height='22' CornerRadius='11' Background='#B8C0CC' BorderBrush='Transparent' BorderThickness='1'>
                <Border x:Name='Knob' Width='16' Height='16' CornerRadius='8' Background='White' HorizontalAlignment='Left' Margin='2,0'/>
              </Border><ControlTemplate.Triggers>
                <Trigger Property='IsChecked' Value='True'><Setter TargetName='Track' Property='Background' Value='#2463AE'/><Setter TargetName='Knob' Property='HorizontalAlignment' Value='Right'/></Trigger>
                <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Track' Property='BorderBrush' Value='#283343'/></Trigger>
              </ControlTemplate.Triggers></ControlTemplate>");
            AutomationProperties.SetName(AutoHang, "新图片自动上绳");
            AutoHang.Checked += AutoChanged; AutoHang.Unchecked += AutoChanged;
            tools.Children.Add(AutoHang);
            var close = LightTheme.Button("", Close, "quiet"); close.Content = LightTheme.CloseIcon();
            close.Width = close.Height = 28; close.MinWidth = 0; close.Padding = new Thickness(0); close.Margin = new Thickness(22, 0, 0, 0);
            AutomationProperties.SetName(close, "关闭图片篮子"); tools.Children.Add(close); Grid.SetColumn(tools, 1); heading.Children.Add(tools);
            heading.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { if (e.OriginalSource is TextBlock) DragMove(); };
            body.Children.Add(new Border { BorderBrush = LightTheme.Brush("#E8EBEF"), BorderThickness = new Thickness(0, 0, 0, 1), Child = heading });

            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 6, 0, 8) };
            PendingTab = LightTheme.Button("待挂", delegate { pendingOnly = true; Refresh(); });
            AllTab = LightTheme.Button("全部", delegate { pendingOnly = false; Refresh(); });
            PendingTab.Margin = new Thickness(0, 0, 10, 0); tabs.Children.Add(PendingTab); tabs.Children.Add(AllTab);
            var tabBar = new Border { BorderBrush = LightTheme.Brush("#E8EBEF"), BorderThickness = new Thickness(0, 0, 0, 1), Child = tabs };
            Grid.SetRow(tabBar, 1); body.Children.Add(tabBar);

            var content = new Grid { ClipToBounds = true };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            var left = new Grid { Margin = new Thickness(16, 10, 12, 12), ClipToBounds = true };
            scroll.Content = thumbnails; left.Children.Add(scroll); left.Children.Add(empty); content.Children.Add(left);
            scroll.ScrollChanged += delegate { UpdateThumbnails(); };
            Loaded += delegate { UpdateThumbnails(); if (previewItem != null) ShowPreview(previewItem); };
            var previewBody = new Grid { ClipToBounds = true };
            previewBody.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
            previewBody.RowDefinitions.Add(new RowDefinition());
            previewBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            previewBody.Children.Add(new TextBlock { Text = "预览", Margin = new Thickness(16, 10, 0, 0), FontSize = 12, Foreground = LightTheme.Brush("#65717F") });
            Grid.SetRow(preview, 1); previewBody.Children.Add(preview);
            Grid.SetRow(previewHint, 1); previewBody.Children.Add(previewHint);
            Grid.SetRow(metadata, 2); previewBody.Children.Add(metadata);
            previewBody.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) { if (e.ClickCount == 2 && previewItem != null) act(ShotAction.Open, previewItem); };
            previewBody.ToolTip = "双击打开原图";
            var previewPanel = new Border { Background = LightTheme.Brush("#F1F4F8"), CornerRadius = new CornerRadius(9), Margin = new Thickness(12, 10, 20, 12), Child = previewBody };
            var right = new Border { BorderBrush = LightTheme.Brush("#E8EBEF"), BorderThickness = new Thickness(1, 0, 0, 0), Child = previewPanel };
            Grid.SetColumn(right, 1); content.Children.Add(right);
            Grid.SetRow(content, 2); body.Children.Add(content);

            var footer = new Grid { Margin = new Thickness(24, 0, 24, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            SelectAllButton = LightTheme.Button("全选", SelectAll);
            SelectAllButton.Background = LightTheme.Brush("#E4EEFC");
            SelectAllButton.BorderBrush = LightTheme.Brush("#8DB0DF");
            SelectAllButton.Foreground = LightTheme.Brush("#205CA7");
            SelectAllButton.MinWidth = 112; SelectAllButton.Height = 38;
            SelectAllButton.VerticalAlignment = VerticalAlignment.Center;
            SelectAllButton.Margin = new Thickness(0, 0, 16, 0); footer.Children.Add(SelectAllButton);
            status.TextTrimming = TextTrimming.CharacterEllipsis; status.VerticalAlignment = VerticalAlignment.Center;
            var information = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            information.Children.Add(status);
            var exportLine = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            exportStatus = new TextBlock { FontSize = 11, Foreground = LightTheme.Brush("#65717F"), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
            exportLine.Children.Add(exportStatus);
            OpenExportButton = LightTheme.Button("打开目录", OpenLastExport, "quiet");
            OpenExportButton.FontSize = 11; OpenExportButton.Height = 20; OpenExportButton.MinWidth = 0; OpenExportButton.Padding = new Thickness(6, 0, 0, 0);
            OpenExportButton.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(OpenExportButton, "打开导出目录");
            exportLine.Children.Add(OpenExportButton);
            ShowFailuresButton = LightTheme.Button("查看失败详情", ShowExportFailures, "quiet");
            ShowFailuresButton.FontSize = 11; ShowFailuresButton.Height = 20; ShowFailuresButton.MinWidth = 0;
            ShowFailuresButton.Padding = new Thickness(8, 0, 0, 0);
            ShowFailuresButton.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(ShowFailuresButton, "查看导出失败详情");
            exportLine.Children.Add(ShowFailuresButton);
            information.Children.Add(exportLine);
            Grid.SetColumn(information, 1); footer.Children.Add(information);
            CopyButton = LightTheme.Button("复制所选", CopySelected);
            CopyButton.Width = 112; CopyButton.Height = 38; CopyButton.MinWidth = 0;
            CopyButton.HorizontalAlignment = HorizontalAlignment.Right; CopyButton.VerticalAlignment = VerticalAlignment.Center;
            CopyButton.Margin = new Thickness(0, 0, 12, 0);
            AutomationProperties.SetName(CopyButton, "复制所选图片");
            CopyButton.ToolTip = "复制所选图片，可一次粘贴多张到微信、QQ";
            Grid.SetColumn(CopyButton, 2); footer.Children.Add(CopyButton);
            ExportButton = LightTheme.Button("导出", ShowExportMenu);
            ExportButton.Width = 96; ExportButton.Height = 38; ExportButton.MinWidth = 0;
            ExportButton.HorizontalAlignment = HorizontalAlignment.Right; ExportButton.VerticalAlignment = VerticalAlignment.Center;
            ExportButton.Margin = new Thickness(0, 0, 12, 0);
            AutomationProperties.SetName(ExportButton, "导出图片"); Grid.SetColumn(ExportButton, 3); footer.Children.Add(ExportButton);
            DeleteButton = LightTheme.Button("删除所选", DeleteSelected);
            DeleteButton.Width = 112; DeleteButton.Height = 38;
            DeleteButton.HorizontalAlignment = HorizontalAlignment.Right; DeleteButton.VerticalAlignment = VerticalAlignment.Center;
            DeleteButton.Margin = new Thickness(0, 0, 12, 0); DeleteButton.Foreground = LightTheme.Brush("#B74E4A");
            DeleteButton.Background = LightTheme.Brush("#FFF8F7"); DeleteButton.BorderBrush = LightTheme.Brush("#ECD6D2");
            DeleteButton.ToolTip = "收集副本移到回收站，外部原图只移除记录";
            AutomationProperties.SetName(DeleteButton, "删除所选图片"); Grid.SetColumn(DeleteButton, 4); footer.Children.Add(DeleteButton);
            HangButton = LightTheme.Button("挂上绳子", Mount, "primary"); HangButton.Width = 144; HangButton.Height = 38;
            HangButton.HorizontalAlignment = HorizontalAlignment.Right; HangButton.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(HangButton, "挂上绳子"); Grid.SetColumn(HangButton, 5); footer.Children.Add(HangButton);
            foreach (var button in new[] { PendingTab, AllTab, SelectAllButton, CopyButton, ExportButton, DeleteButton, HangButton })
            {
                button.FontWeight = FontWeights.Normal;
            }
            foreach (var button in new[] { SelectAllButton, CopyButton, ExportButton, DeleteButton, HangButton }) button.FontSize = 14;
            ExportMenu = new ExportMenu(BeginExport, OpenLastExport, delegate { return lastExportOutput; });
            ExportMenu.PlacementTarget = ExportButton; ExportMenu.Placement = PlacementMode.Top;
            ExportMenu.HorizontalOffset = 8; ExportMenu.VerticalOffset = 4;
            var bottom = new Border { BorderBrush = LightTheme.Brush("#E8EBEF"), BorderThickness = new Thickness(0, 1, 0, 0), Child = footer };
            Grid.SetRow(bottom, 3); body.Children.Add(bottom);
            SourceInitialized += delegate
            {
                int width = (int)Math.Round(Width * dpi), height = (int)Math.Round(Height * dpi);
                WindowsNative.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero,
                    screen.WorkingArea.Left + (screen.WorkingArea.Width - width) / 2,
                    screen.WorkingArea.Top + Math.Min((int)(190 * dpi), Math.Max(16, (screen.WorkingArea.Height - height) / 2)), width, height, 0x14);
            };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); };
            gallery.Changed += GalleryChanged;
            Closed += delegate { closed = true; CloseExportFailures(); gallery.Changed -= GalleryChanged; previewVersion++; preview.Source = null; foreach (var image in pictures.Values) image.Source = null; pictures.Clear(); visiblePictures.Clear(); };
            Refresh();
        }

        void AutoChanged(object sender, RoutedEventArgs e)
        {
            if (!refreshing && gallery.Store.State.AutoHang != (AutoHang.IsChecked == true)) setAutoHang(AutoHang.IsChecked == true);
        }

        void GalleryChanged() { if (!mounting) Refresh(); }

        void Refresh()
        {
            refreshing = true;
            AutoHang.IsChecked = gallery.Store.State.AutoHang;
            refreshing = false;
            selected.RemoveWhere(path => (pendingOnly && gallery.Contains(path)) || !gallery.Collected.Any(i => FileRules.SameFile(i.Path, path)));
            int pending = gallery.Collected.Count(i => !gallery.Contains(i.Path));
            PendingTab.Content = "待挂 " + pending; AllTab.Content = "全部 " + gallery.Collected.Count;
            PendingTab.Tag = pendingOnly ? "primary" : "quiet"; AllTab.Tag = pendingOnly ? "quiet" : "primary";
            double offset = scroll.VerticalOffset;
            thumbnails.Children.Clear(); choices.Clear(); pictures.Clear(); visiblePictures.Clear();
            var items = gallery.Collected.Where(i => !pendingOnly || !gallery.Contains(i.Path)).OrderByDescending(i => i.Written).ToArray();
            foreach (var item in items) AddChoice(item);
            Dispatcher.BeginInvoke(new Action(UpdateThumbnails), System.Windows.Threading.DispatcherPriority.Loaded);
            empty.Text = gallery.Collected.Count == 0 ? "图片篮子还是空的\n截图收集后会出现在这里" : "图片都挂好了\n切换到「全部」查看收集的图片";
            empty.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            scroll.Visibility = items.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            scroll.ScrollToVerticalOffset(offset);
            UpdateSelection();
            if (previewItem != null && !gallery.Collected.Contains(previewItem)) previewItem = null;
            if (previewItem == null) ShowPreview(items.FirstOrDefault());
            else if (previewContent != previewItem.Image) ShowPreview(previewItem);
        }

        void AddChoice(CaptureItem item)
        {
            bool mounted = gallery.Contains(item.Path);
            var stack = new StackPanel();
            var image = new Grid { Height = thumbnailHeight };
            var picture = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(7) }; pictures[item] = picture;
            image.Children.Add(picture);
            var mark = new Border { Width = mounted ? 54 : 21, Height = 21, CornerRadius = new CornerRadius(11),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(7),
                BorderThickness = new Thickness(1) };
            image.Children.Add(mark);
            var frame = new Border { Child = image, Background = LightTheme.Brush("#F1F4F8"), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(8) };
            stack.Children.Add(frame);
            stack.Children.Add(new TextBlock { Text = TimeLabel(item.Written), FontSize = 11, Foreground = LightTheme.Brush("#65717F"), Margin = new Thickness(3, 7, 0, 0) });
            var button = new Button { Style = LightTheme.ButtonStyle, Tag = "quiet", VerticalAlignment = VerticalAlignment.Top };
            Action updateChoice = delegate
            {
                bool chosen = selected.Contains(item.Path);
                mark.Visibility = chosen || mounted ? Visibility.Visible : Visibility.Collapsed;
                frame.BorderBrush = LightTheme.Brush(chosen ? "#2463AE" : button.IsKeyboardFocused ? "#6E97CB" : "#E5EAF1");
                mark.Background = LightTheme.Brush(chosen ? "#2463AE" : "#FFFFFF"); mark.BorderBrush = LightTheme.Brush(chosen ? "#2463AE" : "#9CAABD");
                if (mounted)
                {
                    mark.Width = chosen ? 68 : 54;
                    mark.Child = new TextBlock { Text = chosen ? "\u2713 已挂上" : "已挂上", FontSize = 10, Foreground = chosen ? Brushes.White : LightTheme.Brush("#65717F"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                }
                else mark.Child = chosen ? new TextBlock { Text = "\uE73E", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } : null;
                AutomationProperties.SetItemStatus(button, chosen ? (mounted ? "已选择，已挂上" : "已选择") : mounted ? "已挂上" : "待挂");
            };
            button.IsKeyboardFocusedChanged += delegate { updateChoice(); };
            button.Click += delegate
            {
                if (!selected.Add(item.Path)) selected.Remove(item.Path);
                ShowPreview(item); updateChoice(); UpdateSelection();
            };
            button.Content = stack; button.Height = Double.NaN; button.Padding = new Thickness(0); button.Margin = new Thickness(6, 0, 6, 16);
            button.Template = choiceTemplate;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.ToolTip = Path.GetFileName(item.Path);
            AutomationProperties.SetName(button, Path.GetFileName(item.Path));
            updateChoice();
            thumbnails.Children.Add(button); choices[item.Path] = button;
        }

        void UpdateThumbnails()
        {
            if (closed) return;
            if (scroll.ViewportHeight > 0 && pictures.Count > 0)
            {
                var first = pictures.First();
                var slot = (FrameworkElement)first.Value.Parent;
                var choice = choices[first.Key.Path];
                double spacing = choice.ActualHeight > 0 ? choice.ActualHeight - slot.ActualHeight + choice.Margin.Top + choice.Margin.Bottom : 44;
                int rows = Math.Max(1, (int)Math.Ceiling(scroll.ViewportHeight / 180));
                thumbnailHeight = Math.Max(40, Math.Ceiling(scroll.ViewportHeight / rows - spacing + 1));
                foreach (var picture in pictures.Values)
                    if (Math.Abs(((FrameworkElement)picture.Parent).Height - thumbnailHeight) > 0.5) ((FrameworkElement)picture.Parent).Height = thumbnailHeight;
            }
            var viewport = new Rect(0, 0, scroll.ActualWidth, scroll.ActualHeight);
            visiblePictures.Clear();
            foreach (var entry in pictures)
            {
                var picture = entry.Value;
                var frame = (FrameworkElement)picture.Parent;
                if (frame.ActualWidth == 0 || !frame.IsDescendantOf(scroll)) continue;
                var bounds = frame.TransformToAncestor(scroll).TransformBounds(new Rect(frame.RenderSize));
                if (!bounds.IntersectsWith(viewport)) { picture.Source = null; continue; }
                visiblePictures.Add(picture);
                if (picture.Source != null) continue;
                var cached = entry.Key.Image.CachedThumbnail;
                if (cached != null) { picture.Source = cached; continue; }
                if (loadingPictures.Add(picture)) LoadThumbnail(entry.Key, picture);
            }
        }

        bool ThumbnailCurrent(CaptureItem item, Image picture, ImageContent content)
        {
            Image current;
            return !closed && visiblePictures.Contains(picture) && item.Image == content && pictures.TryGetValue(item, out current) && current == picture;
        }

        async void LoadThumbnail(CaptureItem item, Image picture)
        {
            ImageContent content = item.Image;
            try
            {
                await thumbnailSlots.WaitAsync();
                try
                {
                    if (!ThumbnailCurrent(item, picture, content)) return;
                    var bitmap = await Task.Run(() => content.Thumbnail);
                    if (ThumbnailCurrent(item, picture, content)) picture.Source = bitmap;
                }
                finally { thumbnailSlots.Release(); }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (NotSupportedException) { }
            catch (FormatException) { }
            finally { loadingPictures.Remove(picture); }
        }

        internal static string TimeLabel(DateTime time)
        {
            if (time.Kind == DateTimeKind.Utc) time = time.ToLocalTime();
            return (time.Date == DateTime.Today ? "今天 " : time.ToString("MM-dd ")) + time.ToString("HH:mm");
        }

        async void ShowPreview(CaptureItem item)
        {
            int version = ++previewVersion;
            previewItem = item; previewContent = item == null ? null : item.Image;
            preview.Source = item == null ? null : item.Image.CachedThumbnail;
            previewHint.Visibility = preview.Source == null ? Visibility.Visible : Visibility.Collapsed;
            previewHint.Text = item == null ? "选择图片，查看大图" : "正在加载预览…";
            metadata.Text = item == null ? "" : TimeLabel(item.Written) + " · " + item.Image.PixelWidth + " × " + item.Image.PixelHeight;
            if (item == null || !IsLoaded) return;
            try
            {
                await previewSlot.WaitAsync();
                try
                {
                    if (closed || version != previewVersion) return;
                    ImageContent content = item.Image;
                    var visual = PresentationSource.FromVisual(this);
                    double scale = visual == null ? 1 : visual.CompositionTarget.TransformToDevice.M11;
                    var panel = (Grid)preview.Parent;
                    double width = Math.Max(1, (panel.ActualWidth - preview.Margin.Left - preview.Margin.Right) * scale);
                    double height = Math.Max(1, (panel.RowDefinitions[1].ActualHeight - preview.Margin.Top - preview.Margin.Bottom) * scale);
                    var picture = await Task.Run(() => ReadPreview(item.Path, content, width, height));
                    if (version == previewVersion) { preview.Source = picture; previewHint.Visibility = Visibility.Collapsed; }
                }
                finally { previewSlot.Release(); }
            }
            catch (IOException) { if (version == previewVersion) { preview.Source = null; previewHint.Text = "图片已移动或删除"; previewHint.Visibility = Visibility.Visible; } }
            catch (UnauthorizedAccessException) { if (version == previewVersion) { preview.Source = null; previewHint.Text = "暂时无法读取这张图片"; previewHint.Visibility = Visibility.Visible; } }
            catch (NotSupportedException) { if (version == previewVersion) { preview.Source = null; previewHint.Text = "暂时无法预览这张图片"; previewHint.Visibility = Visibility.Visible; } }
            catch (FormatException) { if (version == previewVersion) { preview.Source = null; previewHint.Text = "暂时无法预览这张图片"; previewHint.Visibility = Visibility.Visible; } }
        }

        static BitmapSource ReadPreview(string path, ImageContent content, double width, double height)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream;
                double ratio = Math.Min(1, Math.Min(width / content.PixelWidth, height / content.PixelHeight));
                if (width / content.PixelWidth < height / content.PixelHeight) bitmap.DecodePixelWidth = Math.Max(1, (int)Math.Ceiling(content.PixelWidth * ratio));
                else bitmap.DecodePixelHeight = Math.Max(1, (int)Math.Ceiling(content.PixelHeight * ratio));
                bitmap.EndInit();
                var color = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                int stride = color.PixelWidth * 4; var pixels = new byte[stride * color.PixelHeight];
                color.CopyPixels(pixels, stride, 0);
                var result = BitmapSource.Create(color.PixelWidth, color.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
                result.Freeze(); return result;
            }
        }

        void SelectAll()
        {
            bool clear = choices.Count > 0 && choices.Keys.All(selected.Contains);
            foreach (string path in choices.Keys)
                if (clear) selected.Remove(path); else selected.Add(path);
            Refresh();
        }

        void ShowExportMenu()
        {
            int chosen = gallery.Collected.Count(item => selected.Contains(item.Path));
            ExportMenu.SetCounts(chosen, gallery.Collected.Count);
            ExportMenu.IsOpen = true;
        }

        async void BeginExport(ExportScope scope, ExportFormat format)
        {
            if (exporting) return;
            // 先固定文件快照，期间即使篮子刷新或文件被清理也不改变本次导出范围。
            var items = (scope == ExportScope.All
                ? gallery.Collected.ToArray()
                : gallery.Collected.Where(item => selected.Contains(item.Path)).ToArray());
            if (items.Length == 0)
            {
                status.Text = scope == ExportScope.All ? "图片篮子还是空的" : "请先选择要导出的图片";
                return;
            }
            using (var picker = new Forms.FolderBrowserDialog { Description = "选择导出位置", ShowNewFolderButton = true })
            {
                if (picker.ShowDialog(new WindowHandleOwner(new WindowInteropHelper(this).Handle)) != Forms.DialogResult.OK) return;
                CloseExportFailures();
                lastExportResult = null;
                lastExportDestination = picker.SelectedPath;
                lastExportScope = scope;
                lastExportFormat = format;
                exporting = true; ExportButton.IsEnabled = false;
                exportStatus.Visibility = Visibility.Visible; OpenExportButton.Visibility = Visibility.Collapsed;
                ShowFailuresButton.Visibility = Visibility.Collapsed;
                exportStatus.ToolTip = null; exportStatus.Text = "正在导出 0/" + items.Length + "…";
                status.Text = "正在导出 " + items.Length + " 张图片";
                try
                {
                    var request = new ExportRequest { Scope = scope, Format = format, Destination = picker.SelectedPath, Files = items.Select(item => item.Path).ToArray() };
                    Action<int, int> report = delegate(int done, int total)
                    {
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            if (closed || !exporting) return;
                            exportStatus.Text = "正在导出 " + done + "/" + total + "…";
                        }));
                    };
                    var result = await Task.Run(() => ExportService.Run(request, report));
                    if (closed) return;
                    lastExportOutput = result.Output;
                    lastExportResult = result;
                    ExportMenu.Refresh();
                    if (result.Error != null)
                    {
                        exportStatus.Text = result.Error; exportStatus.ToolTip = result.Error;
                        OpenExportButton.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        string summary = "已导出 " + result.Succeeded + " 张";
                        if (result.Failures.Count > 0) summary += " · " + result.Failures.Count + " 张失败";
                        exportStatus.Text = summary;
                        exportStatus.ToolTip = result.Failures.Count == 0 ? result.Output
                            : summary + "\n" + string.Join("\n", result.Failures.ToArray());
                        OpenExportButton.Visibility = Visibility.Visible;
                    }
                    ShowFailuresButton.Visibility = result.Failures.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                }
                catch (Exception error)
                {
                    exportStatus.Text = "导出未完成：" + error.Message; exportStatus.ToolTip = exportStatus.Text;
                    OpenExportButton.Visibility = Visibility.Collapsed; ShowFailuresButton.Visibility = Visibility.Collapsed;
                }
                finally { exporting = false; if (!closed) ExportButton.IsEnabled = true; }
                // 导出结果保留在底部状态栏，不能被选中数量覆盖。
                UpdateSelection(false);
            }
        }

        void CloseExportFailures()
        {
            var window = exportFailuresWindow;
            exportFailuresWindow = null;
            if (window == null) return;
            try { window.Close(); }
            catch (InvalidOperationException) { }
        }

        void ShowExportFailures()
        {
            if (lastExportResult == null || lastExportResult.Failures.Count == 0) return;
            if (exportFailuresWindow != null)
            {
                if (!exportFailuresWindow.IsVisible) exportFailuresWindow.Show();
                exportFailuresWindow.Activate();
                return;
            }
            var window = new ExportFailuresWindow(lastExportResult, RetryExport, ExportRetried);
            window.Owner = this;
            window.Closed += delegate { if (ReferenceEquals(exportFailuresWindow, window)) exportFailuresWindow = null; };
            exportFailuresWindow = window;
            window.Show();
        }

        Task<ExportResult> RetryExport(string[] candidates)
        {
            string destination = lastExportDestination;
            var files = (candidates ?? new string[0])
                .Where(path => !string.IsNullOrEmpty(path) && File.Exists(path))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length == 0 || string.IsNullOrEmpty(destination))
                return Task.FromResult(new ExportResult { Error = "失败文件已不在原位置，无法重试。" });
            var request = new ExportRequest
            {
                Scope = lastExportScope, Format = lastExportFormat,
                Destination = destination, Files = files
            };
            return Task.Run(() => ExportService.Run(request, null));
        }

        void ExportRetried(ExportResult result)
        {
            if (closed || result == null) return;
            lastExportResult = result;
            if (!string.IsNullOrEmpty(result.Output)) lastExportOutput = result.Output;
            ExportMenu.Refresh();
            exportStatus.Visibility = Visibility.Visible;
            OpenExportButton.Visibility = result.Output != null && result.Error == null ? Visibility.Visible : Visibility.Collapsed;
            ShowFailuresButton.Visibility = result.Failures.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (result.Error != null)
            {
                exportStatus.Text = result.Error;
                exportStatus.ToolTip = exportStatus.Text;
                return;
            }
            string summary = "重试完成，成功导出 " + result.Succeeded + " 张";
            if (result.Failures.Count > 0) summary += " · " + result.Failures.Count + " 张仍失败";
            exportStatus.Text = summary;
            exportStatus.ToolTip = result.Failures.Count == 0 ? result.Output
                : summary + "\n" + string.Join("\n", result.Failures.ToArray());
        }

        void OpenLastExport()
        {
            string path = lastExportOutput;
            try
            {
                if (path != null && File.Exists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else if (path != null && Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                else
                {
                    exportStatus.Text = "导出目录已被移动或删除";
                    OpenExportButton.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception error) { exportStatus.Text = "无法打开导出目录：" + error.Message; }
        }

        async void CopySelected()
        {
            if (copying) return;
            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 与篮子列表一致：按修改时间从新到旧复制，粘贴后的顺序与界面相同。
            foreach (var item in gallery.Collected.Where(i => selected.Contains(i.Path)).OrderByDescending(i => i.Written))
            {
                if (seen.Add(item.Path)) paths.Add(item.Path);
            }
            var valid = new List<string>();
            int skipped = 0;
            foreach (string path in paths)
            {
                if (File.Exists(path)) valid.Add(path); else skipped++;
            }
            if (valid.Count == 0)
            {
                status.Text = "所选文件已不存在";
                return;
            }
            copying = true; UpdateSelection(false);
            status.Text = "正在复制 " + valid.Count + " 张…";
            try
            {
                await WindowsClipboard.Write(ShotClipboard.Build(valid, valid.Count == 1));
                if (closed) return;
                status.Text = "已复制 " + valid.Count + " 张，可粘贴发送" + (skipped > 0 ? " · 跳过 " + skipped + " 张已不存在" : "");
            }
            catch (Exception error)
            {
                gallery.Store.Log(error);
                status.Text = "复制失败：" + error.Message;
            }
            finally
            {
                copying = false;
                if (!closed) UpdateSelection(false);
            }
        }

        void UpdateSelection(bool updateStatus = true)
        {
            SelectAllButton.IsEnabled = choices.Count > 0;
            SelectAllButton.Content = choices.Count > 0 && choices.Keys.All(selected.Contains) ? "取消全选" : "全选";
            int pending = selected.Count(path => !gallery.Contains(path));
            if (updateStatus) status.Text = "已选 " + selected.Count + " 张" + (pending < selected.Count ? " · " + pending + " 张待挂" : "");
            HangButton.IsEnabled = pending > 0;
            DeleteButton.IsEnabled = selected.Count > 0;
            CopyButton.IsEnabled = selected.Count > 0 && !copying;
            ExportMenu.SetCounts(gallery.Collected.Count(item => selected.Contains(item.Path)), gallery.Collected.Count);
        }

        void Mount()
        {
            var items = gallery.Collected.Where(i => selected.Contains(i.Path) && !gallery.Contains(i.Path)).ToArray();
            mounting = true;
            try { foreach (var item in items) hang(item); }
            finally { mounting = false; selected.Clear(); Refresh(); }
            status.Text = "已挂上 " + items.Count(i => gallery.Contains(i.Path)) + " 张";
        }

        void DeleteSelected()
        {
            var items = gallery.Collected.Where(item => selected.Contains(item.Path)).ToArray();
            if (items.Length == 0) return;
            mounting = true;
            try { foreach (var item in items) act(ShotAction.Trash, item); }
            finally { mounting = false; Refresh(); }
            int removed = items.Count(item => !gallery.Collected.Contains(item));
            status.Text = "已删除 " + removed + " 张" + (removed < items.Length ? " · " + (items.Length - removed) + " 张未删除" : "");
        }
    }

    internal sealed class WindowHandleOwner : Forms.IWin32Window
    {
        public IntPtr Handle { get; private set; }
        public WindowHandleOwner(IntPtr handle) { Handle = handle; }
    }
}
