using System;
using System.Windows.Threading;

namespace ScreenLine
{
    // 启动初始化失败时统一记录日志、释放半初始化的控制器，并给出非阻塞提示。
    internal static class StartupFailure
    {
        internal const string Title = "ShotShelf · 图架";
        internal const string Message = "ShotShelf 初始化失败，程序即将退出。\n请查看程序目录中 data 文件夹下的 error.log 了解详情。";

        internal static void Report(StateStore store, Exception error)
        {
            if (store == null || error == null) return;
            try { store.Log(error); }
            catch (Exception) { }
        }

        internal static void Dispose(StateStore store, IDisposable controller)
        {
            if (controller == null) return;
            try { controller.Dispose(); }
            catch (Exception error) { Report(store, error); }
        }

        // 用托盘气泡代替模态对话框：提示后自动退出，不等待用户点击，
        // 避免常驻托盘程序停在无人察觉的对话框上。
        internal static void Notify(Action shutdown)
        {
            System.Windows.Forms.NotifyIcon icon = null;
            try
            {
                icon = new System.Windows.Forms.NotifyIcon
                {
                    Icon = System.Drawing.SystemIcons.Error,
                    Visible = true,
                    BalloonTipTitle = Title,
                    BalloonTipText = Message,
                    BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Error
                };
                icon.ShowBalloonTip(5000);
            }
            catch (Exception)
            {
                if (icon != null) { icon.Visible = false; icon.Dispose(); icon = null; }
            }
            if (icon == null) { shutdown(); return; }
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += delegate
            {
                timer.Stop();
                icon.Visible = false;
                icon.Dispose();
                shutdown();
            };
            timer.Start();
        }
    }
}
