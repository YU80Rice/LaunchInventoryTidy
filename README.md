# LaunchInventoryTidy

![LaunchInventoryTidy 封面](assets/launchinventorytidy-cover.png)

`LaunchInventoryTidy` 是 Unturned 的 BepInEx 5 背包手动整理插件。它在服务端权威路径上执行整理，并提供事务验证、失败回滚、快捷键恢复、请求准入、玩家级冷却与持久熔断保护。

当前源码与发布版本：**v3.0.1**

## 安装

下载 `publish/LaunchInventoryTidy.zip`，解压到 Unturned 游戏根目录。压缩包结构固定为：

```text
BepInEx/
  plugins/
    LaunchInventoryTidy.dll
```

本压缩包只包含 LIT 本体。请另行安装硬性前置 `LaunchMultiplayerNet.dll`，并放入同一 `BepInEx/plugins/` 目录。

## 前置与其它项目关系

| 项目 / 组件 | 与 LIT 的关系 | 运行时是否必需 |
|---|---|---:|
| [LaunchMultiplayerNet](https://github.com/YU80Rice/LaunchMultiplayerNet) | **硬依赖**。提供模组传输层；LIT 独占 Channel 100。最低版本 **4.0.0**。 | 是 |
| BepInEx 5 | 插件加载器。 | 是 |
| Harmony 2 | UI 注入与运行时 Patch 支持。 | 是 |
| Unturned 3.x 与其 Steamworks.NET 组件 | 游戏宿主与 API。 | 是 |
| [SteamP2PFriends](https://github.com/YU80Rice/SteamP2PFriends) | 可选 P2P Listen Host 协调器。仅在 P2P 中以软依赖、反射桥接方式初始化 LIT 的 P2P 熔断作用域；LIT 不引用它。 | 单机/U3DS 否 |
| [LaunchInPlaceReload](https://github.com/YU80Rice/LaunchInPlaceReload) | 独立兄弟插件，使用 LMN Channel 101；与 LIT 无运行时依赖。 | 否 |
| [LaunchHordeTracker](https://github.com/YU80Rice/LaunchHordeTracker) | 独立兄弟插件，使用 LMN Channel 102；与 LIT 无运行时依赖。 | 否 |
| [UnturnedModManager](https://github.com/YU80Rice/UnturnedModManager) | 可选启动/部署工具，不是 LIT 前置。 | 否 |
| LaunchTidyTestHarness / LaunchP2PDiagnostics | 仅用于测试和审计，绝不可随 LIT 发布包部署。 | 否 |

更完整的通道、依赖方向与部署边界请阅读 [DEPENDENCIES.md](DEPENDENCIES.md)。

## 当前环境状态

| 环境 | 状态 | 已有证据范围 |
|---|---|---|
| 单机 | 已验证 | 自动化覆盖物品守恒、快捷键恢复、故障隔离和关闭流程。 |
| U3DS 专用服务器 | 已验证 | 受控双端快照比对、快捷键恢复、冷却与部署恢复验证。 |
| Steam P2P / SteamP2PFriends Listen Host | Alpha，未正式发布 | v3.0.1 已加入作用域隔离与静态安全桥接；仍需完成 T1-T3 双机动态矩阵。 |

在 P2P 动态矩阵通过并获得独立审计结论前，禁止将 P2P 描述为正式支持或生产可用。

## 安全模型

- 整理事务在 Unity 游戏主线程中执行：`Prepare -> 指纹复核 -> Commit -> Verify/Rollback`。
- Commit 后若出现未知状态，插件 fail-closed 并打开熔断，不会基于猜测执行破坏性回滚。
- 请求受会话令牌、重放检查、玩家级 lease、原子准入和冷却限制保护。
- 持久熔断按运行模式、地图标识与存档槽位隔离；单机与 P2P 不共享熔断文件。
- 被动拾取整理已永久关闭；请通过整理 UI 或原生 Plugin 0 快捷键手动触发。

## 网络通道所有权

LIT 独占 LaunchMultiplayerNet 的 **Channel 100**，其它插件不得复用。

| Channel | 所有者 |
|---:|---|
| 100 | LaunchInventoryTidy |
| 101 | LaunchInPlaceReload |
| 102 | LaunchHordeTracker |
| 103+ | 使用前须在 LaunchMultiplayerNet 项目中登记分配 |

## 从源码构建

项目目标为 .NET Framework 4.7.2，开发环境需要在 `../Libs/` 提供 Unturned/BepInEx 引用程序集。

```powershell
dotnet build .\LaunchInventoryTidy.csproj -c Release -nologo
```

唯一可发布 DLL：

```text
bin/Release/LaunchInventoryTidy.dll
```

TestHarness、测试夹具、审计日志和本地引用 DLL 均不属于发布物。

## 版本与许可

- Assembly/File Version：`3.0.1.0`
- BepInEx 插件版本：`3.0.1`
- DLL 文件名始终为：`LaunchInventoryTidy.dll`（无版本后缀）
- 许可证：[MIT](LICENSE)

更新记录见 [CHANGELOG.md](CHANGELOG.md)，版本索引见 [mod_version_history.md](mod_version_history.md)。
