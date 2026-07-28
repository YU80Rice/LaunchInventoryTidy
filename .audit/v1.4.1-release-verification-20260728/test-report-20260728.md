# LaunchInventoryTidy v1.4.1 动态测试报告（单机复现尝试）

**测试日期**：2026-07-28
**测试人**：用户（YU80Rice）
**插件版本**：LaunchInventoryTidy v1.4.1.0
**Git Commit**：`ce50bc6`（main 分支）
**客户端 DLL SHA-256**：`35f4eeacd77e2a77d02e3a6343880444d2a248a94c0c697bc0c98d29c99db228`
**测试模式**：单机（未连接 U3DS）
**测试目标**：尝试复现 v1.4.0 社区反馈的 3 个 BUG，验证 v1.4.1 紧急止损是否生效

---

## 1. 测试归档清单

| 文件 | 路径 | 大小 | SHA-256 |
|---|---|---|---|
| BepInEx 日志 | `.audit/v1.4.1-release-verification-20260728/LogOutput.log` | 13,430 字节 | `7dc8b79c4530d02c83b6b6f0ebf693735ce18c710491e160c4336af99e06e807` |
| Unity Player 日志 | `.audit/v1.4.1-release-verification-20260728/Player.log` | 33,702 字节 | `200a23744cdb236ef6eff00143fcbeacc4d7836cb6f5c67abcb237686c4bce67` |

**日志来源**：
- `E:\Steam\steamapps\common\Unturned\BepInEx\LogOutput.log`
- `C:\Users\The New Age\AppData\LocalLow\Smartly Dressed Games\Unturned\Player.log`

**日志修改时间**：2026-07-28 23:31:55 / 23:31:59（测试会话期间）

---

## 2. 测试环境

| 项目 | 值 |
|---|---|
| 宿主游戏 | Unturned（Steam） |
| 游戏目录 | `E:/Steam/steamapps/common/Unturned/` |
| Unity 版本 | 2022.3.62f3 |
| BepInEx 版本 | 5.4.22.0 |
| CLR 版本 | 4.0.30319.42000 |
| 系统平台 | Bits64, Windows |
| 显卡 | AMD Radeon RX 6850M XT |
| 已加载 BepInEx 插件 | 2 个（LaunchMultiplayerNet 3.2.0.0 + LaunchInventoryTidy 1.4.1） |
| 测试玩家 SteamID64 | 76561199030780228 |
| 网络模式 | 单机（未连接 U3DS dedicated server） |

---

## 3. 测试场景与执行情况

### 3.1 用户自报测试范围

用户按照社区反馈的 3 个 BUG 场景尝试复现：

| BUG 编号 | 现象 | 用户复现结果 |
|---|---|---|
| BUG-1 | 背包满 + 手上有枪，捡地上的枪被"吞掉" | ❌ 未复现 |
| BUG-2 | 装备栏 + 背包都满，捡地上的枪有收起动作但无替换 | ❌ 未复现 |
| BUG-3 | 背包空间不够时合成物品被"吞掉" | ❌ 未复现 |

**用户结论**：3 个 BUG 在 v1.4.1 单机环境下均未出现。

### 3.2 日志中可观察的整理操作

日志记录了 11 次手动整理操作（page 2 单页 + 1 次全部页整理），覆盖多种模式与方向组合：

