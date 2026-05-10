using System;
using System.Windows;

namespace MindustryLauncherGUI
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 捕获全局未处理的异常
            AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
            {
                Exception ex = (Exception)args.ExceptionObject;
                MessageBox.Show($"程序崩溃！详细原因：\n\n{ex.Message}\n\n堆栈追踪：\n{ex.StackTrace}", "致命错误");
            };

            base.OnStartup(e);
        }
    }
}