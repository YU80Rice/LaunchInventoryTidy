# AUDIT_CHECKLIST.md - LaunchInventoryTidy v1.4.1 外部审查清单

**生成时间**：2026-07-28
**插件版本**：LaunchInventoryTidy v1.4.1.0
**审计阶段**：v1.4.0 BUG 止损 + v1.4.1 紧急修复
**外部审计状态**：静态修复实现通过，允许进入动态测试阶段；不允许发布 v1.4.1

---

## 1. 功能概述

### 本次开发/修改的核心功能和目标

**v1.4.0 -> v1.4.1 紧急止损**：禁用 v1.4.0 引入的被动整理 Patch（`ItemsTryAddItemPatch`），消除社区反馈的 3 个 BUG 触发路径。

**社区反馈的 3 个 BUG**：
1. **BUG-1**：背包满 + 手上有枪，捡地上的枪被"吞掉"
2. **BUG-2**：装备栏 + 背包都满，捡地上的枪有收起动作但无替换
3. **BUG-3**：背包空间不够时合成物品被"吞掉"

**v1.4.0 被动整理 Patch 的根本问题**：
- `InventorySolver.TryPack` 把"尺寸超过当前页面"的新物品排除在 `validCount` 外
- 即使新物品 `Placed=false`，`placedCount == validCount` 仍可能成立，TryPack 返回 true
- Patch 对 `Tag=null` 的新物品没有原位可恢复，`tryFindSpace` 再次失败后静默跳过
- Patch 仍返回 `__result=true`，违反 `tryAddItem` 成功契约
- vanilla 调用方（`ItemManager`/`forceAddItem`）据此销毁地面物品或跳过 `dropItem` 兜底

**v1.4.1 修复方式**：移除 `[HarmonyPatch]` 特性 + 清空类体，`HarmonyInstance.PatchAll()` 不会发现此类，Patch 不会被注册。

---

## 2. 代码变更清单（Diff Checklist）

### 新建文件

| 文件 | 说明 |
|---|---|
| `.audit/v1.4.0-bug-analysis-20260716/items-tryadditem-bug-analysis-v2.md` | v2 修正版分析报告，按外部审计意见改正 v1 报告的所有错误结论 |
| `.audit/v1.4.0-bug-analysis-20260716/v1.4.1-release-gate-checklist.md` | 8 项发布门槛验证报告，含反射证据 + 动态测试矩阵 |
| `AUDIT_CHECKLIST.md` | 本文件，项目根目录外部审查清单 |

### 修改文件

| 文件 | 改动摘要 |
|---|---|
| `Patches/ItemsTryAddItemPatch.cs` | 移除 `[HarmonyPatch]` 特性 + 清空类体（Prefix/Postfix 全部删除），仅保留历史背景注释；引用指向 v2 修正版报告；"可能多次 dequip 动画" 改为 "可能触发 dequip" |
| `Properties/AssemblyInfo.cs` | `AssemblyVersion` / `AssemblyFileVersion` 从 `1.4.0.0` 升级到 `1.4.1.0` |
| `LaunchInventoryTidyPlugin.cs` | `[BepInPlugin]` 名称改为 `LaunchInventoryTidy [v1.4.1 v3.2 网络层适配 + MaxRects + 被动整理已禁用]`；加载日志同步更新；新增"被动整理 Patch 已禁用"提示行 |
| `README.md` | 头部 badge 升级到 v1.4.1；新增 v1.4.1 紧急止损声明；核心功能表标注被动整理已禁用；新增残余风险声明段落；Harmony 用途改为"运行时 UI 注入；被动整理 Patch 已禁用"；"单机模式下可正常使用手动整理" 改为"单机风险低于联机同步场景，但仍需备份并核对物品" |
| `CHANGELOG.md` | 追加 v1.4.1 完整条目（背景/根因/改动/残余风险/未实施的设计/8 项发布门槛）；"可能多次 dequip 动画" 改为 "可能触发 dequip" |

### 标记为 SUPERSEDED 的文件

