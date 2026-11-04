using System;

namespace ScreenLine
{
    // 启动初始化失败时统一记录日志、释放半初始化的控制器，并给出可理解的提示。
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
    }
}
