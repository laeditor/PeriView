# PeriView

Windows 本地外设状态监测应用（WPF / .NET 8）。

## 当前能力

- 蓝牙设备枚举（BLE + Classic）
- 蓝牙电量读取（优先 BLE 标准电池服务，含多级回退探测）
- 2.4G 设备电量读取路由（按厂商/设备类型分发到不同 Provider）
- 主界面设备状态列表
- 最小化到托盘
- 托盘显示首个设备电量摘要

## 项目结构

- `PeriView.App/Services/`：状态采集与聚合服务
- `PeriView.App/Services/BatteryProviders/`：不同来源的电量 Provider（Windows 属性、HID、设备专用 Provider）
- `PeriView.App/ViewModels/`：主界面与设备项的 MVVM 逻辑

