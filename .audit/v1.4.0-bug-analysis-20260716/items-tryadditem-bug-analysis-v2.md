# LaunchInventoryTidy v1.4.0 用户反馈 BUG 溯源分析与修复规划报告（v2 修正版）

**出具日期**：2026-07-28（v2 修正）
**分析对象**：LaunchInventoryTidy v1.4.0（commit `066e154`）
**分析范围**：`Patches/ItemsTryAddItemPatch.cs` 被动整理 Patch + `ManualTidyService.cs` 手动整理路径
**分析依据**：Unturned SDK 源码（U3-SDK）+ 插件源码 + 用户社区反馈 + 外部审计意见
**输出类型**：分析报告 + 修复规划方案（v1.4.1 已实施方案 A）

---

## 修订说明（v1 -> v2）

本 v2 版本根据外部审计意见修正了 v1 报告中的以下错误：

| v1 错误 | v2 修正 |
|---|---|
| 遗漏 TryPack 把超尺寸新物品排除出 validCount 仍返回 true 的确定性吞物路径 | 列为 BUG-1/BUG-3 的**首要确定性根因** |
| 称"Prefix 抛异常后 Harmony 默认回退执行原方法" | 删除该错误描述。Harmony Prefix 异常通常直接向调用栈传播，不会自动执行原方法作为回退 |
| 称"addItem 位置冲突/网络失败导致抛异常" | 修正：`Items.cs:297` 不检查目标空间，`fillSlot` 只覆盖布尔值，`onItemAdded` 异常被内部捕获。真实异常面是第三方委托、`onStateUpdated`、Harmony 交互、空对象等 |
| BUG-2 归因确定为 dequip | 降级为"有条件可解释"。Patch 跳过 page 0/1，若手中枪在 primary/secondary，清空 page 2..6 不会命中 dequip 坐标 |
| 称"3 个 BUG 根因都是清空+重添" | 修正：BUG-1/3 有强静态证据，BUG-2 需运行时复现证据 |
| 称"方案 A 后 3 个 BUG 全部消失" | 修正：方案 A 只消除 `Items.tryAddItem` 被动路径的 3 个触发场景，手动整理路径仍有残余风险 |
| 方案 B 差分移动+回滚可实施 | 承认方案 B 设计不可直接实施，需先解决循环依赖、临时空间、事件抑制、事务回滚 |
| 称"网络风暴导致客机状态不一致" | 降级为"待验证风险"。包数量多不自动意味着不同步 |

---

## 一、用户反馈 BUG 现象

| BUG 编号 | 现象描述 | 用户原话 |
|---|---|---|
| BUG-1 | 背包满+手上有枪，捡地上的枪被"吞掉" | "背包满了，手上有枪，这时候去地上摸一个枪就会直接吞掉好像" |
| BUG-2 | 装备栏+背包都满，捡地上的枪有收起动作但无效果 | "装备栏，背包都塞满了，然后捡地上的枪会有一个收起来的动作，但是没有在背包也没有替换手上的枪" |
| BUG-3 | 背包空间不够时合成物品被吞 | "背包的空间不够，合成东西的话也会直接被吞掉" |

---

## 二、源码溯源

### 2.1 vanilla 捡枪调用链

**关键文件**：
- `U3-SDK/.../Interactable/InteractableItem.cs:18-21`
- `U3-SDK/.../Managers/ItemManager.cs:286-391`
- `U3-SDK/.../Player/PlayerInventory.cs:405-620, 1376-1379, 1742-1807`

**调用链**：
```
玩家按 F 键
  └─> InteractableItem.use()
        └─> ItemManager.takeItem(..., to_page=255)
              └─> 客户端 RPC -> 服务器 ItemManager.ReceiveTakeItemRequest (ItemManager.cs:294)
                    └─> player.inventory.tryAddItem(item, true) (ItemManager.cs:364)
                          └─> 返回 true  -> 从地上移除 + SendDestroyItem + 播 PICKUP 手势
                          └─> 返回 false -> 发 SPACE 消息，物品留在地上（不销毁）
```

**vanilla 捡枪顺序**（`PlayerInventory.tryAddItem`，PlayerInventory.cs:494-595）：
1. 先试 secondary 槽（page 1）
2. 再试 primary 槽（page 0）
3. 最后循环背包页（page 2..6）
4. **只填空位，绝不替换手上枪**

### 2.2 vanilla 合成调用链

**关键文件**：`U3-SDK/.../Player/PlayerCrafting.cs:740-999`

