using System.Threading;
using System.Windows;
using MessageBox = ChineseBuddy.ThemedMessageBox;

namespace ChineseBuddy;

public partial class App
{
    private Mutex? instanceMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        instanceMutex = new Mutex(true, @"Local\ChineseBuddy.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("汉字小伙伴已经在运行，请使用已打开的窗口。", "汉字小伙伴");
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
