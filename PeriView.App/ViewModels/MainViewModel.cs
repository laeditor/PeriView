using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PeriView.App.Models;
using PeriView.App.Services;
using PeriView.App.Services.BatteryProviders;

namespace PeriView.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan SnapshotFreshWindow = TimeSpan.FromMinutes(2);

    private readonly DeviceStatusAggregator _aggregator;
    private readonly DispatcherTimer _refreshTimer;
    private readonly RelayCommand _copyDiagnosticCommand;
    private readonly RelayCommand _exportDebugPropertiesCommand;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private string _statusMessage = "准备就绪。";
    private DeviceStatusItemViewModel? _selectedDevice;
    private List<DeviceStatus> _lastConnectedSnapshot = new();
    private DateTimeOffset _lastSnapshotTime = DateTimeOffset.MinValue;

    public MainViewModel()
    {
        var providers = new IDeviceStatusProvider[]
        {
            new BluetoothBatteryProvider(),
            new BatteryProviderRouter(), // 整合了多种电池提供者，包括HID直接通信和Windows属性回退
            new Usb24GBatteryProvider()
        };

        _aggregator = new DeviceStatusAggregator(providers);

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        _copyDiagnosticCommand = new RelayCommand(CopySelectedDiagnostic, CanCopySelectedDiagnostic);
        _exportDebugPropertiesCommand = new RelayCommand(ExportSelectedDebugProperties, CanExportSelectedDebugProperties);

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _refreshTimer.Start();

        _ = RefreshAsync();
    }

    public ObservableCollection<DeviceStatusItemViewModel> Devices { get; } = new();

    public ICommand RefreshCommand { get; }
    public ICommand CopyDiagnosticCommand => _copyDiagnosticCommand;
    public ICommand ExportDebugPropertiesCommand => _exportDebugPropertiesCommand;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public DeviceStatusItemViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                _copyDiagnosticCommand.RaiseCanExecuteChanged();
                _exportDebugPropertiesCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public void MoveDevice(DeviceStatusItemViewModel source, DeviceStatusItemViewModel? target, bool insertAfter)
    {
        var sourceIndex = Devices.IndexOf(source);
        if (sourceIndex < 0)
        {
            return;
        }

        if (target is null)
        {
            return;
        }

        var targetIndex = Devices.IndexOf(target);
        if (targetIndex < 0)
        {
            return;
        }

        if (insertAfter)
        {
            targetIndex++;
        }

        if (sourceIndex < targetIndex)
        {
            targetIndex--;
        }

        targetIndex = Math.Clamp(targetIndex, 0, Devices.Count - 1);

        if (sourceIndex == targetIndex)
        {
            return;
        }

        Devices.Move(sourceIndex, targetIndex);
        SelectedDevice = source;
    }

    private async Task RefreshAsync()
    {
        if (!await _refreshGate.WaitAsync(0))
        {
            return;
        }

        var hasSnapshot = _lastConnectedSnapshot.Count > 0;

        try
        {
            if (hasSnapshot)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ApplyConnectedStatuses(_lastConnectedSnapshot));
                var age = DateTimeOffset.Now - _lastSnapshotTime;
                StatusMessage = age <= SnapshotFreshWindow
                    ? $"正在刷新设备状态...（先显示 {Devices.Count} 台缓存结果）"
                    : $"正在刷新设备状态...（缓存结果较旧，先临时展示 {Devices.Count} 台）";
            }
            else
            {
                StatusMessage = "正在刷新设备状态...";
            }

            using var timeoutCts = new CancellationTokenSource(RefreshTimeout);
            var statuses = (await _aggregator.GetStatusesAsync(timeoutCts.Token))
                .Where(status => !IsInfrastructurePlaceholder(status))
                .ToList();
            var connectedStatuses = statuses.Where(x => x.IsConnected == true).ToList();

            if (statuses.Count == 0)
            {
                StatusMessage = hasSnapshot
                    ? "本次刷新未返回设备数据，已保留上次列表。请确认蓝牙服务正常后重试。"
                    : "未发现设备。请确认 Windows 蓝牙或 2.4G 接收器已连接，并检查数据源是否可访问。";
                return;
            }

            if (connectedStatuses.Count == 0 && hasSnapshot)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ApplyConnectedStatuses(Array.Empty<DeviceStatus>()));
                _lastConnectedSnapshot.Clear();
                _lastSnapshotTime = DateTimeOffset.MinValue;
                StatusMessage = $"本次刷新发现 {statuses.Count} 台设备，但当前无已连接设备，已清空列表。";
                return;
            }

            // 在UI线程上更新集合
            List<DeviceStatus> orderedStatuses = new();
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                orderedStatuses = ApplyConnectedStatuses(connectedStatuses);
            });

            _lastConnectedSnapshot = orderedStatuses
                .Select(CloneStatus)
                .ToList();
            _lastSnapshotTime = DateTimeOffset.Now;

            var withBattery = connectedStatuses.Count(x => x.BatteryPercent.HasValue);
            var withError = connectedStatuses.Count(x => !string.IsNullOrWhiteSpace(x.Error));

            if (connectedStatuses.Count == 0)
            {
                StatusMessage = $"已发现 {statuses.Count} 台设备，但当前无已连接设备，列表已隐藏未连接项。";
                return;
            }

            StatusMessage = $"已刷新已连接设备 {connectedStatuses.Count} 台（共发现 {statuses.Count} 台），可读取电量 {withBattery} 台，诊断提示 {withError} 台。若设备不开放系统属性或标准电池服务，可能仍需厂商 SDK/私有协议。";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = hasSnapshot
                ? $"刷新超时（>{RefreshTimeout.TotalSeconds:0} 秒），已保留上次结果。请保持设备在线后重试。"
                : $"刷新超时（>{RefreshTimeout.TotalSeconds:0} 秒）。请保持设备在线后重试，或导出调试属性继续排查。";
        }
        catch (Exception ex)
        {
            StatusMessage = hasSnapshot
                ? $"刷新失败: {ex.Message}（已保留上次结果）"
                : $"刷新失败: {ex.Message}";
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private List<DeviceStatus> ApplyConnectedStatuses(IReadOnlyList<DeviceStatus> connectedStatuses)
    {
        var selectedKey = SelectedDevice?.DeviceKey;

        var existingOrder = Devices
            .Select((item, index) => new { item.DeviceKey, index })
            .Where(x => !string.IsNullOrWhiteSpace(x.DeviceKey))
            .ToDictionary(x => x.DeviceKey, x => x.index, StringComparer.OrdinalIgnoreCase);

        var orderedConnectedStatuses = connectedStatuses
            .OrderBy(x => existingOrder.TryGetValue(GetOrderKey(x), out var index) ? index : int.MaxValue)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Devices.Clear();
        foreach (var status in orderedConnectedStatuses)
        {
            Devices.Add(new DeviceStatusItemViewModel(status));
        }

        if (Devices.Count == 0)
        {
            SelectedDevice = null;
            return orderedConnectedStatuses;
        }

        if (!string.IsNullOrWhiteSpace(selectedKey))
        {
            SelectedDevice = Devices.FirstOrDefault(x =>
                x.DeviceKey.Equals(selectedKey, StringComparison.OrdinalIgnoreCase)) ?? Devices[0];
        }
        else
        {
            SelectedDevice = Devices[0];
        }

        return orderedConnectedStatuses;
    }

    private static DeviceStatus CloneStatus(DeviceStatus source)
    {
        return new DeviceStatus
        {
            DeviceKey = source.DeviceKey,
            Name = source.Name,
            IsConnected = source.IsConnected,
            BatteryPercent = source.BatteryPercent,
            IsCharging = source.IsCharging,
            Source = source.Source,
            LastUpdated = source.LastUpdated,
            Error = source.Error,
            DebugProperties = source.DebugProperties
        };
    }

    private static string GetOrderKey(DeviceStatus status)
    {
        if (!string.IsNullOrWhiteSpace(status.DeviceKey))
        {
            return status.DeviceKey;
        }

        return status.Name;
    }

    private static bool IsInfrastructurePlaceholder(DeviceStatus status)
    {
        if (string.IsNullOrWhiteSpace(status.Name))
        {
            return false;
        }

        var name = status.Name.Trim();
        if (name.Equals("2.4G HID", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("NearLink Mouse Dongle", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Contains("dongle", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("receiver", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("nearlink", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("接收器", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private bool CanCopySelectedDiagnostic()
    {
        var text = SelectedDevice?.ErrorFullText;
        return !string.IsNullOrWhiteSpace(text) && text != "-";
    }

    private void CopySelectedDiagnostic()
    {
        var text = SelectedDevice?.ErrorFullText;
        if (string.IsNullOrWhiteSpace(text) || text == "-")
        {
            StatusMessage = "当前选中设备没有可复制的诊断信息。";
            return;
        }

        if (TrySetClipboardText(text, out var clipboardError))
        {
            StatusMessage = "已复制诊断详情到剪贴板。";
            return;
        }

        if (TryWriteFallbackFile("diagnostic", text, out var filePath, out var fileError))
        {
            StatusMessage = $"剪贴板不可用（{SimplifyClipboardError(clipboardError)}），已导出到文件: {filePath}";
            return;
        }

        StatusMessage = $"复制失败: {SimplifyClipboardError(clipboardError)}; 文件导出也失败: {fileError}";
    }

    private bool CanExportSelectedDebugProperties()
    {
        var text = SelectedDevice?.DebugPropertiesFullText;
        return !string.IsNullOrWhiteSpace(text) && text != "-";
    }

    private void ExportSelectedDebugProperties()
    {
        var text = SelectedDevice?.DebugPropertiesFullText;
        if (string.IsNullOrWhiteSpace(text) || text == "-")
        {
            StatusMessage = "当前选中设备没有可导出的调试属性。";
            return;
        }

        if (TrySetClipboardText(text, out var clipboardError))
        {
            StatusMessage = "已导出调试属性到剪贴板。";
            return;
        }

        if (TryWriteFallbackFile("debug-properties", text, out var filePath, out var fileError))
        {
            StatusMessage = $"剪贴板不可用（{SimplifyClipboardError(clipboardError)}），已导出到文件: {filePath}";
            return;
        }

        StatusMessage = $"导出失败: {SimplifyClipboardError(clipboardError)}; 文件导出也失败: {fileError}";
    }

    private static bool TrySetClipboardText(string text, out string error)
    {
        error = "clipboard-write-failed";
        for (var i = 0; i < 8; i++)
        {
            if (TrySetClipboardTextCore(text, out error))
            {
                return true;
            }

            Thread.Sleep(100 + (i * 30));
        }

        return false;
    }

    private static bool TrySetClipboardTextCore(string text, out string error)
    {
        error = string.Empty;

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        var completed = new ManualResetEventSlim(false);
        Exception? threadException = null;
        var success = false;

        var thread = new Thread(() =>
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(text, true);
                success = true;
            }
            catch (Exception ex)
            {
                threadException = ex;
            }
            finally
            {
                completed.Set();
            }
        });

        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!completed.Wait(2500))
        {
            error = "clipboard-timeout";
            return false;
        }

        if (success)
        {
            return true;
        }

        error = threadException?.Message ?? "clipboard-write-failed";
        return false;
    }

    private static string SimplifyClipboardError(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "剪贴板暂时不可用";
        }

        if (error.Contains("OpenClipboard", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("CLIPBRD_E_CANT_OPEN", StringComparison.OrdinalIgnoreCase) ||
            error.Contains("打开剪贴板", StringComparison.OrdinalIgnoreCase))
        {
            return "剪贴板正被其他程序占用";
        }

        if (error.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "写入剪贴板超时";
        }

        return error;
    }

    private static bool TryWriteFallbackFile(string prefix, string content, out string filePath, out string error)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "PeriViewExports");
            Directory.CreateDirectory(folder);

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            filePath = Path.Combine(folder, $"{prefix}-{timestamp}.txt");
            File.WriteAllText(filePath, content);

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            filePath = string.Empty;
            error = ex.Message;
            return false;
        }
    }
}