**调用链**：
```
PlayerCrafting.HandleCraftRequestInternal (PlayerCrafting.cs:740)
  └─> 步骤 1：先消耗材料（不可回滚）
  │     └─> inputItem.Delete / DeleteAmount (行 845-904)
  │     └─> player.crafting.removeItem
  │     └─> Items.removeItem
  └─> 步骤 2：再放产物
        └─> player.inventory.forceAddItem(item, true) (行 954/958)
              └─> tryAddItemAuto 返回 true  -> 产物进入背包
              └─> tryAddItemAuto 返回 false -> ItemManager.dropItem 掉地上兜底
```

**关键**：`PlayerInventory.cs:607` 明确规定，只有 `tryAddItemAuto` 返回 `false` 才掉落产物。vanilla 合成本身不吞物。

### 2.3 Items.tryAddItem 调用点

**调用方契约**：`true = 已放入`，`false = 无空间`

| 调用点 | 文件:行 | 场景 |
|---|---|---|
| `PlayerInventory.tryAddItem` 定位添 | PlayerInventory.cs:436 | 装备槽定位添加 |
| `tryAddItemEquip` | PlayerInventory.cs:479 | 装备到 primary/secondary 槽 |
| `tryAddItemAuto` page 循环 | PlayerInventory.cs:578 | 自动找空位（捡枪/合成产物走这里） |
| loadout 相关 | PlayerInventory.cs:1393/1404/1430/1437/1451 | 装备配置加载 |
| `ItemManager.ReceiveTakeItemRequest` | ItemManager.cs:364 | 捡地上物品 |

### 2.4 Items.removeItem 的副作用

**关键文件**：`Items.cs:316-377`、`PlayerInventory.cs:1742-1807, 1376-1379`

每次 `Items.removeItem` 触发：
1. `sendItemRemove` 网络包（Reliable，行 1376-1379）
2. 若 `equipment.checkSelection(page, x, y)` 命中 -> **`player.equipment.dequip()`**（收起动作）
3. `onInventoryRemoved` 委托
4. `incrementUpdateIndex`

每次 `Items.addItem` 触发：
1. `sendItemAdd` 网络包（Reliable）
2. `onInventoryAdded` 委托（内部捕获异常）
3. `incrementUpdateIndex`

**修正**（v2）：`Items.cs:297` 的 `addItem` 不检查目标空间是否为空，`fillSlot` 只是覆盖布尔占用值。`onItemAdded` 异常被内部捕获。所以"位置冲突抛异常"不是真实异常面。真实异常面是：第三方委托、`onStateUpdated`、Harmony 交互、空对象或其他运行时异常。

### 2.5 插件 Patch 行为

**文件**：`LaunchInventoryTidy/Patches/ItemsTryAddItemPatch.cs`（v1.4.0）

Patch 拦截 `Items.tryAddItem(Item, bool)`，行为流程：
1. 装备槽（page < SLOTS=2）放行
2. `tryFindSpace` 找到空位放行
3. 构建包列表（现有物品 + 新物品，新物品 Tag=null）
4. 调 `InventorySolver.TryPack`（MaxRects + 降序）
5. TryPack 返回 false -> 放行原版
6. TryPack 返回 true -> **清空整个网格** + 按 result 重新添加 + 返回 `__result=true`

### 2.6 TryPack 的 validCount 计算逻辑

**关键文件**：`InventorySolver.cs:93-100`

```csharp
bool isValid = it.size_x > 0 && it.size_y > 0
    && (FitsGrid(it.size_x, it.size_y, width, height)
        || FitsGrid(it.size_y, it.size_x, width, height));
if (isValid) validCount++;
```

**致命问题**：`FitsGrid(sx, sy, width, height)` 要求 `sx <= width && sy <= height`。如果新物品（3x2）的尺寸超过当前页面（如某装备槽页 2x3），`FitsGrid` 返回 false，新物品**不被计入 validCount**。

之后 TryPack 返回 `placedCount == validCount`。如果所有合法物品（不含超尺寸新物品）都放置成功，`placedCount == validCount`，**TryPack 返回 true**，但新物品 `Placed=false`。

---

## 三、根因分析（v2 修正）

### 3.1 BUG-1 根因（捡枪吞掉）- **首要确定性根因**

**强静态证据**：`InventorySolver.cs:93` + `ItemsTryAddItemPatch.cs:119`

**确定性吞物路径**（无需异常即可稳定触发）：

