using System;
using System.Threading;
using System.Windows;

namespace MacroKeyboard
{
    public partial class App : System.Windows.Application
    {
        private static Mutex? _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            const string mutexName = "MacroKeyboard_SingleInstance_Mutex";

            _mutex = new Mutex(true, mutexName, out bool createdNew);

            if (!createdNew)
            {
                // 已有实例在运行
                System.Windows.MessageBox.Show("MacroKeyboard 已经在运行中。\n请检查系统托盘。",
                    "MacroKeyboard", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            base.OnExit(e);
        }
    }
}
