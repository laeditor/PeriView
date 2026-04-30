using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Power;
using Windows.Gaming.Input;
using PeriView.App.Services;

namespace PeriView.App.Services.BatteryProviders;

/// <summary>
/// Xbox游戏控制器电池提供者
/// 参考蓝牙项目中的 c.M 类
/// </summary>
public sealed class XboxGamepadProvider : IBatteryProvider, IDisposable
{
    private readonly ConcurrentDictionary<string, DeviceInfo> _devices = new(StringComparer.OrdinalIgnoreCase);
    private IBatteryProviderContext? _context;
    private bool _disposed;

    /// <summary>
    /// 提供者名称
    /// </summary>
    public string Name => "Xbox游戏控制器提供者";

    /// <summary>
    /// 检查是否支持特定设备
    /// </summary>
    public bool CanHandle(ushort vendorId, ushort productId)
    {
        // 支持Xbox控制器（供应商ID 1118）
        return vendorId == 1118;
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
            // 获取所有原始游戏控制器
            var rawControllers = RawGameController.RawGameControllers;
            if (rawControllers.Count == 0)
            {
                Logger.Debug("未找到游戏控制器");
                return;
            }
            
            Logger.Debug($"找到 {rawControllers.Count} 个游戏控制器");
            
            foreach (var controller in rawControllers)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
                    
                try
                {
                    await ReadControllerBattery(controller, context, cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger.Error($"读取控制器电池信息失败: {controller.DisplayName}", ex);
                }
            }
            
            // 清理已移除的设备
            var currentDeviceKeys = rawControllers
                .Select(c => GetDeviceKey(c))
                .ToHashSet();
            CleanupRemovedDevices(currentDeviceKeys);
        }
        catch (Exception ex)
        {
            Logger.Error("读取Xbox控制器电池信息失败", ex);
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
            var controller = RawGameController.RawGameControllers
                .FirstOrDefault(c => GetDeviceKey(c) == deviceId);
            
            if (controller == null)
            {
                Logger.Debug($"未找到设备ID为 {deviceId} 的Xbox控制器");
                return;
            }
            
            await ReadControllerBattery(controller, context, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.Error($"读取Xbox控制器 {deviceId} 电池信息失败", ex);
        }
    }

    /// <summary>
    /// 读取控制器电池信息
    /// </summary>
    private async Task ReadControllerBattery(RawGameController controller, IBatteryProviderContext context, CancellationToken cancellationToken)
    {
        var deviceKey = GetDeviceKey(controller);
        var displayName = GetDeviceDisplayName(controller);
        
        Logger.Debug($"尝试读取Xbox控制器电池: {displayName} ({deviceKey})");
        
        try
        {
            // 获取电池报告
            var batteryReport = controller.TryGetBatteryReport();
            if (batteryReport == null)
            {
                Logger.Debug($"控制器 {displayName} 不支持电池报告");
                return;
            }
            
            // 计算电池百分比
            int batteryPercent = -1;
            if (batteryReport.RemainingCapacityInMilliwattHours.HasValue && 
                batteryReport.FullChargeCapacityInMilliwattHours.HasValue &&
                batteryReport.FullChargeCapacityInMilliwattHours.Value > 0)
            {
                double remaining = batteryReport.RemainingCapacityInMilliwattHours.Value;
                double full = batteryReport.FullChargeCapacityInMilliwattHours.Value;
                batteryPercent = (int)Math.Round(remaining / full * 100.0);
                batteryPercent = Math.Clamp(batteryPercent, 0, 100);
            }
            
            // 判断是否正在充电（根据电池报告的状态字符串）
            bool isCharging = batteryReport.Status.ToString().Contains("Charging", StringComparison.OrdinalIgnoreCase);
            var connectionStatus = batteryReport.Status.ToString();
            
            // 更新设备信息
            _devices.AddOrUpdate(deviceKey, 
                key => new DeviceInfo 
                { 
                    DeviceKey = key, 
                    DisplayName = displayName,
                    VendorId = (ushort)controller.HardwareVendorId,
                    ProductId = (ushort)controller.HardwareProductId,
                    NonRoamableId = controller.NonRoamableId
                }, 
                (key, existing) => existing);
            
            // 报告电池状态
            context.ReportBattery(displayName, deviceKey, batteryPercent, isCharging, 
                DeviceSource.Xbox, false, connectionStatus);
                
            Logger.Debug($"Xbox控制器电池读取成功: {displayName}, 电量: {batteryPercent}%, 充电: {isCharging}, 状态: {connectionStatus}");
        }
        catch (Exception ex)
        {
            Logger.Error($"读取Xbox控制器电池信息异常: {displayName}", ex);
        }
        
        await Task.CompletedTask;
    }

    /// <summary>
    /// 获取设备显示名称
    /// </summary>
    private string GetDeviceDisplayName(RawGameController controller)
    {
        if (!string.IsNullOrWhiteSpace(controller.DisplayName))
        {
            return controller.DisplayName;
        }
        
        return $"Xbox控制器 (VID: {controller.HardwareVendorId:X4}, PID: {controller.HardwareProductId:X4})";
    }

    /// <summary>
    /// 获取设备唯一标识
    /// </summary>
    private string GetDeviceKey(RawGameController controller)
    {
        // 使用 NonRoamableId 原始字符串而非 GetHashCode()，避免哈希碰撞风险
        var stableId = controller.NonRoamableId?.Replace(":", "").Replace("-", "").Replace(" ", "") ?? "unknown";
        return $"xbox_gamepad_{controller.HardwareVendorId:X4}_{controller.HardwareProductId:X4}_{stableId}";
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
                Logger.Debug($"Xbox控制器已移除: {device.DisplayName}");
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
        public string NonRoamableId { get; set; } = string.Empty;
    }
}