using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace WindowSpy
{
    public partial class App : Application
    {
        public App()
        {
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (!IsAdministrator())
            {
                // 如果不是管理员，尝试重启并请求权限
                var exeName = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                // 如果是 dotnet.exe 启动的（调试模式），可能不适合自动重启，避免死循环或参数丢失
                if (exeName != null && !exeName.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                {
                    var startInfo = new System.Diagnostics.ProcessStartInfo(exeName);
                    startInfo.UseShellExecute = true;
                    startInfo.Verb = "runas"; // 触发 UAC
                    try
                    {
                        System.Diagnostics.Process.Start(startInfo);
                        Application.Current.Shutdown();
                        return;
                    }
                    catch
                    {
                        // 用户取消或失败
                        MessageBox.Show("程序需要管理员权限才能正常运行，请以管理员身份运行。", "权限不足", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
        }

        private bool IsAdministrator()
        {
            try
            {
                var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            LogException(e.Exception);
            MessageBox.Show($"程序发生错误: {e.Exception.Message}\n详细信息请查看 jietu/error.log", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true; // 尝试防止崩溃
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogException(ex);
                MessageBox.Show($"程序发生严重错误: {ex.Message}\n详细信息请查看 jietu/error.log", "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LogException(Exception ex)
        {
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "jietu");
                Directory.CreateDirectory(dir);
                string logPath = Path.Combine(dir, "error.log");
                string content = $"[{DateTime.Now}] {ex.Message}\r\nStack Trace:\r\n{ex.StackTrace}\r\n\r\n";
                File.AppendAllText(logPath, content);
            }
            catch { }
        }
    }
}
