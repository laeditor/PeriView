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

## 开发与运行

```powershell
dotnet restore
dotnet build
dotnet run --project .\PeriView.App\PeriView.App.csproj
```

如果遇到构建时 apphost 被占用（MSB3027 / MSB3021），可先使用以下命令验证代码是否可编译：

```powershell
dotnet build -p:UseAppHost=false
```

## 后续规划

- 增加更多厂商 2.4G 设备电量解析能力
- 扩展更多外设状态（连接质量、充电状态、信号强度等）
- 增加设备识别规则与缓存策略可配置化


