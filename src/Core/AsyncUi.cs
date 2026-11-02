using System;
using System.Threading;
using System.Windows.Forms;

namespace Curl2Url
{
    // 小工具：后台线程干活，结果回 UI 线程；界面已关闭时安静丢弃结果。
    internal static class AsyncUi
    {
        public static void Run(Control owner, Action work, Action<Exception> completed)
        {
            if (owner == null || work == null) return;
            Thread thread = new Thread(delegate()
            {
                Exception error = null;
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                Post(owner, completed, error);
            });
            thread.IsBackground = true;
            thread.Name = "curl2url-db";
            thread.Start();
        }

        private static void Post(Control owner, Action<Exception> completed, Exception error)
        {
            if (completed == null) return;
            try
            {
                if (owner.IsDisposed || !owner.IsHandleCreated) return;
                owner.BeginInvoke(new Action(delegate()
                {
                    if (owner.IsDisposed) return;
                    completed(error);
                }));
            }
            catch (Exception)
            {
                // 窗口正在销毁时 BeginInvoke 会抛异常，忽略即可
            }
        }
    }
}
