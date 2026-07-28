# ⚠️ SUPERSEDED — 本报告已被 v2 修正版取代

**本文件为 v1 原始报告，已被同目录下 `items-tryadditem-bug-analysis-v2.md` 取代。**

外部审计驳回 v1 报告的关键问题：
- 错误描述 Harmony Prefix 异常语义（v1 称"默认回退执行原方法"，实际异常直接传播）
- 错误描述 `Items.addItem` 异常来源（v1 称"位置冲突/网络失败"，实际 `addItem` 不检查目标空间，`fillSlot` 仅覆盖 bool）
- 漏判确定性 bug 路径（TryPack 把超尺寸新物品排除在 validCount 外，导致 placedCount == validCount 仍可能返回 true）
- 对 BUG-2 dequip 归因过度自信，未给出运行时根因证据
- 错误声称"方案 A 后 3 个 BUG 全部消失"，未识别手动整理路径残余风险
- 方案 B（差分移动 + 失败回滚）被审计认定为不可直接实施

**请勿依据本报告作任何修复决策。** 所有有效结论已迁移至 v2 修正版。

---

# LaunchInventoryTidy v1.4.0 用户反馈 BUG 溯源分析与修复规划报告

**出具日期**：2026-07-28
**分析对象**：LaunchInventoryTidy v1.4.0（commit `066e154`）
**分析范围**：`Patches/ItemsTryAddItemPatch.cs` 被动整理 Patch
**分析依据**：Unturned SDK 源码（U3-SDK）+ 插件源码 + 用户社区反馈
**输出类型**：分析报告 + 修复规划方案（不含代码修改）

---

## 一、用户反馈 BUG 现象

| BUG 编号 | 现象描述 | 用户原话 |
|---|---|---|
| BUG-1 | 背包满+手上有枪，捡地上的枪被"吞掉" | "背包满了，手上有枪，这时候去地上摸一个枪就会直接吞掉好像" |
| BUG-2 | 装备栏+背包都满，捡地上的枪有收起动作但无效果 | "装备栏，背包都塞满了，然后捡地上的枪会有一个收起来的动作，但是没有在背包也没有替换手上的枪" |
| BUG-3 | 背包空间不够时合成物品被吞 | "背包的空间不够，合成东西的话也会直接被吞掉" |

---

## 二、源码溯源

### 2.1 vanilla 捡枪调用链（U3-SDK 源码）

**关键文件**：
- `D:\Agent-工作目录\U3-SDK\Assets\Runtime\Assembly-CSharp\Unturned\Interactable\InteractableItem.cs:18-21`
- `D:\Agent-工作目录\U3-SDK\Assets\Runtime\Assembly-CSharp\Unturned\Managers\ItemManager.cs:286-391`
- `D:\Agent-工作目录\U3-SDK\Assets\Runtime\Assembly-CSharp\Unturned\Player\PlayerInventory.cs:405-620, 1376-1379, 1742-1807`

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

**关键文件**：`D:\Agent-工作目录\U3-SDK\Assets\Runtime\Assembly-CSharp\Unturned\Player\PlayerCrafting.cs:740-999`

**调用链**：
```
PlayerCrafting.HandleCraftRequestInternal (PlayerCrafting.cs:740)
  └─> 步骤 1：先消耗材料（不可回滚）
  │     └─> inputItem.Delete / DeleteAmount (行 845-904)
  │     └─> player.crafting.removeItem
  │     └─> Items.removeItem
  └─> 步骤 2：再放产物
        └─> player.inventory.forceAddItem(item, true) (行 954/958)
              └─> tryAddItemAuto 失败时 -> ItemManager.dropItem 掉地上兜底
              └─> forceAddItem (PlayerInventory.cs:607-613) 保证产物不丢失
```

**关键**：vanilla 合成有 `forceAddItem` + `dropItem` 兜底机制，**vanilla 合成本身不吞物**。

### 2.3 Items.tryAddItem 调用点

**调用方约定**：`true = 已放入`，`false = 无空间`

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
2. `onInventoryAdded` 委托
3. `incrementUpdateIndex`

### 2.5 插件 Patch 行为

**文件**：`LaunchInventoryTidy/Patches/ItemsTryAddItemPatch.cs`