```
tryAddItemAuto 逐页尝试
  -> 某个较小页面无法容纳新枪（FitsGrid=false）
  -> Solver 将新枪视为"不合法于该网格"，不计入 validCount
  -> 现有物品全部放置后 placedCount == validCount
  -> TryPack 返回 true
  -> 新枪保持 Placed=false
  -> Tag=null，没有原位置可恢复
  -> tryFindSpace 仍失败，静默跳过
  -> Patch 返回 __result=true
  -> ItemManager 收到 true，销毁地面枪 + 播 PICKUP
  -> 结果：地面枪消失，背包中也没有新枪
```

**辅助证据**：
- `ItemsTryAddItemPatch.cs:119`：`if (p.Placed)` 分支只处理 Placed=true 物品
- `ItemsTryAddItemPatch.cs:124-146`：Placed=false 分支对 Tag=null 新物品只能走 `tryFindSpace`，失败后静默跳过
- `ItemsTryAddItemPatch.cs:150`：无论新物品是否实际加入，都返回 `__result=true`

**触发条件**：
- 背包页面尺寸不足以容纳新物品（如某些装备槽页 2x3 < 新枪 3x2 旋转后 2x3？需确认）
- 或背包页面碎片化，tryFindSpace 找不到空位

**严重性**：Critical。vanilla `ItemManager` 据 `__result=true` 销毁地面物品，物品真实丢失。

### 3.2 BUG-2 根因（收起动作无替换）- **有条件可解释**

**机制存在但归因未确认**：

`PlayerInventory.cs:1754` 确实在移除当前选中坐标时执行 `dequip()`。但：
- Patch 第 1 步 `if (page < SLOTS) return true;` 跳过 page 0/1
- 如果玩家手中枪位于 primary（page 0）或 secondary（page 1），清空 page 2..6 不会命中该坐标
- 只有当手中物品位于 page 2..6（如某些工具在背包页选中使用）时，才会触发 dequip

**需要运行时证据**：
- `equippedPage` / `equippedX` / `equippedY` 的实际值
- 被清空页面与选中坐标的关系
- `dequip` 调用日志

**降级结论**：BUG-2 机制存在，但用户案例归因需运行时复现确认。当前静态分析只能证明"可能解释"，不能证明"确实解释"。

### 3.3 BUG-3 根因（合成吞物）- **高可信**

**强静态证据**：`PlayerCrafting.cs:954` + `PlayerInventory.cs:607` + `ItemsTryAddItemPatch.cs:150`

**致命路径**：

```
PlayerCrafting.HandleCraftRequestInternal
  -> 步骤 1：消耗材料 A + B（不可回滚，Items.removeItem）
  -> 步骤 2：forceAddItem(C)
        -> tryAddItemAuto -> items[page].tryAddItem
        -> 我们的 Patch 拦截
        -> 场景 A：TryPack 返回 false（C 真的装不下）
              -> Patch 放行原版
              -> 原版 tryAddItem 返回 false
              -> forceAddItem 收到 false -> dropItem 兜底 -> C 掉在地上
              -> 这种情况下 C 不被吞
        -> 场景 B：TryPack 返回 true（误判能装下，可能因 validCount 排除超尺寸 C）
              -> Patch 清空网格 + 重排
              -> 新物品 C 若 Placed=false，走 tryFindSpace 失败后静默跳过
              -> Patch 返回 __result=true
              -> forceAddItem 收到 true，不调 dropItem 兜底
              -> 结果：材料 A+B 已消耗，产物 C 也丢了
```

**修正表述**（v2）：不是"Patch 返回 true 就一定绕过兜底并吞物"，而是"Patch 在新物品未实际加入时错误返回 true，才造成吞物"。

**严重性**：Critical。合成先消耗再放产物（不可回滚），Patch 错误返回 true 绕过 dropItem 兜底，材料 + 产物双丢。

---

## 四、Patch 设计层面的根本问题

### 4.1 "清空 + 重添"模式有毒

| 问题 | 影响 |
|---|---|
| **网络包数量大** | N 个物品触发 N 次 sendItemRemove + N 次 sendItemAdd，共 2N 个 Reliable 包 |
| **多余 dequip** | removeItem 命中 equipment.checkSelection 触发 dequip，造成"收起动作"误觉（仅当选中坐标在清空页面内） |
| **状态不一致风险** | 重添过程中若 addItem 失败（第三方委托/onStateUpdated/Harmony 交互异常），物品可能真实丢失 |
| **绕过 vanilla 兜底** | 返回 `__result=true` 让 forceAddItem 不调 dropItem，合成场景致命 |

### 4.2 Patch 违反 tryAddItem 成功契约

**vanilla 调用方期望**：
- tryAddItem 返回 true -> 物品已放入，调用方可销毁源（地上物品/材料）
- tryAddItem 返回 false -> 物品未放入，调用方保留源