| 文件 | 说明 |
|---|---|
| `.audit/v1.4.0-bug-analysis-20260716/items-tryadditem-bug-analysis.md` | v1 原始报告，已被 v2 取代。文件头部已加 `⚠️ SUPERSEDED` 标记，列出 6 项被审计驳回的错误结论 |

### 未修改文件（无功能性变更）

| 文件 | 说明 |
|---|---|
| `InventorySolver.cs` | MaxRects 算法与 FFD 逻辑保持不变。审计识别的 `validCount` 排除超尺寸物品的确定性 bug 路径在禁用 Patch 后无法被触发，不需要修改算法 |
| `ManualTidyService.cs` | 手动整理路径保持原状（残余风险声明已就位） |
| `ManualTidyNetwork.cs` | 协议保持 v1.4 的 `[mode:byte]` 字段，无变更 |
| `ManualTidyWatcher.cs` | Plugin 0 按键处理保持原状 |
| `Patches/PlayerDashboardInventoryUIPatch.cs` | UI 按钮注入保持原状（[C]/[↓]/[整理] 三按钮） |
| `LaunchInventoryTidy.csproj` | `<Deterministic>true</Deterministic>` 已启用，相同源码产出相同哈希 |

---

## 3. 架构合规性说明

### 改动契合现有项目架构

1. **保留 Harmony 注册流程**：`LaunchInventoryTidyPlugin.Awake` 仍调用 `HarmonyInstance.PatchAll()`，未修改 BepInEx 插件骨架。`PatchAll` 通过反射扫描带 `[HarmonyPatch]` 特性的类型，被禁用的 `ItemsTryAddItemPatch` 既无特性又无 Patch 方法，自然不会被登记，无需修改 `Awake` 逻辑。

2. **UI 与网络层零侵入**：手动整理路径（UI 按钮 + Plugin 0 + Channel 100 网络协议）保持 v1.4 原状，避免连带回归风险。

3. **版本号语义合规**：v1.4.0 -> v1.4.1 符合 SemVer PATCH 段升级（紧急止损不改变 API/协议，仅修复 BUG）。

4. **审计文档独立存放**：v2 分析报告 + 发布门槛清单放在 `.audit/v1.4.0-bug-analysis-20260716/`，不污染源码目录，便于外部审计员检索。

5. **旧版报告 SUPERSEDED 标记**：v1 报告未删除，仅在文件头部加显著标记 + 错误结论列表，保留审计链路可追溯性。

### 没有引入"脏代码"

- 未使用 `[if condition] return false;` 之类的运行时开关绕过 Patch（审计明确指出此方式保留误触发面）
- 未保留 Prefix 方法但加 `return true` 跳过（仍是 Patch 注册）
- 未注释 `[HarmonyPatch]` 特性（注释会被编译器忽略，但显式删除更彻底）
- 选择"彻底删除特性 + 清空类体"是最干净的方式：编译产物元数据层即证明无 Patch

---

## 4. 编译与运行环境验证记录

| 项目 | 值 |
|---|---|
| 编译命令 | `dotnet build -c Release -nologo` |
| 编译目录 | `D:/Agent-工作目录/DevelopMyUNMultiplayerModAndModloader/LaunchInventoryTidy` |
| 目标框架 | .NET Framework 4.7.2 |
| 编译耗时 | 00:00:01.64（注释修正后重建） |
| 错误数 | 0 |
| 警告数 | 0 |
| 输出 DLL | `bin/Release/LaunchInventoryTidy.dll` |
| DLL 大小 | 30,208 字节 |
| DLL SHA-256（脏工作树） | `35f4eeacd77e2a77d02e3a6343880444d2a248a94c0c697bc0c98d29c99db228` |
| 确定性编译 | `<Deterministic>true</Deterministic>` 已启用 |
| 编译时间 | 2026-07-28（本地时区） |
| AssemblyVersion | 1.4.1.0 |
| AssemblyFileVersion | 1.4.1.0 |
| BepInPlugin 名称 | `LaunchInventoryTidy [v1.4.1 v3.2 网络层适配 + MaxRects + 被动整理已禁用]` |

