using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using System.Linq;

namespace PeriView.App.Services;

/// <summary>
/// 蓝牙设备发现缓存，用于缓存设备信息和电池数据
/// </summary>
public sealed class BluetoothDiscoveryCache
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

    public BluetoothDiscoveryCache()
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
            return await DeviceInformation.FindAllAsync(string.Empty, BluetoothConstants.DeviceKindRequestedProperties, DeviceInformationKind.Device);
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
                BluetoothConstants.BleEndpointRequestedProperties,
                DeviceInformationKind.AssociationEndpoint);
        }
        catch
        {
            try
            {
                return await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelector(), BluetoothConstants.BleEndpointRequestedProperties);
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
            return await DeviceInformation.FindAllAsync(string.Empty, BluetoothConstants.DeviceInterfaceRequestedProperties, DeviceInformationKind.DeviceInterface);
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
                BluetoothConstants.ClassicEndpointRequestedProperties,
                DeviceInformationKind.AssociationEndpoint);
        }
        catch
        {
            try
            {
                return await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelector(), BluetoothConstants.ClassicEndpointRequestedProperties);
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
                BluetoothConstants.AssociationEndpointServiceRequestedProperties,
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
                BluetoothConstants.GlobalAssociationEndpointRequestedProperties,
                DeviceInformationKind.AssociationEndpoint);
        }
        catch
        {
            return Array.Empty<DeviceInformation>();
        }
    }

    private static Task<IReadOnlyList<UiBatteryEntry>> LoadUiBatteryEntriesAsync()
    {
        // UI 自动化电池条目收集尚未实现，直接返回空列表。
        // TODO: 从 BluetoothBatteryProvider 中提取 UI 自动化文本解析逻辑至此。
        return Task.FromResult<IReadOnlyList<UiBatteryEntry>>(Array.Empty<UiBatteryEntry>());
    }
}

/// <summary>
/// UI电池条目（从Windows设置界面提取）
/// </summary>
public sealed class UiBatteryEntry
{
    public string DeviceName { get; init; } = string.Empty;
    public int BatteryPercent { get; init; }
    public string RawText { get; init; } = string.Empty;
}