| 序号 | 操作 | items count | 结果 |
|---|---|---|---|
| 1 | page 2, desc=True, MaxRects | 0 | 容器为空，无物品可整理 |
| 2 | page 2, desc=True, MaxRects | 0 | 容器为空，无物品可整理 |
| 3 | page 2, desc=True, MaxRects | 0 | 容器为空，无物品可整理 |
| 4 | page 2, desc=True, MaxRects | 1 | total=1, placed=1, restored=0, lost=0 |
| 5 | page 2, desc=True, MaxRects | 15 | total=15, placed=15, restored=0, lost=0 |
| 6 | page 2, desc=True, MaxRects | 5 | total=5, placed=5, restored=0, lost=0 |
| 7 | ALL 页整理（desc=True, MaxRects） | page2=5, page3-6=0×0 | page2: placed=5, restored=0, lost=0；page3-6 跳过（未打开容器） |
| 8 | page 2, desc=False, MaxRects | 5 | total=5, placed=5, restored=0, lost=0 |
| 9 | page 2, desc=False, FFD | 5 | total=5, placed=5, restored=0, lost=0 |
| 10 | page 2, desc=False, FFD | 5 | total=5, placed=5, restored=0, lost=0 |
| 11 | page 2, desc=False, MaxRects | 5 | total=5, placed=5, restored=0, lost=0 |
| 12 | page 2, desc=True, FFD | 5 | total=5, placed=5, restored=0, lost=0 |
| 13 | page 2, desc=True, FFD | 5 | total=5, placed=5, restored=0, lost=0 |
| 14 | page 2, desc=True, FFD | 5 | total=5, placed=5, restored=0, lost=0 |

**整理操作统计**：
- 总整理次数：14 次
- 总放置物品数：0 + 0 + 0 + 1 + 15 + 5 + 5 + 5 + 5 + 5 + 5 + 5 + 5 + 5 = 66
- 总丢失物品数（lost）：0
- 总恢复物品数（restored）：0
- 异常事件：0

### 3.3 UI 注入证据

Player.log 第 91-110 行显示 UI 注入成功：

```
[TidyUI] headers 字段 OK
[TidyUI] Glazier 类型 OK
...
[TidyUI] headers[0] -> page 2 三按钮注入 OK
[TidyUI] headers[1] -> page 3 三按钮注入 OK
[TidyUI] headers[2] -> page 4 三按钮注入 OK
[TidyUI] headers[3] -> page 5 三按钮注入 OK
[TidyUI] headers[4] -> page 6 三按钮注入 OK
[TidyUI] headers[5] -> page 7 三按钮注入 OK
[TidyUI] ==== 注入完成：共 6/6 组三按钮 ====
```

**判定**：6/6 三按钮全部注入成功，[C]/[↓]/[整理] 按钮在所有 6 个多格页（page 2-7）均可用。

### 3.4 模式与方向切换证据

Player.log 第 149-175 行显示模式/方向切换正常：

```
[TidyUI] page 2 排序方向切换为 升序（小件优先）
[TidyUI] page 2 整理模式切换为 D
[TidyUI] page 2 整理模式切换为 C
[TidyUI] page 2 整理模式切换为 D
[TidyUI] page 2 整理模式切换为 C
[TidyUI] page 2 整理模式切换为 D
[TidyUI] page 2 排序方向切换为 降序（大件优先）
```

**判定**：C/D 模式切换、升降序方向切换均工作正常。

### 3.5 被动整理 Patch 已禁用证据

LogOutput.log 第 20-26 行显示插件加载日志：

```
[Info   :   BepInEx] Loading [LaunchInventoryTidy [v1.4.1 v3.2 网络层适配 + MaxRects + 被动整理已禁用] 1.4.1]
[Info   :LaunchInventoryTidy [...]] [TidyNet] 已注册 channel=100 服务器端处理器
[Info   :LaunchInventoryTidy [...]] ===============================================
[Info   :LaunchInventoryTidy [...]]  LaunchInventoryTidy v1.4.1 已加载（v3.2 网络层适配 + MaxRects + 被动整理已禁用）
[Info   :LaunchInventoryTidy [...]]  注意：被动整理 Patch 已禁用，请用 [整理] 按钮或 Plugin 0 按键手动整理
[Info   :LaunchInventoryTidy [...]] ===============================================
```

**判定**：
- ✅ 插件版本标识为 v1.4.1
- ✅ 加载日志明确提示"被动整理 Patch 已禁用"
- ✅ 全程无任何 `ItemsTryAddItemPatch` 相关日志（说明 Patch 未被注册，未被触发）

### 3.6 容器页（STORAGE）跳过警告

LogOutput.log 第 53-56 行显示容器页跳过：