**Patch 违反契约**：
- 新物品未实际加入时仍返回 `__result=true`
- 调用方据此销毁源物品，造成真实丢失

### 4.3 TryPack 的"成功"语义被滥用

`TryPack` 返回 true 意味着"所有合法物品（不含超尺寸）都能放入虚拟网格"，但这**不等于**：
- 新物品实际被放入（可能 Placed=false 但 validCount 未计入）
- 实际执行 removeItem + addItem 不会出错
- 调用方应据此销毁源物品

**算法成功 ≠ 新物品已放入 ≠ 执行成功**，但 Patch 把三者等价了。

### 4.4 手动整理路径的残余风险

**关键文件**：`ManualTidyService.cs:128`

`ManualTidyService.TidyPage` 也执行"清空整个页面 + 按 TryPack 结果重新添加"的大规模重组。虽然手动整理路径不经过 `Items.tryAddItem`（不绕过 forceAddItem 兜底），但仍存在：
- 清空过程触发 2N 网络包
- 重添过程中 addItem 失败可能丢失物品
- Placed=false 物品走恢复逻辑，恢复失败则丢失

**在物品守恒验证（id + amount + quality + state 全量比对）完成前，不得宣称手动整理路径生产安全**。

---

## 五、修复规划方案

### 5.1 修复优先级

| 优先级 | 修复项 | 状态 |
|---|---|---|
| P0 | **彻底禁用被动整理 Patch**（方案 A） | v1.4.1 已实施 |
| P1 | 手动整理路径物品守恒验证 | 待执行 |
| P2 | 重设计被动整理（方案 B 或 C） | 暂不实施 |

### 5.2 方案 A：彻底禁用被动整理（v1.4.1 已实施）

**改动**：移除 `[HarmonyPatch(typeof(Items), "tryAddItem", ...)]` 特性，类留空。

**效果**：
- BUG-1/BUG-3 的首要确定性根因被切断（Patch 不再拦截 tryAddItem）
- BUG-2 的有条件可解释机制也被切断（不再清空网格）
- 玩家仍可用手动整理（[整理] 按钮 + Plugin 0 按键）
- 被动整理功能消失

**实施方式**（按审计推荐）：
- 移除 `[HarmonyPatch]` 特性
- 类留空，加注释说明历史背景
- **不采用**"在 Prefix 内增加条件分支"（会保留误触发面）
- **不采用**"从 .csproj 移除文件"（保留历史参考）

**不能宣称的范围**：
- 方案 A 只消除 `Items.tryAddItem` 被动路径的 3 个社区触发场景
- **不得宣称整个插件已无吞物风险**
- 手动整理路径仍存在残余风险（见 4.4）

### 5.3 方案 B：差分移动 + 失败回滚（外部审计拒绝）

**审计拒绝理由**：
- 满网格重排可能形成 A->B、B->C、C->A 的占位环，没有临时空格时无法逐个差分移动
- `removeItem`/`addItem` 会立即发送网络事件和触发装备副作用，事后回滚不能撤销已发送的事件
- 需要先设计：移动依赖图、循环处理、临时隔离区、事件抑制和提交协议
- 需要解决：如何避免移除当前装备触发 dequip、如何验证物品 ID/amount/quality/state 全量守恒、如何处理其他 Harmony Patch 并发修改
- 最终 `true` 返回前如何确认新物品确实存在

**结论**：方案 B 当前设计不可直接实施。长期更合理的默认方向是保持禁用（方案 A），而不是重新在 `tryAddItem` 内执行复杂事务。

### 5.4 方案 C：建议模式（暂不实施）

被动整理不自动应用，只在 UI 上提示"可整理"，玩家点击 [整理] 按钮才执行。需新增 UI 提示逻辑，暂不实施。

---

## 六、v1.4.1 发布门槛（8 条）

v1.4.1 发布前必须满足：

| 编号 | 门槛 | 验证方式 | 状态 |
|---|---|---|---|
| 1 | 反射检查或 Harmony 日志证明 `Items.tryAddItem(Item,bool)` 不再包含该插件 Prefix | 编译后用反射检查 `typeof(Items).GetMethod("tryAddItem")` 上的 Harmony Patch 列表 | 待验证 |
| 2 | 背包满、页面碎片化时捡取大枪：失败应保留地面物品，成功时物品必须存在于背包 | 双机测试 | 待验证 |
| 3 | primary/secondary 和全部背包页满时捡枪：不得错误销毁地面枪 | 双机测试 | 待验证 |
| 4 | 空间不足时合成：产物必须进入背包或掉落地面 | 双机测试 | 待验证 |
| 5 | 记录操作前后物品守恒：`id + amount + quality + state`，不能只比较数量 | 自动化脚本 | 待验证 |
| 6 | 客户端与服务器使用相同 v1.4.1 DLL 哈希 | SHA-256 比对 | 待验证 |
| 7 | README、CHANGELOG 和版本号明确写明"被动整理已禁用" | 文档审查 | ✅ 已完成 |
| 8 | 对手动 `[整理]` 和 Plugin 0 路径单独标注残余风险；在完成物品守恒验证前，不得宣称其生产安全 | 文档审查 | ✅ 已完成 |

