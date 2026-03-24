using System.Threading;
using System.Threading.Tasks;

namespace PeriView.App.Services.BatteryProviders;

/// <summary>
/// 电池提供者接口，参考蓝牙项目的设计模式
/// </summary>
public interface IBatteryProvider
{
    /// <summary>
    /// 提供者名称
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 检查是否支持特定设备
    /// </summary>
    /// <param name="vendorId">供应商ID</param>
    /// <param name="productId">产品ID</param>
    /// <returns>是否支持</returns>
    bool CanHandle(ushort vendorId, ushort productId);

    /// <summary>
    /// 读取设备电池信息
    /// </summary>
    /// <param name="context">电池提供者上下文</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>任务</returns>
    Task ReadBatteryAsync(IBatteryProviderContext context, CancellationToken cancellationToken);

    /// <summary>
    /// 读取特定设备的电池信息
    /// </summary>
    /// <param name="context">电池提供者上下文</param>
    /// <param name="deviceId">设备ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>任务</returns>
    Task ReadBatteryAsync(IBatteryProviderContext context, string deviceId, CancellationToken cancellationToken);
}