using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace ScreenLine.Tests
{
    internal static class CaptureWindowTests
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string type, string title);
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
        static int checks;

        [STAThread]
        static void Main(string[] args)
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            LightTheme.Install(app);
            if (args.Contains("--fixture")) { app.Startup += delegate { Fixture(app); }; app.Run(); return; }
            app.Startup += async delegate
            {
                try { await Run(); Console.WriteLine("截图窗口：{0} 项验证通过", checks); }
                catch (Exception error) { Console.WriteLine("截图验证失败：" + error); Environment.ExitCode = 1; }
                finally { app.Shutdown(Environment.ExitCode); }
            };
            app.Run();
        }

        static void Fixture(Application app)
        {
            var screen = Forms.Screen.PrimaryScreen.Bounds;
            var lower = new Window { Title = "图架截图测试窗口甲", WindowStyle = WindowStyle.None, Background = Brushes.CornflowerBlue, Topmost = true, ShowInTaskbar = false };
            var upper = new Window { Title = "图架截图测试窗口乙", WindowStyle = WindowStyle.None, Background = Brushes.Coral, Topmost = true, ShowInTaskbar = false };
            lower.Show(); upper.Show();
            WindowsNative.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(lower).Handle, new IntPtr(-1), screen.Left + 100, screen.Top + 100, 300, 220, 0x10);
            WindowsNative.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(upper).Handle, new IntPtr(-1), screen.Left + 220, screen.Top + 150, 300, 220, 0x10);
            upper.Closed += delegate { lower.Close(); app.Shutdown(); };
        }

        static async Task Run()
        {
            var cursor = WindowsNative.Cursor(); IntPtr foreground = WindowsNative.GetForegroundWindow();
            var process = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, "--fixture") { UseShellExecute = false });
            CaptureWindow capture = null;
            try
            {
                for (int i = 0; i < 60 && FindWindow(null, "图架截图测试窗口乙") == IntPtr.Zero; i++) await Task.Delay(50);
                await Task.Delay(150);
                var screen = Forms.Screen.PrimaryScreen;
                int uiThread = Thread.CurrentThread.ManagedThreadId;
                var captureTimer = Stopwatch.StartNew();
                var shot = await CaptureShotRules.CaptureAsync(screen);
                captureTimer.Stop();
                Check(shot.ReadThreadId != uiThread, "整屏抓取在后台 STA 线程执行");
                Check(shot.Picture.PixelWidth == screen.Bounds.Width && shot.Picture.PixelHeight == screen.Bounds.Height, "后台抓取覆盖目标显示器完整像素");
                Check(shot.Windows.Count > 0, "窗口枚举快照随抓屏一次性返回");
                Check(captureTimer.ElapsedMilliseconds <= 300, "后台抓屏在 300ms 内完成，实际 " + captureTimer.ElapsedMilliseconds + "ms");
                var candidates = CaptureWindows.Snapshot(screen, 0);
                var work = screen.WorkingArea;
                var desktop = new Rect(work.Left - screen.Bounds.Left, work.Top - screen.Bounds.Top, work.Width, work.Height);
                Check(candidates.Count > 0 && candidates[candidates.Count - 1] == desktop, "桌面工作区作为窗口识别的最后候选项");
                var desktopOnly = new[] { new Rect(10, 10, 100, 100), desktop };
                Check(CaptureWindows.At(desktopOnly, new Point(desktop.Right - 2, desktop.Bottom - 2)) == desktop, "空白桌面选择桌面区域");
                Check(CaptureWindows.At(desktopOnly, new Point(20, 20)) == desktopOnly[0], "桌面区域不会遮盖前面的窗口");
                WindowsNative.Rectangle taskbar = new WindowsNative.Rectangle();
                IntPtr taskbarHandle = FindWindow("Shell_TrayWnd", null);
                Check(taskbarHandle != IntPtr.Zero && WindowsNative.GetWindowRect(taskbarHandle, out taskbar), "找到主屏任务栏");
                var taskbarBounds = new Rect(taskbar.Left - screen.Bounds.Left, taskbar.Top - screen.Bounds.Top,
                    taskbar.Right - taskbar.Left, taskbar.Bottom - taskbar.Top);
                var taskbarPoint = new Point(taskbarBounds.Left + taskbarBounds.Width / 2, taskbarBounds.Top + taskbarBounds.Height / 2);
                Check(CaptureWindows.At(candidates, taskbarPoint) == taskbarBounds, "识别实际任务栏边界");
                var lower = CaptureWindows.At(candidates, new Point(130, 130));
                var upper = CaptureWindows.At(candidates, new Point(250, 180));
                Check(lower.Left >= 100 && lower.Left < 120 && lower.Top == 100 && lower.Width > 250 && lower.Height > 180, "识别被部分遮挡的窗口边界");
                Check(upper.Left >= 220 && upper.Left < 240 && upper.Top == 150 && upper.Width > 250 && upper.Height > 180, "重叠位置优先识别前方窗口");
                Check(CaptureWindows.At(new[] { new Rect(-20, -10, 50, 50), new Rect(0, 0, 100, 100) }, new Point(10, 10)) == new Rect(-20, -10, 50, 50), "按窗口叠放顺序选择");
                int w = screen.Bounds.Width, h = screen.Bounds.Height;
                var pixels = new byte[w * h * 4];
                for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 185; pixels[i + 1] = 211; pixels[i + 2] = 225; pixels[i + 3] = 255; }
                var picture = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4); picture.Freeze();
                var narrow = new CaptureSurface { Picture = picture, Selection = new Rect(0, 0, 100, 100) };
                narrow.Marks.Add(new CaptureMark { Tool = "马赛克", Start = new Point(10, 10), End = new Point(12, 90), Size = 3 });
                Check(narrow.Export().PixelWidth == 100, "窄条马赛克可正常导出");
                var warmup = new CaptureWindow(screen, picture); warmup.Show(); warmup.UpdateLayout(); await Task.Delay(60); warmup.Close(); await Task.Delay(60);
                var blank = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4); blank.Freeze();
                var buildTimer = Stopwatch.StartNew();
                capture = new CaptureWindow(screen, blank);
                long buildMilliseconds = buildTimer.ElapsedMilliseconds;
                capture.Warm(); await Task.Delay(80);
                long staticBeforePrepare = capture.Surface.StaticLayerBuilds;
                capture.Prepare(picture, shot.Windows);
                Check(capture.Surface.StaticLayerBuilds == staticBeforePrepare + 1, "载入新截图只重建一次静态底图");
                var readyTimer = Stopwatch.StartNew();
                var completion = capture.PickAsync();
                long showMilliseconds = readyTimer.ElapsedMilliseconds;
                await Task.Delay(200);
                Console.WriteLine("截图性能：抓取 " + captureTimer.ElapsedMilliseconds + "ms，构建 " + buildMilliseconds + "ms，显示 " + showMilliseconds + "ms");
                Check(buildMilliseconds <= 50, "截图窗口构建不阻塞 UI 线程超过 50ms，实际 " + buildMilliseconds + "ms");
                Check(showMilliseconds <= 50, "遮罩显示不阻塞 UI 线程超过 50ms，实际 " + showMilliseconds + "ms");
                long staticBuilds = capture.Surface.StaticLayerBuilds;
                for (int i = 0; i < 6; i++)
                {
                    SetCursorPos(screen.Bounds.Left + 300 + i * 35, screen.Bounds.Top + 300 + i * 12);
                    await Task.Delay(30);
                }
                Check(capture.Surface.StaticLayerBuilds == staticBuilds, "连续移动鼠标只刷新动态层，静态底图不重建");
                string appSource = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "src", "App.cs"));
                string shelfSource = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "src", "ShelfWindow.cs"));
                Check(File.Exists(appSource) && File.Exists(shelfSource), "截图源码路径可正确定位");
                string source = File.ReadAllText(appSource), shelfCode = File.ReadAllText(shelfSource);
                Check(!source.Contains("Task.Delay(260)"), "截图流程不再固定等待 260ms");
                Check(source.Contains("Shelf.HideForCapture()") && source.Contains("await Shelf.WaitHidden()") && source.Contains("await CaptureSource(display)"), "抓屏在货架实际隐藏后交给可注入的后台源完成");
                Check(source.Contains("internal Func<Forms.Screen, Task<CaptureShot>> CaptureSource"), "截图源可注入以便验证失败与取消路径");
                Check(shelfCode.Contains("internal async Task<bool> WaitHidden()") && shelfCode.Contains("Task.Delay(600)") && shelfCode.Contains("completed == waiter.Task"),
                    "货架隐藏等待有 600ms 超时并返回真实完成状态");
                Check(shelfCode.Contains("internal void HideImmediately()") && shelfCode.Contains("internal void RestoreVisibility()"), "截图流程提供立即隐藏与可见性恢复");
                string pipelineSource = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "src", "CapturePipeline.cs"));
                Check(File.Exists(pipelineSource), "剪贴板管道源码可正确定位");
                string pipelineCode = File.ReadAllText(pipelineSource);
                Check(!pipelineCode.Contains("clipboardOrigin"), "剪贴板到达不再记录鼠标起点");
                Check(pipelineCode.Contains("Arrived(gallery.Items[gallery.Items.Count - 1], null)"), "剪贴板截图与内置截图共用同一到达动画");
                Check(shelfCode.Contains("origin.HasValue") && shelfCode.Contains("PlayFlight(new ShotFlight"), "文件夹导入仍保留飞入动画");
                string themeSource = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "src", "LightTheme.cs"));
                string flightSource = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "src", "ShotFlight.cs"));
                Check(File.Exists(themeSource) && File.Exists(flightSource), "渲染源码路径可正确定位");
                string themeCode = File.ReadAllText(themeSource), flightCode = File.ReadAllText(flightSource);
                Check(!themeCode.Contains("SoftwareOnly") && !themeCode.Contains("ProcessRenderMode"), "主题安装不再强制软件渲染，WPF 按系统能力使用 GPU");
                Console.WriteLine("渲染层级：Tier " + (RenderCapability.Tier >> 16));
                int visualStart = flightCode.IndexOf("void SetVisual(");
                int visualEnd = flightCode.IndexOf("static BitmapSource RenderPhoto(");
                Check(visualStart > 0 && visualEnd > visualStart, "飞行窗口的逐帧方法可定位");
                string visualCode = flightCode.Substring(visualStart, visualEnd - visualStart);
                Check(!visualCode.Contains("Width =") && !visualCode.Contains("Height =") && !visualCode.Contains("Margin =") && !visualCode.Contains("CornerRadius"),
                    "飞行逐帧只移动窗口，不修改布局属性");
                Check(!visualCode.Contains("new SolidColorBrush") && !visualCode.Contains("new RectangleGeometry") && !visualCode.Contains("DropShadowEffect"),
                    "飞行逐帧不新建画刷、裁剪几何或阴影");
                Check(flightCode.Contains("CompositionTarget.Rendering += Tick") && flightCode.IndexOf("CompositionTarget.Rendering += Tick") == flightCode.LastIndexOf("CompositionTarget.Rendering += Tick"),
                    "所有飞行卡片共用一条渲染循环");
                Check(flightCode.Contains("0x400"), "逐帧移动窗口时抑制 WM_WINDOWPOSCHANGING，避免每次 15ms 的 UI 停顿");
                SetCursorPos(screen.Bounds.Left + 250, screen.Bounds.Top + 180); await Task.Delay(80);
                mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(120);
                Check(capture.Selection == upper, "单击自动识别的窗口并建立选区");
                Check(capture.Surface.Export().PixelWidth == (int)upper.Width && capture.Surface.Export().PixelHeight == (int)upper.Height, "自动选区导出尺寸正确");
                Check(capture.Tools.Values.All(button => button.Content is Viewbox && button.ToolTip != null), "编辑工具使用图标并提供提示");
                SetCursorPos(screen.Bounds.Left + 130, screen.Bounds.Top + 130); await Task.Delay(40);
                mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(80);
                Check(capture.Selection == lower, "点击其他窗口可以切换选区");
                SetCursorPos(screen.Bounds.Left + 480, screen.Bounds.Top + 180); await Task.Delay(40);
                mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(80);
                Check(capture.Selection == upper, "切换窗口时保留前后顺序");
                capture.Tools["矩形"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                SetCursorPos(screen.Bounds.Left + 260, screen.Bounds.Top + 190); await Task.Delay(40); mouse_event(2, 0, 0, 0, UIntPtr.Zero);
                SetCursorPos(screen.Bounds.Left + 360, screen.Bounds.Top + 260); await Task.Delay(80); mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(80);
                Check(capture.Surface.Marks.Count == 1 && capture.Surface.Marks[0].Tool == "矩形", "图标工具可以绘制标注");
                capture.Undo(); Check(capture.Surface.Marks.Count == 0, "撤销图标对应操作正常");
                capture.Redo(); Check(capture.Surface.Marks.Count == 1, "重做图标对应操作正常");
                capture.Select(new Rect(200, 180, 680, 400));
                capture.UpdateLayout();
                var visual = new RenderTargetBitmap((int)capture.ActualWidth, (int)capture.ActualHeight, 96, 96, PixelFormats.Pbgra32); visual.Render(capture);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(visual));
                using (var file = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "capture-toolbar-check.png"))) encoder.Save(file);
                capture.Tools["完成"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var result = await completion;
                Check(result != null && result.PixelWidth == 680 && result.PixelHeight == 400, "完成截图返回编辑后的选区");
                capture = null;
                capture = new CaptureWindow(screen, picture); capture.Show(); await Task.Delay(100);
                SetCursorPos(screen.Bounds.Left + 50, screen.Bounds.Top + 400); await Task.Delay(40); mouse_event(2, 0, 0, 0, UIntPtr.Zero);
                SetCursorPos(screen.Bounds.Left + 190, screen.Bounds.Top + 510); await Task.Delay(80); mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(100);
                Check(capture.Selection == new Rect(50, 400, 140, 110), "拖拽仍可自由框选");
                capture.Close();
                capture = new CaptureWindow(screen, picture); capture.Show(); await Task.Delay(100);
                SetCursorPos(screen.Bounds.Left + (int)taskbarPoint.X, screen.Bounds.Top + (int)taskbarPoint.Y); await Task.Delay(80);
                mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); await Task.Delay(100);
                Check(capture.Selection == taskbarBounds, "单击任务栏即可选中完整区域");
                capture.Close(); capture = null;
                string shelfRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-data", "shelf-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(shelfRoot);
                var shelf = new ShelfWindow(new Gallery(new StateStore(shelfRoot)), delegate { }, delegate { });
                try
                {
                    shelf.Show(); shelf.Reveal(true, true, 60); await Task.Delay(350);
                    var hideTimer = Stopwatch.StartNew();
                    shelf.HideForCapture();
                    await shelf.WaitHidden();
                    hideTimer.Stop();
                    Check(!shelf.Revealed, "截图前货架已完成收起");
                    Check(hideTimer.ElapsedMilliseconds >= 80 && hideTimer.ElapsedMilliseconds <= 300,
                        "等待货架真实收起完成，实际 " + hideTimer.ElapsedMilliseconds + "ms");
                    shelf.HideImmediately();
                    Check(shelf.Visibility == Visibility.Hidden, "等待超时后的强制隐藏立即生效");
                    shelf.RestoreVisibility();
                    Check(shelf.Visibility == Visibility.Visible, "抓屏结束后恢复窗口可见性");
                }
                finally { shelf.Close(); }
            }
            finally
            {
                if (capture != null) capture.Close();
                if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(1500)) process.Kill(); }
                SetCursorPos(cursor.X, cursor.Y); if (foreground != IntPtr.Zero) WindowsNative.ActivateEditor(foreground);
                process.Dispose();
            }
        }

        static void Check(bool value, string description) { if (!value) throw new Exception(description); checks++; }
    }
}
