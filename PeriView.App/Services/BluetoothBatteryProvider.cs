using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;
using System.Text;
using System.Text.RegularExpressions;
using PeriView.App.Models;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Diagnostics;
using Windows.Media.Devices;
using System.Windows.Automation;

namespace PeriView.App.Services;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class BluetoothBatteryProvider : IDeviceStatusProvider
{
    private static readonly TimeSpan EnumerationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DeviceOpenTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan GattOperationTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan AudioAssociationProbeTimeout = TimeSpan.FromMilliseconds(4000);
    private static readonly TimeSpan BatteryResolutionBudget = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan AudioCapabilityProbeBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FastAudioUiProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PrivateProtocolProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan UiSettingsWakeCooldown = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan LastKnownBatteryTtl = TimeSpan.FromMinutes(30);
    private static readonly bool EnableUiSettingsWake = false;
    private static readonly bool EnableDeepBatteryResolution = false;
    private static readonly bool EnableActiveGattBatteryRead = true;
    private const int MaxConcurrentDeviceQueries = 3;
    private static readonly ConcurrentDictionary<string, (int Battery, DateTimeOffset Time)> LastKnownBatteryByDeviceKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Guid BluetoothAepProtocolId = Guid.Parse("{bb7bb05e-5972-42b5-94fc-76eaa7084d49}");
    private static readonly Guid[] HuaweiPrivateServiceUuids =
    {
        Guid.Parse("0000FE2C-0000-1000-8000-00805F9B34FB"),
        Guid.Parse("0000FE2D-0000-1000-8000-00805F9B34FB"),
        Guid.Parse("0000FEE7-0000-1000-8000-00805F9B34FB")
    };

    private static readonly Regex BluetoothAddressRegex = new(@"([0-9A-Fa-f]{2}[:\-]){5}[0-9A-Fa-f]{2}", RegexOptions.Compiled);
    private static readonly Regex UiConnectedBatteryPatternCn = new(@"^(?<name>[^、,，]+?)\s*[、,，]\s*类别[^、,，]*\s*[、,，]\s*电池\s*(?<battery>\d{1,3})\s*[%％]\s*[、,，].*$", RegexOptions.Compiled);
    private static readonly Regex UiConnectedBatteryPatternGeneric = new(@"^(?<name>[^、,，|:：\-]+?)\s*(?:[、,，|:：\-]\s*)?.*?(?:电池|battery)\s*(?<battery>\d{1,3})\s*[%％]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UiConnectedBatteryPatternNoSeparator = new(@"^(?<name>.+?)\s*(?:电池|battery)\s*(?<battery>\d{1,3})\s*[%％].*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex UiBatteryPatternCnLoose = new(@"电池\s*(?<battery>\d{1,3})\s*[%％]", RegexOptions.Compiled);
    private static readonly Regex UiBatteryPatternEnLoose = new(@"battery\s*(?<battery>\d{1,3})\s*[%％]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // 更宽松的匹配：设备名 + 电池 + 数字 + %
    private static readonly Regex UiBatteryUltraLoose = new(@"(?<name>.+?)[、,，].*?(?:电池|battery)\s*(?<battery>\d{1,3})\s*[%％]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] ConnectionPropertyNames =
    {
        "System.Devices.Aep.IsConnected",
        "System.Devices.Connected"
    };

    private static readonly string[] AddressPropertyNames =
    {
        "System.Devices.Aep.Bluetooth.Address",
        "System.Devices.Aep.Bluetooth.Le.Address",
        "System.Devices.Aep.DeviceAddress",
        "System.Devices.Aep.Address"
    };

    private static readonly string[] BatteryPropertyNames =
    {
        "System.Devices.BatteryLifePercent",
        "System.Devices.Aep.BatteryLifePercent",
        "System.Devices.Aep.Bluetooth.BatteryLevel",
        "System.Devices.Aep.Bluetooth.Le.BatteryLevel",
        "System.Devices.Bluetooth.BatteryLevel",
        // Windows 11 和新版本可能使用的属性名
        "System.Devices.Aep.Bluetooth.BatteryLevelStandard",
        "System.Devices.Bluetooth.BatteryLevelStandard",
        "System.Devices.Aep.BatteryLevel",
        "System.Devices.BatteryLevel",
        // Windows 11 23H2+ 蓝牙音频设备电量属性
        "System.Devices.AudioDevice.BatteryLevel",
        "System.Devices.Bluetooth.AudioDevice.BatteryLevel",
        "System.Devices.Aep.Bluetooth.Audio.BatteryLevel",
        "System.Devices.AudioDevice.BatteryLifePercent",
        "System.Devices.Bluetooth.AudioDevice.BatteryLifePercent",
        "System.Devices.Aep.Bluetooth.Audio.BatteryLifePercent",
        "System.Devices.Bluetooth.Le.BatteryLifePercent",
        "System.Devices.Aep.Bluetooth.Le.BatteryLifePercent",
        // 经典蓝牙设备的电量属性（通过 HID 或 SSP 协议）
        "System.Devices.Hid.BatteryLevel",
        "System.Devices.Hid.BatteryStatus",
        "System.Devices.Bluetooth.Hid.BatteryLevel",
        "System.Devices.Aep.Bluetooth.Hid.BatteryLevel",
        // 设备接口级别的电量（DeviceInterface 设备）
        "System.Devices.DeviceInstanceId.BatteryLevel",
        "System.Devices.Interface.BatteryLevel",
        "DEVPKEY_Device_BatteryLevel",
        "DEVPKEY_Bluetooth_BatteryLevel",
        "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2", // DEVPKEY_Bluetooth_LastConnectedTime 附近的属性
        // 电源报告相关
        "System.Devices.PowerReporting.BatteryLevel",
        "System.Devices.Battery.BatteryLevel",
        "System.Devices.BatteryLife",
        "System.Devices.BatteryPercentRelative",
        // 信号强度（某些设备通过 RSSI 推断电量）
        "System.Devices.Aep.SignalStrength",
        "System.Devices.Aep.Bluetooth.SignalStrength",
        // HID 设备电量属性
        "System.Devices.Hid.BatteryStrength",
        "System.Devices.Hid.BatteryPercent",
        // 旧版 Windows 可能使用的属性
        "System.Devices.Aep.Bluetooth.BatteryPercent",
        "System.Devices.Bluetooth.BatteryPercent",
        "System.Devices.Bluetooth.BatteryLife",
        // Windows 11 蓝牙音频特定
        "System.Devices.Bluetooth.A2dp.BatteryLevel",
        "System.Devices.Bluetooth.Headset.BatteryLevel",
        "System.Devices.Aep.Bluetooth.Headset.BatteryLevel",
        "System.Devices.Audio.BatteryLevel",
        // 设备管理器属性（通过 PnP）
        "System.Devices.Pnp.BatteryLevel",
        "System.Devices.PnP.BatteryPercent",
        "System.Devices.PhysicalDeviceLocation.BatteryLevel",
        "System.Devices.BusType.BatteryLevel"
    };

    private static readonly string[] DeviceKindRequestedProperties = BatteryPropertyNames
        .Concat(AddressPropertyNames)
        .Concat(new[]
        {
            "System.Devices.ContainerId",
            "System.Devices.Aep.ContainerId",
            "System.ItemNameDisplay",
            "System.Devices.DeviceInstanceId",
            "System.Devices.ClassGuid"
        })
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static readonly string[] BleEndpointRequestedProperties = BatteryPropertyNames
        .Concat(AddressPropertyNames)
        .Concat(new[]
        {
            "System.Devices.Aep.ContainerId",
            "System.Devices.ContainerId"
        })
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static readonly string[] ClassicEndpointRequestedProperties = BatteryPropertyNames
        .Concat(AddressPropertyNames)
        .Concat(new[]
        {
            "System.Devices.Aep.ContainerId",
            "System.Devices.ContainerId",
            "System.ItemNameDisplay"
        })
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static readonly string[] DeviceInterfaceRequestedProperties = BatteryPropertyNames
        .Concat(AddressPropertyNames)
        .Concat(new[]
        {
            "System.Devices.Aep.ContainerId",
            "System.Devices.ContainerId",
            "System.Devices.DeviceInstanceId",
            "System.ItemNameDisplay",
            "System.Devices.InterfaceClassGuid"
        })
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static readonly string[] AssociationEndpointServiceRequestedProperties = BatteryPropertyNames
        .Concat(AddressPropertyNames)
        .Concat(new[]
        {
            "System.Devices.AepService.AepId",
            "System.Devices.Aep.ContainerId",
            "System.Devices.ContainerId",
            "System.Devices.DeviceInstanceId",
            "System.ItemNameDisplay"
        })
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    // Probe one property per call to avoid API-wide failure on unsupported property keys.
    private static readonly string[] MediaEndpointBatteryPropertyCandidates =
    {
        "System.Devices.AudioDevice.BatteryLevel",
        "System.Devices.AudioDevice.BatteryLifePercent",
        "System.Devices.Bluetooth.AudioDevice.BatteryLevel",
        "System.Devices.Bluetooth.AudioDevice.BatteryLifePercent",
        "System.Devices.Aep.Bluetooth.Audio.BatteryLevel",
        "System.Devices.Aep.Bluetooth.Audio.BatteryLifePercent",
        "System.Devices.Bluetooth.BatteryLevel",
        "System.Devices.Bluetooth.BatteryLifePercent",
        "System.Devices.BatteryLifePercent",
        "System.Devices.BatteryLevel",
        "System.Devices.Aep.BatteryLifePercent",
        "System.Devices.Aep.BatteryLevel"
    };

    private static readonly string[] GlobalAssociationEndpointRequestedProperties = BatteryPropertyNames
        .Concat(AddressPropertyNames)
        .Concat(new[]
        {
            "System.Devices.Aep.ProtocolId",
            "System.Devices.Aep.IsConnected",
            "System.Devices.Connected",
            "System.Devices.Aep.IsPaired",
            "System.Devices.Aep.ContainerId",
            "System.Devices.ContainerId",
            "System.ItemNameDisplay"
        })
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public string Name => "Bluetooth BLE";

    private sealed class BatteryResolutionResult
    {
        public int? BatteryPercent { get; init; }
        public string Trace { get; init; } = string.Empty;
        public string InterfaceProbeDump { get; init; } = string.Empty;
    }

    private sealed class AudioCapabilityProbeResult
    {
        public bool IsAudioCandidate { get; init; }
        public bool HasBleBatteryService { get; init; }
        public bool HasAvrcpProfile { get; init; }
        public bool HasHfpProfile { get; init; }
        public bool HasA2dpProfile { get; init; }
        public string Summary { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
    }

    private sealed class UiBatteryEntry
    {
        public string DeviceName { get; init; } = string.Empty;
        public int BatteryPercent { get; init; }
        public string RawText { get; init; } = string.Empty;
    }

    private sealed class DiscoveryCache
    {
        private readonly Lazy<Task<IReadOnlyList<DeviceInformation>>> _deviceKindDevices;
        private readonly Lazy<Task<IReadOnlyList<DeviceInformation>>> _bleEndpoints;
        private readonly Lazy<Task<IReadOnlyList<DeviceInformation>>> _classicEndpoints;
        private readonly Lazy<Task<IReadOnlyList<DeviceInformation>>> _deviceInterfaces;
        private readonly Lazy<Task<IReadOnlyList<DeviceInformation>>> _associationEndpointServices;
        private readonly Lazy<Task<IReadOnlyList<DeviceInformation>>> _globalAssociationEndpoints;
        private readonly Lazy<Task<IReadOnlyList<UiBatteryEntry>>> _uiBatteryEntries;
        private readonly ConcurrentDictionary<Guid, int?> _containerBatteryCache = new();
        private readonly ConcurrentDictionary<string, int?> _addressBatteryCache = new(StringComparer.OrdinalIgnoreCase);

        public DiscoveryCache()
        {
            _deviceKindDevices = new Lazy<Task<IReadOnlyList<DeviceInformation>>>(LoadDeviceKindDevicesAsync);
            _bleEndpoints = new Lazy<Task<IReadOnlyList<DeviceInformation>>>(LoadBleEndpointsAsync);
            _classicEndpoints = new Lazy<Task<IReadOnlyList<DeviceInformation>>>(LoadClassicEndpointsAsync);
            _deviceInterfaces = new Lazy<Task<IReadOnlyList<DeviceInformation>>>(LoadDeviceInterfacesAsync);
            _associationEndpointServices = new Lazy<Task<IReadOnlyList<DeviceInformation>>>(LoadAssociationEndpointServicesAsync);
            _globalAssociationEndpoints = new Lazy<Task<IReadOnlyList<DeviceInformation>>>(LoadGlobalAssociationEndpointsAsync);
            _uiBatteryEntries = new Lazy<Task<IReadOnlyList<UiBatteryEntry>>>(LoadUiBatteryEntriesAsync);
        }

        public Task<IReadOnlyList<DeviceInformation>> GetDeviceKindDevicesAsync()
        {
            return _deviceKindDevices.Value;
        }

        public Task<IReadOnlyList<DeviceInformation>> GetBleEndpointsAsync()
        {
            return _bleEndpoints.Value;
        }

        public Task<IReadOnlyList<DeviceInformation>> GetClassicEndpointsAsync()
        {
            return _classicEndpoints.Value;
        }

        public Task<IReadOnlyList<DeviceInformation>> GetDeviceInterfacesAsync()
        {
            return _deviceInterfaces.Value;
        }

        public Task<IReadOnlyList<DeviceInformation>> GetAssociationEndpointServicesAsync()
        {
            return _associationEndpointServices.Value;
        }

        public Task<IReadOnlyList<DeviceInformation>> GetGlobalAssociationEndpointsAsync()
        {
            return _globalAssociationEndpoints.Value;
        }

        public Task<IReadOnlyList<UiBatteryEntry>> GetUiBatteryEntriesAsync()
        {
            return _uiBatteryEntries.Value;
        }

        public bool TryGetBatteryByContainer(Guid containerId, out int? batteryPercent)
        {
            return _containerBatteryCache.TryGetValue(containerId, out batteryPercent);
        }

        public void SetBatteryByContainer(Guid containerId, int? batteryPercent)
        {
            _containerBatteryCache[containerId] = batteryPercent;
        }

        public bool TryGetBatteryByAddress(string normalizedAddress, out int? batteryPercent)
        {
            return _addressBatteryCache.TryGetValue(normalizedAddress, out batteryPercent);
        }

        public void SetBatteryByAddress(string normalizedAddress, int? batteryPercent)
        {
            _addressBatteryCache[normalizedAddress] = batteryPercent;
        }

        private static async Task<IReadOnlyList<DeviceInformation>> LoadDeviceKindDevicesAsync()
        {
            try
            {
                return await DeviceInformation.FindAllAsync(string.Empty, DeviceKindRequestedProperties, DeviceInformationKind.Device);
            }
            catch
            {
                return Array.Empty<DeviceInformation>();
            }
        }

        private static async Task<IReadOnlyList<DeviceInformation>> LoadBleEndpointsAsync()
        {
            try
            {
                return await DeviceInformation.FindAllAsync(
                    BluetoothLEDevice.GetDeviceSelector(),
                    BleEndpointRequestedProperties,
                    DeviceInformationKind.AssociationEndpoint);
            }
            catch
            {
                try
                {
                    return await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelector(), BleEndpointRequestedProperties);
                }
                catch
                {
                    return Array.Empty<DeviceInformation>();
                }
            }
        }

        private static async Task<IReadOnlyList<DeviceInformation>> LoadDeviceInterfacesAsync()
        {
            try
            {
                return await DeviceInformation.FindAllAsync(string.Empty, DeviceInterfaceRequestedProperties, DeviceInformationKind.DeviceInterface);
            }
            catch
            {
                return Array.Empty<DeviceInformation>();
            }
        }

        private static async Task<IReadOnlyList<DeviceInformation>> LoadClassicEndpointsAsync()
        {
            try
            {
                return await DeviceInformation.FindAllAsync(
                    BluetoothDevice.GetDeviceSelector(),
                    ClassicEndpointRequestedProperties,
                    DeviceInformationKind.AssociationEndpoint);
            }
            catch
            {
                try
                {
                    return await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelector(), ClassicEndpointRequestedProperties);
                }
                catch
                {
                    return Array.Empty<DeviceInformation>();
                }
            }
        }

        private static async Task<IReadOnlyList<DeviceInformation>> LoadAssociationEndpointServicesAsync()
        {
            try
            {
                return await DeviceInformation.FindAllAsync(
                    string.Empty,
                    AssociationEndpointServiceRequestedProperties,
                    DeviceInformationKind.AssociationEndpointService);
            }
            catch
            {
                return Array.Empty<DeviceInformation>();
            }
        }

        private static async Task<IReadOnlyList<DeviceInformation>> LoadGlobalAssociationEndpointsAsync()
        {
            try
            {
                return await DeviceInformation.FindAllAsync(
                    string.Empty,
                    GlobalAssociationEndpointRequestedProperties,
                    DeviceInformationKind.AssociationEndpoint);
            }
            catch
            {
                return Array.Empty<DeviceInformation>();
            }
        }

        private static Task<IReadOnlyList<UiBatteryEntry>> LoadUiBatteryEntriesAsync()
        {
            return Task.Run<IReadOnlyList<UiBatteryEntry>>(() => CollectUiBatteryEntriesSnapshot());
        }
    }

    public async Task<IReadOnlyList<DeviceStatus>> GetStatusesAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<DeviceStatus>();
        var discoveryCache = new DiscoveryCache();
        var requestedProperties = new[]
        {
            "System.Devices.Aep.IsConnected",
            "System.Devices.Connected",
            "System.Devices.Aep.IsPaired",
            "System.ItemNameDisplay",
            "System.Devices.Aep.ContainerId",
            "System.Devices.ContainerId",
            "System.Devices.DeviceInstanceId",
            "System.Devices.BatteryLifePercent",
            "System.Devices.Aep.BatteryLifePercent",
            "System.Devices.Aep.Bluetooth.BatteryLevel",
            "System.Devices.Aep.Bluetooth.Le.BatteryLevel",
            "System.Devices.Bluetooth.BatteryLevel",
            "System.Devices.Aep.Bluetooth.BatteryLevelStandard",
            "System.Devices.Bluetooth.BatteryLevelStandard",
            "System.Devices.Aep.BatteryLevel",
            "System.Devices.BatteryLevel",
            "System.Devices.Aep.SignalStrength",
            "System.Devices.Hid.BatteryStrength",
            "System.Devices.Aep.Bluetooth.BatteryPercent",
            "System.Devices.Bluetooth.BatteryPercent",
            "System.Devices.Aep.Bluetooth.Address",
            "System.Devices.Aep.Bluetooth.Le.Address",
            "System.Devices.Aep.DeviceAddress",
            "System.Devices.Aep.Address"
        };

        var bleSelector = BluetoothLEDevice.GetDeviceSelector();
        var classicSelector = BluetoothDevice.GetDeviceSelector();

        var bleEnumerationTask = FindAllSafeAsync(bleSelector, requestedProperties, cancellationToken);
        var classicEnumerationTask = FindAllSafeAsync(classicSelector, requestedProperties, cancellationToken);
        await Task.WhenAll(bleEnumerationTask, classicEnumerationTask);

        var (bleInfos, bleEnumerationError) = await bleEnumerationTask;
        var (classicInfos, classicEnumerationError) = await classicEnumerationTask;

        var bleSet = new HashSet<string>(bleInfos.Select(BuildLogicalDeviceKey), StringComparer.OrdinalIgnoreCase);

        var bleStatuses = await ProcessWithConcurrencyAsync(
            bleInfos,
            MaxConcurrentDeviceQueries,
            (info, ct) => BuildBleStatusAsync(info, discoveryCache, ct),
            cancellationToken);
        list.AddRange(bleStatuses);

        var filteredClassicInfos = classicInfos
            .Where(info => !bleSet.Contains(BuildLogicalDeviceKey(info)))
            .ToList();

        var classicStatuses = await ProcessWithConcurrencyAsync(
            filteredClassicInfos,
            MaxConcurrentDeviceQueries,
            async (info, ct) =>
            {
                var propertyConnection = TryGetConnectionProperty(info);
                var runtimeConnection = await ResolveClassicConnectionAsync(info.Id, ct);

                // 对经典设备，即使连接状态未确认也尝试系统属性读取；
                // Windows 托盘/设置页常能提供缓存电量，但连接位可能短暂不同步。
                var batteryResolution = await ResolveBatteryPercentWithBudgetAsync(info, discoveryCache, ct);
                var liveResolvedBattery = batteryResolution.BatteryPercent;
                var resolvedBattery = liveResolvedBattery;
                if (!resolvedBattery.HasValue && TryGetLastKnownBattery(info, out var cachedBattery))
                {
                    resolvedBattery = cachedBattery;
                }
                else if (resolvedBattery.HasValue)
                {
                    SetLastKnownBattery(info, resolvedBattery.Value);
                }

                // 经典蓝牙的属性连接位可能滞后，优先采用运行时连接状态。
                // 当运行时状态不可用时，仅在本轮实时解析到电量时才将属性连接位作为“在线”证据。
                var isConnected = IsLikelyAudioDevice(info)
                    ? (runtimeConnection ?? propertyConnection ?? false)
                    : (runtimeConnection ?? (propertyConnection == true && liveResolvedBattery.HasValue));

                // 鼠标/指针类设备对“是否在线”更敏感：运行时无法确认时，默认按离线处理，避免断开后仍残留在列表。
                if (IsLikelyPointerDevice(info) && runtimeConnection != true)
                {
                    isConnected = false;
                }

                if (!isConnected && IsPaired(info) && runtimeConnection is null)
                {
                    isConnected = false;
                }
                var audioProbe = resolvedBattery.HasValue || !EnableDeepBatteryResolution
                    ? BuildSkippedAudioProbeResult("audio-probe: skipped (fast mode or battery already resolved)")
                    : await BuildAudioCapabilityProbeWithBudgetAsync(info, null, ct);
                var classicDiagnostic = BuildClassicDeviceMessageAsync(info, resolvedBattery, audioProbe);

                return new DeviceStatus
                {
                    DeviceKey = BuildLogicalDeviceKey(info),
                    Name = string.IsNullOrWhiteSpace(info.Name) ? "Unknown Bluetooth Device" : info.Name,
                    Source = "Bluetooth Classic",
                    IsConnected = isConnected,
                    LastUpdated = DateTimeOffset.Now,
                    BatteryPercent = resolvedBattery,
                    Error = classicDiagnostic,
                    DebugProperties = BuildDeviceDebugDump(
                        info,
                        "Bluetooth Classic",
                        isConnected,
                        resolvedBattery,
                        classicDiagnostic,
                        batteryResolution.Trace,
                        batteryResolution.InterfaceProbeDump,
                        audioProbe.Summary,
                        audioProbe.Detail)
                };
            },
            cancellationToken);
        list.AddRange(classicStatuses);

        if (list.Count == 0 && (!string.IsNullOrWhiteSpace(bleEnumerationError) || !string.IsNullOrWhiteSpace(classicEnumerationError)))
        {
            var mergedError = string.Join(" | ", new[] { bleEnumerationError, classicEnumerationError }.Where(x => !string.IsNullOrWhiteSpace(x)));
            list.Add(new DeviceStatus
            {
                DeviceKey = Name,
                Name = Name,
                Source = Name,
                LastUpdated = DateTimeOffset.Now,
                Error = $"蓝牙枚举失败: {mergedError}"
            });
        }
        else if (!string.IsNullOrWhiteSpace(bleEnumerationError) || !string.IsNullOrWhiteSpace(classicEnumerationError))
        {
            var mergedError = string.Join(" | ", new[] { bleEnumerationError, classicEnumerationError }.Where(x => !string.IsNullOrWhiteSpace(x)));
            list.Add(new DeviceStatus
            {
                DeviceKey = "Bluetooth-Enumeration-Warning",
                Name = "Bluetooth Enumeration",
                Source = Name,
                LastUpdated = DateTimeOffset.Now,
                Error = $"部分枚举降级: {mergedError}"
            });
        }

        return list;
    }

    private static async Task<List<TResult>> ProcessWithConcurrencyAsync<TInput, TResult>(
        IReadOnlyList<TInput> inputs,
        int maxConcurrency,
        Func<TInput, CancellationToken, Task<TResult>> processor,
        CancellationToken cancellationToken)
    {
        if (inputs.Count == 0)
        {
            return new List<TResult>();
        }

        using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));
        var results = new TResult[inputs.Count];
        var tasks = new Task[inputs.Count];