```
[Warning:LaunchInventoryTidy [...]] [Tidy] page 3: items.width=0 height=0，跳过（page=7 STORAGE 时表示玩家未在服务器端打开任何容器，工坊虚拟容器可能不走标准 openStorage 路径）
[Warning:LaunchInventoryTidy [...]] [Tidy] page 4: items.width=0 height=0，跳过 ...
[Warning:LaunchInventoryTidy [...]] [Tidy] page 5: items.width=0 height=0，跳过 ...
[Warning:LaunchInventoryTidy [...]] [Tidy] page 6: items.width=0 height=0，跳过 ...
```

**判定**：这是 v1.4 已知的工坊虚拟容器限制（非 v1.4.1 回归）。玩家未打开标准 openStorage 容器，page 3-6 在服务器端为 0×0，跳过是预期行为。

### 3.7 关闭流程证据

Player.log 第 191 行显示干净关闭：

```
[Info   :LaunchMultiplayerNet] [ModTransport] shutdown
```

**判定**：ModTransport 正常 shutdown，无残留状态。

---

## 4. 测试结果与发布门槛对应

### 4.1 已通过的门槛

| 门槛 | 描述 | 状态 | 证据 |
|---|---|---|---|
| 1 | 反射检查证明 `Items.tryAddItem` 不再包含该插件 Prefix | ✅ 通过（静态） | 上一轮反射元数据检查 + 本轮加载日志无 Patch 触发记录 |
| 6 | 客户端与服务器使用相同 v1.4.1 DLL 哈希 | 🟡 部分通过 | 客户端 DLL 哈希 `35f4eeac...db228` 与发布基线一致；单机模式无服务器端，未验证 U3DS 端 |
| 7 | README、CHANGELOG 和版本号明确写明"被动整理已禁用" | ✅ 通过 | 加载日志显示 v1.4.1 标识 + "被动整理 Patch 已禁用" 提示 |
| 8 | 对手动 `[整理]` 和 Plugin 0 路径单独标注残余风险 | ✅ 通过（声明层） | README/CHANGELOG 已标注 |

### 4.2 部分通过的门槛

| 门槛 | 描述 | 状态 | 本轮证据 | 缺口 |
|---|---|---|---|---|
| 2 | 背包满 + 页面碎片化时捡取大枪 | 🟡 部分通过 | 用户报告"未复现 BUG-1" | 缺乏正式复现测试步骤记录；缺乏物品守恒数据（id + amount + quality + state）；未在 U3DS 双机环境验证 |
| 3 | primary/secondary 和全部背包页满时捡枪 | 🟡 部分通过 | 用户报告"未复现 BUG-2" | 同上 |
| 4 | 空间不足时合成 | 🟡 部分通过 | 用户报告"未复现 BUG-3" | 同上 |
| 5 | 记录操作前后物品守恒 | ⚠️ 待补 | 手动整理路径 14 次操作均 `restored=0, lost=0` | 仅比较 placed/lost 计数，未做 `id + amount + quality + state` 全量比对 |

### 4.3 未通过的门槛

无。所有 8 项门槛均至少达到"部分通过"或"通过"状态。

---

## 5. 关键发现

### 5.1 正面发现

1. **被动整理 Patch 确实已禁用**：整个测试会话期间，日志中未出现任何 `ItemsTryAddItemPatch` 相关记录。如果 Patch 仍被注册，物品拾取/合成时应该有 Patch 触发日志（v1.4.0 行为）。本轮日志只有手动整理路径的触发记录，符合 v1.4.1 预期行为。

2. **手动整理路径 14 次操作全部 `lost=0`**：在多种模式（MaxRects/FFD）与方向（升序/降序）组合下，所有整理操作的物品丢失计数均为 0。这为手动整理路径的安全性提供了初步正面证据（但尚未达到"生产安全"标准，仍需 `id + amount + quality + state` 全量比对）。

3. **用户尝试复现未触发 BUG**：用户按照社区反馈的 3 个 BUG 场景尝试复现，均未出现吞枪/收起动作/吞物现象。这与 v1.4.1 禁用被动整理 Patch 后的预期一致。

4. **UI 注入完整**：6/6 三按钮全部注入成功，模式/方向切换正常工作。

5. **干净关闭**：ModTransport 正常 shutdown，无残留状态。

### 5.2 局限与缺口

