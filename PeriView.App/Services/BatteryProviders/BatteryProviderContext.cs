using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using PeriView.App.Models;
using PeriView.App.Services;

namespace PeriView.App.Services.BatteryProviders;

/// <summary>
/// 电池提供者上下文实现，协调各提供者之间的工作
/// </summary>
public sealed class BatteryProviderContext : IBatteryProviderContext, IDisposable
{
    private readonly ConcurrentDictionary<string, DeviceStatus> _deviceStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _reportedDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// 服务提供者（用于依赖注入）
    /// </summary>
    public IServiceProvider Services { get; }

    /// <summary>
    /// 初始化上下文
    /// </summary>
    public BatteryProviderContext()
    {
        Services = new ServiceProvider();
    }

    /// <summary>
    /// 报告设备电池状态
    /// </summary>
    public void ReportBattery(string deviceName, string deviceId, int batteryPercent, bool isCharging,
        DeviceSource source, bool isSleeping = false, string? connectionStatus = null)
    {
        if (_disposed) return;
        
        try
        {
            var deviceStatus = ToDeviceStatus(deviceName, deviceId, batteryPercent, isCharging, source, isSleeping, connectionStatus);
            
            _deviceStatuses.AddOrUpdate(deviceId, deviceStatus, (_, _) => deviceStatus);
            
            Logger.Debug($"报告设备电池状态: {deviceName} ({deviceId}), 电量: {batteryPercent}%, 充电: {isCharging}, 来源: {source}");
        }
        catch (Exception ex)
        {
            Logger.Error($"报告设备电池状态失败: {deviceName} ({deviceId})", ex);
        }
    }

    /// <summary>
    /// 标记设备为已移除
    /// </summary>
    public void RemoveDevice(string deviceId)
    {
        if (_disposed) return;
        
        _deviceStatuses.TryRemove(deviceId, out _);
        Logger.Debug($"移除设备: {deviceId}");
    }

    /// <summary>
    /// 获取当前已知的设备ID集合
    /// </summary>
    public IReadOnlySet<string> GetKnownDeviceIds()
    {
        if (_disposed) return new HashSet<string>();
        
        return new HashSet<string>(_deviceStatuses.Keys, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 检查设备是否已报告过（在指定超时时间内）
    /// </summary>
    public bool IsDeviceReported(string deviceId, int timeoutSeconds)
    {
        if (_disposed) return false;
        
        if (_reportedDevices.TryGetValue(deviceId, out var lastReported))
        {
            var timeout = TimeSpan.FromSeconds(timeoutSeconds);
            return DateTimeOffset.Now - lastReported < timeout;
        }
        
        return false;
    }

    /// <summary>
    /// 标记设备为已报告
    /// </summary>
    public void MarkDeviceReported(string deviceId, int timeoutSeconds)
    {
        if (_disposed) return;
        
        _reportedDevices.AddOrUpdate(deviceId, DateTimeOffset.Now, (_, _) => DateTimeOffset.Now);
        
        // 清理过期的报告记录
        CleanupReportedDevices(timeoutSeconds);
    }

    /// <summary>
    /// 转换设备状态为统一格式
    /// </summary>
    public DeviceStatus ToDeviceStatus(string deviceName, string deviceId, int batteryPercent,
        bool isCharging, DeviceSource source, bool isSleeping = false, string? connectionStatus = null)
    {
        return new DeviceStatus
        {
            DeviceKey = deviceId,
            Name = deviceName,
            BatteryPercent = batteryPercent >= 0 ? batteryPercent : null,
            IsCharging = isCharging,
            Source = source.ToString(),
            LastUpdated = DateTimeOffset.Now,
            IsConnected = !isSleeping && connectionStatus is not null &&
                (connectionStatus.Equals("Connected", StringComparison.OrdinalIgnoreCase) ||
                 connectionStatus.Equals("Charging", StringComparison.OrdinalIgnoreCase)),
            DebugProperties = isSleeping ? "Sleeping" : null
        };
    }

    /// <summary>
    /// 获取所有收集到的设备状态
    /// </summary>
    public IReadOnlyList<DeviceStatus> GetAllDeviceStatuses()
    {
        if (_disposed) return new List<DeviceStatus>();
        
        return _deviceStatuses.Values.ToList();
    }

    /// <summary>
    /// 清空当前轮次的结果（供路由器在每轮开始前调用）
    /// </summary>
    public void ClearResults()
    {
        if (_disposed) return;
        
        lock (_lock)
        {
            _deviceStatuses.Clear();
        }
    }

    /// <summary>
    /// 清理过期的报告记录
    /// </summary>
    private void CleanupReportedDevices(int timeoutSeconds)
    {
        try
        {
            var cutoff = DateTimeOffset.Now - TimeSpan.FromSeconds(timeoutSeconds * 2); // 清理两倍超时时间的记录
            var expiredKeys = _reportedDevices
                .Where(kvp => kvp.Value < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();
            
            foreach (var key in expiredKeys)
            {
                _reportedDevices.TryRemove(key, out _);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"清理过期报告记录失败", ex);
        }
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        _deviceStatuses.Clear();
        _reportedDevices.Clear();
    }

    /// <summary>
    /// 简单的服务提供者实现
    /// </summary>
    private class ServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            // 简单实现，返回null
            return null;
        }
    }
}