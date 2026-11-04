using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ScreenLine
{
    // 篮子页脚的导出下拉：先选范围，再选复制到文件夹或打包成 ZIP。
    internal sealed class ExportMenu : ContextMenu
    {
        static readonly Style submenuStyle = (Style)XamlReader.Parse(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='MenuItem'>
  <Setter Property='Foreground' Value='#283343'/><Setter Property='Cursor' Value='Hand'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='MenuItem'>
    <Grid>
      <Border x:Name='Row' Height='36' Padding='10,0' CornerRadius='7' Background='Transparent'>
        <Grid><Grid.ColumnDefinitions><ColumnDefinition Width='18'/><ColumnDefinition Width='*'/><ColumnDefinition Width='12'/></Grid.ColumnDefinitions>
          <Path x:Name='Mark' Data='M4,4 H20 V20 H4 Z' Stroke='#283343' StrokeThickness='1.65' Width='18' Height='18' Stretch='Uniform'/>
          <ContentPresenter Grid.Column='1' ContentSource='Header' Margin='10,0,0,0' VerticalAlignment='Center'/>
          <Path Grid.Column='2' Data='M9,4 L16,12 L9,20' Stroke='#65717F' StrokeThickness='1.65' Width='12' Height='12' Stretch='Uniform'/>
        </Grid>
      </Border>
      <Popup x:Name='PART_Popup' Placement='Right' AllowsTransparency='True' Focusable='False' IsOpen='{TemplateBinding IsSubmenuOpen}'>
        <Border Width='190' Padding='4' Background='White' BorderBrush='#E0E4EA' BorderThickness='1' CornerRadius='9'>
          <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle'/>
        </Border>
      </Popup>
    </Grid>
    <ControlTemplate.Triggers>
      <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Row' Property='Background' Value='#F0F3F7'/></Trigger>
      <Trigger Property='IsSubmenuOpen' Value='True'><Setter TargetName='Row' Property='Background' Value='#F0F3F7'/></Trigger>
      <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Row' Property='Background' Value='#EDF4FF'/></Trigger>
      <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.4'/></Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
</Style>");

        static readonly Style choiceStyle = (Style)XamlReader.Parse(@"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='MenuItem'>
  <Setter Property='Foreground' Value='#283343'/><Setter Property='Cursor' Value='Hand'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='MenuItem'>
    <Border x:Name='Row' Height='32' Padding='10,0' CornerRadius='7' Background='Transparent'>
      <ContentPresenter ContentSource='Header' VerticalAlignment='Center'/>
    </Border>
    <ControlTemplate.Triggers>
      <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Row' Property='Background' Value='#F0F3F7'/></Trigger>
      <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Row' Property='Background' Value='#EDF4FF'/></Trigger>
    </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
</Style>");

        internal readonly MenuItem SelectedMenu = new MenuItem { Header = "导出所选", Style = submenuStyle };
        internal readonly MenuItem AllMenu = new MenuItem { Header = "导出全部", Style = submenuStyle };
        internal readonly MenuItem OpenItem;
        readonly Func<string> lastOutputPath;
        readonly Menu selectedShell = new Menu { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0) };
        readonly Menu allShell = new Menu { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0) };
        readonly DispatcherTimer close = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        readonly TextBlock detail;

        internal ExportMenu(Action<ExportScope, ExportFormat> export, Action openLast, Func<string> lastOutput)
        {
            lastOutputPath = lastOutput;
            Width = 236; FontFamily = new FontFamily("Segoe UI"); FontSize = 13;
            Foreground = LightTheme.Brush("#283343"); HasDropShadow = false; SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            var panel = new StackPanel();
            var heading = new Grid { Margin = new Thickness(10, 5, 10, 7) };
            heading.Children.Add(new TextBlock { Text = "导出图片", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = LightTheme.Brush("#536172") });
            detail = new TextBlock { FontSize = 11, Foreground = LightTheme.Brush("#65717F"), HorizontalAlignment = HorizontalAlignment.Right };
            heading.Children.Add(detail); panel.Children.Add(heading);
            Build(SelectedMenu, selectedShell, ExportScope.Selected, export);
            Build(AllMenu, allShell, ExportScope.All, export);
            panel.Children.Add(selectedShell); panel.Children.Add(allShell);
            panel.Children.Add(new Border { Height = 1, Background = LightTheme.Brush("#E8EBEF"), Margin = new Thickness(6, 4, 6, 4) });
            OpenItem = new MenuItem { Header = "打开上次导出位置", Style = choiceStyle, IsEnabled = false };
            OpenItem.Click += delegate { IsOpen = false; openLast(); };
            panel.Children.Add(OpenItem);
            var surface = LightTheme.Card(panel); surface.Padding = new Thickness(4);
            Items.Add(surface);
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetBinding(ContentPresenter.ContentProperty, new System.Windows.Data.Binding("Items[0]") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            Template = new ControlTemplate(typeof(ContextMenu)) { VisualTree = content };
            Opened += delegate { Refresh(); };
            Closed += delegate { close.Stop(); SelectedMenu.IsSubmenuOpen = false; AllMenu.IsSubmenuOpen = false; };
            PreviewKeyDown += delegate(object sender, KeyEventArgs args) { if (args.Key == Key.Escape) { IsOpen = false; args.Handled = true; } };
        }

        void Build(MenuItem header, Menu shell, ExportScope scope, Action<ExportScope, ExportFormat> export)
        {
            header.Width = 190; header.FontFamily = FontFamily; header.FontSize = FontSize;
            AutomationProperties.SetName(header, scope == ExportScope.Selected ? "导出所选" : "导出全部");
            foreach (var entry in new[] {
                new KeyValuePair<ExportFormat, string>(ExportFormat.Folder, "复制到文件夹…"),
                new KeyValuePair<ExportFormat, string>(ExportFormat.Zip, "打包为 ZIP…") })
            {
                ExportFormat format = entry.Key;
                var child = new MenuItem { Header = entry.Value, Style = choiceStyle };
                AutomationProperties.SetName(child, (scope == ExportScope.Selected ? "所选" : "全部") + (format == ExportFormat.Folder ? "：复制到文件夹" : "：打包为 ZIP"));
                child.Click += delegate { IsOpen = false; export(scope, format); };
                child.MouseEnter += delegate { close.Stop(); };
                child.MouseLeave += delegate { close.Start(); };
                header.Items.Add(child);
            }
            header.MouseEnter += delegate { close.Stop(); header.IsSubmenuOpen = true; };
            header.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                close.Stop();
                if (!header.IsEnabled) return;
                header.IsSubmenuOpen = true;
                header.Dispatcher.BeginInvoke(new Action(delegate
                {
                    foreach (var entry in header.Items)
                    {
                        var child = entry as MenuItem;
                        if (child == null) continue;
                        child.Focus();
                        break;
                    }
                }), System.Windows.Threading.DispatcherPriority.Input);
                args.Handled = true;
            };
            header.MouseLeave += delegate { if (header.IsSubmenuOpen) close.Start(); };
            header.PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.OriginalSource == header && (args.Key == Key.Right || args.Key == Key.Enter || args.Key == Key.Space))
                {
                    header.IsSubmenuOpen = true;
                    foreach (var item in header.Items) { var child = item as MenuItem; if (child != null) { child.Focus(); break; } }
                    args.Handled = true;
                }
            };
            close.Tick += delegate { close.Stop(); header.IsSubmenuOpen = false; };
            shell.Items.Add(header);
        }

        internal void Refresh()
        {
            string path = lastOutputPath == null ? null : lastOutputPath();
            OpenItem.IsEnabled = path != null && (File.Exists(path) || Directory.Exists(path));
        }

        internal void SetCounts(int selected, int all)
        {
            detail.Text = "全部 " + all + " 张";
            SelectedMenu.Header = "导出所选（" + selected + " 张）";
            AllMenu.Header = "导出全部（" + all + " 张）";
            SelectedMenu.IsEnabled = selected > 0;
            AllMenu.IsEnabled = all > 0;
        }
    }
}