---

## 七、待审计要点（v2）

请外部审计员重点关注：

1. **v1.4.1 实施是否正确**：`ItemsTryAddItemPatch.cs` 是否已移除 `[HarmonyPatch]` 特性，类是否留空
2. **门槛 1 的验证方式是否可行**：反射检查 Harmony Patch 列表的具体实现
3. **门槛 5 的物品守恒验证**：是否需要开发自动化脚本，还是手动比对
4. **手动整理路径的残余风险**：是否需要在 v1.4.1 中也禁用 `ManualTidyService`，或加守恒检查
5. **方案 B 是否值得长期投入**：还是保持方案 A 禁用，转向方案 C

---

## 八、附录：关键源码引用

### 8.1 插件 Patch（v1.4.0）
- `LaunchInventoryTidy/Patches/ItemsTryAddItemPatch.cs:29-152`（Prefix 全文，v1.4.1 已禁用）
- `LaunchInventoryTidy/InventorySolver.cs:93-100`（validCount 计算，遗漏超尺寸新物品）
- `LaunchInventoryTidy/ManualTidyService.cs:128`（手动整理清空+重添，残余风险）

### 8.2 vanilla 捡枪
- `U3-SDK/.../InteractableItem.cs:18-21`（use 方法）
- `U3-SDK/.../ItemManager.cs:286-391`（takeItem + ReceiveTakeItemRequest）
- `U3-SDK/.../PlayerInventory.cs:405-620`（tryAddItem + tryAddItemAuto + forceAddItem）
- `U3-SDK/.../PlayerInventory.cs:607`（forceAddItem 的 dropItem 兜底条件）

### 8.3 vanilla 合成
- `U3-SDK/.../PlayerCrafting.cs:740-999`（HandleCraftRequestInternal）
- `U3-SDK/.../PlayerCrafting.cs:845-904`（先消耗材料，不可回滚）
- `U3-SDK/.../PlayerCrafting.cs:954/958`（forceAddItem 放产物）

### 8.4 vanilla 网络同步
- `U3-SDK/.../Items.cs:297`（addItem 不检查目标空间，fillSlot 覆盖布尔值）
- `U3-SDK/.../Items.cs:316-377`（addItem + removeItem + onStateUpdated）
- `U3-SDK/.../PlayerInventory.cs:1376-1379`（sendItemRemove）
- `U3-SDK/.../PlayerInventory.cs:1742-1807`（onInventoryRemoved + dequip）
- `U3-SDK/.../PlayerInventory.cs:1754`（dequip 触发条件：equipment.checkSelection 命中）

---

## 九、结论（v2 修正）

**3 个 BUG 的根因证据等级**：

| BUG | 证据等级 | 根因 |
|---|---|---|
| BUG-1 | **强静态证据** | TryPack 把超尺寸新物品排除出 validCount 仍返回 true，Patch 返回 `__result=true` 违反成功契约 |
| BUG-2 | **有条件可解释** | dequip 机制存在，但用户案例归因需运行时复现确认 |
| BUG-3 | **强静态证据** | Patch 返回 `__result=true` 绕过 forceAddItem 的 dropItem 兜底，材料+产物双丢 |

**v1.4.1 实施方案 A**：禁用 `ItemsTryAddItemPatch`，切断 BUG-1/BUG-3 的首要根因。

**不能宣称的范围**：
- 方案 A 只消除 `Items.tryAddItem` 被动路径的 3 个触发场景
- 手动整理路径仍有残余风险
- 在物品守恒验证完成前，不得宣称整个插件已无吞物风险

**待外部审计通过后，按 8 条发布门槛逐项验证，全部满足才可发布 v1.4.1**。

---

**报告出具人**：Claude（AI 导师）
**报告审查人**：YU80Rice（人类导演）+ 外部审计员
**Vibecoding 协作**：人类导演 + AI 导师 + 本地 Agent
**版本**：v2（按外部审计意见修正）