Patch 拦截 `Items.tryAddItem(Item, bool)`，行为流程：
1. 装备槽（page < SLOTS=2）放行
2. tryFindSpace 找到空位放行
3. 构建包列表（现有物品 + 新物品）
4. 调 `InventorySolver.TryPack`（MaxRects + 降序）
5. TryPack 返回 false -> 放行原版
6. TryPack 返回 true -> **清空整个网格** + 按 result 重新添加 + 返回 `__result=true`

---

## 三、根因分析

### 3.1 BUG-1 根因（捡枪吞掉）

**核心问题**：Patch 在装箱成功路径上的"清空 + 重添"行为，会触发**网络风暴**和**状态不一致**。

**触发场景**：
1. 背包满（60 格有碎片，剩 5 格不规则空隙）
2. 手上有枪（page 0/1 已占）
3. 玩家捡地上的枪（新枪 3x2=6 格）
4. vanilla `tryAddItemAuto` 循环背包页，每页调 `items[page].tryAddItem`
5. 我们的 Patch 拦截，`tryFindSpace` 失败（碎片不够 6 格）
6. `TryPack` 重排，可能返回 true（MaxRects 找到装箱方案）
7. **Patch 清空整个网格**（50 个物品被 removeItem）
8. 触发 50 次 `sendItemRemove` 网络包 + 可能多次 `dequip`
9. Patch 按 result 重新添加，新枪进入背包
10. Patch 返回 `__result=true`
11. ItemManager 收到 true，**从地上移除新枪 + 播 PICKUP**

**结果**：
- 新枪"看起来被吞掉"（实际进入了背包，但视觉混乱）
- 或者新枪 Placed=false 被丢弃（如果 TryPack 误判），地上消失但背包也没进
- 网络风暴可能导致其他客机状态不同步

**致命风险点**：
- 第 7 步清空网格时，如果后续 addItem 抛异常（位置冲突/网络失败），Prefix 抛异常
- Harmony 默认回退到原方法，但原方法看到的是**空网格**
- 原方法可能返回 true（空网格肯定能放入）
- **原有 50 个物品已经 removeItem，未重新添加 -> 真实丢失**

### 3.2 BUG-2 根因（收起动作无替换）

**核心问题**：Patch 清空网格时触发 `removeItem`，命中 `equipment.checkSelection` 触发 `dequip`（收起动画），但物品未实际替换。

**触发场景**：
1. 装备栏 + 背包都满
2. 玩家捡地上的枪
3. vanilla 试 page 0/1（已占）-> 试背包页
4. 背包页我们的 Patch 拦截
5. **场景 A**：TryPack 返回 true
   - Patch 清空网格，触发 removeItem
   - 某些 removeItem 命中 `equipment.checkSelection(page, x, y)` -> `dequip`（收起动作）
   - Patch 重排，新枪进入背包
   - 但玩家看到"收起动作"，以为没捡到
6. **场景 B**：TryPack 返回 false
   - Patch 放行原版
   - **但 Patch 在放行前已调用 tryFindSpace**（不修改 items）
   - 原版 tryAddItem 返回 false
   - **这种情况下不应该有 dequip**

**场景 A 是 BUG-2 的主要根因**：多余的 dequip 动画误导玩家。

**辅助根因**：vanilla `tryAddItemEquip`（PlayerInventory.cs:479）在试装备槽时可能也触发 dequip，需要进一步确认。

### 3.3 BUG-3 根因（合成吞物）

**核心问题**：vanilla `forceAddItem` 的 `dropItem` 兜底被 Patch 绕过。

**触发场景**：
1. 背包空间不够合成（合成产物 C 装不下）
2. vanilla `HandleCraftRequestInternal` 先消耗 A + B（不可回滚）
3. 调 `forceAddItem(C)` -> `tryAddItemAuto` -> `items[page].tryAddItem`
4. 我们的 Patch 拦截
5. **场景 A**：TryPack 返回 false（C 真的装不下）
   - Patch 放行原版
   - 原版 tryAddItem 返回 false
   - `forceAddItem` 收到 false，**调 `dropItem` 兜底**，C 掉在地上
   - **这种情况下 C 不被吞**
6. **场景 B**：TryPack 返回 true（误判能装下）
   - Patch 清空网格 + 重排
   - **如果重排过程中某些物品的 addItem 抛异常或 Placed=false 丢弃**
   - Patch 返回 `__result=true`
   - `forceAddItem` 收到 true，**不调 dropItem 兜底**
   - **丢失的物品就没了**（材料 A+B 已消耗，产物 C 也丢了）

