using PeriView.App.Models;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PeriView.App.ViewModels;

public sealed class DeviceStatusItemViewModel : INotifyPropertyChanged
{
    private const int DiagnosticPreviewLength = 48;
    private const int DebugPreviewLength = 72;

    private string _deviceKey = string.Empty;
    private string _name = string.Empty;
    private string _isConnectedText = string.Empty;
    private string _batteryText = string.Empty;
    private string _isChargingText = string.Empty;
    private string _source = string.Empty;
    private string _lastUpdatedText = string.Empty;
    private string? _error;
    private string _errorText = string.Empty;
    private string _errorSummary = string.Empty;
    private string _errorFullText = string.Empty;
    private string _debugPropertiesText = string.Empty;
    private string _debugPropertiesSummary = string.Empty;
    private string _debugPropertiesFullText = string.Empty;

    public DeviceStatusItemViewModel(DeviceStatus status)
    {
        UpdateFromStatus(status);
    }

    public void UpdateFromStatus(DeviceStatus status)
    {
        DeviceKey = status.DeviceKey;
        Name = status.Name;
        Source = status.Source;
        IsConnectedText = status.IsConnected switch
        {
            true => "已连接",
            false => "未连接",
            null => "未知"
        };

        BatteryText = status.BatteryPercent.HasValue ? $"{status.BatteryPercent.Value}%" : "N/A";
        IsChargingText = status.IsCharging switch
        {
            true => "是",
            false => "否",
            null => "未知"
        };

        LastUpdatedText = status.LastUpdated.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        Error = status.Error;
        ErrorText = string.IsNullOrWhiteSpace(status.Error) ? "-" : status.Error;
        ErrorFullText = ErrorText;
        ErrorSummary = BuildErrorSummary(ErrorText);

        DebugPropertiesText = string.IsNullOrWhiteSpace(status.DebugProperties) ? "-" : status.DebugProperties;
        DebugPropertiesSummary = BuildPreview(DebugPropertiesText, DebugPreviewLength);
        DebugPropertiesFullText = DebugPropertiesText;
    }

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    public string DeviceKey
    {
        get => _deviceKey;
        private set => SetProperty(ref _deviceKey, value);
    }

    public string IsConnectedText
    {
        get => _isConnectedText;
        private set => SetProperty(ref _isConnectedText, value);
    }

    public string BatteryText
    {
        get => _batteryText;
        private set => SetProperty(ref _batteryText, value);
    }

    public string IsChargingText
    {
        get => _isChargingText;
        private set => SetProperty(ref _isChargingText, value);
    }

    public string Source
    {
        get => _source;
        private set => SetProperty(ref _source, value);
    }

    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        private set => SetProperty(ref _lastUpdatedText, value);
    }

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public string ErrorSummary
    {
        get => _errorSummary;
        private set => SetProperty(ref _errorSummary, value);
    }

    public string ErrorFullText
    {
        get => _errorFullText;
        private set => SetProperty(ref _errorFullText, value);
    }

    public string DebugPropertiesText
    {
        get => _debugPropertiesText;
        private set => SetProperty(ref _debugPropertiesText, value);
    }

    public string DebugPropertiesSummary
    {
        get => _debugPropertiesSummary;
        private set => SetProperty(ref _debugPropertiesSummary, value);
    }

    public string DebugPropertiesFullText
    {
        get => _debugPropertiesFullText;
        private set => SetProperty(ref _debugPropertiesFullText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string BuildErrorSummary(string text)
    {
        return BuildPreview(text, DiagnosticPreviewLength);
    }

    private static string BuildPreview(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "-")
        {
            return "-";
        }

        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength] + "...";
    }
}
