using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenLine
{
    internal sealed class CleanupWindow : Window
    {
        readonly StateStore store;
        readonly Func<long> usage;
        readonly Func<Task<CleanupResult>> check;
        internal readonly CheckBox EnabledBox;
        internal readonly TextBox DaysBox, ThresholdBox;
        internal readonly TextBlock UsageText, StatusText;
        internal readonly Button SaveButton;
        bool closed;
        int usageVersion;

        public CleanupWindow(StateStore store, Func<long> usage, Func<Task<CleanupResult>> check)
        {
            this.store = store;
            this.usage = usage;
            this.check = check;
            Title = "存储与清理";
            Width = 520; SizeToContent = SizeToContent.Height;
            MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height - 40);
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; Topmost = true;
            FontFamily = new FontFamily("Segoe UI");
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var body = new StackPanel();
            var heading = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            heading.Children.Add(new TextBlock { Text = "存储与清理", FontSize = 21, FontWeight = FontWeights.SemiBold, Foreground = LightTheme.Brush("#283343") });
            var close = LightTheme.Button("", Close);
            close.Content = LightTheme.CloseIcon(); close.Width = close.Height = 28; close.MinWidth = 0;
            close.Padding = new Thickness(0); close.Tag = "quiet"; close.HorizontalAlignment = HorizontalAlignment.Right;
            AutomationProperties.SetName(close, "关闭存储与清理");
            heading.Children.Add(close);
            heading.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args) { if (args.OriginalSource is TextBlock) DragMove(); };
            body.Children.Add(heading);
            body.Children.Add(new TextBlock
            {
                Text = "只清理 ShotShelf 收集目录中的截图；图片篮子中的全部图片、桌面挂图和编辑器图片会保留。",
                FontSize = 12, Foreground = LightTheme.Brush("#65717F"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16)
            });
            UsageText = new TextBlock { Text = "当前截图占用 正在计算…", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = LightTheme.Brush("#283343"), Margin = new Thickness(0, 0, 0, 14) };
            body.Children.Add(UsageText);
            EnabledBox = new CheckBox { Content = "自动清理截图", FontSize = 13, Foreground = LightTheme.Brush("#283343"), VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(EnabledBox, "cleanup-enabled");
            body.Children.Add(EnabledBox);
            body.Children.Add(new TextBlock { Text = "开启后每小时检查一次，达到条件的旧文件会移到回收站，清空回收站后才会释放磁盘空间。", FontSize = 11, Foreground = LightTheme.Brush("#65717F"), Margin = new Thickness(22, 3, 0, 14) });
            var fields = new Grid();
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            fields.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
            fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var daysLabel = new TextBlock { Text = "保留天数", FontSize = 13, Foreground = LightTheme.Brush("#343D49"), VerticalAlignment = VerticalAlignment.Center };
            fields.Children.Add(daysLabel);
            DaysBox = Field();
            AutomationProperties.SetAutomationId(DaysBox, "cleanup-days");
            Grid.SetColumn(DaysBox, 1);
            fields.Children.Add(DaysBox);
            var daysUnit = new TextBlock { Text = "天", FontSize = 12, Foreground = LightTheme.Brush("#65717F"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            Grid.SetColumn(daysUnit, 2);
            fields.Children.Add(daysUnit);
            var thresholdLabel = new TextBlock { Text = "占用阈值", FontSize = 13, Foreground = LightTheme.Brush("#343D49"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(thresholdLabel, 2);
            fields.Children.Add(thresholdLabel);
            ThresholdBox = Field();
            AutomationProperties.SetAutomationId(ThresholdBox, "cleanup-threshold");
            Grid.SetRow(ThresholdBox, 2); Grid.SetColumn(ThresholdBox, 1);
            fields.Children.Add(ThresholdBox);
            var thresholdUnit = new TextBlock { Text = "MB", FontSize = 12, Foreground = LightTheme.Brush("#65717F"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            Grid.SetRow(thresholdUnit, 2); Grid.SetColumn(thresholdUnit, 2);
            fields.Children.Add(thresholdUnit);
            body.Children.Add(fields);
            body.Children.Add(new TextBlock
            {
                Text = "超过保留天数的旧截图先删除；仍超出阈值时，按最旧优先继续删除。",
                FontSize = 11, Foreground = LightTheme.Brush("#65717F"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0)
            });
            StatusText = new TextBlock { Text = "", FontSize = 12, Foreground = LightTheme.Brush("#65717F"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
            body.Children.Add(StatusText);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            var cancel = LightTheme.Button("取消", Close);
            cancel.Margin = new Thickness(0, 0, 8, 0);
            actions.Children.Add(cancel);
            SaveButton = LightTheme.Button("保存并检查", Save, "primary");
            AutomationProperties.SetAutomationId(SaveButton, "cleanup-save");
            actions.Children.Add(SaveButton);
            body.Children.Add(actions);
            var card = LightTheme.Card(body);
            card.Padding = new Thickness(24);
            Content = new ScrollViewer
            {
                Content = card,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height - 120)
            };
            EnabledBox.IsChecked = store.State.AutoCleanEnabled;
            DaysBox.Text = store.State.AutoCleanDays.ToString();
            ThresholdBox.Text = store.State.AutoCleanThresholdMb.ToString();
            Closed += delegate { closed = true; usageVersion++; };
            RefreshUsage();
            PreviewKeyDown += delegate(object sender, KeyEventArgs args) { if (args.Key == Key.Escape) Close(); };
        }

        static TextBox Field()
        {
            return new TextBox
            {
                Width = 86, Height = 30, FontSize = 13, Padding = new Thickness(8, 5, 8, 5),
                Foreground = LightTheme.Brush("#283343"), Background = Brushes.White,
                BorderBrush = LightTheme.Brush("#DCE2EA"), BorderThickness = new Thickness(1),
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        async void RefreshUsage()
        {
            int version = ++usageVersion;
            UsageText.Text = "当前截图占用 正在计算…";
            try
            {
                long bytes = await Task.Run(() => usage == null ? 0 : usage());
                if (closed || version != usageVersion) return;
                UsageText.Text = "当前截图占用 " + CleanupRules.FormatBytes(bytes);
            }
            catch (Exception error)
            {
                if (closed || version != usageVersion) return;
                store.Log(error);
                UsageText.Text = "当前截图占用 无法读取";
            }
        }

        void Error(string message)
        {
            StatusText.Foreground = LightTheme.Brush(message == null ? "#65717F" : "#B4372E");
            StatusText.Text = message ?? "";
        }

        async void Save()
        {
            int days, threshold;
            if (!int.TryParse(DaysBox.Text.Trim(), out days) || days < 1 || days > CleanupRules.MaximumDays)
            {
                Error("保留天数需为 1 到 " + CleanupRules.MaximumDays + " 之间的整数。");
                DaysBox.Focus();
                return;
            }
            if (!int.TryParse(ThresholdBox.Text.Trim(), out threshold) || threshold < 1 || threshold > CleanupRules.MaximumThresholdMb)
            {
                Error("占用阈值需为 1 到 " + CleanupRules.MaximumThresholdMb + " MB 之间的整数。");
                ThresholdBox.Focus();
                return;
            }
            var state = store.State;
            bool previousEnabled = state.AutoCleanEnabled;
            int previousDays = state.AutoCleanDays, previousThreshold = state.AutoCleanThresholdMb;
            state.AutoCleanEnabled = EnabledBox.IsChecked == true;
            state.AutoCleanDays = days;
            state.AutoCleanThresholdMb = threshold;
            try { store.Save(); }
            catch (Exception error)
            {
                state.AutoCleanEnabled = previousEnabled;
                state.AutoCleanDays = previousDays;
                state.AutoCleanThresholdMb = previousThreshold;
                EnabledBox.IsChecked = previousEnabled;
                DaysBox.Text = previousDays.ToString();
                ThresholdBox.Text = previousThreshold.ToString();
                store.Log(error);
                Error("保存失败，请重试。");
                return;
            }
            Error(null);
            EnabledBox.IsChecked = state.AutoCleanEnabled;
            RefreshUsage();
            if (!state.AutoCleanEnabled) { StatusText.Text = "已保存，自动清理已关闭。"; return; }
            StatusText.Text = "已保存，正在检查…";
            CleanupResult result = null;
            if (check != null)
            {
                try { result = await check(); }
                catch (Exception error)
                {
                    store.Log(error);
                    if (closed) return;
                    RefreshUsage();
                    Error("保存成功，但清理检查未完成，请稍后重试。");
                    return;
                }
                if (closed) return;
            }
            RefreshUsage();
            StatusText.Foreground = LightTheme.Brush(result != null && (!result.ReachedThreshold || result.Failed || result.Closed) ? "#B4372E" : "#65717F");
            StatusText.Text = CleanupRules.Describe(result, state.AutoCleanThresholdMb);
        }
    }
}
