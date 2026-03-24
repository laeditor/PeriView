# PeriView

Windows 本地外设状态监测应用（WPF / .NET 8）。

## 当前能力

- 蓝牙设备枚举（BLE + Classic）
- 电量读取（优先 BLE 标准电池服务，含多级回退探测）
- 主界面设备状态列表
- 最小化到托盘
- 托盘显示首个设备电量摘要

## 构建

```bash
dotnet build PeriView.sln
```