        for (var i = 0; i < inputs.Count; i++)
        {
            var index = i;
            tasks[index] = Task.Run(async () =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    results[index] = await processor(inputs[index], cancellationToken);
                }
                finally
                {
                    gate.Release();
                }
            }, cancellationToken);
        }

        await Task.WhenAll(tasks);
        return results.ToList();
    }

    private static async Task<(IReadOnlyList<DeviceInformation> devices, string? error)> FindAllSafeAsync(
        string selector,
        string[] requestedProperties,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var devices = await WithOperationTimeoutAsync(
                ct => DeviceInformation.FindAllAsync(selector, requestedProperties).AsTask(ct),
                EnumerationTimeout,
                cancellationToken,
                "蓝牙设备属性枚举超时");
            return (devices, null);
        }
        catch (Exception exWithProps)
        {
            try
            {
                var devices = await WithOperationTimeoutAsync(
                    ct => DeviceInformation.FindAllAsync(selector).AsTask(ct),
                    EnumerationTimeout,
                    cancellationToken,
                    "蓝牙设备基础枚举超时");
                var warning = $"属性查询失败({exWithProps.GetType().Name})，已自动回退到基础枚举";
                return (devices, warning);
            }
            catch (Exception exFallback)
            {
                var error = $"基础枚举失败({exFallback.GetType().Name}: {exFallback.Message})";
                return (Array.Empty<DeviceInformation>(), error);
            }
        }
    }

    private static async Task<DeviceStatus> BuildBleStatusAsync(
        DeviceInformation info,
        DiscoveryCache discoveryCache,
        CancellationToken cancellationToken)
    {
        // 首先确定连接状态
        var isConnected = TryGetConnectionProperty(info);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ble = await CreateBleDeviceWithRetryAsync(info, cancellationToken);
            if (ble is null)
            {
                return new DeviceStatus
                {
                    DeviceKey = BuildLogicalDeviceKey(info),
                    Name = string.IsNullOrWhiteSpace(info.Name) ? "Unknown BLE Device" : info.Name,
                    Source = "Bluetooth BLE",
                    IsConnected = isConnected ?? false,
                    BatteryPercent = null,
                    LastUpdated = DateTimeOffset.Now,
                    Error = "无法创建设备连接对象，可能是设备不可访问或已断开。"
                };
            }

            using (ble)
            {
                // 获取设备名称（优先使用 BLE 设备提供的名称）
                var deviceName = !string.IsNullOrWhiteSpace(ble.Name) ? ble.Name : info.Name;

                var propertyConnection = isConnected;
                var runtimeConnection = ble.ConnectionStatus == BluetoothConnectionStatus.Connected
                    ? true
                    : false;
                var reachabilityConnection = await ProbeBleReachabilityAsync(ble, cancellationToken);
                isConnected = runtimeConnection == true
                    ? true
                    : reachabilityConnection == true
                        ? true
                        : runtimeConnection == false
                            ? false
                            : propertyConnection;
                var hasPositiveConnectionEvidence = runtimeConnection == true || reachabilityConnection == true;

                // 对鼠标类设备更保守：运行时未确认连接时，不接受属性缓存导致的“在线”。
                if (IsLikelyPointerDevice(info) && runtimeConnection != true)
                {
                    isConnected = false;
                }

                // 音频设备常见场景：运行时状态短暂为未连接，但系统属性仍保持连接且可读取系统电量。
                if (IsLikelyAudioDevice(info) && propertyConnection == true && runtimeConnection != true && reachabilityConnection != false)
                {
                    isConnected = true;
                }

                if (!isConnected.HasValue && IsPaired(info))
                {
                    isConnected = false;
                }

                // 连接状态未确认时，仍尝试读取系统侧电量缓存（不做 GATT 主动读）。
                if (isConnected != true)
                {
                    var disconnectedBatteryResolution = await ResolveBatteryPercentWithBudgetAsync(info, discoveryCache, cancellationToken);
                    var disconnectedAudioProbe = disconnectedBatteryResolution.BatteryPercent.HasValue || !EnableDeepBatteryResolution
                        ? BuildSkippedAudioProbeResult("audio-probe: skipped (fast mode or battery already resolved)")
                        : await BuildAudioCapabilityProbeWithBudgetAsync(info, ble, cancellationToken);

                    if (IsLikelyAudioDevice(info) && propertyConnection == true && disconnectedBatteryResolution.BatteryPercent.HasValue)
                    {
                        isConnected = true;
                    }

                    var diagnostic = disconnectedBatteryResolution.BatteryPercent.HasValue
                        ? "连接状态未确认，但已从系统属性/缓存读取到电量。"
                        : "连接状态未确认，且未读取到电量。";

                    return new DeviceStatus
                    {
                        DeviceKey = BuildLogicalDeviceKey(info),
                        Name = string.IsNullOrWhiteSpace(deviceName) ? "Unknown BLE Device" : deviceName,
                        Source = "Bluetooth BLE",
                        IsConnected = isConnected,
                        BatteryPercent = disconnectedBatteryResolution.BatteryPercent,
                        LastUpdated = DateTimeOffset.Now,
                        Error = diagnostic,
                        DebugProperties = BuildDeviceDebugDump(
                            info,
                            "Bluetooth BLE",
                            isConnected,
                            disconnectedBatteryResolution.BatteryPercent,
                            diagnostic,
                            disconnectedBatteryResolution.Trace,
                            disconnectedBatteryResolution.InterfaceProbeDump,
                            disconnectedAudioProbe.Summary,
                            disconnectedAudioProbe.Detail)
                    };
                }

                // 设备已连接，查询电量
                var batteryResolution = await ResolveBatteryPercentWithBudgetAsync(info, discoveryCache, cancellationToken);
                var resolvedBattery = batteryResolution.BatteryPercent;
                string? audioBatteryNote = null;

                if (IsLikelyAudioDevice(info))
                {
                    try
                    {
                        var (uiBattery, uiTrace) = await WithOperationTimeoutAsync(
                            ct => TryReadBatteryFromUiAutomationCacheAsync(info, discoveryCache, ct),
                            FastAudioUiProbeTimeout,
                            cancellationToken,
                            "音频设备 UIA 电量探测超时");

                        if (uiBattery.HasValue)
                        {
                            resolvedBattery = uiBattery;
                            audioBatteryNote = $"音频设备优先采用系统界面电量（UIA）。{uiTrace}";
                        }
                    }
                    catch
                    {
                        // Ignore UIA probe failures and keep prior resolved battery.
                    }
                }

                var status = new DeviceStatus
                {
                    DeviceKey = BuildLogicalDeviceKey(info),
                    Name = string.IsNullOrWhiteSpace(deviceName) ? "Unknown BLE Device" : deviceName,
                    Source = "Bluetooth BLE",
                    IsConnected = true,
                    BatteryPercent = resolvedBattery,
                    LastUpdated = DateTimeOffset.Now
                };

                var preferSystemAudioBattery = IsLikelyAudioDevice(info) && status.BatteryPercent.HasValue;

                if (EnableActiveGattBatteryRead && !preferSystemAudioBattery)
                {
                    // Some BLE devices report disconnected while idle, but still allow on-demand GATT reads.
                    status.Error = await TryReadBatteryLevelAsync(ble, status, cancellationToken);

                    if (!status.BatteryPercent.HasValue && IsTransientGattError(status.Error))
                    {
                        status.Error = await TryReconnectAndReadBatteryAsync(info, status, cancellationToken);
                    }
                }
                else if (preferSystemAudioBattery)
                {
                    status.Error = audioBatteryNote ?? "音频设备优先采用系统电量来源，以与系统托盘保持一致。";
                }
                else if (!status.BatteryPercent.HasValue)
                {
                    status.Error = "快速模式: 已跳过主动 GATT 电量读取。";
                }

                if (status.BatteryPercent.HasValue)
                {
                    status.IsConnected = true;
                    SetLastKnownBattery(info, status.BatteryPercent.Value);
                }
                else if (hasPositiveConnectionEvidence && TryGetLastKnownBattery(info, out var cachedBattery))
                {
                    status.BatteryPercent = cachedBattery;
                    status.Error = "当前未读取到实时电量，已回退到最近一次成功值。";
                }
                else if (string.IsNullOrWhiteSpace(status.Error))
                {
                    status.Error = "未通过 GATT 读到电量，且系统属性中无电量值。";
                }

                if (!hasPositiveConnectionEvidence)
                {
                    status.IsConnected = false;
                }

                var audioProbe = status.BatteryPercent.HasValue || !EnableDeepBatteryResolution
                    ? BuildSkippedAudioProbeResult("audio-probe: skipped (fast mode or battery already resolved)")
                    : await BuildAudioCapabilityProbeWithBudgetAsync(info, ble, cancellationToken);
                if (!status.BatteryPercent.HasValue && audioProbe.IsAudioCandidate)
                {
                    status.Error = BuildAudioAwareDiagnostic(status.Error, audioProbe);
                }

                // 添加调试信息
                status.DebugProperties = BuildDeviceDebugDump(
                    info,
                    "Bluetooth BLE",
                    status.IsConnected,
                    status.BatteryPercent,
                    status.Error,
                    batteryResolution.Trace,
                    batteryResolution.InterfaceProbeDump,
                    audioProbe.Summary,
                    audioProbe.Detail);

                return status;
            }
        }
        catch (Exception ex)
        {
            return new DeviceStatus
            {
                DeviceKey = BuildLogicalDeviceKey(info),
                Name = string.IsNullOrWhiteSpace(info.Name) ? "Unknown BLE Device" : info.Name,
                Source = "Bluetooth BLE",
                IsConnected = isConnected ?? false,
                BatteryPercent = null,
                LastUpdated = DateTimeOffset.Now,
                Error = $"读取失败: {ex.GetType().Name}: {ex.Message}"
            };
        }
    }

    private static async Task<bool?> ProbeBleReachabilityAsync(BluetoothLEDevice ble, CancellationToken cancellationToken)
    {
        try
        {
            var probe = await WithOperationTimeoutAsync(
                ct => ble.GetGattServicesAsync(BluetoothCacheMode.Cached).AsTask(ct),
                GattOperationTimeout,
                cancellationToken,
                "探测 BLE 可达性超时");
            return probe.Status switch
            {
                GattCommunicationStatus.Success => true,
                GattCommunicationStatus.Unreachable => false,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryReadBatteryLevelAsync(
        BluetoothLEDevice ble,
        DeviceStatus status,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var serviceResult = await WithOperationTimeoutAsync(
                ct => ble.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Cached).AsTask(ct),
                GattOperationTimeout,
                cancellationToken,
                "读取 Battery Service 超时");

            if (serviceResult.Status != GattCommunicationStatus.Success || serviceResult.Services.Count == 0)
            {
                serviceResult = await WithOperationTimeoutAsync(
                    ct => ble.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached).AsTask(ct),
                    GattOperationTimeout,
                    cancellationToken,
                    "读取 Battery Service(uncached) 超时");
            }

            if (serviceResult.Status != GattCommunicationStatus.Success || serviceResult.Services.Count == 0)
            {
                if (serviceResult.Status == GattCommunicationStatus.AccessDenied)
                {
                    return "读取被系统拒绝(AccessDenied)，请确认蓝牙权限和设备访问授权。";
                }

                if (serviceResult.Status == GattCommunicationStatus.Unreachable)
                {
                    return "设备当前不可达(Unreachable)，请尝试重连设备后再刷新。";
                }

                return "设备未提供标准 Battery Service (0x180F)。";
            }

            var batteryService = serviceResult.Services[0];
            try
            {
                var characteristicResult = await WithOperationTimeoutAsync(
                    ct => batteryService.GetCharacteristicsForUuidAsync(
                        GattCharacteristicUuids.BatteryLevel,
                        BluetoothCacheMode.Cached).AsTask(ct),
                    GattOperationTimeout,
                    cancellationToken,
                    "读取 Battery Characteristic 超时");

                if (characteristicResult.Status != GattCommunicationStatus.Success || characteristicResult.Characteristics.Count == 0)
                {
                    characteristicResult = await WithOperationTimeoutAsync(
                        ct => batteryService.GetCharacteristicsForUuidAsync(
                            GattCharacteristicUuids.BatteryLevel,
                            BluetoothCacheMode.Uncached).AsTask(ct),
                        GattOperationTimeout,
                        cancellationToken,
                        "读取 Battery Characteristic(uncached) 超时");
                }

                if (characteristicResult.Status != GattCommunicationStatus.Success || characteristicResult.Characteristics.Count == 0)
                {
                    return "Battery Service 中未找到 Battery Level Characteristic (0x2A19)。";
                }

                var characteristic = characteristicResult.Characteristics[0];
                var readResult = await WithOperationTimeoutAsync(
                    ct => characteristic.ReadValueAsync(BluetoothCacheMode.Cached).AsTask(ct),
                    GattOperationTimeout,
                    cancellationToken,
                    "读取电量值超时");

                if (readResult.Status != GattCommunicationStatus.Success)
                {
                    readResult = await WithOperationTimeoutAsync(
                        ct => characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct),
                        GattOperationTimeout,
                        cancellationToken,
                        "读取电量值(uncached) 超时");
                }

                if (readResult.Status != GattCommunicationStatus.Success)
                {
                    return $"读取电量特征失败: {readResult.Status}";
                }

                var reader = DataReader.FromBuffer(readResult.Value);
                if (reader.UnconsumedBufferLength > 0)
                {
                    status.BatteryPercent = reader.ReadByte();
                    return null;
                }

                return "电量特征返回空数据。";
            }
            finally
            {
                batteryService.Dispose();
            }
        }
        catch (TimeoutException ex)
        {
            return ex.Message;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "读取电量操作被取消。";
        }
    }

    private static bool? TryGetBoolProperty(DeviceInformation info, string propertyName)
    {
        if (!info.Properties.TryGetValue(propertyName, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            bool boolValue => boolValue,
            byte b => b != 0,
            sbyte sb => sb != 0,
            short s => s != 0,
            ushort us => us != 0,
            int i => i != 0,
            uint ui => ui != 0,
            long l => l != 0,
            ulong ul => ul != 0,
            string text when bool.TryParse(text, out var parsed) => parsed,
            string text when int.TryParse(text, out var numeric) => numeric != 0,
            _ => null
        };
    }

    private static bool? TryGetConnectionProperty(DeviceInformation info)
    {
        foreach (var propertyName in ConnectionPropertyNames)
        {
            var value = TryGetBoolProperty(info, propertyName);
            if (value.HasValue)
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsPaired(DeviceInformation info)
    {
        if (info.Pairing.IsPaired)
        {
            return true;
        }

        var pairedProperty = TryGetBoolProperty(info, "System.Devices.Aep.IsPaired");
        return pairedProperty == true;
    }

    private static async Task<bool?> ResolveClassicConnectionAsync(string deviceId, CancellationToken cancellationToken)
    {
        try
        {
            var device = await WithOperationTimeoutAsync(
                ct => BluetoothDevice.FromIdAsync(deviceId).AsTask(ct),
                DeviceOpenTimeout,
                cancellationToken,
                "连接经典蓝牙设备超时");
            if (device is null)
            {
                return null;
            }

            using (device)
            {
                return device.ConnectionStatus == BluetoothConnectionStatus.Connected;
            }
        }
        catch
        {
            return null;
        }
    }

    private static int? TryGetBatteryPercentFromProperties(IReadOnlyDictionary<string, object> properties)
    {
        foreach (var name in BatteryPropertyNames)
        {
            if (!properties.TryGetValue(name, out var value) || value is null)
            {
                continue;
            }

            var parsed = ParseBatteryValue(value);
            if (parsed is >= 0 and <= 100)
            {
                return parsed;
            }
        }

        // Some drivers expose non-standard battery keys. Heuristically scan keys containing "battery".
        foreach (var pair in properties)
        {
            if (pair.Value is null)
            {
                continue;
            }

            if (!pair.Key.Contains("battery", StringComparison.OrdinalIgnoreCase) &&
                !pair.Key.Contains("Battery", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parsed = ParseBatteryValue(pair.Value);
            if (parsed is >= 0 and <= 100)
            {
                return parsed;
            }
        }

        return null;
    }

    private static int ParseBatteryValue(object value)
    {
        return value switch
        {
            byte b => b,
            sbyte sb => sb,
            short s => s,
            ushort us => us,
            int i => i,
            uint ui => unchecked((int)ui),
            long l => (int)l,
            ulong ul => (int)ul,
            float f => (int)f,
            double d => (int)d,
            decimal dc => (int)dc,
            string text when int.TryParse(text, out var i) => i,
            string text when float.TryParse(text, out var f) => (int)f,
            // Windows 有时以 IInspectable/PropertyValue 形式返回，尝试转换
            object obj when obj.GetType().Name.Contains("Byte") => Convert.ToInt32(obj),
            object obj when obj.GetType().Name.Contains("Int32") => Convert.ToInt32(obj),
            object obj when obj.GetType().Name.Contains("UInt32") => unchecked((int)Convert.ToUInt32(obj)),
            _ => -1
        };
    }

    private static async Task<int?> ResolveBatteryPercentAsync(DeviceInformation info, DiscoveryCache discoveryCache)
    {
        var result = await ResolveBatteryPercentWithTraceAsync(info, discoveryCache);
        return result.BatteryPercent;
    }

    private static async Task<BatteryResolutionResult> ResolveBatteryPercentWithBudgetAsync(
        DeviceInformation info,
        DiscoveryCache discoveryCache,
        CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(BatteryResolutionBudget);

        try
        {
            return await ResolveBatteryPercentWithTraceAsync(info, discoveryCache, linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BatteryResolutionResult
            {
                BatteryPercent = null,
                Trace = $"battery-resolution-budget-timeout>{BatteryResolutionBudget.TotalSeconds:0.#}s"
            };
        }
    }

    private static async Task<AudioCapabilityProbeResult> BuildAudioCapabilityProbeWithBudgetAsync(
        DeviceInformation info,
        BluetoothLEDevice? existingBle,
        CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(AudioCapabilityProbeBudget);

        try
        {
            return await BuildAudioCapabilityProbeAsync(info, existingBle, linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AudioCapabilityProbeResult
            {
                IsAudioCandidate = IsLikelyAudioDevice(info),
                Summary = "audio-probe: candidate=true; timed-out",
                Detail = $"audio-probe-canceled: per-device-budget-timeout>{AudioCapabilityProbeBudget.TotalSeconds:0.#}s"
            };
        }
    }

    private static AudioCapabilityProbeResult BuildSkippedAudioProbeResult(string summary)
    {
        return new AudioCapabilityProbeResult
        {
            IsAudioCandidate = false,
            Summary = summary,
            Detail = string.Empty
        };
    }

    private static async Task<BatteryResolutionResult> ResolveBatteryPercentWithTraceAsync(
        DeviceInformation info,
        DiscoveryCache discoveryCache,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var trace = new List<string>();
        IReadOnlyList<DeviceInformation>? deviceKindDevices = null;
        IReadOnlyList<DeviceInformation>? bleEndpoints = null;
        IReadOnlyList<DeviceInformation>? classicEndpoints = null;
        IReadOnlyList<DeviceInformation>? deviceInterfaces = null;
        IReadOnlyList<DeviceInformation>? associationEndpointServices = null;
        IReadOnlyList<DeviceInformation>? globalAssociationEndpoints = null;
        string interfaceProbeDump = string.Empty;

        async Task<IReadOnlyList<DeviceInformation>> GetDeviceKindDevicesAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deviceKindDevices is null)
            {
                deviceKindDevices = await discoveryCache.GetDeviceKindDevicesAsync();
            }

            return deviceKindDevices;
        }

        async Task<IReadOnlyList<DeviceInformation>> GetBleEndpointsAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bleEndpoints is null)
            {
                bleEndpoints = await discoveryCache.GetBleEndpointsAsync();
            }

            return bleEndpoints;
        }

        async Task<IReadOnlyList<DeviceInformation>> GetDeviceInterfacesAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deviceInterfaces is null)
            {
                deviceInterfaces = await discoveryCache.GetDeviceInterfacesAsync();
            }

            return deviceInterfaces;
        }

        async Task<IReadOnlyList<DeviceInformation>> GetClassicEndpointsAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (classicEndpoints is null)
            {
                classicEndpoints = await discoveryCache.GetClassicEndpointsAsync();
            }

            return classicEndpoints;
        }

        async Task<IReadOnlyList<DeviceInformation>> GetAssociationEndpointServicesAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (associationEndpointServices is null)
            {
                associationEndpointServices = await discoveryCache.GetAssociationEndpointServicesAsync();
            }

            return associationEndpointServices;
        }

        async Task<IReadOnlyList<DeviceInformation>> GetGlobalAssociationEndpointsAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (globalAssociationEndpoints is null)
            {
                globalAssociationEndpoints = await discoveryCache.GetGlobalAssociationEndpointsAsync();
            }

            return globalAssociationEndpoints;
        }

        var direct = TryGetBatteryPercentFromProperties(info.Properties);
        if (direct.HasValue)
        {
            trace.Add($"direct-properties: hit {direct.Value}%");
            return new BatteryResolutionResult { BatteryPercent = direct, Trace = string.Join("\n", trace) };
        }

        trace.Add("direct-properties: miss");

        var allowDeepResolution = EnableDeepBatteryResolution || IsLikelyAudioDevice(info);
        if (!allowDeepResolution)
        {
            trace.Add("fast-battery-resolution: enabled");
            var candidateAddressesFast = GetCandidateBluetoothAddresses(info);
            if (candidateAddressesFast.Count > 0)
            {
                trace.Add($"address-candidates: {string.Join(",", candidateAddressesFast)}");

                var fromRelatedByAddressFast = await TryReadBatteryFromRelatedBleEndpointByAddressAsync(
                    await GetBleEndpointsAsync(),
                    candidateAddressesFast);
                cancellationToken.ThrowIfCancellationRequested();
                if (fromRelatedByAddressFast.HasValue)
                {
                    trace.Add($"related-ble-by-address: hit {fromRelatedByAddressFast.Value}%");
                    return new BatteryResolutionResult { BatteryPercent = fromRelatedByAddressFast, Trace = string.Join("\n", trace) };
                }

                var fromClassicEndpointFast = await TryReadBatteryFromRelatedClassicEndpointByAddressAsync(
                    await GetClassicEndpointsAsync(),
                    candidateAddressesFast,
                    cancellationToken);
                if (fromClassicEndpointFast.HasValue)
                {
                    trace.Add($"related-classic-endpoint-by-address: hit {fromClassicEndpointFast.Value}%");
                    return new BatteryResolutionResult { BatteryPercent = fromClassicEndpointFast, Trace = string.Join("\n", trace) };
                }

                if (IsLikelyAudioDevice(info))
                {
                    var (fromHuaweiPrivateFast, huaweiPrivateFastTrace) = await TryReadBatteryFromHuaweiPrivateProtocolAsync(info, null, cancellationToken);
                    trace.Add(huaweiPrivateFastTrace);
                    if (fromHuaweiPrivateFast.HasValue)
                    {
                        trace.Add($"huawei-private-fast: hit {fromHuaweiPrivateFast.Value}%");
                        return new BatteryResolutionResult
                        {
                            BatteryPercent = fromHuaweiPrivateFast,
                            Trace = string.Join("\n", trace),
                            InterfaceProbeDump = interfaceProbeDump
                        };
                    }

                    var (fromMediaFast, mediaFastTrace) = await TryReadBatteryFromMediaDeviceAsync(info, candidateAddressesFast, cancellationToken);
                    trace.Add(mediaFastTrace);
                    if (fromMediaFast.HasValue)
                    {
                        trace.Add($"media-device-fast: hit {fromMediaFast.Value}%");
                        return new BatteryResolutionResult
                        {
                            BatteryPercent = fromMediaFast,
                            Trace = string.Join("\n", trace),
                            InterfaceProbeDump = interfaceProbeDump
                        };
                    }

                    var (fromAudioAepFast, audioAepFastTrace) = await TryReadBatteryFromAudioAssociationEndpointsAsync(info, candidateAddressesFast, cancellationToken);
                    trace.Add(audioAepFastTrace);
                    if (fromAudioAepFast.HasValue)
                    {
                        trace.Add($"audio-aep-fast: hit {fromAudioAepFast.Value}%");
                        return new BatteryResolutionResult
                        {
                            BatteryPercent = fromAudioAepFast,
                            Trace = string.Join("\n", trace),
                            InterfaceProbeDump = interfaceProbeDump
                        };
                    }

                    try
                    {
                        var (fromUiAudioFast, uiAudioTrace) = await WithOperationTimeoutAsync(
                            ct => TryReadBatteryFromUiAutomationCacheAsync(info, discoveryCache, ct),
                            FastAudioUiProbeTimeout,
                            cancellationToken,
                            "快速音频 UIA 探测超时");
                        trace.Add(uiAudioTrace);
                        if (fromUiAudioFast.HasValue)
                        {
                            trace.Add($"uia-audio-fast: hit {fromUiAudioFast.Value}%");
                            return new BatteryResolutionResult
                            {
                                BatteryPercent = fromUiAudioFast,
                                Trace = string.Join("\n", trace),
                                InterfaceProbeDump = interfaceProbeDump
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        trace.Add($"uia-audio-fast: failed {ex.GetType().Name}");
                    }
                }
            }
            else
            {
                trace.Add("address-candidates: none");

                if (IsLikelyAudioDevice(info))
                {
                    var (fromMediaFastNoAddr, mediaFastNoAddrTrace) = await TryReadBatteryFromMediaDeviceAsync(info, Array.Empty<string>(), cancellationToken);
                    trace.Add(mediaFastNoAddrTrace);
                    if (fromMediaFastNoAddr.HasValue)
                    {
                        trace.Add($"media-device-fast(no-address): hit {fromMediaFastNoAddr.Value}%");
                        return new BatteryResolutionResult
                        {
                            BatteryPercent = fromMediaFastNoAddr,
                            Trace = string.Join("\n", trace),
                            InterfaceProbeDump = interfaceProbeDump
                        };
                    }

                    var (fromAudioAepFastNoAddr, audioAepFastNoAddrTrace) = await TryReadBatteryFromAudioAssociationEndpointsAsync(info, Array.Empty<string>(), cancellationToken);
                    trace.Add(audioAepFastNoAddrTrace);
                    if (fromAudioAepFastNoAddr.HasValue)
                    {
                        trace.Add($"audio-aep-fast(no-address): hit {fromAudioAepFastNoAddr.Value}%");
                        return new BatteryResolutionResult
                        {
                            BatteryPercent = fromAudioAepFastNoAddr,
                            Trace = string.Join("\n", trace),
                            InterfaceProbeDump = interfaceProbeDump
                        };
                    }

                    try
                    {
                        var (fromUiAudioFastNoAddr, uiAudioNoAddrTrace) = await WithOperationTimeoutAsync(
                            ct => TryReadBatteryFromUiAutomationCacheAsync(info, discoveryCache, ct),
                            FastAudioUiProbeTimeout,
                            cancellationToken,
                            "快速音频 UIA 探测超时");
                        trace.Add(uiAudioNoAddrTrace);
                        if (fromUiAudioFastNoAddr.HasValue)
                        {
                            trace.Add($"uia-audio-fast(no-address): hit {fromUiAudioFastNoAddr.Value}%");
                            return new BatteryResolutionResult
                            {
                                BatteryPercent = fromUiAudioFastNoAddr,
                                Trace = string.Join("\n", trace),
                                InterfaceProbeDump = interfaceProbeDump
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        trace.Add($"uia-audio-fast(no-address): failed {ex.GetType().Name}");
                    }
                }
            }

            return new BatteryResolutionResult
            {
                BatteryPercent = null,
                Trace = string.Join("\n", trace),
                InterfaceProbeDump = interfaceProbeDump
            };
        }

        // UIA 路径对所有设备开启，尝试从Windows设置页面读取电量
        var (fromUiAutomationPriority, uiProbeTracePriority) = await TryReadBatteryFromUiAutomationCacheAsync(info, discoveryCache, cancellationToken);
        trace.Add(uiProbeTracePriority);
        if (fromUiAutomationPriority.HasValue)
        {
            trace.Add($"uia-text-priority: hit {fromUiAutomationPriority.Value}%");
            return new BatteryResolutionResult
            {
                BatteryPercent = fromUiAutomationPriority,
                Trace = string.Join("\n", trace),
                InterfaceProbeDump = interfaceProbeDump
            };
        }
        trace.Add("uia-text-priority: miss");

        var containerId = TryGetGuidProperty(info, "System.Devices.Aep.ContainerId")
            ?? TryGetGuidProperty(info, "System.Devices.ContainerId");

        if (containerId.HasValue)
        {
            trace.Add($"container-id: {containerId.Value:D}");

            // 只检查是否已缓存为"未找到"，不返回缓存的电量值
            if (discoveryCache.TryGetBatteryByContainer(containerId.Value, out var cachedByContainer))
            {
                if (!cachedByContainer.HasValue)
                {
                    trace.Add("container-cache: miss");
                    // 继续执行，不返回
                }
                // 如果缓存有值，忽略缓存，继续查询最新值
            }

            var fromContainer = TryReadBatteryFromContainerDevicesAsync(containerId.Value, await GetDeviceKindDevicesAsync());
            if (fromContainer.HasValue)
            {
                // 只缓存"未找到"，不缓存有效值
                trace.Add($"container-device-properties: hit {fromContainer.Value}%");
                return new BatteryResolutionResult { BatteryPercent = fromContainer, Trace = string.Join("\n", trace) };
            }

            trace.Add("container-device-properties: miss");

            // Windows settings can show battery for some classic devices via a related BLE endpoint.
            var fromRelatedByContainer = await TryReadBatteryFromRelatedBleEndpointAsync(containerId.Value, await GetBleEndpointsAsync());
            if (fromRelatedByContainer.HasValue)
            {
                trace.Add($"related-ble-by-container: hit {fromRelatedByContainer.Value}%");
                return new BatteryResolutionResult { BatteryPercent = fromRelatedByContainer, Trace = string.Join("\n", trace) };
            }

            trace.Add("related-ble-by-container: miss");
            discoveryCache.SetBatteryByContainer(containerId.Value, null);
        }
        else
        {
            trace.Add("container-id: missing");
        }

        var candidateAddresses = GetCandidateBluetoothAddresses(info);
        if (candidateAddresses.Count > 0)
        {
            trace.Add($"address-candidates: {string.Join(",", candidateAddresses)}");
        }
        else
        {
            trace.Add("address-candidates: none");
        }

        var cachedMissCount = 0;
        var cachedAddressCount = 0;

        // 检查是否所有候选地址都已被缓存为"未找到"
        foreach (var candidateAddress in candidateAddresses)
        {
            if (discoveryCache.TryGetBatteryByAddress(candidateAddress, out var cachedByAddress))
            {
                cachedAddressCount++;
                if (!cachedByAddress.HasValue)
                {
                    cachedMissCount++;
                    trace.Add($"address-cache({candidateAddress}): miss");
                }
                // 注意：不再返回缓存的电量值，因为电量会变化，需要每次都重新查询
            }
        }

        // If all candidates are already cached as miss in this refresh cycle, skip expensive probing.
        if (candidateAddresses.Count > 0 && cachedAddressCount == candidateAddresses.Count && cachedMissCount == candidateAddresses.Count)
        {
            trace.Add("address-cache(all-candidates): miss");
            return new BatteryResolutionResult { BatteryPercent = null, Trace = string.Join("\n", trace) };
        }

        var fromRelatedByAddress = await TryReadBatteryFromRelatedBleEndpointByAddressAsync(await GetBleEndpointsAsync(), candidateAddresses);
        cancellationToken.ThrowIfCancellationRequested();
        if (fromRelatedByAddress.HasValue)
        {
            trace.Add($"related-ble-by-address: hit {fromRelatedByAddress.Value}%");
            return new BatteryResolutionResult { BatteryPercent = fromRelatedByAddress, Trace = string.Join("\n", trace) };
        }

        trace.Add("related-ble-by-address: miss");

        var fromClassicEndpoint = await TryReadBatteryFromRelatedClassicEndpointByAddressAsync(
            await GetClassicEndpointsAsync(),
            candidateAddresses,
            cancellationToken);
        if (fromClassicEndpoint.HasValue)
        {
            trace.Add($"related-classic-endpoint-by-address: hit {fromClassicEndpoint.Value}%");
            return new BatteryResolutionResult { BatteryPercent = fromClassicEndpoint, Trace = string.Join("\n", trace) };
        }

        trace.Add("related-classic-endpoint-by-address: miss");

        // 对经典蓝牙音频设备走短路径：优先媒体/AEP探测，避免被后续低命中慢策略拖超时。
        var isAudioCandidate = IsLikelyAudioDevice(info);
        if (isAudioCandidate)
        {
            trace.Add("audio-classic-shortpath: enabled");

            var (fromMediaDeviceFast, mediaProbeTraceFast) = await TryReadBatteryFromMediaDeviceAsync(info, candidateAddresses, cancellationToken);
            trace.Add(mediaProbeTraceFast);
            if (fromMediaDeviceFast.HasValue)
            {
                trace.Add($"media-device: hit {fromMediaDeviceFast.Value}%");
                return new BatteryResolutionResult
                {
                    BatteryPercent = fromMediaDeviceFast,
                    Trace = string.Join("\n", trace),
                    InterfaceProbeDump = interfaceProbeDump
                };
            }

            // Use an isolated token here so short-path probing is not interrupted by upstream refresh cancellation.
            var (fromAudioAepFast, audioAepTraceFast) = await TryReadBatteryFromAudioAssociationEndpointsAsync(info, candidateAddresses, CancellationToken.None);
            trace.Add(audioAepTraceFast);
            if (fromAudioAepFast.HasValue)
            {
                trace.Add($"audio-aep-probe: hit {fromAudioAepFast.Value}%");
                return new BatteryResolutionResult
                {
                    BatteryPercent = fromAudioAepFast,
                    Trace = string.Join("\n", trace),
                    InterfaceProbeDump = interfaceProbeDump
                };
            }

            trace.Add("audio-classic-shortpath: media+aep miss, skip slow-fallbacks");
            foreach (var candidateAddress in candidateAddresses)
            {
                discoveryCache.SetBatteryByAddress(candidateAddress, null);
            }

            return new BatteryResolutionResult
            {
                BatteryPercent = null,
                Trace = string.Join("\n", trace),
                InterfaceProbeDump = interfaceProbeDump
            };
        }

        var fromGlobalAep = TryReadBatteryFromGlobalAssociationEndpoints(
            info,
            await GetGlobalAssociationEndpointsAsync(),
            candidateAddresses,
            out var globalAepCandidateCount,
            out var globalAepHit);

        trace.Add($"global-aep-candidates: {globalAepCandidateCount}");
        if (!string.IsNullOrWhiteSpace(globalAepHit))
        {
            trace.Add($"global-aep-hit: {globalAepHit}");
        }

        if (fromGlobalAep.HasValue)
        {
            trace.Add($"global-aep-probe: hit {fromGlobalAep.Value}%");
            return new BatteryResolutionResult { BatteryPercent = fromGlobalAep, Trace = string.Join("\n", trace) };
        }

        trace.Add("global-aep-probe: miss");

        var fromCreateFromId = await TryReadBatteryFromCreateFromIdAsync(info, cancellationToken);
        if (fromCreateFromId.HasValue)
        {
            trace.Add($"create-from-id-probe: hit {fromCreateFromId.Value}%");
            return new BatteryResolutionResult { BatteryPercent = fromCreateFromId, Trace = string.Join("\n", trace) };
        }

        trace.Add("create-from-id-probe: miss");

        var fromDirectAddress = await TryReadBatteryFromBluetoothAddressDirectAsync(candidateAddresses);
        cancellationToken.ThrowIfCancellationRequested();
        if (fromDirectAddress.HasValue)
        {
            trace.Add($"direct-ble-by-address: hit {fromDirectAddress.Value}%");
            return new BatteryResolutionResult { BatteryPercent = fromDirectAddress, Trace = string.Join("\n", trace) };
        }

        trace.Add("direct-ble-by-address: miss");

        // Try to get battery from system device level (different from endpoint level)
        var fromSystemDeviceLevel = TryReadBatteryFromSystemDeviceLevelAsync(info, await GetDeviceKindDevicesAsync(), candidateAddresses);
        if (fromSystemDeviceLevel.HasValue)
        {
            trace.Add($"system-device-level: hit {fromSystemDeviceLevel.Value}%");
            return new BatteryResolutionResult { BatteryPercent = fromSystemDeviceLevel, Trace = string.Join("\n", trace) };
        }

        trace.Add("system-device-level: miss");

        var fromSystemByHeuristic = TryReadBatteryFromSystemDevicesByHeuristicAsync(info, await GetDeviceKindDevicesAsync(), candidateAddresses);
        if (fromSystemByHeuristic.HasValue)
        {
            trace.Add($"system-device-heuristic: hit {fromSystemByHeuristic.Value}%");
            return new BatteryResolutionResult { BatteryPercent = fromSystemByHeuristic, Trace = string.Join("\n", trace) };
        }

        trace.Add("system-device-heuristic: miss");

        var fromDeviceInterface = TryReadBatteryFromDeviceInterfaces(
            info,
            await GetDeviceInterfacesAsync(),
            candidateAddresses,
            out interfaceProbeDump,
            out var matchedInterfaceCount);

        trace.Add($"device-interface-candidates: {matchedInterfaceCount}");

        if (fromDeviceInterface.HasValue)
        {
            trace.Add($"device-interface-probe: hit {fromDeviceInterface.Value}%");
            return new BatteryResolutionResult
            {
                BatteryPercent = fromDeviceInterface,
                Trace = string.Join("\n", trace),
                InterfaceProbeDump = interfaceProbeDump
            };
        }

        trace.Add("device-interface-probe: miss");

        var fromAepService = TryReadBatteryFromAssociationEndpointServices(
            info,
            await GetAssociationEndpointServicesAsync(),
            candidateAddresses,
            out var aepServiceMatchedCount,
            out var aepServiceHitProperty);

        trace.Add($"aep-service-candidates: {aepServiceMatchedCount}");
        if (!string.IsNullOrWhiteSpace(aepServiceHitProperty))
        {
            trace.Add($"aep-service-hit-property: {aepServiceHitProperty}");
        }

        if (fromAepService.HasValue)
        {
            trace.Add($"aep-service-probe: hit {fromAepService.Value}%");
            return new BatteryResolutionResult
            {
                BatteryPercent = fromAepService,
                Trace = string.Join("\n", trace),
                InterfaceProbeDump = interfaceProbeDump
            };
        }

        trace.Add("aep-service-probe: miss");

        var fromBthRegistry = TryReadBatteryFromBluetoothRegistryCache(candidateAddresses, out var registryHitPath);
        if (fromBthRegistry.HasValue)
        {
            trace.Add($"bth-registry-cache: hit {fromBthRegistry.Value}% ({registryHitPath})");
            return new BatteryResolutionResult
            {
                BatteryPercent = fromBthRegistry,
                Trace = string.Join("\n", trace),
                InterfaceProbeDump = interfaceProbeDump
            };
        }

        trace.Add("bth-registry-cache: miss");

        // 策略8: 尝试通过 WMI 查询 PnP 设备获取电量
        var fromWmi = TryReadBatteryFromWmi(info, candidateAddresses);
        if (fromWmi.HasValue)
        {
            trace.Add($"wmi-pnp: hit {fromWmi.Value}%");
            return new BatteryResolutionResult
            {
                BatteryPercent = fromWmi,
                Trace = string.Join("\n", trace),
                InterfaceProbeDump = interfaceProbeDump
            };
        }
        trace.Add("wmi-pnp: miss");

        // 策略9: 尝试通过 Windows 蓝牙 API 获取关联的 LE 设备电量
        var fromAssociatedLe = await TryReadBatteryFromAssociatedLeDeviceAsync(info, candidateAddresses);
        cancellationToken.ThrowIfCancellationRequested();
        if (fromAssociatedLe.HasValue)
        {
            trace.Add($"associated-le: hit {fromAssociatedLe.Value}%");
            return new BatteryResolutionResult
            {
                BatteryPercent = fromAssociatedLe,
                Trace = string.Join("\n", trace),
                InterfaceProbeDump = interfaceProbeDump
            };
        }
        trace.Add("associated-le: miss");

        // 策略10: 尝试通过 Windows 11 BluetoothDevice.GetAsync 获取电池信息
        var fromWin11BluetoothApi = await TryReadBatteryFromWin11BluetoothApiAsync(info, candidateAddresses);
        cancellationToken.ThrowIfCancellationRequested();
        if (fromWin11BluetoothApi.HasValue)
        {
            trace.Add($"win11-bluetooth-api: hit {fromWin11BluetoothApi.Value}%");
            return new BatteryResolutionResult
            {
                BatteryPercent = fromWin11BluetoothApi,
                Trace = string.Join("\n", trace),
                InterfaceProbeDump = interfaceProbeDump
            };
        }
        trace.Add("win11-bluetooth-api: miss");

        // 策略11: 尝试通过 SetupAPI 获取设备属性
        var fromSetupApi = TryReadBatteryFromSetupApi(info, candidateAddresses);
        if (fromSetupApi.HasValue)
        {
            trace.Add($"setupapi: hit {fromSetupApi.Value}%");
            return new BatteryResolutionResult
            {
                BatteryPercent = fromSetupApi,
                Trace = string.Join("\n", trace),
                InterfaceProbeDump = interfaceProbeDump
            };
        }
        trace.Add("setupapi: miss");

        // 策略12: 尝试通过 Windows.Media.Devices 获取蓝牙音频设备电量
        var (fromMediaDevice, mediaProbeTrace) = await TryReadBatteryFromMediaDeviceAsync(info, candidateAddresses, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        trace.Add(mediaProbeTrace);
        if (fromMediaDevice.HasValue)
        {
            trace.Add($"media-device: hit {fromMediaDevice.Value}%");
            return new BatteryResolutionResult
            {
                BatteryPercent = fromMediaDevice,
                Trace = string.Join("\n", trace),
                InterfaceProbeDump = interfaceProbeDump
            };
        }
        trace.Add("media-device: miss");

        // 策略13: 直接扫系统 AssociationEndpoint（使用安全属性键）并按地址/名称匹配
        var (fromAudioAep, audioAepTrace) = await TryReadBatteryFromAudioAssociationEndpointsAsync(info, candidateAddresses, cancellationToken);
        trace.Add(audioAepTrace);
        if (fromAudioAep.HasValue)
        {
            trace.Add($"audio-aep-probe: hit {fromAudioAep.Value}%");
            return new BatteryResolutionResult
            {
                BatteryPercent = fromAudioAep,
                Trace = string.Join("\n", trace),
                InterfaceProbeDump = interfaceProbeDump
            };
        }
        trace.Add("audio-aep-probe: miss");

        foreach (var candidateAddress in candidateAddresses)
        {
            discoveryCache.SetBatteryByAddress(candidateAddress, null);
        }

        return new BatteryResolutionResult
        {
            BatteryPercent = null,
            Trace = string.Join("\n", trace),
            InterfaceProbeDump = interfaceProbeDump
        };
    }

    private static int? TryReadBatteryFromDeviceInterfaces(
        DeviceInformation source,
        IReadOnlyList<DeviceInformation> deviceInterfaces,
        IReadOnlyList<string> targetAddresses,
        out string interfaceProbeDump,
        out int matchedInterfaceCount)
    {
        var targetName = source.Name;
        if (string.IsNullOrWhiteSpace(targetName))
        {
            targetName = GetStringProperty(source, "System.ItemNameDisplay");
        }

        var targetContainerId = TryGetGuidProperty(source, "System.Devices.Aep.ContainerId")
            ?? TryGetGuidProperty(source, "System.Devices.ContainerId");

        var matchedInterfaces = new List<DeviceInformation>();

        foreach (var candidate in deviceInterfaces)
        {
            var candidateContainerId = TryGetGuidProperty(candidate, "System.Devices.Aep.ContainerId")
                ?? TryGetGuidProperty(candidate, "System.Devices.ContainerId");

            var relatedByContainer = targetContainerId.HasValue && candidateContainerId == targetContainerId.Value;
            var relatedByAddressOrName = IsLikelySameDevice(source, candidate, targetAddresses, targetName);

            if (!relatedByContainer && !relatedByAddressOrName)
            {
                continue;
            }

            matchedInterfaces.Add(candidate);
        }

        matchedInterfaceCount = matchedInterfaces.Count;
        interfaceProbeDump = BuildDeviceInterfaceProbeDump(matchedInterfaces);

        foreach (var matchedInterface in matchedInterfaces)
        {
            var battery = TryGetBatteryPercentFromProperties(matchedInterface.Properties);
            if (battery.HasValue)
            {
                return battery;
            }
        }

        return null;
    }

    private static int? TryReadBatteryFromAssociationEndpointServices(
        DeviceInformation source,
        IReadOnlyList<DeviceInformation> services,
        IReadOnlyList<string> targetAddresses,
        out int matchedCount,
        out string? hitProperty)
    {
        var targetName = source.Name;
        if (string.IsNullOrWhiteSpace(targetName))
        {
            targetName = GetStringProperty(source, "System.ItemNameDisplay");
        }

        var targetContainerId = TryGetGuidProperty(source, "System.Devices.Aep.ContainerId")
            ?? TryGetGuidProperty(source, "System.Devices.ContainerId");

        var matchedServices = new List<DeviceInformation>();
        foreach (var candidate in services)
        {
            var candidateContainerId = TryGetGuidProperty(candidate, "System.Devices.Aep.ContainerId")
                ?? TryGetGuidProperty(candidate, "System.Devices.ContainerId");

            var relatedByContainer = targetContainerId.HasValue && candidateContainerId == targetContainerId.Value;
            var relatedByAddressOrName = IsLikelySameDevice(source, candidate, targetAddresses, targetName);
            if (relatedByContainer || relatedByAddressOrName)
            {
                matchedServices.Add(candidate);
            }
        }

        matchedCount = matchedServices.Count;
        hitProperty = null;

        foreach (var matched in matchedServices)
        {
            foreach (var key in BatteryPropertyNames)
            {
                if (!matched.Properties.TryGetValue(key, out var value) || value is null)
                {
                    continue;
                }

                var parsed = ParseBatteryValue(value);
                if (parsed is >= 0 and <= 100)
                {
                    hitProperty = key;
                    return parsed;
                }
            }

            foreach (var pair in matched.Properties)
            {
                if (pair.Value is null)
                {
                    continue;
                }

                if (!pair.Key.Contains("battery", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parsed = ParseBatteryValue(pair.Value);
                if (parsed is >= 0 and <= 100)
                {
                    hitProperty = pair.Key;
                    return parsed;
                }
            }
        }

        return null;
    }

    private static int? TryReadBatteryFromSystemDeviceLevelAsync(
        DeviceInformation source,
        IReadOnlyList<DeviceInformation> devices,
        IReadOnlyList<string> targetAddresses)
    {
        // Windows sometimes exposes battery at the Device level rather than AssociationEndpoint level
        // Try to find the matching device in DeviceInformationKind.Device
        var targetName = source.Name;
        if (string.IsNullOrWhiteSpace(targetName) && source.Properties.TryGetValue("System.ItemNameDisplay", out var displayNameObj) && displayNameObj is string displayName)
        {
            targetName = displayName;
        }

        var containerId = TryGetGuidProperty(source, "System.Devices.Aep.ContainerId")
            ?? TryGetGuidProperty(source, "System.Devices.ContainerId");

        foreach (var device in devices)
        {
            // Match by container ID
            if (containerId.HasValue)
            {
                var deviceContainerId = TryGetGuidProperty(device, "System.Devices.ContainerId");
                if (deviceContainerId == containerId.Value)
                {
                    var battery = TryGetBatteryPercentFromProperties(device.Properties);
                    if (battery.HasValue)
                    {
                        return battery;
                    }
                }
            }

            // Match by address
            if (targetAddresses.Count > 0)
            {
                var deviceAddress = TryGetNormalizedBluetoothAddress(device)
                    ?? TryExtractRemoteBluetoothAddress(device.Id)
                    ?? TryExtractRemoteBluetoothAddress(GetStringProperty(device, "System.Devices.DeviceInstanceId"));

                if (!string.IsNullOrWhiteSpace(deviceAddress) && targetAddresses.Contains(deviceAddress, StringComparer.OrdinalIgnoreCase))
                {
                    var battery = TryGetBatteryPercentFromProperties(device.Properties);
                    if (battery.HasValue)
                    {
                        return battery;
                    }
                }
            }

            // Match by name (fallback)
            if (!string.IsNullOrWhiteSpace(targetName))
            {
                var deviceName = device.Name;
                if (string.IsNullOrWhiteSpace(deviceName))
                {
                    deviceName = GetStringProperty(device, "System.ItemNameDisplay");
                }

                if (!string.IsNullOrWhiteSpace(deviceName) &&
                    (deviceName.Equals(targetName, StringComparison.OrdinalIgnoreCase) ||
                     deviceName.Contains(targetName, StringComparison.OrdinalIgnoreCase) ||
                     targetName.Contains(deviceName, StringComparison.OrdinalIgnoreCase)))
                {
                    var battery = TryGetBatteryPercentFromProperties(device.Properties);
                    if (battery.HasValue)
                    {
                        return battery;
                    }
                }
            }
        }

        return null;
    }

    private static int? TryReadBatteryFromSystemDevicesByHeuristicAsync(
        DeviceInformation source,
        IReadOnlyList<DeviceInformation> devices,
        IReadOnlyList<string> targetAddresses)
    {
        var targetName = source.Name;
        if (string.IsNullOrWhiteSpace(targetName) && source.Properties.TryGetValue("System.ItemNameDisplay", out var displayNameObj) && displayNameObj is string displayName)
        {
            targetName = displayName;
        }

        foreach (var device in devices)
        {
            var battery = TryGetBatteryPercentFromProperties(device.Properties);
            if (!battery.HasValue)
            {
                continue;
            }

            if (IsLikelySameDevice(source, device, targetAddresses, targetName))
            {
                return battery;
            }
        }

        return null;
    }

    private static bool IsLikelySameDevice(
        DeviceInformation source,
        DeviceInformation candidate,
        IReadOnlyList<string> targetAddresses,
        string? targetName)
    {
        var candidateAddress = TryGetNormalizedBluetoothAddress(candidate)
            ?? TryExtractRemoteBluetoothAddress(candidate.Id)
            ?? TryExtractRemoteBluetoothAddress(GetStringProperty(candidate, "System.Devices.DeviceInstanceId"));

        if (targetAddresses.Count > 0 &&
            !string.IsNullOrWhiteSpace(candidateAddress) &&
            targetAddresses.Contains(candidateAddress, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var candidateName = candidate.Name;
        if (string.IsNullOrWhiteSpace(candidateName))
        {
            candidateName = GetStringProperty(candidate, "System.ItemNameDisplay");
        }

        if (string.IsNullOrWhiteSpace(targetName) || string.IsNullOrWhiteSpace(candidateName))
        {
            return false;
        }

        var left = targetName.Trim();
        var right = candidateName.Trim();

        return left.Equals(right, StringComparison.OrdinalIgnoreCase)
            || left.Contains(right, StringComparison.OrdinalIgnoreCase)
            || right.Contains(left, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int?> TryReadBatteryFromBluetoothAddressDirectAsync(IReadOnlyList<string> candidateAddresses)
    {
        foreach (var normalizedAddress in candidateAddresses)
        {
            if (!ulong.TryParse(normalizedAddress, System.Globalization.NumberStyles.HexNumber, null, out var bluetoothAddress))
            {
                continue;
            }

            try
            {
                var ble = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);
                if (ble is null)
                {
                    continue;
                }

                using (ble)
                {
                    var probeStatus = new DeviceStatus();
                    _ = await TryReadBatteryLevelAsync(ble, probeStatus);
                    if (probeStatus.BatteryPercent.HasValue)
                    {
                        return probeStatus.BatteryPercent;
                    }
                }
            }
            catch
            {
                // Ignore and continue probing other candidate addresses.
            }
        }

        return null;
    }

    private static async Task<int?> TryReadBatteryFromRelatedBleEndpointByAddressAsync(
        IReadOnlyList<DeviceInformation> endpoints,
        IReadOnlyList<string> targetAddresses)
    {
        if (targetAddresses.Count == 0)
        {
            return null;
        }

        foreach (var endpoint in endpoints)
        {
            var endpointAddress = TryGetNormalizedBluetoothAddress(endpoint)
                ?? TryExtractRemoteBluetoothAddress(endpoint.Id);

            if (string.IsNullOrWhiteSpace(endpointAddress) || !targetAddresses.Contains(endpointAddress, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var batteryFromProps = TryGetBatteryPercentFromProperties(endpoint.Properties);
            if (batteryFromProps.HasValue)
            {
                return batteryFromProps;
            }

            try
            {
                var ble = await BluetoothLEDevice.FromIdAsync(endpoint.Id);
                if (ble is null)
                {
                    continue;
                }

                using (ble)
                {
                    var probeStatus = new DeviceStatus();
                    _ = await TryReadBatteryLevelAsync(ble, probeStatus);
                    if (probeStatus.BatteryPercent.HasValue)
                    {
                        return probeStatus.BatteryPercent;
                    }
                }
            }
            catch
            {
                // Ignore and continue probing other related endpoints.
            }
        }

        return null;
    }

    private static async Task<int?> TryReadBatteryFromRelatedClassicEndpointByAddressAsync(
        IReadOnlyList<DeviceInformation> endpoints,
        IReadOnlyList<string> targetAddresses,
        CancellationToken cancellationToken)
    {
        if (targetAddresses.Count == 0)
        {
            return null;
        }

        foreach (var endpoint in endpoints)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var endpointAddress = TryGetNormalizedBluetoothAddress(endpoint)
                ?? TryExtractRemoteBluetoothAddress(endpoint.Id);

            if (string.IsNullOrWhiteSpace(endpointAddress) || !targetAddresses.Contains(endpointAddress, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var batteryFromProps = TryGetBatteryPercentFromProperties(endpoint.Properties);
            if (batteryFromProps.HasValue)
            {
                return batteryFromProps;
            }

            try
            {
                var updated = await WithOperationTimeoutAsync(
                    ct => DeviceInformation.CreateFromIdAsync(
                        endpoint.Id,
                        ClassicEndpointRequestedProperties,
                        DeviceInformationKind.AssociationEndpoint).AsTask(ct),
                    DeviceOpenTimeout,
                    cancellationToken,
                    "经典端点 CreateFromId 探测超时");

                var batteryFromUpdated = TryGetBatteryPercentFromProperties(updated.Properties);
                if (batteryFromUpdated.HasValue)
                {
                    return batteryFromUpdated;
                }
            }
            catch
            {
                // Ignore and continue probing other endpoints.
            }
        }

        return null;
    }

    private static async Task<int?> TryReadBatteryFromCreateFromIdAsync(DeviceInformation info, CancellationToken cancellationToken)
    {
        var requested = BatteryPropertyNames
            .Concat(AddressPropertyNames)
            .Concat(new[]
            {
                "System.Devices.Aep.ContainerId",
                "System.Devices.ContainerId",
                "System.Devices.DeviceInstanceId",
                "System.ItemNameDisplay"
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var kinds = new[]
        {
            DeviceInformationKind.AssociationEndpoint,
            DeviceInformationKind.Device,
            DeviceInformationKind.DeviceInterface
        };

        foreach (var kind in kinds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var updated = await WithOperationTimeoutAsync(
                    ct => DeviceInformation.CreateFromIdAsync(info.Id, requested, kind).AsTask(ct),
                    DeviceOpenTimeout,
                    cancellationToken,
                    $"CreateFromId 探测超时({kind})");

                var battery = TryGetBatteryPercentFromProperties(updated.Properties);
                if (battery.HasValue)
                {
                    return battery;
                }
            }
            catch
            {
                // Try next kind.
            }
        }

        return null;
    }

    private static int? TryReadBatteryFromGlobalAssociationEndpoints(
        DeviceInformation source,
        IReadOnlyList<DeviceInformation> endpoints,
        IReadOnlyList<string> targetAddresses,
        out int matchedCount,
        out string? hit)
    {
        hit = null;
        var targetName = source.Name;
        if (string.IsNullOrWhiteSpace(targetName))
        {
            targetName = GetStringProperty(source, "System.ItemNameDisplay");
        }

        var candidates = new List<DeviceInformation>();

        foreach (var endpoint in endpoints)
        {
            var protocolId = TryGetGuidProperty(endpoint, "System.Devices.Aep.ProtocolId");
            if (protocolId.HasValue && protocolId.Value != BluetoothAepProtocolId)
            {
                continue;
            }

            if (!IsLikelySameDevice(source, endpoint, targetAddresses, targetName))
            {
                continue;
            }

            candidates.Add(endpoint);
        }

        matchedCount = candidates.Count;
        if (matchedCount == 0)
        {
            return null;
        }

        // Prefer connected candidates first, mirroring Settings surface.
        var ordered = candidates
            .OrderByDescending(c => TryGetConnectionProperty(c) == true)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var candidate in ordered)
        {
            foreach (var key in BatteryPropertyNames)
            {
                if (!candidate.Properties.TryGetValue(key, out var value) || value is null)
                {
                    continue;
                }

                var parsed = ParseBatteryValue(value);
                if (parsed is >= 0 and <= 100)
                {
                    hit = $"{candidate.Name}|{key}";
                    return parsed;
                }
            }

            foreach (var pair in candidate.Properties)
            {
                if (pair.Value is null || !pair.Key.Contains("battery", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parsed = ParseBatteryValue(pair.Value);
                if (parsed is >= 0 and <= 100)
                {
                    hit = $"{candidate.Name}|{pair.Key}";
                    return parsed;
                }
            }
        }

        return null;
    }

    private static int? TryReadBatteryFromContainerDevicesAsync(Guid containerId, IReadOnlyList<DeviceInformation> devices)
    {
        foreach (var device in devices)
        {
            var deviceContainerId = TryGetGuidProperty(device, "System.Devices.ContainerId");
            if (deviceContainerId != containerId)
            {
                continue;
            }

            var battery = TryGetBatteryPercentFromProperties(device.Properties);
            if (battery.HasValue)
            {
                return battery;
            }
        }

        return null;
    }

    private static string BuildClassicDeviceMessageAsync(DeviceInformation info, int? batteryPercent)
    {
        return BuildClassicDeviceMessageAsync(info, batteryPercent, null);
    }

    private static string BuildClassicDeviceMessageAsync(
        DeviceInformation info,
        int? batteryPercent,
        AudioCapabilityProbeResult? probe)
    {
        if (batteryPercent.HasValue)
        {
            return "经典蓝牙设备，电量来自系统属性或同设备 BLE 端点。";
        }

        if (probe is not null && probe.IsAudioCandidate)
        {
            var profiles = BuildAudioProfileSummary(probe);
            return $"音频设备未读取到电量。{profiles}。Windows 对耳机常见的 AVRCP/HFP 电量上报公开 API 支持有限，通常仍需厂商私有协议或 SDK。";
        }

        var vendorHint = GetVendorHint(info.Name);
        if (!string.IsNullOrWhiteSpace(vendorHint))
        {
            return "该设备是经典蓝牙通道，且系统属性未提供电量；通常需要厂商 SDK/私有协议。" + vendorHint;
        }

        return "该设备是经典蓝牙通道，且系统属性未提供电量；通常需要厂商 SDK/私有协议。";
    }

    private static string BuildDeviceDebugDump(
        DeviceInformation info,
        string channel,
        bool? isConnected,
        int? batteryPercent,
        string? diagnostic,
        string? batteryResolutionTrace = null,
        string? interfaceProbeDump = null,
        string? audioCapabilitySummary = null,
        string? audioCapabilityDetail = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Channel: {channel}");
        builder.AppendLine($"Name: {info.Name}");
        builder.AppendLine($"Id: {info.Id}");
        builder.AppendLine($"LogicalKey(Container): {BuildLogicalDeviceKey(info)}");
        builder.AppendLine($"IsConnected(Resolved): {(isConnected.HasValue ? isConnected.Value : "null")}");
        builder.AppendLine($"BatteryPercent(Resolved): {(batteryPercent.HasValue ? batteryPercent.Value : "null")}");

        if (!string.IsNullOrWhiteSpace(diagnostic))
        {
            builder.AppendLine($"Diagnostic: {diagnostic}");
        }

        if (!string.IsNullOrWhiteSpace(batteryResolutionTrace))
        {
            builder.AppendLine();
            builder.AppendLine("BatteryResolutionTrace:");
            builder.AppendLine(batteryResolutionTrace);
        }

        if (!string.IsNullOrWhiteSpace(audioCapabilitySummary))
        {
            builder.AppendLine();
            builder.AppendLine("AudioCapabilitySummary:");
            builder.AppendLine(audioCapabilitySummary);
        }

        if (!string.IsNullOrWhiteSpace(audioCapabilityDetail))
        {
            builder.AppendLine();
            builder.AppendLine("AudioCapabilityDetail:");
            builder.AppendLine(audioCapabilityDetail);
        }

        builder.AppendLine();
        builder.AppendLine("DeviceInformation.Properties:");
        builder.AppendLine(BuildPropertiesDump(info.Properties));

        if (!string.IsNullOrWhiteSpace(interfaceProbeDump))
        {
            builder.AppendLine();
            builder.AppendLine("DeviceInterfaceProbe:");
            builder.AppendLine(interfaceProbeDump);
        }

        return builder.ToString();
    }

    private static string BuildDeviceInterfaceProbeDump(IReadOnlyList<DeviceInformation> matchedInterfaces)
    {
        if (matchedInterfaces.Count == 0)
        {
            return "<none>";
        }

        var builder = new StringBuilder();
        var index = 1;
        foreach (var matchedInterface in matchedInterfaces)
        {
            builder.AppendLine($"[{index}] Name={matchedInterface.Name}");
            builder.AppendLine($"    Id={matchedInterface.Id}");
            builder.AppendLine($"    BatteryFromProperties={TryGetBatteryPercentFromProperties(matchedInterface.Properties)?.ToString() ?? "null"}");
            builder.AppendLine("    Properties:");
            var dump = BuildPropertiesDump(matchedInterface.Properties)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\n", "\n    ", StringComparison.Ordinal);
            builder.AppendLine("    " + dump.TrimEnd());
            builder.AppendLine();
            index++;
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildPropertiesDump(IReadOnlyDictionary<string, object> properties)
    {
        if (properties.Count == 0)
        {
            return "<empty>";
        }

        var ordered = properties
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var builder = new StringBuilder();
        foreach (var pair in ordered)
        {
            builder.Append(pair.Key);
            builder.Append(" = ");
            builder.AppendLine(FormatPropertyValue(pair.Value));
        }

        return builder.ToString();
    }

    private static string FormatPropertyValue(object? value)
    {
        if (value is null)
        {
            return "<null>";
        }

        if (value is string text)
        {
            return text;
        }

        if (value is Guid guid)
        {
            return guid.ToString("D");
        }

        if (value is DateTimeOffset dto)
        {
            return dto.ToString("O");
        }

        if (value is DateTime dt)
        {
            return dt.ToString("O");
        }

        if (value is byte[] byteArray)
        {
            return "0x" + Convert.ToHexString(byteArray);
        }

        if (value is IReadOnlyList<object> roList)
        {
            return "[" + string.Join(", ", roList.Select(FormatPropertyValue)) + "]";
        }

        if (value is IEnumerable<object> enumerable)
        {
            return "[" + string.Join(", ", enumerable.Select(FormatPropertyValue)) + "]";
        }

        return value.ToString() ?? "<unknown>";
    }

    private static string BuildLogicalDeviceKey(DeviceInformation info)
    {
        var containerId = TryGetGuidProperty(info, "System.Devices.Aep.ContainerId")
            ?? TryGetGuidProperty(info, "System.Devices.ContainerId");

        if (containerId.HasValue)
        {
            return containerId.Value.ToString("D");
        }

        var normalizedAddress = TryGetNormalizedBluetoothAddress(info)
            ?? TryExtractRemoteBluetoothAddress(info.Id);

        if (!string.IsNullOrWhiteSpace(normalizedAddress))
        {
            return "BTADDR:" + normalizedAddress;
        }

        return info.Id;
    }

    private static string? TryGetNormalizedBluetoothAddress(DeviceInformation info)
    {
        foreach (var propertyName in AddressPropertyNames)
        {
            if (!info.Properties.TryGetValue(propertyName, out var value) || value is null)
            {
                continue;
            }

            var normalized = value switch
            {
                ulong ul => ul.ToString("X12"),
                long l => unchecked((ulong)l).ToString("X12"),
                byte[] bytes when bytes.Length > 0 => Convert.ToHexString(bytes),
                string text => NormalizeBluetoothAddress(text),
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    private static string? GetStringProperty(DeviceInformation info, string propertyName)
    {
        if (!info.Properties.TryGetValue(propertyName, out var value) || value is null)
        {
            return null;
        }

        return value.ToString();
    }

    private static string? TryExtractRemoteBluetoothAddress(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        var matches = BluetoothAddressRegex.Matches(deviceId);
        if (matches.Count == 0)
        {
            return null;
        }

        var remoteAddress = matches[^1].Value;
        return NormalizeBluetoothAddress(remoteAddress);
    }

    private static IReadOnlyList<string> GetCandidateBluetoothAddresses(DeviceInformation info)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? raw)
        {
            var normalized = NormalizeBluetoothAddress(raw);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            if (seen.Add(normalized))
            {
                candidates.Add(normalized);
            }
        }

        AddCandidate(TryGetNormalizedBluetoothAddress(info));

        foreach (Match match in BluetoothAddressRegex.Matches(info.Id))
        {
            AddCandidate(match.Value);
        }

        var deviceInstanceId = GetStringProperty(info, "System.Devices.DeviceInstanceId");
        if (!string.IsNullOrWhiteSpace(deviceInstanceId))
        {
            foreach (Match match in BluetoothAddressRegex.Matches(deviceInstanceId))
            {
                AddCandidate(match.Value);
            }
        }

        return candidates;
    }

    private static string? NormalizeBluetoothAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var hex = new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length < 12)
        {
            return null;
        }

        return hex[^12..];
    }

    private static async Task<int?> TryReadBatteryFromRelatedBleEndpointAsync(Guid containerId, IReadOnlyList<DeviceInformation> endpoints)
    {
        foreach (var endpoint in endpoints)
        {
            var endpointContainerId = TryGetGuidProperty(endpoint, "System.Devices.Aep.ContainerId")
                ?? TryGetGuidProperty(endpoint, "System.Devices.ContainerId");

            if (endpointContainerId != containerId)
            {
                continue;
            }

            var batteryFromProps = TryGetBatteryPercentFromProperties(endpoint.Properties);
            if (batteryFromProps.HasValue)
            {
                return batteryFromProps;
            }

            try
            {
                var ble = await BluetoothLEDevice.FromIdAsync(endpoint.Id);
                if (ble is null)
                {
                    continue;
                }

                using (ble)
                {
                    var probeStatus = new DeviceStatus();
                    var readError = await TryReadBatteryLevelAsync(ble, probeStatus);
                    if (probeStatus.BatteryPercent.HasValue)
                    {
                        return probeStatus.BatteryPercent;
                    }

                    if (string.IsNullOrWhiteSpace(readError))
                    {
                        continue;
                    }
                }
            }
            catch
            {
                // Ignore and continue probing other related endpoints.
            }
        }

        return null;
    }

    private static string GetVendorHint(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return string.Empty;
        }

        var name = deviceName.ToLowerInvariant();
        if (name.Contains("logitech") || name.Contains("logi") || name.Contains("g pro") || name.Contains("mx "))
        {
            return " 建议尝试 Logitech 路线（G HUB / Options+ 相关接口）。";
        }

        if (name.Contains("razer"))
        {
            return " 建议尝试 Razer 路线（Synapse/Chroma 相关接口）。";
        }

        if (name.Contains("steelseries") || name.Contains("arctis"))
        {
            return " 建议尝试 SteelSeries 路线（GG/Engine 相关接口）。";
        }

        if (name.Contains("corsair") || name.Contains("hs80") || name.Contains("dark core"))
        {
            return " 建议尝试 Corsair 路线（iCUE 相关接口）。";
        }

        if (name.Contains("sony") || name.Contains("wh-") || name.Contains("wf-"))
        {
            return " 建议优先通过厂商应用或设备专有协议获取电量。";
        }

        return string.Empty;
    }

    private static Guid? TryGetGuidProperty(DeviceInformation info, string propertyName)
    {
        if (!info.Properties.TryGetValue(propertyName, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            Guid guid => guid,
            string text when Guid.TryParse(text, out var parsed) => parsed,
            _ => null
        };
    }

    private static string BuildAudioAwareDiagnostic(string? original, AudioCapabilityProbeResult probe)
    {
        var baseline = string.IsNullOrWhiteSpace(original) ? "未读取到电量。" : original;
        var profileSummary = BuildAudioProfileSummary(probe);

        if (probe.HasBleBatteryService)
        {
            return $"{baseline} 已检测为音频设备，且可见 BLE Battery Service，但读取仍失败，可能是设备休眠、权限或厂商限制。{profileSummary}。";
        }

        return $"{baseline} 已检测为音频设备，但未发现可读 BLE Battery Service。{profileSummary}。很多耳机通过 AVRCP/HFP 厂商扩展上报电量，Windows 公共 API 往往无法直接读取百分比。";
    }

    private static string BuildAudioProfileSummary(AudioCapabilityProbeResult probe)
    {
        var profiles = new List<string>();
        if (probe.HasA2dpProfile)
        {
            profiles.Add("A2DP");
        }

        if (probe.HasAvrcpProfile)
        {
            profiles.Add("AVRCP");
        }

        if (probe.HasHfpProfile)
        {
            profiles.Add("HFP/HSP");
        }

        if (profiles.Count == 0)
        {
            profiles.Add("未探测到标准音频 Profile");
        }

        return $"音频 Profile: {string.Join(", ", profiles)}";
    }

    private static bool IsLikelyAudioDevice(DeviceInformation info)
    {
        var name = info.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = GetStringProperty(info, "System.ItemNameDisplay") ?? string.Empty;
        }

        var lowered = name.ToLowerInvariant();
        var markers = new[]
        {
            "headset",
            "headphone",
            "earbud",
            "earbuds",
            "airpods",
            "buds",
            "speaker",
            "wh-",
            "wf-",
            "a2dp",
            "hfp",
            // 常见耳机品牌/系列
            "rose",
            "cambrian",
            "freebuds",
            "buds",
            "earpods",
            "jabra",
            "sony",
            "bose",
            "sennheiser",
            "beats",
            "akg",
            "铁三角",
            "漫步者",
            "倍思",
            "iems",
            "iem"
        };

        if (markers.Any(lowered.Contains))
        {
            return true;
        }

        var classGuid = GetStringProperty(info, "System.Devices.ClassGuid");
        return !string.IsNullOrWhiteSpace(classGuid)
            && (classGuid.Contains("audio", StringComparison.OrdinalIgnoreCase)
                || classGuid.Contains("4d36e96c", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLikelyPointerDevice(DeviceInformation info)
    {
        var name = info.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = GetStringProperty(info, "System.ItemNameDisplay") ?? string.Empty;
        }

        var lowered = name.ToLowerInvariant();
        if (lowered.Contains("mouse") || lowered.Contains("mice") || lowered.Contains("trackball") || lowered.Contains("鼠标"))
        {
            return true;
        }

        var classGuid = GetStringProperty(info, "System.Devices.ClassGuid");
        return !string.IsNullOrWhiteSpace(classGuid)
            && classGuid.Contains("4d36e96f", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<AudioCapabilityProbeResult> BuildAudioCapabilityProbeAsync(
        DeviceInformation info,
        BluetoothLEDevice? existingBle,
        CancellationToken cancellationToken)
    {
        if (!IsLikelyAudioDevice(info))
        {
            return new AudioCapabilityProbeResult
            {
                IsAudioCandidate = false,
                Summary = "audio-probe: skipped (not likely audio device)"
            };
        }

        var detail = new List<string>();
        var hasBleBatteryService = false;
        var hasAvrcp = false;
        var hasHfp = false;
        var hasA2dp = false;
        var probeCanceled = false;

        BluetoothLEDevice? ownedBle = null;
        try
        {
            var ble = existingBle;
            if (ble is null)
            {
                ble = await CreateBleDeviceWithRetryAsync(info, cancellationToken);
                ownedBle = ble;
            }

            if (ble is not null)
            {
                var gatt = await WithOperationTimeoutAsync(
                    ct => ble.GetGattServicesAsync(BluetoothCacheMode.Cached).AsTask(ct),
                    GattOperationTimeout,
                    cancellationToken,
                    "音频设备 GATT 服务探测超时");

                detail.Add($"ble-gatt-status: {gatt.Status}");
                if (gatt.Status == GattCommunicationStatus.Success)
                {
                    var serviceUuids = gatt.Services
                        .Select(s => s.Uuid)
                        .Distinct()
                        .Select(ToShortOrLongUuid)
                        .Take(12)
                        .ToList();

                    hasBleBatteryService = gatt.Services.Any(s => s.Uuid == GattServiceUuids.Battery);
                    detail.Add($"ble-gatt-services: {serviceUuids.Count}");
                    detail.Add("ble-gatt-uuids: " + (serviceUuids.Count > 0 ? string.Join(", ", serviceUuids) : "<none>"));
                    detail.Add($"ble-battery-service-visible: {hasBleBatteryService}");
                }
            }
            else
            {
                detail.Add("ble-open: failed");
            }
        }
        catch (OperationCanceledException)
        {
            probeCanceled = true;
            detail.Add("ble-probe-canceled: timeout-or-refresh-canceled");
        }
        catch (Exception ex)
        {
            detail.Add($"ble-probe-error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ownedBle?.Dispose();
        }

        try
        {
            var classic = await WithOperationTimeoutAsync(
                ct => BluetoothDevice.FromIdAsync(info.Id).AsTask(ct),
                DeviceOpenTimeout,
                cancellationToken,
                "音频设备经典蓝牙探测超时");

            if (classic is not null)
            {
                using (classic)
                {
                    var rfcomm = await WithOperationTimeoutAsync(
                        ct => classic.GetRfcommServicesAsync(BluetoothCacheMode.Cached).AsTask(ct),
                        GattOperationTimeout,
                        cancellationToken,
                        "音频设备 RFCOMM 服务探测超时");

                    detail.Add($"rfcomm-error: {rfcomm.Error}");
                    var shortUuids = rfcomm.Services
                        .Select(s => TryGetBluetoothShortUuid(s.ServiceId.Uuid))
                        .Where(v => v.HasValue)
                        .Select(v => v!.Value)
                        .Distinct()
                        .ToList();

                    hasAvrcp = shortUuids.Any(v => v is 0x110C or 0x110E or 0x110F);
                    hasHfp = shortUuids.Any(v => v is 0x111E or 0x111F or 0x1108 or 0x1112);
                    hasA2dp = shortUuids.Any(v => v is 0x110A or 0x110B or 0x110D);

                    detail.Add($"rfcomm-services: {rfcomm.Services.Count}");
                    detail.Add("rfcomm-short-uuids: " + (shortUuids.Count > 0
                        ? string.Join(", ", shortUuids.Select(v => "0x" + v.ToString("X4")))
                        : "<none>"));
                }
            }
            else
            {
                detail.Add("classic-open: failed");
            }
        }
        catch (OperationCanceledException)
        {
            probeCanceled = true;
            detail.Add("classic-probe-canceled: timeout-or-refresh-canceled");
        }
        catch (Exception ex)
        {
            detail.Add($"classic-probe-error: {ex.GetType().Name}: {ex.Message}");
        }

        var profileSummary = probeCanceled && !hasA2dp && !hasAvrcp && !hasHfp
            ? "音频 Profile: 探测超时或被取消"
            : BuildAudioProfileSummary(new AudioCapabilityProbeResult { HasA2dpProfile = hasA2dp, HasAvrcpProfile = hasAvrcp, HasHfpProfile = hasHfp });

        var summary = $"audio-probe: candidate=true; ble-battery-service={(hasBleBatteryService ? "yes" : "no")}; {profileSummary}";

        return new AudioCapabilityProbeResult
        {
            IsAudioCandidate = true,
            HasBleBatteryService = hasBleBatteryService,
            HasAvrcpProfile = hasAvrcp,
            HasHfpProfile = hasHfp,
            HasA2dpProfile = hasA2dp,
            Summary = summary,
            Detail = string.Join("\n", detail)
        };
    }

    private static ushort? TryGetBluetoothShortUuid(Guid guid)
    {
        var bytes = guid.ToByteArray();
        if (bytes[4] != 0x00 || bytes[5] != 0x00)
        {
            return null;
        }

        var suffix = "-0000-1000-8000-00805f9b34fb";
        var text = guid.ToString("D");
        if (!text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Convert.ToUInt16(text.Substring(4, 4), 16);
    }

    private static string ToShortOrLongUuid(Guid guid)
    {
        var shortUuid = TryGetBluetoothShortUuid(guid);
        if (shortUuid.HasValue)
        {
            return "0x" + shortUuid.Value.ToString("X4");
        }

        return guid.ToString("D");
    }

    private static bool IsTransientGattError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return error.Contains("Unreachable", StringComparison.OrdinalIgnoreCase)
            || error.Contains("超时", StringComparison.OrdinalIgnoreCase)
            || error.Contains("不可达", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> TryReconnectAndReadBatteryAsync(
        DeviceInformation info,
        DeviceStatus status,
        CancellationToken cancellationToken)
    {
        await Task.Delay(ReconnectDelay, cancellationToken);
        var reconnectDevice = await CreateBleDeviceWithRetryAsync(info, cancellationToken);
        if (reconnectDevice is null)
        {
            return "设备重连失败，无法读取电量。";
        }

        using (reconnectDevice)
        {
            var retryError = await TryReadBatteryLevelAsync(reconnectDevice, status, cancellationToken);
            return retryError;
        }
    }

    private static async Task<BluetoothLEDevice?> CreateBleDeviceWithRetryAsync(
        DeviceInformation info,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var direct = await TryOpenBleDeviceByIdAsync(info.Id, cancellationToken);
        if (direct is not null)
        {
            return direct;
        }

        await Task.Delay(ReconnectDelay, cancellationToken);
        direct = await TryOpenBleDeviceByIdAsync(info.Id, cancellationToken);
        if (direct is not null)
        {
            return direct;
        }

        var fallbackAddress = TryGetNormalizedBluetoothAddress(info)
            ?? TryExtractRemoteBluetoothAddress(info.Id);

        if (string.IsNullOrWhiteSpace(fallbackAddress)
            || !ulong.TryParse(fallbackAddress, System.Globalization.NumberStyles.HexNumber, null, out var addressValue))
        {
            return null;
        }

        return await TryOpenBleDeviceByAddressAsync(addressValue, cancellationToken);
    }

    private static async Task<BluetoothLEDevice?> TryOpenBleDeviceByIdAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            return await WithOperationTimeoutAsync(
                ct => BluetoothLEDevice.FromIdAsync(id).AsTask(ct),
                DeviceOpenTimeout,
                cancellationToken,
                "创建 BLE 设备连接超时");
        }
        catch
        {
            return null;
        }
    }

    private static async Task<BluetoothLEDevice?> TryOpenBleDeviceByAddressAsync(ulong address, CancellationToken cancellationToken)
    {
        try
        {
            return await WithOperationTimeoutAsync(
                ct => BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(ct),
                DeviceOpenTimeout,
                cancellationToken,
                "通过蓝牙地址创建设备连接超时");
        }
        catch
        {
            return null;
        }
    }

    private static async Task<T> WithOperationTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string timeoutMessage)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout);

        try
        {
            return await operation(linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(timeoutMessage);
        }
    }

    /// <summary>
    /// 尝试通过 WMI 查询 PnP 设备获取蓝牙设备电量。
    /// 针对经典蓝牙音频设备，Windows 可能在 Win32_PnPEntity 或相关类中暴露电量信息。
    /// </summary>
    private static int? TryReadBatteryFromWmi(DeviceInformation info, IReadOnlyList<string> candidateAddresses)
    {
        try
        {
            // 使用 WMI 查询所有蓝牙相关设备
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%Bluetooth%' OR DeviceID LIKE '%BLUETOOTH%'");

            foreach (var obj in searcher.Get())
            {
                try
                {
                    var deviceId = obj["DeviceID"]?.ToString() ?? string.Empty;
                    var name = obj["Name"]?.ToString() ?? string.Empty;
                    var pnpDeviceId = obj["PNPDeviceID"]?.ToString() ?? string.Empty;

                    // 尝试匹配设备地址
                    foreach (var address in candidateAddresses)
                    {
                        // 检查设备 ID 或名称是否包含蓝牙地址
                        if (deviceId.Contains(address, StringComparison.OrdinalIgnoreCase) ||
                            pnpDeviceId.Contains(address, StringComparison.OrdinalIgnoreCase) ||
                            name.Contains(info.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            // 尝试从设备属性中获取电量
                            // 某些设备会在 Description、Status 或 ConfigManagerUserConfig 中报告电量
                            var description = obj["Description"]?.ToString() ?? string.Empty;
                            var status = obj["Status"]?.ToString() ?? string.Empty;
                            var caption = obj["Caption"]?.ToString() ?? string.Empty;

                            // 尝试从字符串中解析电量百分比
                            var batteryFromDescription = TryParseBatteryFromText(description);
                            if (batteryFromDescription.HasValue)
                            {
                                return batteryFromDescription;
                            }

                            var batteryFromStatus = TryParseBatteryFromText(status);
                            if (batteryFromStatus.HasValue)
                            {
                                return batteryFromStatus;
                            }

                            var batteryFromCaption = TryParseBatteryFromText(caption);
                            if (batteryFromCaption.HasValue)
                            {
                                return batteryFromCaption;
                            }
                        }
                    }
                }
                catch
                {
                    // 继续处理下一个设备
                    continue;
                }
            }

            // 尝试查询更广泛的蓝牙设备（包括 HID 音频设备）
            using var audioSearcher = new System.Management.ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%" + info.Name.Replace("'", "''") + "%'");

            foreach (var obj in audioSearcher.Get())
            {
                try
                {
                    var deviceId = obj["DeviceID"]?.ToString() ?? string.Empty;
                    var name = obj["Name"]?.ToString() ?? string.Empty;

                    // 检查是否是音频设备或 HID 设备
                    if (deviceId.Contains("HID", StringComparison.OrdinalIgnoreCase) ||
                        deviceId.Contains("AUDIO", StringComparison.OrdinalIgnoreCase) ||
                        deviceId.Contains("BLUETOOTH", StringComparison.OrdinalIgnoreCase))
                    {
                        // 尝试从所有文本属性中解析电量
                        var allText = $"{name} {obj["Description"]} {obj["Status"]} {obj["Caption"]}";
                        var battery = TryParseBatteryFromText(allText);
                        if (battery.HasValue)
                        {
                            return battery;
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"WMI query failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// 从文本中尝试解析电量百分比。
    /// </summary>
    private static int? TryParseBatteryFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // 常见模式："Battery: 80%", "80% Battery", "Battery Level: 80"
        var patterns = new[]
        {
            @"battery\s*[:=]?\s*(\d{1,3})\s*%",
            @"battery\s*level\s*[:=]?\s*(\d{1,3})",
            @"电量\s*[:=]?\s*(\d{1,3})",
            @"(\d{1,3})\s*%\s*battery",
            @"power\s*[:=]?\s*(\d{1,3})\s*%",
            @"charge\s*[:=]?\s*(\d{1,3})",
            @"remaining\s*[:=]?\s*(\d{1,3})",
        };

        foreach (var pattern in patterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var battery))
            {
                if (battery >= 0 && battery <= 100)
                {
                    return battery;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 尝试通过 Windows 蓝牙 API 查找关联的 LE 设备并读取电量。
    /// 许多经典蓝牙音频设备会同时暴露一个 LE 端点用于电量报告。
    /// </summary>
    private static async Task<int?> TryReadBatteryFromAssociatedLeDeviceAsync(DeviceInformation info, IReadOnlyList<string> candidateAddresses)
    {
        try
        {
            // 获取经典蓝牙设备
            var bluetoothDevice = await Windows.Devices.Bluetooth.BluetoothDevice.FromIdAsync(info.Id);
            if (bluetoothDevice is null)
            {
                return null;
            }

            using (bluetoothDevice)
            {
                // 获取设备的地址
                var deviceAddress = bluetoothDevice.BluetoothAddress;
                var addressString = deviceAddress.ToString("X12");

                // 尝试查找与此经典设备关联的 LE 设备
                // 很多耳机会同时广播一个 LE 端点用于电量报告
                var leDeviceSelector = Windows.Devices.Bluetooth.BluetoothLEDevice.GetDeviceSelector();
                var leDevices = await DeviceInformation.FindAllAsync(leDeviceSelector);

                foreach (var leDeviceInfo in leDevices)
                {
                    try
                    {
                        var leDevice = await Windows.Devices.Bluetooth.BluetoothLEDevice.FromIdAsync(leDeviceInfo.Id);
                        if (leDevice is null)
                        {
                            continue;
                        }

                        using (leDevice)
                        {
                            // 检查地址是否匹配
                            var leAddress = leDevice.BluetoothAddress.ToString("X12");
                            if (candidateAddresses.Any(addr =>
                                string.Equals(addr, leAddress, StringComparison.OrdinalIgnoreCase)))
                            {
                                // 找到匹配的 LE 设备，尝试读取电量
                                var batteryResult = await TryReadBatteryFromLeDeviceAsync(leDevice);
                                if (batteryResult.HasValue)
                                {
                                    return batteryResult;
                                }
                            }
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }

                // 如果没找到匹配的，尝试所有已连接的 LE 设备
                foreach (var leDeviceInfo in leDevices.Where(d => d.Name.Contains(info.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        var leDevice = await Windows.Devices.Bluetooth.BluetoothLEDevice.FromIdAsync(leDeviceInfo.Id);
                        if (leDevice is null)
                        {
                            continue;
                        }

                        using (leDevice)
                        {
                            var batteryResult = await TryReadBatteryFromLeDeviceAsync(leDevice);
                            if (batteryResult.HasValue)
                            {
                                return batteryResult;
                            }
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Associated LE device query failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// 从 LE 设备读取电池电量。
    /// </summary>
    private static async Task<int?> TryReadBatteryFromLeDeviceAsync(Windows.Devices.Bluetooth.BluetoothLEDevice leDevice)
    {
        try
        {
            // 获取电池服务 (0x180F)
            var batteryServiceResult = await leDevice.GetGattServicesForUuidAsync(GattServiceUuids.Battery);
            if (batteryServiceResult.Status != Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success)
            {
                return null;
            }

            foreach (var service in batteryServiceResult.Services)
            {
                try
                {
                    using (service)
                    {
                        var characteristicResult = await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel);
                        if (characteristicResult.Status != Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success)
                        {
                            continue;
                        }

                        foreach (var characteristic in characteristicResult.Characteristics)
                        {
                            try
                            {
                                var readResult = await characteristic.ReadValueAsync();
                                if (readResult.Status == Windows.Devices.Bluetooth.GenericAttributeProfile.GattCommunicationStatus.Success)
                                {
                                    var reader = Windows.Storage.Streams.DataReader.FromBuffer(readResult.Value);
                                    var batteryLevel = reader.ReadByte();
                                    if (batteryLevel <= 100)
                                    {
                                        return batteryLevel;
                                    }
                                }
                            }
                            catch
                            {
                                continue;
                            }
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }
        }
        catch
        {
            // 忽略错误
        }

        return null;
    }

    /// <summary>
    /// 尝试通过 Windows 11 蓝牙 API 获取电池信息。
    /// Windows 11 引入了 BluetoothDevice 的电池属性支持。
    /// </summary>
    private static async Task<int?> TryReadBatteryFromWin11BluetoothApiAsync(DeviceInformation info, IReadOnlyList<string> candidateAddresses)
    {
        try
        {
            // 尝试获取经典蓝牙设备
            var bluetoothDevice = await BluetoothDevice.FromIdAsync(info.Id);
            if (bluetoothDevice is null)
            {
                return null;
            }

            using (bluetoothDevice)
            {
                // Windows 11 可能通过 ConnectionStatus 或其他属性暴露电量
                // 尝试获取设备的连接信息，某些设备会在这里包含电量

                // 尝试查找 RFComm 服务中的电池信息
                var rfcommServices = await bluetoothDevice.GetRfcommServicesAsync(BluetoothCacheMode.Cached);
                if (rfcommServices.Services.Count > 0)
                {
                    foreach (var service in rfcommServices.Services)
                    {
                        try
                        {
                            // 检查服务名称或 ID 是否包含电池相关信息
                            var serviceName = service.ServiceId.ToString();
                            if (!string.IsNullOrEmpty(serviceName) &&
                                (serviceName.Contains("battery", StringComparison.OrdinalIgnoreCase) ||
                                 serviceName.Contains("power", StringComparison.OrdinalIgnoreCase)))
                            {
                                // 尝试连接并读取数据
                                // 注意：这需要知道具体的协议
                            }
                        }
                        catch
                        {
                            continue;
                        }
                    }
                }

                // 尝试通过反射获取 Windows 11 内部电量属性
                // Windows 设置可能使用这些未公开的属性
                var batteryFromReflection = TryGetBatteryFromReflection(bluetoothDevice);
                if (batteryFromReflection.HasValue)
                {
                    return batteryFromReflection.Value;
                }

                // 尝试获取设备的属性（Windows 11 可能在内部属性中暴露电量）
                // 通过 DeviceInformation 重新查询以获取最新属性
                var updatedInfo = await DeviceInformation.CreateFromIdAsync(
                    info.Id,
                    new[]
                    {
                        "System.Devices.Bluetooth.BatteryLifePercent",
                        "System.Devices.Bluetooth.BatteryLevel",
                        "System.Devices.BatteryLifePercent",
                        "System.Devices.BatteryLevel"
                    },
                    DeviceInformationKind.AssociationEndpoint);

                if (updatedInfo != null)
                {
                    foreach (var prop in updatedInfo.Properties)
                    {
                        var battery = TryParseToBatteryPercent(prop.Value);
                        if (battery.HasValue)
                        {
                            return battery.Value;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Win11 Bluetooth API query failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// 尝试通过反射获取 BluetoothDevice 的内部电量属性。
    /// Windows 11 设置应用可能使用这些未公开的属性。
    /// </summary>
    private static int? TryGetBatteryFromReflection(BluetoothDevice device)
    {
        try
        {
            // 尝试获取 BatteryLevel 属性（可能在 Windows 11 中存在）
            var batteryLevelProperty = device.GetType().GetProperty("BatteryLevel");
            if (batteryLevelProperty != null)
            {
                var value = batteryLevelProperty.GetValue(device);
                if (value is byte b && b <= 100)
                {
                    Logger.Debug($"Found BatteryLevel via reflection: {b}%");
                    return b;
                }
                if (value is int i && i >= 0 && i <= 100)
                {
                    Logger.Debug($"Found BatteryLevel via reflection: {i}%");
                    return i;
                }
            }

            // 尝试获取 BatteryLifePercent 属性
            var batteryLifeProperty = device.GetType().GetProperty("BatteryLifePercent");
            if (batteryLifeProperty != null)
            {
                var value = batteryLifeProperty.GetValue(device);
                if (value is byte bl && bl <= 100)
                {
                    Logger.Debug($"Found BatteryLifePercent via reflection: {bl}%");
                    return bl;
                }
                if (value is int il && il >= 0 && il <= 100)
                {
                    Logger.Debug($"Found BatteryLifePercent via reflection: {il}%");
                    return il;
                }
            }

            // 尝试查找所有包含 "Battery" 或 "Power" 的属性
            var properties = device.GetType().GetProperties();
            foreach (var prop in properties)
            {
                if (prop.Name.Contains("Battery", StringComparison.OrdinalIgnoreCase) ||
                    prop.Name.Contains("Power", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var value = prop.GetValue(device);
                        Logger.Debug($"Found property {prop.Name}: {value} (Type: {value?.GetType()})");

                        if (TryParseToBatteryPercent(value) is int battery)
                        {
                            return battery;
                        }
                    }
                    catch
                    {
                        // 忽略访问错误
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"Reflection failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// 尝试通过 SetupAPI 获取蓝牙设备属性。
    /// 这是 Windows 设备管理器使用的底层 API。
    /// </summary>
    private static int? TryReadBatteryFromSetupApi(DeviceInformation info, IReadOnlyList<string> candidateAddresses)
    {
        try
        {
            // 蓝牙设备类 GUID: {e0cbf06c-cd8b-4647-bb8a-263b43f0f974}
            var bluetoothClassGuid = new Guid("{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}");

            // 获取设备信息集
            var hDevInfo = SetupApi.SetupDiGetClassDevs(
                ref bluetoothClassGuid,
                IntPtr.Zero,
                IntPtr.Zero,
                SetupApi.DIGCF_PRESENT | SetupApi.DIGCF_PROFILE);

            if (hDevInfo == IntPtr.Zero || hDevInfo == new IntPtr(-1))
            {
                return null;
            }

            try
            {
                var deviceInfoData = new SetupApi.SP_DEVINFO_DATA();
                deviceInfoData.cbSize = Marshal.SizeOf(typeof(SetupApi.SP_DEVINFO_DATA));

                uint index = 0;
                while (SetupApi.SetupDiEnumDeviceInfo(hDevInfo, index, ref deviceInfoData))
                {
                    try
                    {
                        // 尝试获取设备实例 ID
                        var instanceId = GetDeviceInstanceId(hDevInfo, deviceInfoData);

                        // 检查是否匹配目标设备
                        if (!string.IsNullOrEmpty(instanceId))
                        {
                            // 尝试匹配地址
                            foreach (var address in candidateAddresses)
                            {
                                if (instanceId.Contains(address, StringComparison.OrdinalIgnoreCase))
                                {
                                    // 找到了匹配的设备，尝试读取各种属性
                                    var battery = TryReadBatteryFromDeviceProperties(hDevInfo, deviceInfoData);
                                    if (battery.HasValue)
                                    {
                                        return battery;
                                    }
                                }
                            }

                            // 也尝试匹配设备名称
                            var friendlyName = GetDevicePropertyString(hDevInfo, deviceInfoData, SetupApi.SPDRP_FRIENDLYNAME);
                            if (string.IsNullOrEmpty(friendlyName))
                            {
                                friendlyName = GetDevicePropertyString(hDevInfo, deviceInfoData, SetupApi.SPDRP_DEVICEDESC);
                            }

                            if (!string.IsNullOrEmpty(friendlyName) &&
                                friendlyName.Contains(info.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                var battery = TryReadBatteryFromDeviceProperties(hDevInfo, deviceInfoData);
                                if (battery.HasValue)
                                {
                                    return battery;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // 继续下一个设备
                    }

                    index++;
                }
            }
            finally
            {
                SetupApi.SetupDiDestroyDeviceInfoList(hDevInfo);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"SetupAPI query failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// 从 SetupAPI 设备属性中尝试读取电量。
    /// </summary>
    private static int? TryReadBatteryFromDeviceProperties(IntPtr hDevInfo, SetupApi.SP_DEVINFO_DATA deviceInfoData)
    {
        try
        {
            // 尝试读取硬件 ID
            var hardwareId = GetDevicePropertyString(hDevInfo, deviceInfoData, SetupApi.SPDRP_HARDWAREID);
            if (!string.IsNullOrEmpty(hardwareId))
            {
                // 某些设备会在硬件 ID 中包含电量信息
                var battery = TryParseBatteryFromText(hardwareId);
                if (battery.HasValue)
                {
                    return battery;
                }
            }

            // 尝试读取设备描述
            var deviceDesc = GetDevicePropertyString(hDevInfo, deviceInfoData, SetupApi.SPDRP_DEVICEDESC);
            if (!string.IsNullOrEmpty(deviceDesc))
            {
                var battery = TryParseBatteryFromText(deviceDesc);
                if (battery.HasValue)
                {
                    return battery;
                }
            }

            // 尝试读取驱动器键名（可能包含有用信息）
            var driverKey = GetDevicePropertyString(hDevInfo, deviceInfoData, SetupApi.SPDRP_DRIVER);
            if (!string.IsNullOrEmpty(driverKey))
            {
                // 尝试从注册表中读取驱动相关属性
                var batteryFromReg = TryReadBatteryFromRegistry(driverKey);
                if (batteryFromReg.HasValue)
                {
                    return batteryFromReg;
                }
            }
        }
        catch
        {
            // 忽略错误
        }

        return null;
    }

    /// <summary>
    /// 获取 SetupAPI 设备的字符串属性。
    /// </summary>
    private static string? GetDevicePropertyString(IntPtr hDevInfo, SetupApi.SP_DEVINFO_DATA deviceInfoData, uint property)
    {
        try
        {
            var buffer = new byte[512];
            if (SetupApi.SetupDiGetDeviceRegistryProperty(
                hDevInfo,
                ref deviceInfoData,
                property,
                out _,
                buffer,
                (uint)buffer.Length,
                out _))
            {
                // 转换为字符串（Unicode）
                var result = Encoding.Unicode.GetString(buffer);
                return result.TrimEnd('\0');
            }
        }
        catch
        {
            // 忽略错误
        }

        return null;
    }

    /// <summary>
    /// 获取设备实例 ID。
    /// </summary>
    private static string? GetDeviceInstanceId(IntPtr hDevInfo, SetupApi.SP_DEVINFO_DATA deviceInfoData)
    {
        try
        {
            // 首先尝试获取硬件 ID
            var hardwareId = GetDevicePropertyString(hDevInfo, deviceInfoData, SetupApi.SPDRP_HARDWAREID);
            if (!string.IsNullOrEmpty(hardwareId))
            {
                return hardwareId;
            }

            // 然后尝试获取设备实例 ID
            var buffer = new byte[512];
            if (SetupApi.SetupDiGetDeviceInstanceId(
                hDevInfo,
                ref deviceInfoData,
                buffer,
                (uint)buffer.Length / 2, // 字符数
                out _))
            {
                return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
            }
        }
        catch
        {
            // 忽略错误
        }

        return null;
    }

    /// <summary>
    /// 尝试从注册表读取电池信息。
    /// </summary>
    private static int? TryReadBatteryFromRegistry(string driverKey)
    {
        try
        {
            // 尝试从设备驱动注册表项读取电量
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $"SYSTEM\\CurrentControlSet\\Control\\Class\\{driverKey}");

            if (key != null)
            {
                // 尝试读取各种可能的电量值
                var batteryValue = key.GetValue("BatteryLevel") ??
                                  key.GetValue("BatteryPercent") ??
                                  key.GetValue("BatteryLifePercent");

                if (batteryValue != null)
                {
                    if (int.TryParse(batteryValue.ToString(), out var battery))
                    {
                        if (battery >= 0 && battery <= 100)
                        {
                            return battery;
                        }
                    }
                }
            }
        }
        catch
        {
            // 忽略错误
        }

        return null;
    }

    private static int? TryReadBatteryFromBluetoothRegistryCache(
        IReadOnlyList<string> candidateAddresses,
        out string? hitPath)
    {
        hitPath = null;

        if (candidateAddresses.Count == 0)
        {
            return null;
        }

        foreach (var address in candidateAddresses)
        {
            var deviceKeys = new[]
            {
                $"SYSTEM\\CurrentControlSet\\Services\\BTHPORT\\Parameters\\Devices\\{address}",
                $"SYSTEM\\CurrentControlSet\\Services\\BTHPORT\\Parameters\\Keys\\{address}",
                $"SYSTEM\\CurrentControlSet\\Enum\\BTHENUM",
                $"SYSTEM\\CurrentControlSet\\Enum\\Bluetooth"
            };

            foreach (var path in deviceKeys)
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                    if (key is null)
                    {
                        continue;
                    }

                    var directBattery = TryReadBatteryFromRegistryValueBag(key);
                    if (directBattery.HasValue)
                    {
                        hitPath = path;
                        return directBattery;
                    }

                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        if (!subKeyName.Contains(address, StringComparison.OrdinalIgnoreCase) &&
                            !subKeyName.Contains("BTH", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        using var subKey = key.OpenSubKey(subKeyName);
                        if (subKey is null)
                        {
                            continue;
                        }

                        var subBattery = TryReadBatteryFromRegistryValueBag(subKey);
                        if (subBattery.HasValue)
                        {
                            hitPath = path + "\\" + subKeyName;
                            return subBattery;
                        }
                    }
                }
                catch
                {
                    // Ignore and continue probing other registry paths.
                }
            }
        }

        return null;
    }

    private static int? TryReadBatteryFromRegistryValueBag(Microsoft.Win32.RegistryKey key)
    {
        var directNames = new[]
        {
            "BatteryLevel",
            "BatteryPercent",
            "BatteryLifePercent",
            "BatteryPercentage",
            "HeadsetBattery",
            "LeftBatteryLevel",
            "RightBatteryLevel",
            "CaseBatteryLevel"
        };

        foreach (var name in directNames)
        {
            var value = key.GetValue(name);
            if (TryParseToBatteryPercent(value) is int battery)
            {
                return battery;
            }
        }

        foreach (var valueName in key.GetValueNames())
        {
            if (!valueName.Contains("battery", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = key.GetValue(valueName);
            if (TryParseToBatteryPercent(value) is int battery)
            {
                return battery;
            }

            if (value is byte[] bytes)
            {
                if (bytes.Length > 0 && bytes[0] <= 100)
                {
                    return bytes[0];
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 尝试通过 Windows.Devices.Power.Battery API 获取电量。
    /// </summary>
    private static int? TryReadBatteryFromPowerApi()
    {
        try
        {
            // 获取聚合电池报告（通常是系统电池，但某些蓝牙设备也会显示在这里）
            var battery = Windows.Devices.Power.Battery.AggregateBattery;
            if (battery != null)
            {
                var report = battery.GetReport();
                if (report != null)
                {
                    // 检查是否是蓝牙设备的电池
                    // 注意：这通常返回系统电池，但在某些情况下可能包含外设
                    if (report.RemainingCapacityInMilliwattHours.HasValue &&
                        report.FullChargeCapacityInMilliwattHours.HasValue &&
                        report.FullChargeCapacityInMilliwattHours.Value > 0)
                    {
                        var percent = (int)((report.RemainingCapacityInMilliwattHours.Value /
                            (double)report.FullChargeCapacityInMilliwattHours.Value) * 100);
                        return Math.Clamp(percent, 0, 100);
                    }
                }
            }
        }
        catch
        {
            // 忽略错误
        }

        return null;
    }

    /// <summary>
    /// 尝试通过 Windows.Media.Devices 获取蓝牙音频设备电量。
    /// Windows 11 可能通过此 API 暴露蓝牙耳机电量。
    /// </summary>
    private static async Task<(int? battery, string trace)> TryReadBatteryFromMediaDeviceAsync(
        DeviceInformation info,
        IReadOnlyList<string> targetAddresses,
        CancellationToken cancellationToken = default)
    {
        var probeTrace = "media-device: miss";
        var traceNotes = new List<string>();

        try
        {
            var defaultRenderDeviceId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            var defaultCaptureDeviceId = MediaDevice.GetDefaultAudioCaptureId(AudioDeviceRole.Default);

            var audioSelector = MediaDevice.GetAudioRenderSelector();
            var audioDevices = await FindMediaEndpointsSafeAsync(audioSelector, "render", traceNotes, cancellationToken);

            var captureSelector = MediaDevice.GetAudioCaptureSelector();
            var captureDevices = await FindMediaEndpointsSafeAsync(captureSelector, "capture", traceNotes, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            var mergedCandidates = audioDevices
                .Concat(captureDevices)
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();

            var targetContainerId = TryGetGuidProperty(info, "System.Devices.Aep.ContainerId")
                ?? TryGetGuidProperty(info, "System.Devices.ContainerId");
            var targetName = string.IsNullOrWhiteSpace(info.Name)
                ? GetStringProperty(info, "System.ItemNameDisplay")
                : info.Name;
            var targetNameText = targetName ?? string.Empty;

            var matchedCandidates = mergedCandidates
                .Where(candidate => IsLikelySameDevice(info, candidate, targetAddresses, targetName))
                .OrderByDescending(candidate =>
                    candidate.Id.Equals(defaultRenderDeviceId, StringComparison.OrdinalIgnoreCase) ||
                    candidate.Id.Equals(defaultCaptureDeviceId, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(candidate => ScoreAudioEndpointCandidate(candidate, targetNameText, targetAddresses))
                .ThenByDescending(candidate =>
                {
                    var candidateContainer = TryGetGuidProperty(candidate, "System.Devices.Aep.ContainerId")
                        ?? TryGetGuidProperty(candidate, "System.Devices.ContainerId");
                    return targetContainerId.HasValue && candidateContainer == targetContainerId;
                })
                .ToList();

            var fallbackCandidates = matchedCandidates.Count > 0
                ? matchedCandidates
                : mergedCandidates
                    .OrderByDescending(candidate =>
                        candidate.Id.Equals(defaultRenderDeviceId, StringComparison.OrdinalIgnoreCase) ||
                        candidate.Id.Equals(defaultCaptureDeviceId, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(candidate => ScoreAudioEndpointCandidate(candidate, targetNameText, targetAddresses))
                    .ThenByDescending(candidate => IsLikelyAudioDevice(candidate))
                    .ToList();

            if (fallbackCandidates.Count == 0)
            {
                probeTrace = $"media-device: no audio endpoint candidate ({string.Join("; ", traceNotes)})";
                return (null, probeTrace);
            }

            // 控制探测上限，避免在异常设备环境下拉长刷新时间。
            var probeLimit = Math.Min(16, fallbackCandidates.Count);
            var bestHitProperty = string.Empty;
            for (var i = 0; i < probeLimit; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var matched = fallbackCandidates[i];

                var batteryFromProperties = TryReadBatteryFromPropertyBag(matched.Properties, out var hitProperty);
                if (batteryFromProperties.HasValue)
                {
                    bestHitProperty = hitProperty ?? "unknown";
                    probeTrace = $"media-device-candidates: matched={matchedCandidates.Count}, scanned={probeLimit}, hit={matched.Name}|{bestHitProperty}; {string.Join("; ", traceNotes)}";
                    return (batteryFromProperties, probeTrace);
                }

                var (singleProbeBattery, singleProbeProperty, singleProbeNote) = await TryReadBatteryBySinglePropertyProbeAsync(matched.Id, cancellationToken);
                if (singleProbeBattery.HasValue)
                {
                    bestHitProperty = singleProbeProperty ?? "unknown";
                    probeTrace = $"media-device-candidates: matched={matchedCandidates.Count}, scanned={probeLimit}, hit={matched.Name}|{bestHitProperty}; {singleProbeNote}; {string.Join("; ", traceNotes)}";
                    return (singleProbeBattery, probeTrace);
                }
            }

            probeTrace = $"media-device-candidates: matched={matchedCandidates.Count}, scanned={probeLimit}, hit=none; {string.Join("; ", traceNotes)}";
        }
        catch (Exception ex)
        {
            probeTrace = $"media-device-exception: {ex.GetType().Name}: {ex.Message}";
            Logger.Debug($"Media device query failed: {ex.Message}");
        }

        return (null, probeTrace);
    }

    private static int ScoreAudioEndpointCandidate(
        DeviceInformation candidate,
        string targetName,
        IReadOnlyList<string> targetAddresses)
    {
        var score = 0;

        if (EndpointIdContainsAddress(candidate.Id, targetAddresses))
        {
            score += 4;
        }

        var candidateName = string.IsNullOrWhiteSpace(candidate.Name)
            ? GetStringProperty(candidate, "System.ItemNameDisplay") ?? string.Empty
            : candidate.Name;

        if (!string.IsNullOrWhiteSpace(targetName) && !string.IsNullOrWhiteSpace(candidateName))
        {
            if (candidateName.Contains(targetName, StringComparison.OrdinalIgnoreCase) ||
                targetName.Contains(candidateName, StringComparison.OrdinalIgnoreCase))
            {
                score += 4;
            }

            if (ContainsAnyKeyword(targetName, new[] { "huawei", "freebuds" }) &&
                ContainsAnyKeyword(candidateName, new[] { "huawei", "freebuds", "buds", "headset", "hands-free" }))
            {
                score += 3;
            }
        }

        return score;
    }

    private static bool ContainsAnyKeyword(string text, IReadOnlyList<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(text) || keywords.Count == 0)
        {
            return false;
        }

        return keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<(int? battery, string trace)> TryReadBatteryFromHuaweiPrivateProtocolAsync(
        DeviceInformation info,
        BluetoothLEDevice? existingBle,
        CancellationToken cancellationToken)
    {
        var name = string.IsNullOrWhiteSpace(info.Name) ? GetStringProperty(info, "System.ItemNameDisplay") : info.Name;
        if (string.IsNullOrWhiteSpace(name) ||
            (!name.Contains("HUAWEI", StringComparison.OrdinalIgnoreCase) &&
             !name.Contains("FREEBUDS", StringComparison.OrdinalIgnoreCase)))
        {
            return (null, "huawei-private: skipped non-huawei-device");
        }

        var ownsBle = false;
        BluetoothLEDevice? ble = existingBle;

        try
        {
            if (ble is null)
            {
                ble = await CreateBleDeviceWithRetryAsync(info, cancellationToken);
                ownsBle = ble is not null;
            }

            if (ble is null)
            {
                return (null, "huawei-private: ble-create-failed");
            }

            var serviceResult = await WithOperationTimeoutAsync(
                ct => ble.GetGattServicesAsync(BluetoothCacheMode.Cached).AsTask(ct),
                PrivateProtocolProbeTimeout,
                cancellationToken,
                "华为私有协议服务枚举超时");

            if (serviceResult.Status != GattCommunicationStatus.Success || serviceResult.Services.Count == 0)
            {
                return (null, $"huawei-private: service-status={serviceResult.Status}");
            }

            var candidateServices = serviceResult.Services
                .Where(s => HuaweiPrivateServiceUuids.Contains(s.Uuid) || IsLikelyHuaweiPrivateService(s.Uuid))
                .ToList();

            foreach (var service in candidateServices)
            {
                using (service)
                {
                    var chResult = await WithOperationTimeoutAsync(
                        ct => service.GetCharacteristicsAsync(BluetoothCacheMode.Cached).AsTask(ct),
                        GattOperationTimeout,
                        cancellationToken,
                        "华为私有协议特征枚举超时");

                    if (chResult.Status != GattCommunicationStatus.Success || chResult.Characteristics.Count == 0)
                    {
                        continue;
                    }

                    foreach (var ch in chResult.Characteristics)
                    {
                        if (!ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
                        {
                            continue;
                        }

                        var readResult = await WithOperationTimeoutAsync(
                            ct => ch.ReadValueAsync(BluetoothCacheMode.Cached).AsTask(ct),
                            GattOperationTimeout,
                            cancellationToken,
                            "华为私有协议特征读取超时");

                        if (readResult.Status != GattCommunicationStatus.Success || readResult.Value is null)
                        {
                            continue;
                        }

                        using var reader = DataReader.FromBuffer(readResult.Value);
                        var payload = new byte[reader.UnconsumedBufferLength];
                        reader.ReadBytes(payload);
                        if (TryParseHuaweiPrivateBattery(payload, out var battery))
                        {
                            return (battery, $"huawei-private: hit service={service.Uuid}, char={ch.Uuid}");
                        }
                    }
                }
            }

            return (null, $"huawei-private: no-hit services={candidateServices.Count}");
        }
        catch (Exception ex)
        {
            return (null, $"huawei-private: {ex.GetType().Name}");
        }
        finally
        {
            if (ownsBle)
            {
                ble?.Dispose();
            }
        }
    }

    private static bool IsLikelyHuaweiPrivateService(Guid serviceUuid)
    {
        var text = serviceUuid.ToString("N");
        return text.StartsWith("0000fe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseHuaweiPrivateBattery(IReadOnlyList<byte> payload, out int battery)
    {
        battery = -1;
        if (payload.Count == 0)
        {
            return false;
        }

        var candidates = new List<int>();
        for (var i = 0; i < payload.Count; i++)
        {
            var value = payload[i];
            if (value is > 0 and <= 100)
            {
                candidates.Add(value);
            }
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        // 对耳机/耳仓场景取最小值，通常更接近系统托盘显示。
        battery = candidates.Min();
        return true;
    }

    private static async Task<IReadOnlyList<DeviceInformation>> FindMediaEndpointsSafeAsync(
        string selector,
        string channel,
        List<string> traceNotes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var devices = await WithOperationTimeoutAsync(
                ct => DeviceInformation.FindAllAsync(selector).AsTask(ct),
                AudioAssociationProbeTimeout,
                cancellationToken,
                $"媒体端点枚举超时({channel})");
            traceNotes.Add($"media-enum-{channel}: {devices.Count} endpoints");
            return devices;
        }
        catch (Exception ex)
        {
            traceNotes.Add($"media-enum-{channel}-failed: {ex.GetType().Name}");
            return Array.Empty<DeviceInformation>();
        }
    }

    private static int? TryReadBatteryFromUiAutomationTextCache(DeviceInformation source, out string trace)
    {
        trace = "uia-text-probe: miss";

        try
        {
            var targetName = string.IsNullOrWhiteSpace(source.Name)
                ? GetStringProperty(source, "System.ItemNameDisplay")
                : source.Name;

            if (string.IsNullOrWhiteSpace(targetName))
            {
                trace = "uia-text-probe: skipped no-target-name";
                return null;
            }

            // Pass 1: 纯后台扫描，不唤起设置页，避免影响用户。
            var firstPass = TryReadBatteryFromUiAutomationSnapshot(targetName, false, out var firstTrace);
            if (firstPass.HasValue)
            {
                trace = firstTrace;
                return firstPass;
            }

            if (!EnableUiSettingsWake)
            {
                trace = firstTrace + "; wake=disabled";
                return null;
            }

            // Pass 2 (可选): 仅在启用时才尝试静默唤起设置页。
            if (DateTimeOffset.Now - _lastUiSettingsWake < UiSettingsWakeCooldown)
            {
                trace = firstTrace + "; wake=cooldown";
                return null;
            }

            Process? settingsProcess = null;
            try
            {
                settingsProcess = TryStartBluetoothSettingsMinimized();
                if (settingsProcess is null)
                {
                    trace = firstTrace + "; wake=start-failed";
                    return null;
                }

                _lastUiSettingsWake = DateTimeOffset.Now;
                Thread.Sleep(1800);

                var secondPass = TryReadBatteryFromUiAutomationSnapshot(targetName, true, out var secondTrace);
                trace = secondTrace;
                return secondPass;
            }
            finally
            {
                TryStopSettingsProcess(settingsProcess);
            }
        }
        catch (Exception ex)
        {
            trace = $"uia-text-probe-exception: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private static async Task<(int? battery, string trace)> TryReadBatteryFromUiAutomationCacheAsync(
        DeviceInformation source,
        DiscoveryCache discoveryCache,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var targetName = string.IsNullOrWhiteSpace(source.Name)
            ? GetStringProperty(source, "System.ItemNameDisplay")
            : source.Name;

        if (string.IsNullOrWhiteSpace(targetName))
        {
            return (null, "uia-cache: skipped no-target-name");
        }

        IReadOnlyList<UiBatteryEntry> entries;
        try
        {
            entries = await discoveryCache.GetUiBatteryEntriesAsync();
        }
        catch (Exception ex)
        {
            return (null, $"uia-cache-exception: {ex.GetType().Name}: {ex.Message}");
        }

        if (entries.Count == 0)
        {
            return (null, "uia-cache: no-entry");
        }

        var bestScore = int.MinValue;
        UiBatteryEntry? best = null;
        foreach (var entry in entries)
        {
            var score = ComputeUiNameMatchScore(targetName, entry.DeviceName);
            if (score > bestScore)
            {
                bestScore = score;
                best = entry;
            }
        }

        if (best is null || bestScore < 1)
        {
            // 音频设备常见 UI 文本名与设备名不完全一致：
            // 1) 只有一个候选时直接采用；
            // 2) 否则使用规范化名称近似匹配回退。
            if (IsLikelyAudioDevice(source) && entries.Count == 1)
            {
                var single = entries[0];
                return (single.BatteryPercent, $"uia-cache: candidates=1, fallback=single-entry, hit={single.DeviceName}:{single.BatteryPercent}%, text={single.RawText}");
            }

            if (IsLikelyAudioDevice(source))
            {
                foreach (var entry in entries)
                {
                    if (IsLooseUiNameMatch(targetName, entry.DeviceName))
                    {
                        return (entry.BatteryPercent, $"uia-cache: candidates={entries.Count}, fallback=loose-name, hit={entry.DeviceName}:{entry.BatteryPercent}%, text={entry.RawText}");
                    }
                }
            }

            return (null, $"uia-cache: candidates={entries.Count}, best-score={bestScore}, miss");
        }

        return (best.BatteryPercent, $"uia-cache: candidates={entries.Count}, hit={best.DeviceName}:{best.BatteryPercent}%, score={bestScore}, text={best.RawText}");
    }

    private static IReadOnlyList<UiBatteryEntry> CollectUiBatteryEntriesSnapshot()
    {
        var list = new List<UiBatteryEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var debugTexts = new List<string>(); // 用于调试

        // 仅在显式启用时尝试打开蓝牙设置页面，默认保持静默扫描。
        Process? settingsProcess = null;
        bool openedByUs = false;
        try
        {
            if (EnableUiSettingsWake)
            {
                // 强制尝试唤起蓝牙设置页：SystemSettings 进程常驻时仅检测进程会误判为“已打开”。
                settingsProcess = TryStartBluetoothSettingsMinimized();
                if (settingsProcess != null)
                {
                    openedByUs = true;
                    Thread.Sleep(2500); // 等待页面加载和电量刷新
                }
                else
                {
                    Thread.Sleep(600);
                }
            }
            else
            {
                Thread.Sleep(250);
            }
        }
        catch { }

        try
        {
            var root = AutomationElement.RootElement;
            if (root is null)
            {
                return list;
            }

            var elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            var scanLimit = Math.Min(elements.Count, 6000);
            for (var i = 0; i < scanLimit; i++)
            {
                string text;
                try
                {
                    text = elements[i]?.Current.Name?.Trim() ?? string.Empty;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                // 收集包含"电池"的文本用于调试
                if (text.Contains("电池", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("battery", StringComparison.OrdinalIgnoreCase))
                {
                    debugTexts.Add(text);
                }

                if (!ContainsBatteryAndConnectedHint(text))
                {
                    continue;
                }

                if (!TryParseConnectedDeviceBatteryEntry(text, out var deviceName, out var battery))
                {
                    continue;
                }

                var key = deviceName + "|" + battery;
                if (!seen.Add(key))
                {
                    continue;
                }

                list.Add(new UiBatteryEntry
                {
                    DeviceName = deviceName,
                    BatteryPercent = battery,
                    RawText = text
                });
            }
        }
        finally
        {
            // 如果是我们打开的，尝试关闭它
            if (openedByUs && settingsProcess != null)
            {
                TryStopSettingsProcess(settingsProcess);
            }
        }

        // 将调试信息写入文件
        try
        {
            var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "PeriViewExports");
            System.IO.Directory.CreateDirectory(folder);
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var debugFile = System.IO.Path.Combine(folder, $"uia-debug-{timestamp}.txt");
            var content = $"UIA Scan Results:\nFound {list.Count} valid entries\nFound {debugTexts.Count} texts with '电池'\nSettings opened by us: {openedByUs}\n\n";
            if (debugTexts.Count > 0)
            {
                content += "All battery-related texts:\n" + string.Join("\n", debugTexts);
            }
            System.IO.File.WriteAllText(debugFile, content);
        }
        catch { }

        return list;
    }

    private static DateTimeOffset _lastUiSettingsWake = DateTimeOffset.MinValue;

    private static int? TryReadBatteryFromUiAutomationSnapshot(string targetName, bool wakeTriggered, out string trace)
    {
        var root = AutomationElement.RootElement;
        if (root is null)
        {
            trace = "uia-text-probe: skipped no-root";
            return null;
        }

        var elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        var scanned = 0;
        var matchedName = 0;
        var scanLimit = Math.Min(elements.Count, 6000);
        var bestEntryScore = int.MinValue;
        var bestEntryName = string.Empty;
        var bestEntryBattery = -1;
        string? bestEntryText = null;

        for (var i = 0; i < scanLimit; i++)
        {
            scanned++;

            string text;
            try
            {
                text = elements[i]?.Current.Name?.Trim() ?? string.Empty;
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!ContainsBatteryAndConnectedHint(text))
            {
                continue;
            }

            if (text.Contains(targetName, StringComparison.OrdinalIgnoreCase) ||
                NameHintMatchesBluetoothUi(targetName, text))
            {
                matchedName++;
            }

            if (TryParseConnectedDeviceBatteryEntry(text, out var uiName, out var uiBattery))
            {
                var score = ComputeUiNameMatchScore(targetName, uiName);
                if (score > bestEntryScore)
                {
                    bestEntryScore = score;
                    bestEntryName = uiName;
                    bestEntryBattery = uiBattery;
                    bestEntryText = text;
                }
                continue;
            }

            var battery = TryParseBatteryFromBluetoothUiText(text, targetName);
            if (battery.HasValue)
            {
                trace = $"uia-text-probe: scanned={scanned}, matched={matchedName}, wake={wakeTriggered}, hit-text={text}";
                return battery;
            }
        }

        if (bestEntryScore >= 1 && bestEntryBattery is >= 0 and <= 100)
        {
            trace = $"uia-text-probe: scanned={scanned}, matched={matchedName}, wake={wakeTriggered}, hit-best-entry={bestEntryName}:{bestEntryBattery}%, score={bestEntryScore}, text={bestEntryText}";
            return bestEntryBattery;
        }

        trace = $"uia-text-probe: scanned={scanned}, matched={matchedName}, wake={wakeTriggered}, hit=none";
        return null;
    }

    private static bool TryParseConnectedDeviceBatteryEntry(string text, out string deviceName, out int batteryPercent)
    {
        deviceName = string.Empty;
        batteryPercent = -1;

        // 先尝试严格匹配
        var match = UiConnectedBatteryPatternCn.Match(text);
        if (match.Success)
        {
            deviceName = match.Groups["name"].Value.Trim();
            if (int.TryParse(match.Groups["battery"].Value, out batteryPercent) && batteryPercent is >= 0 and <= 100)
            {
                return true;
            }
        }

        // 兼容英文和混合语言界面，例如 "WH-1000XM5, Audio, Battery 78%, Connected"
        var genericMatch = UiConnectedBatteryPatternGeneric.Match(text);
        if (genericMatch.Success)
        {
            deviceName = genericMatch.Groups["name"].Value.Trim();
            if (IsLikelyUiDeviceName(deviceName) && int.TryParse(genericMatch.Groups["battery"].Value, out batteryPercent) && batteryPercent is >= 0 and <= 100)
            {
                return true;
            }
        }

        // 兼容没有逗号分隔的文本，例如 "HUAWEI FreeBuds Pro 3 电池 82%"
        var noSeparatorMatch = UiConnectedBatteryPatternNoSeparator.Match(text);
        if (noSeparatorMatch.Success)
        {
            deviceName = noSeparatorMatch.Groups["name"].Value.Trim();
            if (IsLikelyUiDeviceName(deviceName) && int.TryParse(noSeparatorMatch.Groups["battery"].Value, out batteryPercent) && batteryPercent is >= 0 and <= 100)
            {
                return true;
            }
        }

        // 尝试超宽松匹配
        var looseMatch = UiBatteryUltraLoose.Match(text);
        if (looseMatch.Success)
        {
            deviceName = looseMatch.Groups["name"].Value.Trim();
            if (IsLikelyUiDeviceName(deviceName) && int.TryParse(looseMatch.Groups["battery"].Value, out batteryPercent) && batteryPercent is >= 0 and <= 100)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLikelyUiDeviceName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var text = name.Trim();
        if (text.Length < 2 || text.Length > 80)
        {
            return false;
        }

        // 过滤明显不是设备名的UI文本（路径/代码片段/命令输出）。
        if (text.Contains('\\') || text.Contains(".cs", StringComparison.OrdinalIgnoreCase) || text.Contains("namespace", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static Process? TryStartBluetoothSettingsMinimized()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ms-settings:bluetooth",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized
            };
            return Process.Start(psi);
        }
        catch
        {
            return null;
        }
    }

    private static void TryStopSettingsProcess(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (process.HasExited)
            {
                return;
            }

            // 只关闭当前刚拉起的实例，避免影响用户已打开的设置页。
            if (DateTime.Now - process.StartTime > TimeSpan.FromSeconds(20))
            {
                return;
            }

            process.CloseMainWindow();
            Thread.Sleep(120);
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch
        {
            // Ignore cleanup errors.
        }
    }

    private static int? TryParseBatteryFromBluetoothUiText(string text, string targetName)
    {
        var likelyTargetLine = text.Contains(targetName, StringComparison.OrdinalIgnoreCase);

        var exactMatch = UiConnectedBatteryPatternCn.Match(text);
        if (exactMatch.Success)
        {
            var name = exactMatch.Groups["name"].Value.Trim();
            if (name.Equals(targetName, StringComparison.OrdinalIgnoreCase) ||
                targetName.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(targetName, StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(exactMatch.Groups["battery"].Value, out var exactBattery) && exactBattery is >= 0 and <= 100)
                {
                    return exactBattery;
                }
            }
        }

        // Loose patterns are only valid when this line likely belongs to the target device.
        if (!likelyTargetLine)
        {
            return null;
        }

        var matchCn = UiBatteryPatternCnLoose.Match(text);
        if (matchCn.Success && int.TryParse(matchCn.Groups["battery"].Value, out var batteryCn) && batteryCn is >= 0 and <= 100)
        {
            return batteryCn;
        }

        var matchEn = UiBatteryPatternEnLoose.Match(text);
        if (matchEn.Success && int.TryParse(matchEn.Groups["battery"].Value, out var batteryEn) && batteryEn is >= 0 and <= 100)
        {
            return batteryEn;
        }

        return null;
    }

    private static int ComputeUiNameMatchScore(string targetName, string candidateName)
    {
        if (string.IsNullOrWhiteSpace(targetName) || string.IsNullOrWhiteSpace(candidateName))
        {
            return 0;
        }

        var t = targetName.Trim();
        var c = candidateName.Trim();
        if (t.Equals(c, StringComparison.OrdinalIgnoreCase))
        {
            return 10;
        }

        var score = 0;
        if (c.Contains(t, StringComparison.OrdinalIgnoreCase) || t.Contains(c, StringComparison.OrdinalIgnoreCase))
        {
            score += 6;
        }

        var tokens = t.Split(new[] { ' ', '-', '_', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var token in tokens)
        {
            if (c.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                score += 1;
            }
        }

        return score;
    }

    private static bool IsLooseUiNameMatch(string targetName, string candidateName)
    {
        if (string.IsNullOrWhiteSpace(targetName) || string.IsNullOrWhiteSpace(candidateName))
        {
            return false;
        }

        static string Normalize(string text)
        {
            var chars = text
                .Where(c => char.IsLetterOrDigit(c))
                .Select(char.ToLowerInvariant)
                .ToArray();
            return new string(chars);
        }

        var t = Normalize(targetName);
        var c = Normalize(candidateName);
        if (string.IsNullOrWhiteSpace(t) || string.IsNullOrWhiteSpace(c))
        {
            return false;
        }

        if (t.Equals(c, StringComparison.Ordinal) || t.Contains(c, StringComparison.Ordinal) || c.Contains(t, StringComparison.Ordinal))
        {
            return true;
        }

        var tokens = targetName
            .Split(new[] { ' ', '-', '_', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 3)
            .Select(Normalize)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (tokens.Length == 0)
        {
            return false;
        }

        var hits = tokens.Count(token => c.Contains(token, StringComparison.Ordinal));
        return hits >= Math.Max(1, tokens.Length / 2);
    }

    private static void SetLastKnownBattery(DeviceInformation info, int batteryPercent)
    {
        if (batteryPercent is < 0 or > 100)
        {
            return;
        }

        var key = BuildLogicalDeviceKey(info);
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        LastKnownBatteryByDeviceKey[key] = (batteryPercent, DateTimeOffset.Now);
    }

    private static bool TryGetLastKnownBattery(DeviceInformation info, out int batteryPercent)
    {
        batteryPercent = -1;
        var key = BuildLogicalDeviceKey(info);
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (!LastKnownBatteryByDeviceKey.TryGetValue(key, out var cache))
        {
            return false;
        }

        if (DateTimeOffset.Now - cache.Time > LastKnownBatteryTtl)
        {
            LastKnownBatteryByDeviceKey.TryRemove(key, out _);
            return false;
        }

        batteryPercent = cache.Battery;
        return true;
    }

    private static bool ContainsBatteryAndConnectedHint(string text)
    {
        var hasBattery = text.Contains("电池", StringComparison.OrdinalIgnoreCase) ||
                         text.Contains("battery", StringComparison.OrdinalIgnoreCase);
        // 只要有"电池"关键字和百分号即可，不要求"已连接"
        var hasPercent = text.Contains('%') || text.Contains('％');
        return hasBattery && hasPercent;
    }

    private static bool NameHintMatchesBluetoothUi(string targetName, string text)
    {
        if (string.IsNullOrWhiteSpace(targetName) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var hints = targetName
            .Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length >= 3)
            .ToArray();

        return hints.Any(hint => text.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<(int? battery, string trace)> TryReadBatteryFromAudioAssociationEndpointsAsync(
        DeviceInformation source,
        IReadOnlyList<string> targetAddresses,
        CancellationToken cancellationToken)
    {
        try
        {
            using var localCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            localCts.CancelAfter(AudioAssociationProbeTimeout);
            var probeToken = localCts.Token;

            probeToken.ThrowIfCancellationRequested();

            var endpoints = await WithOperationTimeoutAsync(
                ct => DeviceInformation.FindAllAsync(
                        string.Empty,
                        Array.Empty<string>(),
                        DeviceInformationKind.AssociationEndpoint)
                    .AsTask(ct),
                AudioAssociationProbeTimeout,
                probeToken,
                "音频 AssociationEndpoint 枚举超时");

            var targetName = string.IsNullOrWhiteSpace(source.Name)
                ? GetStringProperty(source, "System.ItemNameDisplay")
                : source.Name;

            var candidates = endpoints
                .Where(x => IsLikelySameDevice(source, x, targetAddresses, targetName) || EndpointIdContainsAddress(x.Id, targetAddresses))
                .ToList();

            foreach (var candidate in candidates)
            {
                probeToken.ThrowIfCancellationRequested();
                var battery = TryReadBatteryFromPropertyBag(candidate.Properties, out var hitProperty);
                if (battery.HasValue)
                {
                    return (battery.Value, $"audio-aep-candidates: {candidates.Count}, hit={candidate.Name}|{hitProperty}");
                }

                var (probeBattery, probeProperty, probeNote) = await TryReadBatteryBySinglePropertyProbeAsync(candidate.Id, probeToken);
                if (probeBattery.HasValue)
                {
                    return (probeBattery.Value, $"audio-aep-candidates: {candidates.Count}, hit={candidate.Name}|{probeProperty}; {probeNote}");
                }
            }

            return (null, $"audio-aep-candidates: {candidates.Count}, hit=none");
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested
                ? (null, "audio-aep-canceled: upstream-timeout-or-refresh-canceled")
                : (null, $"audio-aep-canceled: soft-timeout>{AudioAssociationProbeTimeout.TotalMilliseconds:0}ms");
        }
        catch (Exception ex)
        {
            return (null, $"audio-aep-exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<(int? battery, string? hitProperty, string note)> TryReadBatteryBySinglePropertyProbeAsync(
        string endpointId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return (null, null, "single-probe-skip empty-endpoint-id");
        }

        var kinds = new[]
        {
            DeviceInformationKind.AssociationEndpoint,
            DeviceInformationKind.Device,
            DeviceInformationKind.AssociationEndpointService
        };

        var failures = 0;
        var attempts = 0;
        const int maxAttempts = 36;

        foreach (var kind in kinds)
        {
            foreach (var propertyName in MediaEndpointBatteryPropertyCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                attempts++;
                if (attempts > maxAttempts)
                {
                    return (null, null, $"single-probe-miss attempts={attempts - 1} fail={failures} capped={maxAttempts}");
                }

                try
                {
                    var endpoint = await DeviceInformation.CreateFromIdAsync(endpointId, new[] { propertyName }, kind);
                    if (endpoint is null)
                    {
                        continue;
                    }

                    if (!endpoint.Properties.TryGetValue(propertyName, out var value) || value is null)
                    {
                        continue;
                    }

                    var battery = TryParseToBatteryPercent(value);
                    if (battery.HasValue)
                    {
                        return (battery.Value, propertyName, $"single-probe-hit-kind={kind}");
                    }
                }
                catch
                {
                    failures++;
                }
            }
        }

        return (null, null, $"single-probe-miss attempts={attempts} fail={failures}");
    }

    private static bool EndpointIdContainsAddress(string? endpointId, IReadOnlyList<string> targetAddresses)
    {
        if (string.IsNullOrWhiteSpace(endpointId) || targetAddresses.Count == 0)
        {
            return false;
        }

        return targetAddresses.Any(address => endpointId.Contains(address, StringComparison.OrdinalIgnoreCase));
    }

    private static int? TryReadBatteryFromPropertyBag(IReadOnlyDictionary<string, object> properties, out string? hitProperty)
    {
        foreach (var key in BatteryPropertyNames)
        {
            if (!properties.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            var parsed = TryParseToBatteryPercent(value);
            if (parsed.HasValue)
            {
                hitProperty = key;
                return parsed.Value;
            }
        }

        foreach (var pair in properties)
        {
            if (pair.Value is null ||
                (!pair.Key.Contains("battery", StringComparison.OrdinalIgnoreCase) &&
                 !pair.Key.Contains("charge", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var parsed = TryParseToBatteryPercent(pair.Value);
            if (parsed.HasValue)
            {
                hitProperty = pair.Key;
                return parsed.Value;
            }
        }

        hitProperty = null;
        return null;
    }

    /// <summary>
    /// 尝试将属性值转换为电池百分比。
    /// </summary>
    private static int? TryParseToBatteryPercent(object? value)
    {
        if (value is null)
        {
            return null;
        }

        // 尝试直接转换
        if (value is byte b && b <= 100)
        {
            return b;
        }

        if (value is int i && i >= 0 && i <= 100)
        {
            return i;
        }

        if (value is long l && l >= 0 && l <= 100)
        {
            return (int)l;
        }

        if (value is double d && d >= 0 && d <= 100)
        {
            return (int)d;
        }

        // 尝试从字符串解析
        if (value is string s)
        {
            if (int.TryParse(s, out var parsed) && parsed >= 0 && parsed <= 100)
            {
                return parsed;
            }

            var parsedFromText = TryParseBatteryFromText(s);
            if (parsedFromText.HasValue)
            {
                return parsedFromText;
            }

            var percentMatches = Regex.Matches(s, @"(\d{1,3})\s*%", RegexOptions.IgnoreCase);
            if (percentMatches.Count > 0)
            {
                var values = new List<int>();
                foreach (Match match in percentMatches)
                {
                    if (int.TryParse(match.Groups[1].Value, out var valueFromText) && valueFromText is >= 0 and <= 100)
                    {
                        values.Add(valueFromText);
                    }
                }

                if (values.Count > 0)
                {
                    return (int)Math.Round(values.Average());
                }
            }
        }

        if (value is byte[] byteArray && byteArray.Length > 0)
        {
            var candidates = byteArray.Where(x => x <= 100).Select(x => (int)x).ToList();
            if (candidates.Count > 0)
            {
                return (int)Math.Round(candidates.Average());
            }
        }

        if (value is IEnumerable<object> list)
        {
            var parsed = list
                .Select(TryParseToBatteryPercent)
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .Where(x => x is >= 0 and <= 100)
                .ToList();

            if (parsed.Count > 0)
            {
                return (int)Math.Round(parsed.Average());
            }
        }

        return null;
    }

    /// <summary>
    /// SetupAPI P/Invoke 声明。
    /// </summary>
    private static class SetupApi
    {
        public const uint SPDRP_DEVICEDESC = 0x00000000;
        public const uint SPDRP_HARDWAREID = 0x00000001;
        public const uint SPDRP_COMPATIBLEIDS = 0x00000002;
        public const uint SPDRP_SERVICE = 0x00000004;
        public const uint SPDRP_DRIVER = 0x00000009;
        public const uint SPDRP_FRIENDLYNAME = 0x0000000C;
        public const uint SPDRP_LOCATION_INFORMATION = 0x0000000D;
        public const uint SPDRP_PHYSICAL_DEVICE_OBJECT_NAME = 0x0000000E;
        public const uint SPDRP_BUS_TYPE_GUID = 0x00000013;
        public const uint SPDRP_LEGACY_BUS_TYPE = 0x00000014;
        public const uint SPDRP_BUS_NUMBER = 0x00000015;
        public const uint SPDRP_ENUMERATOR_NAME = 0x00000016;
        public const uint SPDRP_SECURITY = 0x00000017;

        public const uint DIGCF_DEFAULT = 0x00000001;
        public const uint DIGCF_PRESENT = 0x00000002;
        public const uint DIGCF_ALLCLASSES = 0x00000004;
        public const uint DIGCF_PROFILE = 0x00000008;
        public const uint DIGCF_DEVICEINTERFACE = 0x00000010;

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid,
            IntPtr enumerator,
            IntPtr hwndParent,
            uint flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInfo(
            IntPtr deviceInfoSet,
            uint memberIndex,
            ref SP_DEVINFO_DATA deviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceRegistryProperty(
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData,
            uint property,
            out uint propertyRegDataType,
            byte[] propertyBuffer,
            uint propertyBufferSize,
            out uint requiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInstanceId(
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData,
            byte[] deviceInstanceId,
            uint deviceInstanceIdSize,
            out uint requiredSize);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid classGuid;
            public int devInst;
            public IntPtr reserved;
        }
    }

}
