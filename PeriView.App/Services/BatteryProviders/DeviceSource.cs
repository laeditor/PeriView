namespace PeriView.App.Services.BatteryProviders;

/// <summary>
/// 设备来源类型
/// </summary>
public enum DeviceSource
{
    /// <summary>
    /// Windows设备属性
    /// </summary>
    WindowsProperties,
    
    /// <summary>
    /// HID设备直接通信
    /// </summary>
    Hid,
    
    /// <summary>
    /// Xbox游戏控制器
    /// </summary>
    Xbox,
    
    /// <summary>
    /// 蓝牙GATT服务
    /// </summary>
    BluetoothGatt,
    
    /// <summary>
    /// 音频设备
    /// </summary>
    AudioDevice,
    
    /// <summary>
    /// 未知来源
    /// </summary>
    Unknown
}