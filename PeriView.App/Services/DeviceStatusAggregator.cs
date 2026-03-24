using PeriView.App.Models;

namespace PeriView.App.Services;

public sealed class DeviceStatusAggregator
{
    private readonly IReadOnlyList<IDeviceStatusProvider> _providers;

    public DeviceStatusAggregator(IEnumerable<IDeviceStatusProvider> providers)
    {
        _providers = providers.ToList();
    }

    public async Task<IReadOnlyList<DeviceStatus>> GetStatusesAsync(CancellationToken cancellationToken = default)
    {
        var all = new List<DeviceStatus>();

        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var statuses = await provider.GetStatusesAsync(cancellationToken);
                if (statuses.Count > 0)
                {
                    all.AddRange(statuses);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                all.Add(new DeviceStatus
                {
                    DeviceKey = $"{provider.Name}-timeout",
                    Name = provider.Name,
                    Source = provider.Name,
                    LastUpdated = DateTimeOffset.Now,
                    Error = $"Provider timeout/canceled: {ex.Message}"
                });
            }
            catch (Exception ex)
            {
                all.Add(new DeviceStatus
                {
                    DeviceKey = $"{provider.Name}-error",
                    Name = provider.Name,
                    Source = provider.Name,
                    LastUpdated = DateTimeOffset.Now,
                    Error = $"Provider failed: {ex.GetType().Name}: {ex.Message}"
                });
            }
        }

        return all;
    }
}