### 反射元数据验证证据

使用 PowerShell + `System.Reflection.Assembly.LoadFile` 加载 DLL，对 `LaunchInventoryTidy.Patches.ItemsTryAddItemPatch` 类型执行元数据检查：

```
[INFO] Type found: LaunchInventoryTidy.Patches.ItemsTryAddItemPatch
[INFO] Custom attributes count: 0
[INFO] Declared methods count: 0
[PROOF] HarmonyPatch attribute NOT present on ItemsTryAddItemPatch
```

**判定**：
- `GetCustomAttributesData()` 返回 0 个特性 -> `[HarmonyPatch]` 已移除
- `GetMethods(BindingFlags.Public|NonPublic|Static|Instance|DeclaredOnly)` 返回 0 个方法 -> 无 Prefix/Postfix/Transpiler 可注册
- `HarmonyInstance.PatchAll()` 在 BepInEx Awake 中调用时，按惯例扫描所有 `[HarmonyPatch]` 特性标注的类型；本类既无特性又无 Patch 方法，不可能被登记

### 哈希基线状态

> ⚠️ 当前哈希基于脏工作树（未提交 + 注释修正后的源码）。按外部审计要求，正式发布前必须：
> 1. 提交所有代码变更到 git
> 2. 从干净 checkout 重新 Release 编译
> 3. 用新哈希作为最终发布基线
>
> 由于 `<Deterministic>true</Deterministic>` 已启用，提交后从相同 commit 重建将得到相同哈希。

---

## 5. 风险与副作用评估

### 原 Patch 触发路径移除状态（v1.4.0 -> v1.4.1，等待动态回归确认）

| v1.4.0 风险 | 触发路径 | v1.4.1 状态 |
|---|---|---|
| 捡枪被吞（BUG-1） | `Items.tryAddItem` 被被动 Patch 拦截 | 🟡 原 Patch 触发机制已移除，等待动态回归确认 |
| 收起动作无替换（BUG-2） | 被动 Patch 清空装备栏页触发 dequip（v1 报告未给出运行时根因证据） | 🟡 原 Patch 触发机制已移除，等待动态回归确认；BUG-2 本身待运行时复现验证 |
| 合成吞物（BUG-3） | `forceAddItem` 调用 `tryAddItem` 被动 Patch 拦截 | 🟡 原 Patch 触发机制已移除，等待动态回归确认 |
| 2N 次 Reliable 网络包风暴 | 被动 Patch 清空整个网格触发 N 次 `removeItem` | 🟡 原 Patch 触发机制已移除，等待动态回归确认 |

> ⚠️ 静态证据只能证明相关 Patch 路径已从编译产物中移除。BUG-2 本来就没有得到运行时根因确认；BUG-1/3 也尚未执行复现回归。动态测试通过后才能写"已消除"。

### 残余风险（未消除）

| 风险 | 触发路径 | 缓解措施 |
|---|---|---|
| 手动整理路径清空+重添失败 | `[整理]` 按钮 / Plugin 0 按键触发 `ManualTidyService.TidyPage` | 声明残余风险；单机风险低于联机同步场景，但仍需备份并核对物品；联机模式手动核对物品数量 |
| 容器页整理无效（工坊虚拟容器） | 服务器端 `items[STORAGE]` 为 0×0 | v1.4 已加诊断日志；无修复方案 |
| `InventorySolver.TryPack` 对超尺寸物品返回 true | 仅在被禁用的被动 Patch 路径触发 | 不再触发；手动整理路径对超尺寸物品保留原位，不丢失 |

### 潜在副作用

1. **用户体验回退**：v1.4.0 用户依赖被动整理自动重排，v1.4.1 需要手动按 [整理] 按钮。CHANGELOG 已明确说明。
2. **手动整理使用频率上升**：可能暴露 `ManualTidyService.TidyPage` 的残余风险。需要动态测试阶段重点验证。
3. **网络包数量预期**：v1.4.0 被动整理每次拾取触发 2N 个 Reliable 包，v1.4.1 仅在手动整理时触发。网络负载反而下降。

