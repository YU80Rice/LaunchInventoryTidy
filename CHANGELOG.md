# 📒 更新日志 (Changelog)

本文件记录 LaunchInventoryTidy 模组的所有版本变更。
版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

---

## [v1.4.0] - 2026/07/16

### 🎉 MaxRects 装箱算法 + C/D 模式切换按钮

#### ✨ 新增功能

##### MaxRects 算法（C 优先级，剩余大矩形优先）
- **`InventorySolver.TryPackMaxRects`**：标准 MaxRects 实现
- **BSSF（Best Short Side Fit）**：短边剩余最小者优先，tie-break 取长边剩余最小
- **矩形分裂**：放置物品后把被占用的矩形分裂为最多 4 个新剩余矩形（上/下/左/右）
- **`PruneContainedRects`**：清理被包含的矩形，避免列表膨胀
- **保证剩余空间为若干大矩形而非碎片**，适合需要保留大连续空间的场景

##### TidyMode 枚举
```csharp
public enum TidyMode : byte
{
    MaxRects = 0,  // C: 剩余大矩形优先
    FFD = 1,       // D: 大件优先贪心
}
```

##### UI 模式切换按钮
- 每个多格页标题栏新增 `[C]/[D]` 按钮（位于 `[↓]/[↑]` 按钮左侧）
- 每页独立记忆模式状态，默认 C（MaxRects）
- 容器页（STORAGE）使用专用布局 `STORAGE_MODE_POS_OFFSET_X=-330f` 避让 rot_x/y/z 按钮
- 新增 `s_PageTidyMode` / `s_ModeButtons` 字典
- 新增 `HandleModeClick` 方法，`CreatePageDelegate` 改用 `ButtonKind` 枚举

##### 网络协议扩展
- 协议追加 `[mode: byte]` 字段（在 sortDescending 后）
- `SendTidyAllRequest` / `SendTidyPageRequest` 加 `TidyMode mode` 参数
- 服务器端 `HandleRequestTidyPage` 读取 mode（带 try-catch 向后兼容 v1.3）

#### 🔧 改进

##### 调用方透传 mode 参数
- `ManualTidyService` 4 个方法签名加 `TidyMode mode = TidyMode.MaxRects`
- `ItemsTryAddItemPatch` 被动整理恒用 `TidyMode.MaxRects`（C 优先）
- `ManualTidyWatcher` Plugin 0 按键恒用 `TidyMode.MaxRects`

##### 诊断日志（容器页整理问题排查）
- `ManualTidyNetwork.HandleRequestTidyPage` 打印 items 实际状态（width/height/count）
- `ManualTidyService.TidyPage` 把静默 return 改为带日志 return，便于排查"容器页整理无效"类问题

#### 📐 布局常量
- 玩家页：`MODE_POS_OFFSET_X=-220f` / `MODE_SIZE_X=40f`
- 容器页：`STORAGE_MODE_POS_OFFSET_X=-330f`
- 视觉顺序：`[C/D] 5px [↓/↑] 5px [整理] 70px/180px 安全区`

#### 📦 版本号
- `AssemblyVersion` / `AssemblyFileVersion`: 1.4.0.0
- BepInEx 插件名: `LaunchInventoryTidy [v1.4 v3.2 网络层适配 + MaxRects]`

---

## [v1.3.0] - 2026/07/15

### 🔧 v3.2 网络层适配 + NRE 修复

#### ✨ 适配 LaunchMultiplayerNet v3.2
- 新增 `[BepInDependency(LaunchMultiplayerNetPlugin.Guid, HardDependency)]` 声明
- 移除手动 `ModTransport.Initialize()` 调用（v3.0+ 由 LaunchMultiplayerNetPlugin.Awake 自动初始化）
- 仅注册本插件的服务器端通道处理器（`ManualTidyNetwork.RegisterHandlers`）

#### 🐛 Bug 修复

##### SteamPlayerID == 运算符 NRE 陷阱
- **根因**：`SteamPlayerID.cs:136-139` 重载了 `==`/`!=` 运算符但未做 null 检查，`sp.playerID == null` 会触发 NRE
- **修复**：`ManualTidyNetwork.ResolvePlayerBySteamId` 改用 `ReferenceEquals(pid, null)` 判空，并用局部变量 `pid` 避免双重属性访问

##### Provider.isServer 死分支移除
- **根因**：v2.0 弃用 listen server 后，房主 = 普通客户端，`Provider.isServer` 在 dedicated server 模式下恒为 false
- **修复**：`PlayerDashboardInventoryUIPatch.HandleTidyClick` 移除 `if (Provider.isServer)` 分支，统一走网络请求路径

