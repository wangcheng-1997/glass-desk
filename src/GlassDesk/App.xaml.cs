using System.Drawing;
using System.Windows;
using GlassDesk.Services;
using GlassDesk.UI;
using Forms = System.Windows.Forms;
using System.Windows.Threading;

namespace GlassDesk;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private Forms.NotifyIcon? _trayIcon;
    private GlassDeskController? _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstanceMutex = new Mutex(true, "GlassDesk.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Shutdown(2);
            return;
        }

        _controller = new GlassDeskController();
        _controller.StatusChanged += OnStatusChanged;
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = LoadApplicationIcon(),
            Text = "GlassDesk 窗口透明化",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _trayIcon.DoubleClick += (_, _) => ShowSettings();

        if (e.Args.Any(argument => string.Equals(argument, "--smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            var shutdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            shutdownTimer.Tick += (_, _) =>
            {
                shutdownTimer.Stop();
                Shutdown();
            };
            shutdownTimer.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_controller is not null)
        {
            _controller.StatusChanged -= OnStatusChanged;
            _controller.Dispose();
        }

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("增加透明度", null, (_, _) => _controller?.IncreaseOpacity());
        menu.Items.Add("降低透明度", null, (_, _) => _controller?.DecreaseOpacity());
        menu.Items.Add("恢复原始透明度", null, (_, _) => _controller?.ResetForeground());
        menu.Items.Add("应用默认透明度到当前窗口", null, (_, _) => _controller?.ApplyToForeground());
        menu.Items.Add("当前窗口信息", null, (_, _) => ShowForegroundInfo());
        menu.Items.Add("只读镜像模式", null, (_, _) => _controller?.StartMirrorForForeground());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        menu.Items.Add("退出 GlassDesk", null, (_, _) => Shutdown());
        return menu;
    }

    private void ShowSettings()
    {
        if (_controller is null) return;
        var window = new SettingsWindow(_controller);
        if (MainWindow is { IsVisible: true } owner && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }
        window.ShowDialog();
    }

    private static Icon LoadApplicationIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/GlassDesk;component/Assets/glassdesk-icon.ico"));
        if (resource is null) return SystemIcons.Application;

        using var stream = resource.Stream;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    private void ShowForegroundInfo()
    {
        var target = _controller?.GetForegroundTarget();
        if (target is null)
        {
            System.Windows.MessageBox.Show("当前没有可识别的前台窗口。", "GlassDesk", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var text = $"窗口：{target.Title}\n进程：{target.ProcessName}\n路径：{target.ProcessPath}\n"
            + $"类名：{target.WindowClassName}\nHWND：0x{target.Hwnd.ToInt64():X}\n"
            + $"完整性级别：{target.IntegrityLevel}\n受保护内容：{target.IsProtectedContent}";
        System.Windows.MessageBox.Show(text, "GlassDesk 当前窗口", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnStatusChanged(object? sender, string message)
    {
        _trayIcon?.ShowBalloonTip(1800, "GlassDesk", message, Forms.ToolTipIcon.Info);
    }
}