**场景 B 是 BUG-3 的根因**：Patch 返回 `__result=true` 绕过了 vanilla 的兜底机制。

**特别危险**：
- 合成是**先消耗再放产物**（不可回滚）
- 如果 Patch 在放产物时出错，材料已经没了，产物也没了
- 玩家损失最大

---

## 四、Patch 设计层面的根本问题

### 4.1 "清空 + 重添"模式有毒

当前 Patch 在装箱成功后采用"清空整个网格 + 按 result 重新添加"的模式，存在 4 个根本问题：

| 问题 | 影响 |
|---|---|
| **网络风暴** | N 个物品触发 N 次 sendItemRemove + N 次 sendItemAdd，共 2N 个 Reliable 包 |
| **多余 dequip** | removeItem 命中 equipment.checkSelection 触发 dequip，造成"收起动作"误觉 |
| **状态不一致风险** | 重添过程中任何 addItem 异常都会导致物品真实丢失 |
| **绕过 vanilla 兜底** | 返回 `__result=true` 让 forceAddItem 不调 dropItem，合成场景致命 |

### 4.2 被动整理的时机不对

`Items.tryAddItem` 是 vanilla 高频调用点（捡枪/合成/装备/loadout），在此时机做"清空 + 重添"大规模重组，副作用远大于收益。

**vanilla 调用方期望**：
- tryAddItem 返回 true -> 物品已放入，调用方可销毁源（地上物品/材料）
- tryAddItem 返回 false -> 物品未放入，调用方保留源（物品留地上/材料不消耗）

**Patch 违反契约**：
- 返回 true 但实际可能丢失物品
- 返回 true 绕过 forceAddItem 的 dropItem 兜底

### 4.3 装箱算法的"成功"语义被滥用

`TryPack` 返回 true 意味着"所有合法物品都能放入虚拟网格"，但这**不等于**"实际执行 removeItem + addItem 不会出错"。

实际执行时可能：
- addItem 抛异常（位置冲突、size 不匹配、网络包失败）
- removeItem 触发 dequip 改变了 equipment 状态
- 其他 Patch 干扰

**算法成功 ≠ 执行成功**，但 Patch 把两者等价了。

---

## 五、修复规划方案

### 5.1 修复优先级

| 优先级 | 修复项 | 理由 |
|---|---|---|
| P0 | **彻底禁用被动整理 Patch** | 单一改动即可消除 3 个 BUG，风险最低 |
| P1 | 重设计被动整理为"差分模式" | 保留功能但消除副作用 |
| P2 | 网络包批量化 | 性能优化，非阻塞 |
| P3 | UI 提示玩家"被动整理已禁用" | 用户体验 |

### 5.2 方案 A：彻底禁用被动整理（P0 推荐）

**改动**：移除或注释 `[HarmonyPatch(typeof(Items), "tryAddItem", ...)]` 特性，让 Patch 不再被 Harmony 注册。

**效果**：
- BUG-1/2/3 全部消失（vanilla tryAddItem 行为恢复）
- 玩家仍可用手动整理（[整理] 按钮 + Plugin 0 按键）
- 被动整理功能消失（用户需主动点整理按钮）

**优点**：
- 单点改动，风险极低
- 立即修复所有 BUG
- 不影响手动整理功能

**缺点**：
- 失去"物品添加时自动整理"的便利
- 用户需要养成手动整理习惯

**实施建议**：
- 短期（v1.4.1）：直接禁用被动整理 Patch
- 长期（v1.5.0）：重设计为差分模式（方案 B）

### 5.3 方案 B：重设计为差分模式（P1）

**核心思路**：不清空整个网格，只移动需要移动的物品。

**实现步骤**：
1. 在 Prefix 中**只读取**当前物品布局（不清空）
2. 调 `TryPack` 计算最优布局
3. 计算差分：哪些物品需要从 (oldX, oldY) 移到 (newX, newY)
4. **只移动差分物品**：逐个 `removeItem` + `addItem`
5. 新物品按 `TryPack` 结果添加
6. 任何一步失败时**回滚**：恢复原位 + 返回 false 让 vanilla 兜底

