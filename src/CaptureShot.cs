using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace ScreenLine
{
    internal sealed class CaptureShot
    {
        public readonly BitmapSource Picture;
        public readonly List<Rect> Windows;
        public readonly int ReadThreadId;

        public CaptureShot(BitmapSource picture, List<Rect> windows, int readThreadId)
        {
            Picture = picture;
            Windows = windows;
            ReadThreadId = readThreadId;
        }
    }

    // 复用一条 STA 线程抓屏，避免在 UI 线程复制整屏像素。
    internal static class CaptureWorker
    {
        static readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();

        static CaptureWorker()
        {
            var thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "ShotShelf 截图抓取"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public static Task<T> Run<T>(Func<T> work)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Add(delegate
            {
                try { completion.TrySetResult(work()); }
                catch (Exception error) { completion.TrySetException(error); }
            });
            return completion.Task;
        }

        static void Loop()
        {
            foreach (var work in queue.GetConsumingEnumerable())
            {
                try { work(); }
                catch { }
            }
        }
    }

    internal static class CaptureShotRules
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmFlush();

        internal static Task<CaptureShot> CaptureAsync(Forms.Screen screen)
        {
            return CaptureWorker.Run(delegate
            {
                DwmFlush();
                var picture = ReadScreen(screen);
                var windows = CaptureWindows.Snapshot(screen);
                return new CaptureShot(picture, windows, Thread.CurrentThread.ManagedThreadId);
            });
        }

        internal static BitmapSource ReadScreen(Forms.Screen display)
        {
            var bounds = display.Bounds;
            int width = bounds.Width, height = bounds.Height;
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
                var bits = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                try
                {
                    int stride = bits.Stride;
                    var pixels = new byte[stride * height];
                    Marshal.Copy(bits.Scan0, pixels, 0, pixels.Length);
                    var image = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, pixels, stride);
                    image.Freeze();
                    return image;
                }
                finally { bitmap.UnlockBits(bits); }
            }
        }
    }
}
