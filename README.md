# 🎒 LaunchInventoryTidy

> Unturned 一键整理背包模组 · BepInEx 5 插件
> v1.4：MaxRects 装箱算法 + C/D 模式切换按钮 + 双端自适应联机

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)
[![Version](https://img.shields.io/badge/version-1.4.0-blue.svg)](./CHANGELOG.md)
[![.NET](https://img.shields.io/badge/.NET%20Framework-4.7.2-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Unturned](https://img.shields.io/badge/Unturned-3.x-59B200?logo=steam)](https://store.steampowered.com/app/304930/Unturned/)
[![BepInEx](https://img.shields.io/badge/BepInEx-5-FF7B00?logo=nuget)](https://github.com/BepInEx/BepInEx)
[![Harmony](https://img.shields.io/badge/Harmony-2-blue)](https://github.com/pardeike/Harmony)

🌐 **开源仓库**：[github.com/YU80Rice/LaunchInventoryTidy](https://github.com/YU80Rice/LaunchInventoryTidy)

---

## 📌 前置与联动声明

本插件是 [UMM 模组家族](https://github.com/YU80Rice/UnturnedModManager) 的成员之一，与其他家族成员存在明确的前置依赖与通道占用关系。**部署前请务必阅读本节。**

### 🔧 前置依赖（必装）

| 依赖 | 版本 | 仓库 | 用途 |
|---|---|---|---|
| **LaunchMultiplayerNet** | **v3.2+** | [github.com/YU80Rice/LaunchMultiplayerNet](https://github.com/YU80Rice/LaunchMultiplayerNet) | 双端自适应网络传输层（本插件独占 Channel 100） |
| BepInEx | 5.x | [github.com/BepInEx/BepInEx](https://github.com/BepInEx/BepInEx) | 模组加载器 |
| Harmony | 2.x | [github.com/pardeike/Harmony](https://github.com/pardeike/Harmony) | 运行时方法注入（被动整理 Patch） |
| Unturned | 3.x | [store.steampowered.com/app/304930](https://store.steampowered.com/app/304930/Unturned/) | 宿主游戏 |
| Steamworks.NET | - | [github.com/rlabrecque/Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET) | P2P 传输底层（随 Unturned 分发） |

> ⚠️ **LaunchMultiplayerNet v3.2+ 是硬性前置**：
> - v3.0 重构为独立 BepInEx 插件模式（`[BepInDependency(HardDependency)]` 自动加载）
> - v3.2 修复 `SteamPlayerID ==` 运算符 NRE 陷阱（必须用 `ReferenceEquals` 判空）
> - v3.2 修复 dedicated server 模式下客户端 -> 服务器方向的网络路由
>
> 前置库文件名必须为裸名 `LaunchMultiplayerNet.dll`（不带版本后缀），详见 [前置库仓库](https://github.com/YU80Rice/LaunchMultiplayerNet) 的"裸名铁律"。

### 🌐 联动项目矩阵

| 项目 | 角色 | 仓库 | 与本插件的关系 |
|---|---|---|---|
| **Unturned Mod Manager** | 宿主启动器（可选，推荐） | [github.com/YU80Rice/UnturnedModManager](https://github.com/YU80Rice/UnturnedModManager) | 一键部署 BepInEx 核心 + 性能优化模组；**本插件与前置库需手动放入 plugins/** |
| **LaunchMultiplayerNet** | 前置库（必装） | [github.com/YU80Rice/LaunchMultiplayerNet](https://github.com/YU80Rice/LaunchMultiplayerNet) | 提供 Channel 100 通信基建 + 双端 RPC 框架 |
| **LaunchInPlaceReload** | 兄弟模组 | [github.com/YU80Rice/LaunchInPlaceReload](https://github.com/YU80Rice/LaunchInPlaceReload) | 占用 Channel 101 (`RepackAmmo`)，与本插件无通道冲突 |
| **LaunchHordeTracker** | 兄弟模组 | [github.com/YU80Rice/LaunchHordeTracker](https://github.com/YU80Rice/LaunchHordeTracker) | 占用 Channel 102 (`HordeStatus`)，与本插件无通道冲突 |

### 📡 通道占用声明

本插件在 `LaunchMultiplayerNet.ModChannels` 中**独占 Channel 100**：

```csharp
public const int TidyPage = 100;  // ← 本插件独占
```

子消息类型（`EModMessage`）：

| 子消息 | 值 | 方向 | 协议字节序 |
|---|---|---|---|
| `RequestTidyPage` | `1` | 客机 -> 服务器 | `[page:byte][sortDescending:bool][mode:byte]` |

**mode 字段**（v1.4 新增）：
- `0` = MaxRects（C 优先级，剩余大矩形优先）
- `1` = FFD（D 优先级，大件优先贪心）

服务器端读取 mode 时带 try-catch，v1.3 客户端（无 mode 字节）会被识别并回退 MaxRects。

**通道分配规则**：其他模组请从 Channel 103 起分配，详见 [LaunchMultiplayerNet 仓库的 `ModChannels.cs`](https://github.com/YU80Rice/LaunchMultiplayerNet/blob/main/ModChannels.cs)。

### 💡 部署路径

```
<Unturned 游戏目录>/
└── BepInEx/
    └── plugins/
        ├── LaunchMultiplayerNet.dll   ← 前置库 v3.2+（必装）
        ├── LaunchInventoryTidy.dll     ← 本插件 v1.4
        ├── LaunchInPlaceReload.dll     ← 兄弟模组（可选）
        └── LaunchHordeTracker.dll      ← 兄弟模组（可选）
```

> 💡 **独立部署说明**：即使没有 [UMM 启动器](https://github.com/YU80Rice/UnturnedModManager)，只要玩家本地已有现成的 **BepInEx 5** 环境，也可以直接把 `LaunchInventoryTidy.dll` + `LaunchMultiplayerNet.dll` 放入 `BepInEx/plugins/` 即可使用。
>
> ⚠️ **关于 UMM 启动器的自动部署范围**：UMM 启动器**仅自动部署 BepInEx 核心与 2 个性能优化模组**（`WaterPerfOptimizer.dll` / `LaunchPerfOptimizer.dll`）。**本插件 `LaunchInventoryTidy.dll` 与前置库 `LaunchMultiplayerNet.dll` 不在自动部署清单内**，无论是否使用 UMM 启动器，这两个 DLL 都需要用户手动放入 `BepInEx/plugins/` 目录。

---

## 📖 项目简介

`LaunchInventoryTidy` 是 [UMM 模组家族](https://github.com/YU80Rice/UnturnedModManager) 的成员之一，为 Unturned 玩家提供智能背包整理能力。模组自动重排玩家 5 个多格页（ITEMS / BACKPACK / VEST / SHIRT / PANTS）以及 STORAGE 容器页（储物箱/展示柜/车辆后备箱）的物品布局。

### ✨ 核心功能

| 入口 | 触发 | 行为 |
|---|---|---|
| **被动整理** | 物品被添加到背包时（`Items.tryAddItem` Patch） | 自动重排该页，恒用 C 模式（MaxRects） |
| **[整理] 按钮** | 标题栏点击 | 整理当前页；`Ctrl + 点击` 一键整理全身 |
| **[C]/[D] 按钮** | 标题栏点击 | 切换该页整理模式（每页独立记忆） |
| **[↓]/[↑] 按钮** | 标题栏点击 | 切换该页排序方向（每页独立记忆） |
| **Plugin 0 按键** | Unturned 原生 Plugin 0 键 | 整理全身（恒用 C 模式 + 降序） |

### 🧠 算法核心（v1.4 新增 MaxRects）

`InventorySolver` 支持两种 2D 装箱模式，可通过 UI 按钮实时切换：

#### C 模式 - MaxRects（默认，剩余大矩形优先）
- 维护剩余矩形列表，初始为整个网格
- 放置物品时用 **BSSF（Best Short Side Fit）** 选最佳矩形：短边剩余最小者优先，tie-break 取长边剩余最小
- 放置后**分裂被占用的矩形**为最多 4 个新剩余矩形（上/下/左/右）
- `PruneContainedRects` 清理被包含的矩形
- **保证剩余空间为若干大矩形而非碎片**，适合需要保留大连续空间的场景

#### D 模式 - FFD（大件优先贪心）
- 贪心 First-Fit 行主序扫描（y 外 x 内）
- 大件优先，快速放置
- 剩余空间易碎片化（v1.3 前的默认行为）

两种模式共用：
- **纯 C#**，零外部依赖，可独立单元测试
- **支持旋转**：自动尝试 rot=0 / rot=1 两种放置，正方形跳过旋转分支
- **排序策略**：面积 -> 长边 -> size_x（稳定 tie-break）
- **异常物品处理**：size=0 或尺寸超容器的物品 Placed=false，调用方保留原位

### 🌐 双端自适应联机

基于 [LaunchMultiplayerNet](https://github.com/YU80Rice/LaunchMultiplayerNet) v3.2+ 前置库，使用 **Channel 100 (TidyPage)** 通信：

```
客机点击整理按钮 / 按 Plugin 0
    └─> ManualTidyNetwork.SendTidyPageRequest(page, desc, mode)
            └─> LaunchMultiplayerNet 通道 100 发往 U3DS 服务器
                    └─> 服务器收到包，反查 sender CSteamID -> Player
                            └─> ManualTidyService.TidyPage(player.inventory.items[page], ...)
                                    └─> Items.removeItem/addItem 触发 onItemAdded/onItemRemoved
                                            └─> Unturned 原生网络同步自动推送回所有客机
```

**v2.0+ 架构变更**：弃用 listen server，所有玩家（含房主）都作为普通客户端，请求统一发往 U3DS dedicated server。房主不再走本地直接执行路径。

**安全性**：服务器只对 sender 自己的 `Player.inventory` 操作，不会越权修改他人背包。

---

## 📦 项目结构

```
LaunchInventoryTidy/
├── LaunchInventoryTidy.csproj           # .NET Framework 4.7.2 库工程
├── LaunchInventoryTidyPlugin.cs         # BepInEx 插件入口 v1.4.0
├── InventorySolver.cs                   # 装箱算法（MaxRects + FFD，纯 C# 可单测）
├── ManualTidyService.cs                 # 整理服务：4 方法签名带 mode 参数
├── ManualTidyNetwork.cs                 # 网络层：Channel 100 协议含 mode 字节
├── ManualTidyWatcher.cs                 # MonoBehaviour：监听 Plugin 0 按键
├── Patches/
│   ├── ItemsTryAddItemPatch.cs          # 被动整理：物品添加时重排（恒用 MaxRects）
│   └── PlayerDashboardInventoryUIPatch.cs # UI 注入：[C]/[↓]/[整理] 三按钮
├── Properties/AssemblyInfo.cs           # 程序集元数据 v1.4.0.0
├── LICENSE                              # MIT
├── README.md                            # 本文件
├── CHANGELOG.md                         # 版本演进
└── CONTRIBUTING.md                      # Vibecoding 声明与致谢
```

---

## 🔧 构建要求

- **目标框架**：.NET Framework 4.7.2（与 Unturned 游戏一致）
- **IDE**：Visual Studio 2019 / 2022 / JetBrains Rider
- **依赖 DLL**（不随源码分发，需自行从 Unturned 游戏目录或对应仓库提取）：

| DLL | 来源 |
|---|---|
| `Assembly-CSharp.dll` | Unturned 游戏目录 `/Unturned_Data/Managed/` |
| `UnityEngine.dll` | Unturned 游戏目录 `/Unturned_Data/Managed/` |
| `UnityEngine.CoreModule.dll` | Unturned 游戏目录 `/Unturned_Data/Managed/` |
| `com.rlabrecque.steamworks.net.dll` | [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET) |
| `BepInEx.dll` | [BepInEx 5](https://github.com/BepInEx/BepInEx) |
| `0Harmony.dll` | [Harmony 2](https://github.com/pardeike/Harmony) |
| `LaunchMultiplayerNet.dll` | [LaunchMultiplayerNet](https://github.com/YU80Rice/LaunchMultiplayerNet) 仓库自行编译 v3.2+ |

### 📂 Libs 目录配置

本工程的 `.csproj` 中 `<HintPath>` 默认指向 `..\Libs\*.dll`，即与本仓库**同级目录**的 `Libs/` 文件夹：

```
some-folder/
├── LaunchInventoryTidy/        ← 本仓库
└── Libs/                      ← 把上面 7 个 DLL 放在这里
    ├── Assembly-CSharp.dll
    ├── UnityEngine.dll
    ├── UnityEngine.CoreModule.dll
    ├── com.rlabrecque.steamworks.net.dll
    ├── BepInEx.dll
    ├── 0Harmony.dll
    └── LaunchMultiplayerNet.dll
```

> 如果你的目录结构不同，请修改 `.csproj` 中 `<HintPath>` 节点的相对路径。

---

## 🚀 使用方式

### 玩家侧

1. **部署**：把编译好的 `LaunchInventoryTidy.dll` **和** `LaunchMultiplayerNet.dll` v3.2+（前置库）一起放入 `<游戏目录>/BepInEx/plugins/`
   - **UMM 启动器用户**：启动器仅自动部署 BepInEx 核心与性能优化模组，**本插件与 `LaunchMultiplayerNet.dll` 不在自动部署清单内，仍需手动放入 `BepInEx/plugins/`**
   - **独立 BepInEx 用户**：同样需要手动放入上述两个 DLL
   - 两个 DLL 缺一不可：缺前置库则客机整理键无响应，缺本插件则按键完全无效
2. **打开背包**：每个多格页标题栏右侧出现三个按钮：`[C] [↓] [整理]`
   - `[C]`/`[D]`：切换整理模式（每页独立记忆，默认 C）
   - `[↓]`/`[↑]`：切换排序方向（每页独立记忆，默认 ↓ 降序）
   - `[整理]`：整理当前页；`Ctrl + 整理` 一键整理全身
3. **Plugin 0 按键**：游戏内 `Settings -> Controls -> Plugin 0` 绑定按键后，按该键整理全身（恒用 C 模式 + 降序）

### 房主 / 客机行为

v2.0+ 架构：所有玩家（含房主）统一走网络请求路径。

| 角色 | 行为 |
|---|---|
| **所有玩家** | 通过 Channel 100 发送整理请求 -> U3DS 服务器代为执行 -> 原生事件链自动同步回所有客户端 |

### 🎯 容器页（STORAGE）支持

v1.4 支持整理打开的容器（储物箱/展示柜/车辆后备箱）：
- 打开容器后，容器页标题栏同样出现 `[C] [↓] [整理]` 三按钮（布局让出右侧 180px 避让 rot_x/y/z 按钮）
- 服务器端通过 `player.inventory.items[STORAGE]` 拿到 `InteractableStorage._items` 引用（同一实例）
- 修改后触发 `BarricadeManager.updateState` 自动同步给所有客机

**已知限制**：纯客户端虚拟容器（不走标准 `openStorage` 路径的工坊物品）服务器端 `items[STORAGE]` 为 0×0，整理无效。v1.4 已加诊断日志便于排查。

---

## 📜 版本协议

- **MIT 协议开源**：自由使用、修改、分发、商业利用
- **《未转变者》(Unturned) 版权归 Smartly Dressed Games 所有**
- 本模组仅为玩家社区的非官方辅助工具，不包含任何游戏资产，不修改游戏可执行文件
- 所有 BepInEx 插件均以 `.dll` 独立文件形式部署，可随时通过 `.disabled` 后缀停用或物理删除

---

## 🤝 致谢

本项目延续了 [UMM 主仓库](https://github.com/YU80Rice/UnturnedModManager) 的 Vibecoding 协作范式与致谢体系。

🙏 **完整 Vibecoding 声明与六位关键贡献者致谢**：详见 [CONTRIBUTING.md](./CONTRIBUTING.md)
