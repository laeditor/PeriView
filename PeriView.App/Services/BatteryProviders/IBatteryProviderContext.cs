using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PeriView.App.Models;

namespace PeriView.App.Services.BatteryProviders;

/// <summary>
/// 电池提供者上下文接口，协调各提供者之间的工作
/// </summary>
public interface IBatteryProviderContext
{
    /// <summary>
    /// 报告设备电池状态
    /// </summary>
    /// <param name="deviceName">设备显示名称</param>
    /// <param name="deviceId">设备唯一标识</param>
    /// <param name="batteryPercent">电池百分比（-1表示未知）</param>
    /// <param name="isCharging">是否正在充电</param>
    /// <param name="source">设备来源</param>
    /// <param name="isSleeping">设备是否休眠（可选）</param>
    /// <param name="connectionStatus">连接状态（可选）</param>
    void ReportBattery(string deviceName, string deviceId, int batteryPercent, bool isCharging, 
        DeviceSource source, bool isSleeping = false, string? connectionStatus = null);

    /// <summary>
    /// 标记设备为已移除
    /// </summary>
    /// <param name="deviceId">设备唯一标识</param>
    void RemoveDevice(string deviceId);

    /// <summary>
    /// 获取当前已知的设备ID集合
    /// </summary>
    /// <returns>设备ID集合</returns>
    IReadOnlySet<string> GetKnownDeviceIds();

    /// <summary>
    /// 检查设备是否已报告过
    /// </summary>
    /// <param name="deviceId">设备唯一标识</param>
    /// <param name="timeoutSeconds">超时秒数</param>
    /// <returns>是否已报告</returns>
    bool IsDeviceReported(string deviceId, int timeoutSeconds);

    /// <summary>
    /// 标记设备为已报告
    /// </summary>
    /// <param name="deviceId">设备唯一标识</param>
    /// <param name="timeoutSeconds">超时秒数</param>
    void MarkDeviceReported(string deviceId, int timeoutSeconds);

    /// <summary>
    /// 服务提供者（用于依赖注入）
    /// </summary>
    IServiceProvider Services { get; }

    /// <summary>
    /// 转换设备状态为统一格式
    /// </summary>
    /// <param name="deviceName">设备名称</param>
    /// <param name="deviceId">设备ID</param>
    /// <param name="batteryPercent">电池百分比</param>
    /// <param name="isCharging">是否充电</param>
    /// <param name="source">设备来源</param>
    /// <param name="isSleeping">是否休眠</param>
    /// <param name="connectionStatus">连接状态</param>
    /// <returns>设备状态对象</returns>
    DeviceStatus ToDeviceStatus(string deviceName, string deviceId, int batteryPercent, 
        bool isCharging, DeviceSource source, bool isSleeping = false, string? connectionStatus = null);
}