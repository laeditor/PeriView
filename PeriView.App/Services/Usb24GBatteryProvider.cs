using System.Runtime.Versioning;
using PeriView.App.Models;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;

namespace PeriView.App.Services;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class Usb24GBatteryProvider : IDeviceStatusProvider
{
    private static readonly TimeSpan EnumerationTimeout = TimeSpan.FromSeconds(6);
    private const ushort GenericDesktopUsagePage = 0x01;
    private const ushort MouseUsageId = 0x02;

    private static readonly string[] ConnectionPropertyNames =
    {
        "System.Devices.Aep.IsConnected",
        "System.Devices.Connected"
    };

    private static readonly string[] BatteryPropertyNames =
    {
        "System.Devices.BatteryLifePercent",
        "System.Devices.BatteryLevel",
        "System.Devices.Aep.BatteryLifePercent",
        "System.Devices.Aep.BatteryLevel",
        "System.Devices.Hid.BatteryLevel",
        "System.Devices.Hid.BatteryPercent",
        "System.Devices.Hid.BatteryStrength",
        "System.Devices.Interface.BatteryLevel",
        "System.Devices.Pnp.BatteryLevel"
    };

    private static readonly string[] RequestedProperties = ConnectionPropertyNames
        .Concat(BatteryPropertyNames)
        .Concat(new[]
        {
            "System.ItemNameDisplay",
            "System.Devices.ContainerId",
            "System.Devices.Aep.ContainerId",
            "System.Devices.DeviceInstanceId"
        })
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public string Name => "2.4G HID";

    public async Task<IReadOnlyList<DeviceStatus>> GetStatusesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var mouseSelector = HidDevice.GetDeviceSelector(GenericDesktopUsagePage, MouseUsageId);
            var hidMouseInterfaces = await FindAllWithTimeoutAsync(mouseSelector, RequestedProperties, cancellationToken);
            if (hidMouseInterfaces.Count == 0)
            {
                return new[]
                {
                    BuildEmptyStatus("未发现可访问的 HID 鼠标接口。")
                };
            }

