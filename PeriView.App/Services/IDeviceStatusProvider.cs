using PeriView.App.Models;

namespace PeriView.App.Services;

public interface IDeviceStatusProvider
{
    string Name { get; }

    Task<IReadOnlyList<DeviceStatus>> GetStatusesAsync(CancellationToken cancellationToken = default);
}
