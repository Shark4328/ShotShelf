using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenLine
{
    internal sealed class ExportFailuresWindow : Window
    {
        readonly Func<string[], Task<ExportResult>> retry;
        readonly Action<ExportResult> completed;
        readonly StackPanel rows = new StackPanel();
        readonly TextBlock status = new TextBlock
        {
            FontSize = 12, Foreground = LightTheme.Brush("#65717F"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        };
        internal readonly Button CopyButton, RetryButton;
        readonly List<string> failures = new List<string>();
        readonly List<string> reasons = new List<string>();
        readonly List<string> paths = new List<string>();
        bool busy, closed;

        internal ExportFailuresWindow(ExportResult result, Func<string[], Task<ExportResult>> retry, Action<ExportResult> completed)
        {
            this.retry = retry;
            this.completed = completed;
            Title = "导出失败详情 · 图架";
            Width = 560; Height = 460; MinWidth = 460; MinHeight = 320;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false; Topmost = true;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            Background = LightTheme.Brush("#F4F6F9");
            AutomationProperties.SetName(this, "导出失败详情");

            var layout = new Grid { Margin = new Thickness(20) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.Children.Add(new TextBlock
            {
                Text = "导出失败详情", FontSize = 21, FontWeight = FontWeights.SemiBold,
                Foreground = LightTheme.Brush("#283343"), Margin = new Thickness(0, 0, 0, 6)
            });
            Grid.SetRow(status, 1); layout.Children.Add(status);

            var scroller = new ScrollViewer
            {
                Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MinHeight = 120, Margin = new Thickness(0, 0, 0, 14)
            };
            AutomationProperties.SetName(scroller, "导出失败文件列表");
            Grid.SetRow(scroller, 2); layout.Children.Add(scroller);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            CopyButton = LightTheme.Button("复制清单", Copy);
            CopyButton.Margin = new Thickness(0, 0, 8, 0);
            AutomationProperties.SetName(CopyButton, "复制失败清单");
            actions.Children.Add(CopyButton);
            RetryButton = LightTheme.Button("重试失败项", Retry, "primary");
            AutomationProperties.SetName(RetryButton, "重试导出失败项");
            actions.Children.Add(RetryButton);
            var close = LightTheme.Button("关闭", Close);
            close.Margin = new Thickness(8, 0, 0, 0);
            AutomationProperties.SetName(close, "关闭导出失败详情");
            actions.Children.Add(close);
            Grid.SetRow(actions, 3); layout.Children.Add(actions);

            Content = new Border
            {
                Background = Brushes.White, BorderBrush = LightTheme.Brush("#E0E4EA"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Child = layout
            };
            Render(result);
            Closed += delegate { closed = true; };
        }

        internal int FailureCount { get { return failures.Count; } }

        void Render(ExportResult result)
        {
            failures.Clear(); reasons.Clear(); paths.Clear(); rows.Children.Clear();
            if (result != null)
            {
                for (int index = 0; index < result.Failures.Count; index++)
                {
                    failures.Add(result.Failures[index]);
                    reasons.Add(index < result.FailureReasons.Count ? result.FailureReasons[index] : "未知错误");
                    paths.Add(index < result.FailurePaths.Count ? result.FailurePaths[index] : null);
                }
            }
            for (int index = 0; index < failures.Count; index++)
            {
                string path = paths[index];
                var row = new Border
                {
                    Background = LightTheme.Brush(index % 2 == 0 ? "#F7F9FC" : "#FFFFFF"),
                    BorderBrush = LightTheme.Brush("#E8ECF1"), BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(10, 8, 10, 8)
                };
                var stack = new StackPanel();
                stack.Children.Add(new TextBlock
                {
                    Text = failures[index], FontSize = 13, FontWeight = FontWeights.SemiBold,
                    Foreground = LightTheme.Brush("#283343"), TextTrimming = TextTrimming.CharacterEllipsis
                });
                stack.Children.Add(new TextBlock
                {
                    Text = reasons[index], FontSize = 11, Foreground = LightTheme.Brush("#8A3E37"),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0)
                });
                row.Child = stack;
                if (!string.IsNullOrEmpty(path)) row.ToolTip = path;
                AutomationProperties.SetName(row, failures[index] + "：" + reasons[index]);
                rows.Children.Add(row);
            }
            if (failures.Count == 0)
            {
                rows.Children.Add(new TextBlock
                {
                    Text = "没有失败项。", FontSize = 13, Foreground = LightTheme.Brush("#65717F"),
                    Margin = new Thickness(10)
                });
            }
            status.Text = failures.Count == 0 ? "所有文件均已成功导出。"
                : "共 " + failures.Count + " 张失败。请检查文件是否被占用，或确认源文件仍在原位置。";
            RetryButton.IsEnabled = !busy && paths.Any(path => !string.IsNullOrEmpty(path) && File.Exists(path));
            RetryButton.Visibility = failures.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            CopyButton.IsEnabled = failures.Count > 0;
        }

        void Copy()
        {
            try
            {
                var lines = new List<string>();
                for (int index = 0; index < failures.Count; index++)
                    lines.Add((index + 1) + ". " + failures[index] + " — " + reasons[index]);
                Clipboard.SetText(string.Join(Environment.NewLine, lines.ToArray()));
                status.Foreground = LightTheme.Brush("#65717F");
                status.Text = "失败清单已复制到剪贴板。";
            }
            catch (Exception error)
            {
                status.Foreground = LightTheme.Brush("#B4372E");
                status.Text = "无法复制失败清单：" + error.Message;
            }
        }

        async void Retry()
        {
            if (busy || retry == null || paths.Count == 0) return;
            busy = true; RetryButton.IsEnabled = false; CopyButton.IsEnabled = false;
            status.Foreground = LightTheme.Brush("#65717F");
            status.Text = "正在重试失败项…";
            try
            {
                var result = await retry(paths.ToArray());
                if (closed) return;
                if (result == null)
                {
                    status.Foreground = LightTheme.Brush("#B4372E");
                    status.Text = "重试未完成，请稍后再试。";
                    return;
                }
                if (completed != null) completed(result);
                Render(result);
                if (result.Error != null)
                {
                    status.Foreground = LightTheme.Brush("#B4372E");
                    status.Text = result.Error;
                }
                else if (result.Failures.Count == 0)
                {
                    status.Foreground = LightTheme.Brush("#4F7A4A");
                    status.Text = "重试完成，成功导出 " + result.Succeeded + " 张。";
                }
                else
                {
                    status.Foreground = LightTheme.Brush("#B4372E");
                    status.Text = "重试完成，成功 " + result.Succeeded + " 张，仍有 " + result.Failures.Count + " 张失败。";
                }
            }
            catch (Exception error)
            {
                status.Foreground = LightTheme.Brush("#B4372E");
                status.Text = "重试未完成：" + error.Message;
            }
            finally
            {
                if (!closed)
                {
                    busy = false; CopyButton.IsEnabled = failures.Count > 0;
                    RetryButton.IsEnabled = paths.Any(path => !string.IsNullOrEmpty(path) && File.Exists(path));
                }
            }
        }
    }
}