### 对其他模块的影响评估

| 模块 | 影响 | 说明 |
|---|---|---|
| 存档系统 | 无 | 被动整理 Patch 仅拦截 `Items.tryAddItem`，不写入存档 |
| 网络同步 | 正向影响 | 移除被动 Patch 后，每次拾取不再触发 2N 次 Reliable 包风暴 |
| UI 响应 | 无 | UI 按钮注入 Patch（`PlayerDashboardInventoryUIPatch`）保持原状 |
| 合成系统 | 正向影响 | `PlayerCrafting.HandleCraftRequestInternal` 不再被 Patch 干扰，走 vanilla `forceAddItem` + `dropItem` 兜底 |
| 装备栏 | 正向影响 | `equipment.checkSelection` -> `dequip()` 不再被 Patch 触发的清空操作误调用 |
| 容器页整理 | 无 | STORAGE 容器整理路径保持原状，仍受工坊虚拟容器限制 |

---

## 6. 测试用例与建议

### 静态验证（已通过）

- ✅ 编译通过：0 errors / 0 warnings
- ✅ 反射元数据检查：`ItemsTryAddItemPatch` 类型 0 自定义特性、0 声明方法
- ✅ 文档完整：README/CHANGELOG/版本号/插件名/日志全部标注"被动整理已禁用"
- ✅ 旧版 v1 报告已标记 SUPERSEDED

### 动态测试矩阵

| 场景 | 单机 | U3DS 双机 | 核心通过标准 | 优先级 |
| :--- | :---: | :---: | :--- | :---: |
| 碎片背包捡大枪 | 必须 | 必须 | 枪进入库存或保留地面 | 首轮必须（测试 A） |
| 装备槽及所有页面全满捡枪 | 必须 | 必须 | 地面枪不得错误消失 | 首轮必须（测试 A） |
| 空间不足合成 | 必须 | 必须 | 产物进入库存或掉落地面 | 首轮必须（测试 B） |
| UI 手动整理当前页 | 建议 | 必须 | 完整物品状态守恒 | 发布前必须（测试 C） |
| Plugin 0 整理全部页 | 建议 | 必须 | 五页完整物品状态守恒 | 发布前必须（测试 C） |
| STORAGE 容器整理 | 可选 | 必须 | 容器与所有观察客户端最终一致 | 发布前必须 |
| 双端 DLL 哈希一致 | - | 必须 | 客户端 = 服务器 = 基线 | 首轮必须（测试 D） |

### 物品守恒比较字段（必须全量比对）

```text
page
item id
amount
quality
完整 state 字节数组
物品实例数量
```

仅比较背包总数量不合格。

### 测试用例详情

#### 测试 A：捡枪不丢失（门槛 2/3/5，单机 + U3DS 双机，首轮必须）
1. 单机场景：背包+装备栏全满，地面放枪，记录 `id + amount + quality + state`，拾取，记录操作后状态
2. U3DS 双机场景：客户端连接 U3DS，同上操作，服务器端 + 客户端同时记录
3. 通过标准：地面枪保留 OR 进入背包；无静默销毁；服务器端与客户端最终状态一致

#### 测试 B：合成不吞物（门槛 4/5，单机 + U3DS 双机，首轮必须）
1. 单机场景：背包空间不足以装合成产物，触发合成，记录材料+产物 `id + amount + quality + state`
2. U3DS 双机场景：客户端连接 U3DS，同上操作，服务器端 + 客户端同时记录
3. 通过标准：产物在背包 OR 地面；材料按 vanilla 规则消耗；服务器端与客户端最终状态一致

#### 测试 C：手动整理残余风险评估（门槛 5，发布前必须）
1. U3DS 双机模式，背包碎片化
2. 按 [整理] 按钮，记录操作前后 `id + amount + quality + state` 全量
3. 重复 10 次以放大统计样本
4. 通过标准：物品守恒（无丢失/复制/变质）

