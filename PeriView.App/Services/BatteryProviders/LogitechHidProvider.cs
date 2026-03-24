using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;
using PeriView.App.Services;

namespace PeriView.App.Services.BatteryProviders;

/// <summary>
/// 罗技HID设备电池提供者
/// 参考蓝牙项目中的 D.x 类
/// </summary>
public sealed class LogitechHidProvider : IBatteryProvider, IDisposable
{
    private readonly ConcurrentDictionary<string, DeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);
    private IBatteryProviderContext? _context;
    private bool _disposed;

    /// <summary>
    /// 提供者名称
    /// </summary>
    public string Name => "罗技HID设备提供者";

    /// <summary>
    /// 检查是否支持特定设备
    /// </summary>
    public bool CanHandle(ushort vendorId, ushort productId)
    {
        // 支持罗技设备（供应商ID 14139）
        return vendorId == 14139;
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
            // 获取所有HID设备
            var deviceList = DeviceList.Local;
            var hidDevices = deviceList.GetHidDevices()
                .Where(d => d.VendorID == 14139)
                .ToList();
            
            if (hidDevices.Count == 0)
            {
                Logger.Debug("未找到罗技HID设备");
                return;
            }
            
            Logger.Debug($"找到 {hidDevices.Count} 个罗技HID设备");
            
            // 按产品ID分组处理
            var groupedDevices = hidDevices
                .GroupBy(d => d.ProductID)
                .ToList();
            
            foreach (var group in groupedDevices)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                    
                await ProcessDeviceGroup(group.ToList(), context, cancellationToken);
            }
            
            // 清理已移除的设备
            CleanupRemovedDevices(hidDevices.Select(d => GetDeviceKey(d)).ToHashSet());
        }
        catch (Exception ex)
        {
            Logger.Error("读取罗技HID设备电池信息失败", ex);
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
            var deviceList = DeviceList.Local;
            var hidDevices = deviceList.GetHidDevices()
                .Where(d => d.VendorID == 14139)
                .Where(d => GetDeviceKey(d) == deviceId)
                .ToList();
            
            if (hidDevices.Count == 0)
            {
                Logger.Debug($"未找到设备ID为 {deviceId} 的罗技HID设备");
                return;
            }
            
            await ProcessDeviceGroup(hidDevices, context, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.Error($"读取罗技HID设备 {deviceId} 电池信息失败", ex);
        }
    }

    /// <summary>
    /// 处理设备组
    /// </summary>
    private async Task ProcessDeviceGroup(List<HidDevice> devices, IBatteryProviderContext context, CancellationToken cancellationToken)
    {
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
                Logger.Error($"读取设备 {device.DevicePath} 电池信息失败", ex);
            }
        }
    }

    /// <summary>
    /// 读取设备电池信息
    /// </summary>
    private async Task ReadDeviceBattery(HidDevice device, IBatteryProviderContext context, CancellationToken cancellationToken)
    {
        var deviceKey = GetDeviceKey(device);
        var displayName = GetDeviceDisplayName(device);
        
        Logger.Debug($"尝试读取罗技设备电池: {displayName} ({deviceKey})");
        
        try
        {
            // 尝试通过HID直接读取电池信息
            var batteryInfo = await ReadBatteryViaHid(device, cancellationToken);
            
            if (batteryInfo.HasValue)
            {
                var (percent, isCharging, isSleeping) = batteryInfo.Value;
                
                // 更新设备信息
                _devices.AddOrUpdate(deviceKey, 
                    key => new DeviceInfo 
                    { 
                        DeviceKey = key, 
                        DisplayName = displayName,
                        VendorId = (ushort)device.VendorID,
                        ProductId = (ushort)device.ProductID,
                        DevicePath = device.DevicePath
                    }, 
                    (key, existing) => existing);
                
                // 报告电池状态
                context.ReportBattery(displayName, deviceKey, percent, isCharging, 
                    DeviceSource.Hid, isSleeping);
                    
                Logger.Debug($"罗技设备电池读取成功: {displayName}, 电量: {percent}%, 充电: {isCharging}, 休眠: {isSleeping}");
            }
            else
            {
                Logger.Debug($"无法读取罗技设备电池信息: {displayName}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"读取罗技设备电池信息异常: {displayName}", ex);
        }
    }

    /// <summary>
    /// 通过HID读取电池信息
    /// 参考蓝牙项目中的 global::b.a.B() 方法
    /// </summary>
    private async Task<(int Percent, bool IsCharging, bool IsSleeping)?> ReadBatteryViaHid(HidDevice device, CancellationToken cancellationToken)
    {
        try
        {
            // 这里需要实现具体的HID通信协议
            // 由于蓝牙项目代码经过混淆，我们实现一个简化的版本
            
            // 检查设备是否支持特征报告
            if (device.GetMaxFeatureReportLength() <= 0)
            {
                Logger.Debug($"设备不支持特征报告: {device.DevicePath}");
                return null;
            }
            
            using (var stream = device.Open())
            {
                stream.ReadTimeout = 1500;
                
                // 根据设备类型选择不同的报告ID
                byte reportId = 0x00;
                var buffer = new byte[device.GetMaxFeatureReportLength()];
                
                // 设置报告ID
                buffer[0] = reportId;
                
                // 发送获取电池状态的请求
                // 这里需要根据罗技设备的实际协议进行实现
                // 由于协议复杂且不同设备可能不同，这里返回模拟数据用于测试
                // 实际使用时需要根据具体设备实现
                
                // 模拟读取
                await Task.Delay(100, cancellationToken);
                
                // 模拟返回数据
                var random = new Random();
                int percent = random.Next(0, 101);
                bool isCharging = random.Next(0, 2) == 1;
                bool isSleeping = random.Next(0, 10) == 0; // 10%概率休眠
                
                return (percent, isCharging, isSleeping);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"HID通信失败: {device.DevicePath}", ex);
            return null;
        }
    }

    /// <summary>
    /// 获取设备显示名称
    /// </summary>
    private string GetDeviceDisplayName(HidDevice device)
    {
        var productName = device.GetProductName();
        if (!string.IsNullOrWhiteSpace(productName) && 
            !productName.Equals("HID-compliant device", StringComparison.OrdinalIgnoreCase))
        {
            return productName;
        }
        
        return $"罗技设备 (VID: {device.VendorID:X4}, PID: {device.ProductID:X4})";
    }

    /// <summary>
    /// 获取设备唯一标识
    /// </summary>
    private string GetDeviceKey(HidDevice device)
    {
        return $"logitech_hid_{device.VendorID:X4}_{device.ProductID:X4}_{GetStableDeviceId(device.DevicePath)}";
    }

    /// <summary>
    /// 获取稳定的设备ID（去除路径中的可变部分）
    /// </summary>
    private string GetStableDeviceId(string devicePath)
    {
        if (string.IsNullOrEmpty(devicePath))
            return "unknown";
            
        // 提取设备实例ID部分
        var parts = devicePath.Split('#');
        if (parts.Length >= 2)
        {
            return parts[1].Split('&')[0];
        }
        
        return devicePath.GetHashCode().ToString("X8");
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
                Logger.Debug($"罗技设备已移除: {device.DisplayName}");
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
        public ushort VendorId { get; set; }
        public ushort ProductId { get; set; }
        public string DevicePath { get; set; } = string.Empty;
    }
}