# 📒 更新日志 (Changelog)

本文件记录 LaunchInventoryTidy 模组的所有版本变更。
版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

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
- 依赖前置库：`LaunchMultiplayerNet.dll`（v1.1.1+）

### 🔒 已知限制

- 整理过程是**服务器权威**的：客机发送请求后需等待服务器处理 + 网络同步回程
- 装箱算法为 NP-hard，极端情况（>100 物品 + 网格紧张）可能耗时较长，但有快速粗筛兜底
- 被动整理 Patch 通过 IL emit 修改 `PlayerDashboardInventoryUI` 构造函数，对 Unturned 版本敏感