> 门槛 8 已在声明层通过（README/CHANGELOG 已标注残余风险），测试 C 仅用于满足门槛 5 的运行时验证要求。

#### 测试 D：双端哈希一致（门槛 6，首轮必须）
1. 部署 DLL 到客户端 + U3DS
2. 计算 SHA-256
3. 通过标准：两端哈希一致且等于基线 `35f4eeac...db228`（注：基线哈希需从干净 commit 重建后最终确定）

### 测试报告归档建议

- 测试日志、录像、哈希记录归档到 `.audit/v1.4.1-release-verification-<YYYYMMDD>/`
- 测试报告需包含：测试场景、操作步骤、操作前后物品守恒表、测试结论

---

## 7. 8 项发布门槛状态总览

| 门槛 | 描述 | 状态 |
|---|---|---|
| 1 | 反射检查证明 `Items.tryAddItem` 不再包含该插件 Prefix | ✅ 已通过（静态元数据层） |
| 2 | 背包满 + 页面碎片化时捡取大枪：失败应保留地面物品，成功时物品必须存在于背包 | ⏸️ 待动态测试（单机 + U3DS 双机） |
| 3 | primary/secondary 和全部背包页满时捡枪：不得错误销毁地面枪 | ⏸️ 待动态测试（单机 + U3DS 双机） |
| 4 | 空间不足时合成：产物必须进入背包或掉落地面 | ⏸️ 待动态测试（单机 + U3DS 双机） |
| 5 | 记录操作前后物品守恒：`id + amount + quality + state`，不能只比较数量 | ⏸️ 待动态测试 |
| 6 | 客户端与服务器使用相同 v1.4.1 DLL 哈希 | ⏸️ 待部署验证 |
| 7 | README、CHANGELOG 和版本号明确写明"被动整理已禁用" | ✅ 已通过 |
| 8 | 对手动 `[整理]` 和 Plugin 0 路径单独标注残余风险；在完成物品守恒验证前，不得宣称其生产安全 | ✅ 已通过（声明层） |

---

## 8. 最终裁决建议

**静态层**：✅ 满足发布门槛 1/7/8（静态可验证部分）

**动态层**：⏸️ 门槛 2/3/4/5/6 需要在游戏内动态测试验证

**建议**：
1. 在外部审计员确认本报告后，进入动态测试阶段
2. 首轮必须完成测试 A（捡枪不丢失，单机 + U3DS 双机）+ 测试 B（合成不吞物，单机 + U3DS 双机）+ 测试 D（双端哈希一致）
3. 测试 C（手动整理残余风险评估）可在 A/B/D 之后执行，但发布前必须完成
4. 测试 C 通过前，README/CHANGELOG 的"残余风险声明"必须保留
5. 全部 8 项门槛通过后，可正式发布 v1.4.1

---

## 9. 详细文档索引

| 文档 | 位置 | 说明 |
|---|---|---|
| 本审查清单 | `AUDIT_CHECKLIST.md` | 项目根目录外部审查入口 |
| v1.4.1 发布门槛验证报告 | `.audit/v1.4.0-bug-analysis-20260716/v1.4.1-release-gate-checklist.md` | 8 项门槛详细验证 + 反射证据 + 动态测试矩阵 |
| v2 修正版分析报告 | `.audit/v1.4.0-bug-analysis-20260716/items-tryadditem-bug-analysis-v2.md` | BUG-1/2/3 根因分析 + 方案 A/B 评估 |
| v1 原始报告（SUPERSEDED） | `.audit/v1.4.0-bug-analysis-20260716/items-tryadditem-bug-analysis.md` | 已被 v2 取代，保留作为审计链路 |
| 更新日志 | `CHANGELOG.md` | v1.4.1 完整条目 |
| 用户手册 | `README.md` | v1.4.1 紧急止损声明 + 残余风险声明 |
