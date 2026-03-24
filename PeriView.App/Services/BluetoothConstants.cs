using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace PeriView.App.Services;

/// <summary>
/// 蓝牙电池提供器的常量定义
/// </summary>
public static class BluetoothConstants
{
    // 超时配置
    public static readonly TimeSpan EnumerationTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DeviceOpenTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan GattOperationTimeout = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan AudioAssociationProbeTimeout = TimeSpan.FromMilliseconds(4000);
    public static readonly TimeSpan BatteryResolutionBudget = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan AudioCapabilityProbeBudget = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(350);
    public static readonly TimeSpan UiSettingsWakeCooldown = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan LastKnownBatteryTtl = TimeSpan.FromMinutes(30);
    public static readonly bool EnableUiSettingsWake = false;
    public const int MaxConcurrentDeviceQueries = 3;

    // 缓存
    public static readonly ConcurrentDictionary<string, (int Battery, DateTimeOffset Time)> LastKnownBatteryByDeviceKey = new(StringComparer.OrdinalIgnoreCase);

    // GUID
    public static readonly Guid BluetoothAepProtocolId = Guid.Parse("{bb7bb05e-5972-42b5-94fc-76eaa7084d49}");

    // 正则表达式
    public static readonly Regex BluetoothAddressRegex = new(@"([0-9A-Fa-f]{2}[:\-]){5}[0-9A-Fa-f]{2}", RegexOptions.Compiled);
    public static readonly Regex UiConnectedBatteryPatternCn = new(@"^(?<name>[^、,，]+?)\s*[、,，]\s*类别[^、,，]*\s*[、,，]\s*电池\s*(?<battery>\d{1,3})%\s*[、,，].*$", RegexOptions.Compiled);
    public static readonly Regex UiBatteryPatternCnLoose = new(@"电池\s*(?<battery>\d{1,3})%", RegexOptions.Compiled);
    public static readonly Regex UiBatteryPatternEnLoose = new(@"battery\s*(?<battery>\d{1,3})%", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    public static readonly Regex UiBatteryUltraLoose = new(@"(?<name>.+?)[、,，].*?电池\s*(?<battery>\d{1,3})%", RegexOptions.Compiled);

    // 属性名称数组
    public static readonly string[] ConnectionPropertyNames =
    {
        "System.Devices.Aep.IsConnected",
        "System.Devices.Connected"
    };

    public static readonly string[] AddressPropertyNames =
    {
        "System.Devices.Aep.Bluetooth.Address",
        "System.Devices.Aep.Bluetooth.Le.Address",
        "System.Devices.Aep.DeviceAddress",
        "System.Devices.Aep.Address"
    };

    public static readonly string[] BatteryPropertyNames =
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

    // 请求的属性数组（组合）
    public static readonly string[] DeviceKindRequestedProperties = BatteryPropertyNames
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

    public static readonly string[] BleEndpointRequestedProperties = BatteryPropertyNames
        .Concat(AddressPropertyNames)
        .Concat(new[]
        {
            "System.Devices.Aep.ContainerId",
            "System.Devices.ContainerId"
        })
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static readonly string[] ClassicEndpointRequestedProperties = BatteryPropertyNames
        .Concat(AddressPropertyNames)
        .Concat(new[]
        {
            "System.Devices.Aep.ContainerId",
            "System.Devices.ContainerId",
            "System.ItemNameDisplay"
        })
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static readonly string[] DeviceInterfaceRequestedProperties = BatteryPropertyNames
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

    public static readonly string[] AssociationEndpointServiceRequestedProperties = BatteryPropertyNames
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

    // 媒体端点电池属性候选（逐个探测以避免API整体失败）
    public static readonly string[] MediaEndpointBatteryPropertyCandidates =
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

    public static readonly string[] GlobalAssociationEndpointRequestedProperties = BatteryPropertyNames
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
}