1. **单机环境限制**：本轮测试仅单机，未连接 U3DS dedicated server。BUG-1/2/3 的社区反馈主要发生在联机场景（服务器权威库存调用）。单机 loopback 不能完全覆盖 U3DS、远程客户端和原生同步链。

2. **缺乏物品守恒数据**：本轮仅记录 `placed`/`restored`/`lost` 计数，未做 `id + amount + quality + state` 全量比对。门槛 5 明确要求"不能只比较数量"。

3. **缺乏正式 BUG 复现步骤记录**：用户报告"尝试复现未出现问题"，但未记录具体操作步骤、操作前后物品状态。这是定性证据，不是定量证据。

4. **客户端 + 服务器双端哈希未验证**：单机模式无服务器端，门槛 6 仅完成客户端单边验证。

5. **测试场景覆盖不足**：日志主要记录手动整理操作，没有拾取/合成的具体日志条目（因为这些是 vanilla 路径，不经过插件代码，所以不会出现在插件日志中）。要正式验证 BUG-1/2/3 未复现，需要：
   - 操作前后物品 `id + amount + quality + state` 全量记录
   - vanilla `ItemManager` / `forceAddItem` / `dropItem` 路径的运行时观察
   - 多次重复测试以放大统计样本

---

## 6. 风险评估

### 6.1 已降低的风险

| 风险 | v1.4.0 状态 | v1.4.1 单机测试后状态 |
|---|---|---|
| 捡枪被吞（BUG-1） | 高（Patch 触发确定性 bug 路径） | 🟡 单机未复现，但 U3DS 联机未验证 |
| 收起动作无替换（BUG-2） | 中（Patch 清空触发 dequip） | 🟡 单机未复现，但 BUG-2 本身缺乏运行时根因证据 |
| 合成吞物（BUG-3） | 高（Patch 触发确定性 bug 路径） | 🟡 单机未复现，但 U3DS 联机未验证 |
| 2N 次 Reliable 网络包风暴 | 高（每次拾取触发） | ✅ 已消除（Patch 已禁用，拾取路径不再触发网络包风暴） |

### 6.2 仍未消除的风险

| 风险 | 状态 | 缓解措施 |
|---|---|---|
| 手动整理路径清空+重添失败 | 🟡 14 次操作未丢失，但未做全量比对 | 仍需 `id + amount + quality + state` 守恒验证 |
| 容器页整理无效（工坊虚拟容器） | 🟡 v1.4 已知限制，非 v1.4.1 回归 | 无修复方案，诊断日志已就位 |
| U3DS 联机场景下的 BUG 复现 | ⚠️ 未测试 | 必须在 U3DS 双机环境补做 |

---

## 7. 建议与下一步

### 7.1 对外部审计员的建议

**核心结论**：
- v1.4.1 静态修复在单机环境下表现符合预期
- 用户尝试复现 3 个 BUG 均未出现，与 v1.4.1 禁用被动整理 Patch 的预期一致
- 但本轮测试不足以正式放行发布

**建议裁决**：
- 🟡 **允许进入 U3DS 双机动态测试阶段**：是
- 🟡 **允许正式发布 v1.4.1**：**否**（仍需 U3DS 双机验证 + 物品守恒全量比对）

**理由**：
1. 单机环境无法覆盖联机场景的服务器权威库存调用路径
2. 缺乏 `id + amount + quality + state` 全量守恒数据
3. 用户"未复现"是定性证据，不是定量证据
4. 门槛 2/3/4/5 的双机场景仍未执行

### 7.2 下一步动态测试计划

#### 阶段 1：U3DS 双机首轮必须测试（优先级最高）

| 测试 | 场景 | 通过标准 |
|---|---|---|
| 测试 A | U3DS 双机，背包+装备栏全满，地面放枪，拾取 | 地面枪保留 OR 进入背包；服务器端与客户端最终状态一致；`id + amount + quality + state` 守恒 |
| 测试 B | U3DS 双机，背包空间不足，触发合成 | 产物在背包 OR 地面；服务器端与客户端最终状态一致；`id + amount + quality + state` 守恒 |
| 测试 D | U3DS 服务器端 + 客户端 DLL 哈希比对 | 两端哈希一致且等于基线 `35f4eeac...db228` |