#### 📦 版本号
- `AssemblyVersion` / `AssemblyFileVersion`: 1.3.0.0
- BepInEx 插件名: `LaunchInventoryTidy [v1.3 v3.2 网络层适配]`

---

## [v1.2.0] - 2026/07/15

### 🔧 v3.0 网络层适配

#### ✨ 适配 LaunchMultiplayerNet v3.0
- 协议改为 vanilla SteamChannel(id=200) + `[SteamCall]` RPC 框架
- `ManualTidyNetwork` 改用 `ModTransport.SendToServer` / `RegisterServerHandler` API
- 移除 SteamNetworking P2P 直发逻辑

#### 🐛 Bug 修复
- 修复 listen server 模式下 SDR 路由不可用导致客机请求无法抵达服务器的问题（v3.0 改用 vanilla SteamChannel 绕过）

#### 📦 版本号
- `AssemblyVersion` / `AssemblyFileVersion`: 1.2.0.0

---

## [v1.1.0] - 2026/07/14

### 🔧 v2.x 网络层适配（已废弃）

> ⚠️ 本版本对应的 LaunchMultiplayerNet v2.x 架构（SteamNetworking P2P）已在 v3.0 弃用

#### ✨ 适配 LaunchMultiplayerNet v2.x
- 改用 SteamNetworking P2P 直发架构
- `ManualTidyWatcher` 加 Poll 机制轮询 P2P 消息

#### 🐛 已知问题（v3.0 修复）
- listen server 模式下 SDR 路由不可用
- dedicated server 模式下未验证

---

## [v1.0.0] - 2026/07/14

### 🎉 首次开源发布

LaunchInventoryTidy 作为 UMM 模组家族成员，首次开源至 GitHub。

### ✨ 核心功能

#### 装箱算法 (`InventorySolver.cs`)

- **FFD/FFI 启发式 + DFS 回溯 2D 装箱**：纯 C# 实现，零外部依赖，可独立单元测试
- **支持旋转**：自动尝试 rot=0 / rot=1 两种放置，正方形跳过旋转分支减少分支因子
- **排序策略**：面积 -> 长边 -> size_x（FFD 经典三段 tie-break）
- **双向支持**：`sortDescending=true` 大件优先（默认）/ `false` 小件优先
- **快速粗筛**：面积总和超网格容量立即失败，避免无谓回溯
- **性能**：典型背包场景（≤50 物品）毫秒级完成

#### 被动整理 (Harmony Patches)

- `ItemsTryAddItemPatch`：物品被添加到背包时自动重排该页
- `PlayerDashboardInventoryUIPatch`：玩家打开背包 UI 时重排所有 5 个多格页（425 行 IL emit）

#### 手动整理

- 监听 Unturned 原生 **Plugin 0** 按键
- 房主：直接执行算法
- 客机：通过 P2P 通道 100 发送整理请求

#### 双端自适应网络层 (`ManualTidyNetwork.cs`)

- 协议：Channel 100 = `ModChannels.TidyPage`
- 客机 -> 服务器：`[RequestTidyPage: byte][page: byte][sortDescending: bool]`
  - `page = 0xFF`：整理全部 5 个多格页（SLOTS..PANTS）
  - `page ∈ [2..6]`：仅整理指定页
- 服务器端：通过 sender CSteamID 在 `Provider.clients` 反查 Player，调用 `ManualTidyService`
- 安全性：服务器只对 sender 自己的 `Player.inventory` 操作，不越权

### 🏗️ 设计原则

- **算法层纯净**：`InventorySolver` 不引用 Unity / Unturned 类型，便于单元测试与跨项目复用
- **被动+手动双轨**：被动整理覆盖拾取/拖拽场景，手动整理提供显式控制
- **双端一致性**：客机不需要本地物品状态，所有权威修改在服务器端完成，原生事件链自动同步

### 📦 构建产物

- `LaunchInventoryTidy.dll` - BepInEx 插件
- 目标框架：.NET Framework 4.7.2
- 部署路径：`BepInEx/plugins/`
- 依赖前置库：`LaunchMultiplayerNet.dll`（v1.1.1+，现已要求 v3.2+）

### 🔒 已知限制

- 整理过程是**服务器权威**的：客机发送请求后需等待服务器处理 + 网络同步回程
- 装箱算法为 NP-hard，极端情况（>100 物品 + 网格紧张）可能耗时较长，但有快速粗筛兜底
- 被动整理 Patch 通过 IL emit 修改 `PlayerDashboardInventoryUI` 构造函数，对 Unturned 版本敏感
