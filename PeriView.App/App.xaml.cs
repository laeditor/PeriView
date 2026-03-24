using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using PeriView.App.ViewModels;

namespace PeriView.App;

public partial class App : System.Windows.Application
{
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private DispatcherTimer? _trayRefreshTimer;
    private bool _isExplicitExit;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        SetupTray();

        if (MainWindow is MainWindow window)
        {
            window.Closing += MainWindow_Closing;
            window.StateChanged += MainWindow_StateChanged;
            UpdateTrayText(window);
        }

        _trayRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _trayRefreshTimer.Tick += (_, _) =>
        {
            if (MainWindow is MainWindow mw)
            {
                UpdateTrayText(mw);
            }
        };
        _trayRefreshTimer.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayRefreshTimer is not null)
        {
            _trayRefreshTimer.Stop();
        }

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        base.OnExit(e);
    }

    private void SetupTray()
    {
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "PeriView"
        };

        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开", null, (_, _) => ShowMainWindow());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());
        _notifyIcon.ContextMenuStrip = menu;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isExplicitExit)
        {
            return;
        }

        e.Cancel = true;
        if (sender is Window window)
        {
            window.Hide();
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (sender is Window window && window.WindowState == WindowState.Minimized)
        {
            window.Hide();
        }
    }

    private void ShowMainWindow()
    {
        if (MainWindow is not Window window)
        {
            return;
        }

        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        UpdateTrayText(window);
    }

    private void ExitApplication()
    {
        _isExplicitExit = true;
        Shutdown();
    }

    private void UpdateTrayText(Window window)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        var vm = window.DataContext as MainViewModel;
        var first = vm?.Devices.FirstOrDefault();

        var text = first is null
            ? "PeriView - 无设备"
            : $"PeriView - {first.Name}: {first.BatteryText}";

        _notifyIcon.Text = TrimNotifyText(text);
    }

    private static string TrimNotifyText(string text)
    {
        const int max = 63;
        if (text.Length <= max)
        {
            return text;
        }

        return text[..max];
    }
}
