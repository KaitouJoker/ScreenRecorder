using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;

namespace ScreenRecorder
{
    /// <summary>
    /// App.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class App : Application
    {
        public static volatile Mutex Mutex = null;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 외부 보안 에이전트(StarPlayerAgent 등)가 TerminateProcess로 프로세스를 강제 종료하지 못하도록 DACL 보호 설정
            ProcessProtection.ProtectFromTermination();

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                try
                {
                    var ex = args.ExceptionObject as Exception;
                    var logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                    System.IO.File.WriteAllText(logPath, $"[Fatal Crash {DateTime.Now}]:\n{ex}");
                }
                catch { }
            };

            DispatcherUnhandledException += (s, args) =>
            {
                try
                {
                    var logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");
                    System.IO.File.WriteAllText(logPath, $"[Dispatcher Crash {DateTime.Now}]:\n{args.Exception}");
                }
                catch { }
            };

#if DEBUG
            ScreenRecorder.Properties.Resources.Culture = new System.Globalization.CultureInfo("en-US");
#endif
            try
            {
                Mutex = new Mutex(true, AppConstants.AppName, out bool isNew);
                if (isNew)
                {
                    AppManager.Instance.Initialize();

                    base.OnStartup(e);
                }
                else
                {
                    MessageBox.Show("The program is already running", "", MessageBoxButton.OK, MessageBoxImage.Error);
                    Environment.Exit(-1);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "", MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.Exit(-10);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AppCommands.Instance.Dispose();
            AppConfig.Instance.Dispose();
            AppManager.Instance.Dispose();

            base.OnExit(e);
        }
    }
}