            var statusList = new List<DeviceStatus>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var device in hidMouseInterfaces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsLikely24GDevice(device))
                {
                    continue;
                }

                var key = BuildDeviceKey(device);
                if (!seenKeys.Add(key))
                {
                    continue;
                }

                var battery = TryGetBatteryPercent(device);
                var connected = TryGetConnection(device);
                var name = ResolveDisplayName(device);
                var diagnostic = battery.HasValue
                    ? null
                    : "实时模式下未读取到电量属性。该设备可能使用厂商私有 HID 协议，需专用 SDK/驱动。";

                statusList.Add(new DeviceStatus
                {
                    DeviceKey = key,
                    Name = name,
                    Source = Name,
                    IsConnected = connected ?? true,
                    BatteryPercent = battery,
                    LastUpdated = DateTimeOffset.Now,
                    Error = diagnostic,
                    DebugProperties = BuildDebugDump(device, battery, connected)
                });
            }

            if (statusList.Count == 0)
            {
                return new[]
                {
                    BuildEmptyStatus("已发现 HID 鼠标接口，但未识别到 2.4G 设备。")
                };
            }

            return statusList;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new[]
            {
                new DeviceStatus
                {
                    DeviceKey = "2.4g-hid-timeout",
                    Name = "2.4G HID",
                    Source = Name,
                    IsConnected = true,
                    LastUpdated = DateTimeOffset.Now,
                    Error = $"实时枚举超时（>{EnumerationTimeout.TotalSeconds:0} 秒），已跳过本轮 2.4G 查询。"
                }
            };
        }
        catch (Exception ex)
        {
            return new[]
            {
                new DeviceStatus
                {
                    DeviceKey = "2.4g-hid-error",
                    Name = "2.4G HID",
                    Source = Name,
                    IsConnected = true,
                    LastUpdated = DateTimeOffset.Now,
                    Error = $"实时枚举失败: {ex.GetType().Name}: {ex.Message}"
                }
            };
        }
    }

    private static DeviceStatus BuildEmptyStatus(string message)
    {
        return new DeviceStatus
        {
            DeviceKey = "2.4g-hid-empty",
            Name = "2.4G HID",
            Source = "2.4G HID",
            IsConnected = true,
            LastUpdated = DateTimeOffset.Now,
            Error = message
        };
    }

    private static async Task<IReadOnlyList<DeviceInformation>> FindAllWithTimeoutAsync(
        string selector,
        string[] properties,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(EnumerationTimeout);
        return await DeviceInformation.FindAllAsync(selector, properties).AsTask(timeoutCts.Token);
    }

    private static bool IsLikely24GDevice(DeviceInformation info)
    {
        var id = info.Id ?? string.Empty;
        var name = ResolveDisplayName(info);
        var idUpper = id.ToUpperInvariant();
        var nameUpper = name.ToUpperInvariant();

        var containsBluetooth = idUpper.Contains("BTH") || nameUpper.Contains("BLUETOOTH");
        if (containsBluetooth)
        {
            return false;
        }

        return idUpper.Contains("USB")
            || idUpper.Contains("HID")
            || nameUpper.Contains("2.4G")
            || nameUpper.Contains("DONGLE")
            || nameUpper.Contains("RECEIVER")
            || nameUpper.Contains("WIRELESS");
    }

    private static bool? TryGetConnection(DeviceInformation info)
    {
        foreach (var propertyName in ConnectionPropertyNames)
        {
            if (!info.Properties.TryGetValue(propertyName, out var value) || value is null)
            {
                continue;
            }

            if (value is bool b)
            {
                return b;
            }

            if (bool.TryParse(value.ToString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static int? TryGetBatteryPercent(DeviceInformation info)
    {
        foreach (var propertyName in BatteryPropertyNames)
        {
            if (!info.Properties.TryGetValue(propertyName, out var value) || value is null)
            {
                continue;
            }

            if (TryConvertToBattery(value, out var battery))
            {
                return battery;
            }
        }

        return null;
    }

    private static bool TryConvertToBattery(object value, out int battery)
    {
        battery = -1;
        switch (value)
        {
            case byte b:
                battery = b;
                break;
            case sbyte sb:
                battery = sb;
                break;
            case short s:
                battery = s;
                break;
            case ushort us:
                battery = us;
                break;
            case int i:
                battery = i;
                break;
            case uint ui:
                battery = (int)ui;
                break;
            case long l when l is >= int.MinValue and <= int.MaxValue:
                battery = (int)l;
                break;
            case float f:
                battery = (int)Math.Round(f);
                break;
            case double d:
                battery = (int)Math.Round(d);
                break;
            default:
            {
                var text = value.ToString();
                if (!string.IsNullOrWhiteSpace(text) && int.TryParse(text, out var parsed))
                {
                    battery = parsed;
                }
                break;
            }
        }

        return battery is >= 0 and <= 100;
    }

    private static string ResolveDisplayName(DeviceInformation info)
    {
        if (!string.IsNullOrWhiteSpace(info.Name))
        {
            return info.Name;
        }

        if (info.Properties.TryGetValue("System.ItemNameDisplay", out var value)
            && !string.IsNullOrWhiteSpace(value?.ToString()))
        {
            return value!.ToString()!;
        }

        return "2.4G HID Device";
    }

    private static string BuildDeviceKey(DeviceInformation info)
    {
        var id = info.Id ?? string.Empty;
        if (string.IsNullOrWhiteSpace(id))
        {
            return $"2.4g-hid-{ResolveDisplayName(info)}";
        }

        return $"2.4g-hid-{id}";
    }

    private static string BuildDebugDump(DeviceInformation info, int? battery, bool? connected)
    {
        var lines = new List<string>
        {
            $"id={info.Id}",
            $"name={ResolveDisplayName(info)}",
            $"connected={(connected.HasValue ? connected.Value.ToString() : "null")}",
            $"battery={(battery.HasValue ? battery.Value.ToString() : "null")}",
            "properties:"
        };

        foreach (var pair in info.Properties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"{pair.Key}={pair.Value}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}