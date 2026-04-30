using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PeriView.App.Models;
using PeriView.App.Services;

namespace PeriView.App.Services.BatteryProviders;

/// <summary>
/// 电池提供者路由器，根据设备类型路由到合适的提供者
/// </summary>
public class BatteryProviderRouter : IDeviceStatusProvider
{
    private readonly List<IBatteryProvider> _providers;
    private readonly BatteryProviderContext _context;

    /// <summary>
    /// 提供者名称
    /// </summary>
    public string Name => "电池提供者路由器";

    /// <summary>
    /// 初始化路由器
    /// </summary>
    public BatteryProviderRouter()
    {
        _providers = new List<IBatteryProvider>();
        _context = new BatteryProviderContext();
        
        // 注册默认提供者（按优先级顺序）
        // 注意：LogitechHidProvider 和 RazerHidProvider 的 HID 电池协议尚未实现，
        // 暂时取消注册以避免产生不可靠的电量数据。
        RegisterProvider(new WindowsPropertyProvider());
        RegisterProvider(new XboxGamepadProvider());
    }

    /// <summary>
    /// 注册提供者
    /// </summary>
    /// <param name="provider">提供者实例</param>
    public void RegisterProvider(IBatteryProvider provider)
    {
        if (provider == null)
            throw new ArgumentNullException(nameof(provider));

        _providers.Add(provider);
    }

    /// <summary>
    /// 获取所有设备状态
    /// </summary>
    public async Task<IReadOnlyList<DeviceStatus>> GetStatusesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // 清空前一轮的结果
            _context.ClearResults();
            
            // 并行执行所有提供者
            var tasks = _providers.Select(provider => 
                provider.ReadBatteryAsync(_context, cancellationToken)).ToList();
            
            await Task.WhenAll(tasks);
            
            // 返回所有收集到的设备状态
            return _context.GetAllDeviceStatuses();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error($"电池提供者路由器执行失败: {ex.Message}", ex);
            return new List<DeviceStatus>
            {
                new DeviceStatus
                {
                    DeviceKey = "battery-router-error",
                    Name = "电池提供者路由器",
                    Source = Name,
                    IsConnected = true,
                    LastUpdated = DateTimeOffset.Now,
                    Error = $"路由器执行失败: {ex.GetType().Name}: {ex.Message}"
                }
            };
        }
    }

    /// <summary>
    /// 根据供应商ID和产品ID选择提供者
    /// </summary>
    /// <param name="vendorId">供应商ID</param>
    /// <param name="productId">产品ID</param>
    /// <returns>合适的提供者，如果没有则返回默认提供者</returns>
    public IBatteryProvider SelectProvider(ushort vendorId, ushort productId)
    {
        // 按注册顺序查找第一个支持的提供者
        var provider = _providers.FirstOrDefault(p => p.CanHandle(vendorId, productId));
        
        // 如果没有特定提供者支持，返回Windows属性提供者作为默认
        return provider ?? _providers.FirstOrDefault(p => p is WindowsPropertyProvider) 
            ?? _providers.First();
    }
}