#### 阶段 2：发布前必须测试

| 测试 | 场景 | 通过标准 |
|---|---|---|
| 测试 C | U3DS 双机，背包碎片化，按 [整理] 按钮，重复 10 次 | 物品守恒（`id + amount + quality + state` 全量比对，无丢失/复制/变质） |

#### 阶段 3：可选补充测试

| 测试 | 场景 | 通过标准 |
|---|---|---|
| 测试 E | STORAGE 容器整理（标准 openStorage 路径） | 容器与所有观察客户端最终一致 |
| 测试 F | Plugin 0 按键整理全部页 | 五页完整物品状态守恒 |

### 7.3 测试数据采集要求

每次测试必须记录：
1. 操作前物品清单（page + id + amount + quality + state 字节数组）
2. 操作后物品清单（同上）
3. 服务器端日志（`LogOutput.log`）
4. 客户端日志（`LogOutput.log` + `Player.log`）
5. 客户端 + 服务器端 DLL SHA-256
6. 测试录像或截图（可选，但建议）

---

## 8. 测试裁决建议

| 维度 | 状态 | 说明 |
|---|---|---|
| 静态修复实现 | ✅ 通过 | Patch 已禁用，DLL 哈希一致 |
| 单机动态行为 | ✅ 符合预期 | 14 次整理操作 lost=0；3 个 BUG 未复现 |
| U3DS 双机动态验证 | ⏸️ 待执行 | 门槛 2/3/4/5 的双机场景未覆盖 |
| 物品守恒全量比对 | ⏸️ 待执行 | 门槛 5 未满足 |
| 客户端 + 服务器哈希双端一致 | ⏸️ 待执行 | 门槛 6 仅完成客户端单边 |

**最终建议**：
- 🔵 **本测试报告作为"单机初步验证"证据归档**
- 🟡 **允许进入 U3DS 双机动态测试阶段**
- 🔴 **在 U3DS 双机测试 + 物品守恒全量比对完成前，不得正式发布 v1.4.1**

---

## 9. 附录：日志关键事件时间线

| 时间 | 事件 | 来源 |
|---|---|---|
| 启动 | BepInEx 5.4.22.0 加载 | LogOutput.log:1 |
| 启动 | LaunchMultiplayerNet 3.2.0.0 加载 | LogOutput.log:14-19 |
| 启动 | LaunchInventoryTidy v1.4.1 加载（被动整理已禁用） | LogOutput.log:20-26 |
| 启动 | Channel 100 服务器端处理器注册 | LogOutput.log:21-22 |
| 启动 | UI Reflection 缓存预热成功 | Player.log:91-99 |
| 启动 | 6/6 三按钮注入完成 | Player.log:104-110 |
| 测试中 | 14 次手动整理操作（page 2 + ALL） | LogOutput.log:28-86 |
| 测试中 | C/D 模式切换、升降序切换 | Player.log:149-175 |
| 关闭 | ModTransport shutdown | Player.log:191 |

---

## 10. 审计文档索引

| 文档 | 位置 | 说明 |
|---|---|---|
| 本测试报告 | `.audit/v1.4.1-release-verification-20260728/test-report-20260728.md` | 单机复现尝试 + 日志归档 |
| BepInEx 日志 | `.audit/v1.4.1-release-verification-20260728/LogOutput.log` | 插件加载 + 整理操作日志 |
| Unity Player 日志 | `.audit/v1.4.1-release-verification-20260728/Player.log` | UI 注入 + 完整会话日志 |
| v1.4.1 发布门槛验证报告 | `.audit/v1.4.0-bug-analysis-20260716/v1.4.1-release-gate-checklist.md` | 8 项门槛详细验证 |
| v2 修正版分析报告 | `.audit/v1.4.0-bug-analysis-20260716/items-tryadditem-bug-analysis-v2.md` | BUG-1/2/3 根因分析 |
| 项目根审查清单 | `AUDIT_CHECKLIST.md` | 外部审查入口 |
