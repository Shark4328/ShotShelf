using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ScreenLine.Tests
{
    // 验证 ShotShelf 与旧 PicRope 使用不同的实例互斥体和消息窗口，两个版本可以同时运行。
    internal static class CoexistenceTests
    {
        const string LegacyMutex = @"Local\TangHao.ScreenLine.Windows";
        const string LegacyWindow = "ScreenLine.Windows.Messages";
        const int LegacyExitMessage = 0x8002;
        static int checks;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string className, string title);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr w, IntPtr l);

        [STAThread]
        public static int Main(string[] arguments)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (arguments.Length > 0 && arguments[0].StartsWith("--hold-", StringComparison.Ordinal)) return Hold(arguments[0] == "--hold-legacy");
            try
            {
                Run().GetAwaiter().GetResult();
                Console.WriteLine("共存测试：{0} 项通过", checks);
                return 0;
            }
            catch (Exception error)
            {
                Console.WriteLine("共存测试失败：" + error);
                return 1;
            }
        }

        static int Hold(bool legacy)
        {
            bool created;
            using (var mutex = new Mutex(true, legacy ? LegacyMutex : WindowsNative.InstanceMutex, out created))
            {
                if (!created) { Console.WriteLine("BUSY"); return 3; }
                using (var source = new HwndSource(new HwndSourceParameters(legacy ? LegacyWindow : WindowsNative.MessageWindow) { ParentWindow = WindowsNative.MessageParent }))
                {
                    int exitMessage = legacy ? LegacyExitMessage : WindowsNative.ExitMessage;
                    source.AddHook(delegate(IntPtr window, int message, IntPtr w, IntPtr l, ref bool handled)
                    {
                        if (message == exitMessage) { Console.WriteLine("EXIT"); Console.Out.Flush(); Environment.Exit(0); }
                        return IntPtr.Zero;
                    });
                    Console.WriteLine("READY"); Console.Out.Flush();
                    var reader = new Thread(new ThreadStart(delegate
                    {
                        string line;
                        while ((line = Console.ReadLine()) != null) if (line == "QUIT") { source.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); return; }
                    }));
                    reader.IsBackground = true;
                    reader.Start();
                    Dispatcher.Run();
                }
            }
            return 0;
        }

        static async Task Run()
        {
            Check(WindowsNative.InstanceMutex != LegacyMutex && WindowsNative.MessageWindow != LegacyWindow,
                "ShotShelf 使用独立的实例互斥体与消息窗口名");

            using (var picRope = await StartHolder("--hold-legacy"))
            {
                bool created;
                using (var shotShelf = new Mutex(true, WindowsNative.InstanceMutex, out created))
                {
                    Check(created, "PicRope 先运行时 ShotShelf 仍可启动");
                    WindowsNative.RequestExit();
                    await Task.Delay(400);
                    Check(!picRope.HasExited, "ShotShelf 的退出消息不会发给 PicRope");
                }
                Check(FindWindowEx(WindowsNative.MessageParent, IntPtr.Zero, null, LegacyWindow) != IntPtr.Zero,
                    "旧版消息窗口仍然存在且未被占用");
                Stop(picRope);
            }

            using (var shotShelf = await StartHolder("--hold-new"))
            {
                bool created;
                using (var picRope = new Mutex(true, LegacyMutex, out created))
                    Check(created, "ShotShelf 先运行时 PicRope 的实例标识仍可获取");
                Stop(shotShelf);
            }

            VerifyOwnExit();

            using (var picRope = await StartHolder("--hold-legacy"))
            {
                IntPtr window = FindWindowEx(WindowsNative.MessageParent, IntPtr.Zero, null, LegacyWindow);
                Check(window != IntPtr.Zero, "旧版消息窗口仍能定位");
                SendMessage(window, LegacyExitMessage, IntPtr.Zero, IntPtr.Zero);
                for (int i = 0; i < 40 && !picRope.HasExited; i++) await Task.Delay(50);
                Check(picRope.HasExited, "模拟的 PicRope 进程可独立退出");
            }
        }

        static async Task<Process> StartHolder(string argument)
        {
            var start = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, argument)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8
            };
            var process = Process.Start(start);
            if (process == null) throw new Exception("无法启动共存测试进程");
            var read = process.StandardOutput.ReadLineAsync();
            if (await Task.WhenAny(read, Task.Delay(8000)) != read) { try { process.Kill(); } catch (Exception) { } throw new Exception("共存测试进程未就绪"); }
            string ready = read.Result;
            if (ready != "READY") { try { process.Kill(); } catch (Exception) { } throw new Exception("共存测试进程状态异常：" + ready); }
            return process;
        }

        static void VerifyOwnExit()
        {
            Exception failure = null;
            var thread = new Thread(new ThreadStart(delegate
            {
                try
                {
                    var dispatcher = Dispatcher.CurrentDispatcher;
                    bool exited = false;
                    using (var events = new WindowsEvents())
                    {
                        events.ExitRequested += delegate { exited = true; dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); };
                        var timeout = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, delegate { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }, dispatcher);
                        timeout.Start();
                        WindowsNative.RequestExit();
                        Dispatcher.Run();
                        timeout.Stop();
                    }
                    Check(exited, "--quit 的退出消息由本程序自己的消息窗口接收");
                }
                catch (Exception error) { failure = error; }
            }));
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) throw failure;
        }

        static void Stop(Process process)
        {
            try { if (!process.HasExited) { process.StandardInput.WriteLine("QUIT"); process.WaitForExit(3000); } }
            catch (Exception) { }
            try { if (!process.HasExited) process.Kill(); process.WaitForExit(3000); } catch (Exception) { }
        }

        static void Check(bool condition, string title)
        {
            if (!condition) throw new Exception(title);
            checks++; Console.WriteLine("通过：" + title);
        }
    }
}
