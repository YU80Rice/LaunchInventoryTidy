# 🎒 LaunchInventoryTidy

> Unturned 一键整理背包模组 · BepInEx 5 插件
> 基于 FFD/FFI 启发式 + DFS 回溯的 2D 装箱算法，支持被动整理与按键触发的手动整理，双端自适应联机。

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](./LICENSE)
[![.NET](https://img.shields.io/badge/.NET%20Framework-4.7.2-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Unturned](https://img.shields.io/badge/Unturned-3.x-59B200?logo=steam)](https://store.steampowered.com/app/304930/Unturned/)
[![BepInEx](https://img.shields.io/badge/BepInEx-5-FF7B00?logo=nuget)](https://github.com/BepInEx/BepInEx)
[![Harmony](https://img.shields.io/badge/Harmony-2-blue)](https://github.com/pardeike/Harmony)

🌐 **开源仓库**：[github.com/YU80Rice/LaunchInventoryTidy](https://github.com/YU80Rice/LaunchInventoryTidy)

---

## 📖 项目简介

`LaunchInventoryTidy` 是 [UMM 模组家族](https://github.com/YU80Rice/UnturnedModManager) 的成员之一，为 Unturned 玩家提供智能背包整理能力。模组自动重排玩家 5 个多格页（ITEMS / BACKPACK / VEST / SHIRT / PANTS）的物品布局，让背包瞬间井然有序。

### ✨ 核心功能

| 模式 | 触发 | 行为 |
|---|---|---|
| **被动整理** | 物品被添加到背包时（`Items.tryAddItem` Patch） | 自动重排该页，让新物品找到最优位置 |
| **被动整理** | 玩家打开背包 UI 时（`PlayerDashboardInventoryUI` Patch） | 一键重排所有 5 个多格页 |
| **手动整理** | Unturned 原生 **Plugin 0** 按键 | 整理全部 5 个多格页（需在 Settings → Controls 绑定） |

### 🧠 算法核心

`InventorySolver` 实现了 **FFD/FFI（First-Fit Decreasing/Increasing）+ DFS 回溯** 的 2D 装箱算法：

- **纯 C#**，零外部依赖，可独立单元测试
- **支持旋转**：自动尝试 rot=0 / rot=1 两种放置，正方形跳过旋转分支
- **排序策略**：面积 → 长边 → size_x（FFD 经典启发式）
- **回溯深度**：典型背包场景（≤50 物品）毫秒级完成
- **双向支持**：`sortDescending=true` 大件优先（默认）/ `false` 小件优先

### 🌐 双端自适应联机

基于 [LaunchMultiplayerNet](https://github.com/YU80Rice/LaunchMultiplayerNet) 前置库，使用 **Channel 100 (TidyPage)** 通信：

```
客机按下 Plugin 0
    └─> SendTidyPage(ALL_PAGES=0xFF, sortDescending=true)
            └─> 服务器收到包
                    └─> 反查 sender CSteamID -> Player
                            └─> ManualTidyService.TidyAllPlayerPages(player.inventory)
                                    └─> Items.removeItem/addItem 触发 onItemAdded/onItemRemoved
                                            └─> Unturned 原生网络同步自动推送回所有客机
```

**安全性**：服务器只对 sender 自己的 `Player.inventory` 操作，不会越权修改他人背包。

---

## 📦 项目结构

```
LaunchInventoryTidy/
├── LaunchInventoryTidy.csproj           # .NET Framework 4.7.2 库工程
├── LaunchInventoryTidyPlugin.cs         # BepInEx 插件入口
├── InventorySolver.cs                   # FFD+DFS 装箱算法（纯 C#，可单测）
├── ManualTidyService.cs                 # 整理服务：对 PlayerInventory 执行重排
├── ManualTidyNetwork.cs                 # P2P 网络层：Channel 100 协议
├── ManualTidyWatcher.cs                 # MonoBehaviour：监听 Plugin 0 按键 + 每帧 Poll
├── Patches/
│   ├── ItemsTryAddItemPatch.cs          # 被动整理：物品添加时重排
│   └── PlayerDashboardInventoryUIPatch.cs # 被动整理：打开背包 UI 时重排（IL emit）
├── Properties/AssemblyInfo.cs           # 程序集元数据 v1.0.0.0
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
| `LaunchMultiplayerNet.dll` | [LaunchMultiplayerNet](https://github.com/YU80Rice/LaunchMultiplayerNet) 仓库自行编译 |

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

1. **部署**：把编译好的 `LaunchInventoryTidy.dll` 放入 `<游戏目录>/BepInEx/plugins/`
   - UMM 启动器用户：启动器会自动部署依赖前置库
   - 独立 BepInEx 用户：需手动放入 `LaunchMultiplayerNet.dll` 到 `BepInEx/plugins/`
2. **绑定按键**：游戏内 `Settings → Controls → Plugin 0` 绑定一个按键（如 F1）
3. **触发整理**：打开背包 → 按下 Plugin 0 按键 → 背包瞬间整理完毕

### 房主 / 客机行为

| 角色 | 行为 |
|---|---|
| **房主**（`Provider.isServer=true`） | 直接在本地 `PlayerInventory` 上跑算法 |
| **客机**（`Provider.isServer=false`） | 通过 P2P 通道 100 发送请求，服务器代为执行，原生事件链自动同步回所有客机 |

---

## 🔗 联动项目

### 🚀 Unturned Mod Manager (UMM) 主仓库

🌐 **https://github.com/YU80Rice/UnturnedModManager**

UMM 启动器是本插件的"宿主启动器"，提供 BepInEx 一键部署 + DXVK 极速优化 + 一键启动游戏能力。

> 💡 **独立部署说明**：即使没有 UMM 启动器，只要玩家本地已有现成的 **BepInEx 5** 环境，也可以直接把 `LaunchInventoryTidy.dll` + `LaunchMultiplayerNet.dll` 放入 `BepInEx/plugins/` 即可使用。

### 🌐 LaunchMultiplayerNet 前置库

🌐 **https://github.com/YU80Rice/LaunchMultiplayerNet**

本插件依赖此前置库实现双端自适应联机。Channel 100 (`ModChannels.TidyPage`) 由本插件独占。

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