**关键改动**：
- 不再"清空 + 重添"，改为"差分移动"
- 失败时回滚，返回 false 让 vanilla 处理
- 不再返回 `__result=true` 绕过 forceAddItem 兜底

**优点**：
- 保留被动整理功能
- 副作用大幅降低
- 失败时回滚，不丢物品

**缺点**：
- 实现复杂度高（差分计算 + 回滚）
- 仍可能触发多次 sendItemRemove/sendItemAdd
- 需要充分测试

### 5.4 方案 C：被动整理改为"建议模式"（P2 备选）

**核心思路**：被动整理不自动应用，只在 UI 上提示"可整理"。

**实现步骤**：
1. Patch 不再执行重组，只计算"是否能整理"
2. 如果能整理，在 UI 上显示提示（如标题栏闪烁）
3. 玩家点击 [整理] 按钮才实际执行
4. 完全避免在 tryAddItem 时机做修改

**优点**：
- 完全消除 tryAddItem 时机风险
- 玩家保留主动权
- 实现简单

**缺点**：
- 需要新增 UI 提示逻辑
- 失去"自动整理"的便利

### 5.5 短期建议（v1.4.1）

**强烈建议采用方案 A（彻底禁用被动整理）**：

1. **风险最低**：单一改动，不引入新 bug
2. **立即修复**：3 个 BUG 全部消失
3. **保留核心功能**：手动整理（[整理] 按钮 + Plugin 0）仍可用
4. **用户教育成本低**：在 README 说明"被动整理已禁用，请手动点整理按钮"

### 5.6 长期规划（v1.5.0）

如果用户反馈希望恢复被动整理：
1. 实现方案 B（差分模式）
2. 充分单元测试（覆盖 BUG-1/2/3 场景）
3. 双机回归测试
4. 发布前至少 1 周社区 Beta 测试

---

## 六、待审计要点

请外部审计员重点关注以下问题：

1. **方案 A 是否可接受**：禁用被动整理是否会严重影响用户体验？
2. **方案 B 的可行性**：差分模式能否在 v1.5.0 实现？是否值得投入？
3. **vanilla forceAddItem 兜底是否被正确理解**：审计 `PlayerInventory.cs:607-613` 确认 dropItem 兜底逻辑
4. **PATCH 装备槽放行是否正确**：审计 `if (page < SLOTS) return true;` 是否覆盖所有装备槽场景
5. **是否需要保留被动整理的"重排提示"功能**：方案 C 是否值得考虑

---

## 七、附录：关键源码引用

### 7.1 插件 Patch
- `LaunchInventoryTidy/Patches/ItemsTryAddItemPatch.cs:29-152`（Prefix 全文）

### 7.2 vanilla 捡枪
- `U3-SDK/.../InteractableItem.cs:18-21`（use 方法）
- `U3-SDK/.../ItemManager.cs:286-391`（takeItem + ReceiveTakeItemRequest）
- `U3-SDK/.../PlayerInventory.cs:405-620`（tryAddItem + tryAddItemAuto + forceAddItem）

### 7.3 vanilla 合成
- `U3-SDK/.../PlayerCrafting.cs:740-999`（HandleCraftRequestInternal）

### 7.4 vanilla 网络同步
- `U3-SDK/.../Items.cs:316-377`（addItem + removeItem + onStateUpdated）
- `U3-SDK/.../PlayerInventory.cs:1376-1379`（sendItemRemove）
- `U3-SDK/.../PlayerInventory.cs:1742-1807`（onInventoryRemoved + dequip）

---

## 八、结论

**3 个 BUG 的根因都是 `ItemsTryAddItemPatch` 的"清空 + 重添"模式**：

1. **BUG-1**：清空+重添触发网络风暴 + 状态不一致，新枪视觉上"被吞"
2. **BUG-2**：清空时 removeItem 触发 dequip，多余的"收起动作"
3. **BUG-3**：返回 `__result=true` 绕过 vanilla `forceAddItem` 的 `dropItem` 兜底，合成场景物品真实丢失

**推荐修复方案**：v1.4.1 彻底禁用被动整理 Patch（方案 A），v1.5.0 评估方案 B（差分模式）。

**待外部审计通过后再实施修复**。

---

**报告出具人**：Claude（AI 导师）
**报告审查人**：YU80Rice（人类导演）
**Vibecoding 协作**：人类导演 + AI 导师 + 本地 Agent
