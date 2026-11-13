using System;
using DiskSentinel.Services;
using Microsoft.UI.Xaml;

namespace DiskSentinel;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        BootTask.Dbg("OnLaunched 被调用");
        // 开机静默检测模式：读一遍、落盘、立刻退出，不创建任何窗口。
        if (BootTask.IsBootCheckMode)
        {
            BootTask.Dbg("确认进入开机检测模式");
            BootTask.RunBootCheckAndExit();
            return;
        }

        BootTask.Dbg("进入普通 GUI 模式");
        _window = new MainWindow();
        _window.Activate();
    }
}
