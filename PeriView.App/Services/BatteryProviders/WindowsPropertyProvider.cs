using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using PeriView.App.Services;

namespace PeriView.App.Services.BatteryProviders;

/// <summary>
/// Windows设备属性电池提供者（通用回退提供者）
/// 参考蓝牙项目中的 Windows 属性读取逻辑
/// </summary>
public sealed class WindowsPropertyProvider : IBatteryProvider, IDisposable
{
    private readonly ConcurrentDictionary<string, DeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);
    private IBatteryProviderContext? _context;
    private bool _disposed;

    /// <summary>
    /// 提供者名称
    /// </summary>
    public string Name => "Windows设备属性提供者";

    /// <summary>
    /// 检查是否支持特定设备
    /// </summary>
    public bool CanHandle(ushort vendorId, ushort productId)
    {
        // Windows属性提供者支持所有设备（作为回退）
        return true;
    }

    /// <summary>
    /// 读取所有设备电池信息
    /// </summary>
    public async Task ReadBatteryAsync(IBatteryProviderContext context, CancellationToken cancellationToken)
    {
        if (_disposed) return;
        
        _context = context;
        
        try
        {
            // 查询所有蓝牙设备
            var deviceSelector = "System.Devices.Aep.ProtocolId:=\"{E0CBF06C-CD8D-4647-BB8A-263E43C208F5}\"";
            var devices = await DeviceInformation.FindAllAsync(deviceSelector);
            
            if (devices.Count == 0)
            {
                Logger.Debug("未找到蓝牙设备");
                return;
            }
            
            Logger.Debug($"找到 {devices.Count} 个蓝牙设备");
            
            foreach (var device in devices)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                    
                try
                {
                    await ReadDeviceBattery(device, context, cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger.Error($"读取设备电池信息失败: {device.Name}", ex);
                }
            }
            
            // 清理已移除的设备
            var currentDeviceKeys = devices.Select(d => GetDeviceKey(d)).ToHashSet();
            CleanupRemovedDevices(currentDeviceKeys);
        }
        catch (Exception ex)
        {
            Logger.Error("读取Windows设备属性电池信息失败", ex);
        }
    }

    /// <summary>
    /// 读取特定设备电池信息
    /// </summary>
    public async Task ReadBatteryAsync(IBatteryProviderContext context, string deviceId, CancellationToken cancellationToken)
    {
        if (_disposed) return;
        
        _context = context;
        
        try
        {
            // 查找指定设备
            var deviceSelector = "System.Devices.Aep.ProtocolId:=\"{E0CBF06C-CD8D-4647-BB8A-263E43C208F5}\"";
            var devices = await DeviceInformation.FindAllAsync(deviceSelector);
            var device = devices.FirstOrDefault(d => GetDeviceKey(d) == deviceId);
            
            if (device == null)
            {
                Logger.Debug($"未找到设备ID为 {deviceId} 的蓝牙设备");
                return;
            }
            
            await ReadDeviceBattery(device, context, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.Error($"读取Windows设备属性电池信息失败: {deviceId}", ex);
        }
    }

    /// <summary>
    /// 读取设备电池信息
    /// </summary>
    private async Task ReadDeviceBattery(DeviceInformation device, IBatteryProviderContext context, CancellationToken cancellationToken)
    {
        var deviceKey = GetDeviceKey(device);
        var displayName = GetDeviceDisplayName(device);
        
        Logger.Debug($"尝试读取Windows设备属性电池: {displayName} ({deviceKey})");
        
        try
        {
            // 从设备属性中读取电池百分比
            int? batteryPercent = TryGetBatteryPercentFromProperties(device.Properties);
            bool isCharging = false;
            bool isConnected = TryGetConnectionStatus(device.Properties);
            
            if (batteryPercent.HasValue)
            {
                // 更新设备信息
                _devices.AddOrUpdate(deviceKey, 
                    key => new DeviceInfo 
                    { 
                        DeviceKey = key, 
                        DisplayName = displayName,
                        DeviceId = device.Id
                    }, 
                    (key, existing) => existing);
                
                // 报告电池状态
                context.ReportBattery(displayName, deviceKey, batteryPercent.Value, isCharging, 
                    DeviceSource.WindowsProperties, false, isConnected ? "Connected" : "Disconnected");
                    
                Logger.Debug($"Windows设备属性电池读取成功: {displayName}, 电量: {batteryPercent.Value}%, 连接状态: {isConnected}");
            }
            else
            {
                Logger.Debug($"无法从Windows属性读取电池信息: {displayName}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"读取Windows设备属性电池信息异常: {displayName}", ex);
        }
        
        await Task.CompletedTask;
    }

    /// <summary>
    /// 从设备属性中尝试读取电池百分比
    /// </summary>
    private int? TryGetBatteryPercentFromProperties(IReadOnlyDictionary<string, object> properties)
    {
        // 尝试标准电池属性名称
        foreach (var name in BluetoothConstants.BatteryPropertyNames)
        {
            if (!properties.TryGetValue(name, out var value) || value is null)
                continue;

            var parsed = ParseBatteryValue(value);
            if (parsed is >= 0 and <= 100)
                return parsed;
        }

        // 启发式扫描包含"battery"的键
        foreach (var pair in properties)
        {
            if (pair.Value is null)
                continue;

            if (!pair.Key.Contains("battery", StringComparison.OrdinalIgnoreCase) &&
                !pair.Key.Contains("Battery", StringComparison.OrdinalIgnoreCase))
                continue;

            var parsed = ParseBatteryValue(pair.Value);
            if (parsed is >= 0 and <= 100)
                return parsed;
        }

        return null;
    }

    /// <summary>
    /// 解析电池值
    /// </summary>
    private int? ParseBatteryValue(object value)
    {
        try
        {
            if (value is byte b)
                return b;
            if (value is short s)
                return s;
            if (value is int i)
                return i;
            if (value is long l)
                return (int)l;
            if (value is uint ui)
                return (int)ui;
            if (value is ulong ul)
                return (int)ul;
            if (value is string str && int.TryParse(str, out int parsed))
                return parsed;
            
            return Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 获取连接状态
    /// </summary>
    private bool TryGetConnectionStatus(IReadOnlyDictionary<string, object> properties)
    {
        foreach (var name in BluetoothConstants.ConnectionPropertyNames)
        {
            if (!properties.TryGetValue(name, out var value) || value is null)
                continue;

            if (value is bool b)
                return b;
            if (value is string str && bool.TryParse(str, out bool parsed))
                return parsed;
            if (value is int i)
                return i != 0;
        }

        return false;
    }

    /// <summary>
    /// 获取设备显示名称
    /// </summary>
    private string GetDeviceDisplayName(DeviceInformation device)
    {
        if (!string.IsNullOrWhiteSpace(device.Name))
            return device.Name;

        // 尝试从属性中获取显示名称
        if (device.Properties.TryGetValue("System.ItemNameDisplay", out var displayNameObj) && 
            displayNameObj is string displayName && !string.IsNullOrWhiteSpace(displayName))
            return displayName;

        return "Unknown Bluetooth Device";
    }

    /// <summary>
    /// 获取设备唯一标识
    /// </summary>
    private string GetDeviceKey(DeviceInformation device)
    {
        // 使用设备ID作为唯一标识
        return $"windows_property_{device.Id.GetHashCode():X8}";
    }

    /// <summary>
    /// 清理已移除的设备
    /// </summary>
    private void CleanupRemovedDevices(HashSet<string> currentDeviceKeys)
    {
        var removedKeys = _devices.Keys
            .Where(key => !currentDeviceKeys.Contains(key))
            .ToList();
            
        foreach (var key in removedKeys)
        {
            if (_devices.TryRemove(key, out var device))
            {
                Logger.Debug($"Windows属性设备已移除: {device.DisplayName}");
                _context?.RemoveDevice(key);
            }
        }
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        _devices.Clear();
        _context = null;
    }

    /// <summary>
    /// 设备信息
    /// </summary>
    private class DeviceInfo
    {
        public string DeviceKey { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
    }
}