# AUDIT_CHECKLIST.md - LaunchInventoryTidy v2.0.6.13 外部审查清单

**生成时间**：2026-07-31（v2.0.6.13 Codex 架构审计重构 - 协程化测试驱动 + 网络回环 + 真实 drain）
**插件版本**：LaunchInventoryTidy v2.0.6.13
**审计阶段**：v2.0.0 架构级重构 -> v2.0.1 静态安全门槛修订 -> v2.0.2 二次审计修订 -> v2.0.3 三次审计修订 -> v2.0.4 四次审计修订 -> v2.0.5 五次审计修订 -> v2.0.6 六次审计修订 -> v2.0.6.1 LMN 3.3.2.0 集成 -> v2.0.6.2 U3DS 兼容性修复 -> v2.0.6.3 并发安全加固 -> v2.0.6.4 Codex v2.0.6.3 审计阻断项修复 -> v2.0.6.5 Codex v2.0.6.4 审计 5 项阻断项修复 -> v2.0.6.6 -> v2.0.6.7 -> v2.0.6.8 -> v2.0.6.9 -> v2.0.6.11 -> v2.0.6.12 Codex v2.0.6.11 冒烟复盘 -> v2.0.6.13 第一版（一键自动化测试系统）-> **v2.0.6.13 第二版（Codex 架构审计重构：协程化 + 网络回环 + 真实 drain）**
**外部审计状态**：
- v2.0.6.12 修复：🟢 静态通过 + 单机冒烟通过
- v2.0.6.13 第一版（自动化测试系统）：🔴 Codex FAIL 打回重编译（4 Critical + 2 Medium）
- **v2.0.6.13 第二版（重构）**：🟡 已实现 + 双配置 0 errors 编译通过，静态待 Codex 复审
- **v2.0.6.13 Round 6（Codex 第五轮 FAIL 修复）**：🟡 四项阻断项已修复 + 双配置 0 errors 编译通过，静态待 Codex 第六轮复审
- **v2.0.6.13 Round 7（Codex 第六轮 FAIL 修复）**：🟡 四项阻断项（AT-FIX-03/EVID-01/FIX-04/REP-01）已修复 + 双配置 0 errors 编译通过，静态待 Codex 第七轮复审
- **v2.0.6.13 Round 8（实测 FAIL 修复）**：🟡 两项阻断项（SP-CONS MaxRects/FFD DescFalse Rejected + SP-HK Hotkey fixture BLOCKED）已修复 + 双配置 0 errors 编译通过，静态待 Codex 第八轮复审

---

## 0. v2.0.6.13 重构编译验证（TestHarness + Release 双配置）

### 0.1 TestHarness 配置

- **编译命令**：`dotnet build LaunchInventoryTidy.csproj -c TestHarness -nologo`
- **结果**：0 errors / 0 warnings
- **编译耗时**：0.92 秒
- **TestHarness DLL 大小**：214,016 bytes
- **TestHarness DLL SHA-256**：`6A2FEA8774573B2454E4829FC4D305801569C6A6EB4609B30B8FA69BCBE2EF24`
- **TestHarness PDB SHA-256**：`65EEE77D41DA11F283437C7DFE3211EA59A6C9B43154D80E520D18CDD911033D`
- **写入时间**：2026-07-31 12:20:31

### 0.2 Release 配置

- **编译命令**：`dotnet build LaunchInventoryTidy.csproj -c Release -nologo`
- **结果**：0 errors / 0 warnings
- **编译耗时**：0.85 秒
- **Release DLL 大小**：159,744 bytes
- **Release DLL SHA-256**：`1FC645295B8897D3435985F5B5E88FBE6D2EF5A1B1976F18C91170948834841C`
- **Release PDB SHA-256**：`26E15F4F320F2BB4B2B0701A6BD2F76130F3170BE2B8EB011D8FD42D7E9B6A04`
- **写入时间**：2026-07-31 12:20:39
- **AssemblyVersion**：2.0.6.13

### 0.3 新增/修改源文件清单

| 文件 | 大小 | SHA-256 | 类型 |
| --- | --- | --- | --- |
| `TidyThreadAndRateGuard.cs` | 3,303 bytes | `492F49CB19B86C6058ACF364E6C4ABF8CFB6AC235FAFBE60114EDE9B1F7C6320` | 新增（生产代码） |
| `IndependentSnapshot.cs` | 9,407 bytes | `02B8DA13879697CA2D206F67337594490150DDF84A94BE5F63CFA0CB9CBBA50C` | 新增（TestHarness） |
| `FixtureValidator.cs` | 4,993 bytes | `7D74A63448A56A29C846E59697EF1DA2701FAA127D5748679996ECCF2B49DD85` | 新增（TestHarness） |
| `NetworkTestProbe.cs` | 6,831 bytes | `2300E63785F982021440BC93423779E12239B6DCC1DC72884F00F724EF4887B4` | 新增（TestHarness） |
| `ShutdownTestProbe.cs` | 4,958 bytes | `A79D6FAA094096894D489A1115DFF25577689A49AEAD9A4267A24A66487CC186` | 新增（TestHarness） |
| `AutoTestDriver.cs` | 36,743 bytes | `4566AF3EFAC016B97883D6DD6201E201FF4493DBAD12740A64C9349F36FB0A1A` | 重写（协程化） |
| `CommandTidyAutoTest.cs` | 6,327 bytes | `84FC904E1E56DC9EA007F14A92DAD2F3CCD5D6BE1B118E77AA49061C854E9C88` | 修改（StartAllSuites 入口） |
| `LaunchInventoryTidyPlugin.cs` | 40,473 bytes | `6CF297DE49F2CF3B7D49D6F0709D7E8F5ACE5FC958E5397104440B088790A0AE` | 修改（版本+主线程门+协程化） |
| `ManualTidyNetwork.cs` | 102,257 bytes | `50A63F13566F0DBC5AA88BCD502262D5D7950ABB1CCEEFD20750E5D0573750F5` | 修改（NetworkTestProbe hook） |
| `Properties/AssemblyInfo.cs` | 665 bytes | `DEEB96DC0AB1945267430A7BD6B0437B37A34014187940C7E1F67236D654FE2D` | 修改（版本 2.0.6.13） |
| `LaunchInventoryTidy.csproj` | 5,018 bytes | `AAB58AE469B2F9A307AB76E3C9AFF71572170275FD2DF071210F7935D09CC308` | 修改（5 个新 Compile Include） |
| `run_tests.ps1` | 22,894 bytes | `E7B210C179A76B895F3C590E9B52A293A4D33DF0DD3C9480F6CEAEBBA0C233EC` | 重写（try/finally + marker + 退出码） |

## 0b. v2.0.6.13 Round 6 编译验证（Codex 第五轮 FAIL 修复后）

### TestHarness 配置

- **编译命令**：`dotnet build LaunchInventoryTidy.csproj -c TestHarness -nologo`
- **结果**：0 errors / 0 warnings
- **编译耗时**：约 1.75 秒
- **TestHarness DLL 大小**：250,368 bytes
- **TestHarness DLL SHA-256**：`35AF81DFCCB002A7DB19F1B9342B8A62CC553C848CC9793D4CD58BF765201084`
- **写入时间**：2026-07-31 16:01

### Release 配置

- **编译命令**：`dotnet build LaunchInventoryTidy.csproj -c Release -nologo`
- **结果**：0 errors / 0 warnings
- **编译耗时**：约 0.71 秒
- **Release DLL 大小**：158,720 bytes
- **Release DLL SHA-256**：`355DAF23800185BA278C385BEA2DF03D022788DE38FC8CF90DDB3A6E154B5227`
- **写入时间**：2026-07-31 16:02
- **AssemblyVersion**：2.0.6.13

### Round 6 新增/修改源文件清单

| 文件 | 改动类型 | Round 6 修复项 |
| --- | --- | --- |
| `RequestAdmissionStore.cs` | 回滚 | AT-FIX-02 相关：撤销 Round 5 误加的生产协议修改，保持仅 lease 释放 + ledger Failed |
| `IndependentSnapshot.cs` | 修改 | AT-FI-02：新增 `SameExactLayout` + `ComputeContentSha256` |
| `AutoTestDriver.cs` | 修改 | AT-FI-02 + AT-SD-02 + AT-FIX-02：SP-FI 精确布局断言、SP-SD 两阶段关闭断言、TestFixtureSession 集成 |
| `TestFixtureSession.cs` | 新增 | AT-FIX-02：TestHarness-only 自动夹具会话 |
| `LaunchInventoryTidy.csproj` | 修改 | 新增 `TestFixtureSession.cs` Compile Include |
| `run_tests.ps1` | 修改 | AT-REL-02：fail-closed Release 哈希验证 + finally 始终从 `bin\Release` 恢复 |

## 0a. v2.0.6.13 重构摘要（Codex 架构审计 §3-§4 对照）

### Codex v2.0.6.12 审计阻断项修复对照

| Codex 阻断项 | 修复位置 | 修复方式 |
| --- | --- | --- |
| 🔴 SP-HK 绕过网络 | `AutoTestDriver.RunSpHkCaseCoroutine` | 改用 `TrySendTidyRequest` + `NetworkTestProbe.HasValidReply` |
| 🔴 SP-CONS 单页采样 | `AutoTestDriver.RunSpConsCaseCoroutine` | 改用 `IndependentSnapshot.CaptureAllPages` 全页快照 |
| 🔴 SP-SD 硬编码 PASS | `AutoTestDriver.RunSpSdCoroutine` | 改用 `QueuedTidyRequest` + 真实 `Shutdown()` + `ShutdownTestProbe.IsPassing` |
| 🔴 Thread.Sleep 阻塞 | `AutoTestDriver` 全文 | 改用 `yield return new WaitForSecondsRealtime` + `yield return null` |
| 🟡 Fixture 不验证 | `FixtureValidator.TryValidateAllRequiredShapes` | 前置验证，不满足标记 BLOCKED（非 PASS/SKIPPED） |
| 🟡 版本不一致 | `AssemblyInfo.cs` + `BepInPlugin` | 统一 2.0.6.13 |

### 新增能力（Codex §3-§4 对照）

| 能力 | 实现位置 | Codex §对照 |
| --- | --- | --- |
| 主线程断言 + 玩家级冷却 | `TidyThreadAndRateGuard` | §3.1 生产代码安全边界 |
| 全页独立只读快照 | `IndependentSnapshot.CaptureAllPages` | §3.2 不调用 solver/CommitPage/mappings/MutationJournal |
| 守恒断言（全页多重集合） | `IndependentSnapshot.SameItemMultiset` | §3.2 id+amount+quality+state |
| 布局断言 | `IndependentSnapshot.AllPagesInBoundsAndNonOverlapping` | §3.2 无越界、无重叠 |
| 规范化 JSON 导出 | `IndependentSnapshot.WriteCanonicalJson` | §3.2 UTF-8 无 BOM |
| 网络回环探针 | `NetworkTestProbe.RecordCommit/RecordHotkey/HasValidReply` | §3.3 (requestId, CommitResult+HotkeyFlowResult) |
| 关闭在途观察 | `ShutdownTestProbe.Capture/IsPassing` | §3.4 5 项机械通过条件 |
| Fixture 前置验证 | `FixtureValidator.TryValidateAllRequiredShapes/TryValidateHotkeyFixture` | §4 SP-CONS/SP-HK fixture |
| 协程化测试驱动 | `AutoTestDriver.RunAllSuitesCoroutine` | §4 通用负向约束（禁用 Thread.Sleep） |
| 真实 Shutdown drain | `AutoTestDriver.RunSpSdCoroutine` + `MainThreadDispatcher.Shutdown` | §4 SP-SD 真实 drain |
| completion.marker | `AutoTestDriver.WriteCompletionMarker` | 测试完成可靠信号 |
| try/finally DLL 恢复 | `run_tests.ps1` | 失败也能恢复 Release DLL |
| 失败退出码 | `run_tests.ps1` | 0=PASS / 1=ENV / 2=TEST_FAIL / 3=RESTORE_FAIL |

### Codex §4 负向约束遵守

| 约束 | 实现 |
| --- | --- |
| 1. 不恢复 ItemsTryAddItemPatch | ✅ 仅 #if TIDY_TEST_HARNESS 新增测试代码，不修改 Patch |
| 2. 不修改 LaunchMultiplayerNet | ✅ 仅使用 LMN 公开 API |
| 3. TestHarness 不编译进 Release | ✅ 全部 #if TIDY_TEST_HARNESS 包裹 |
| 4. 不降低 1.5s 限流 / 不绕过 PlayerOperationGate | ✅ 直接调用 ManualTidyService.TidyAllPlayerPages，不绕过限流 |
| 5. 请求间隔 >= 2.0 秒 | ✅ RequestIntervalSeconds = 2.5f |
| 6. 出现意外 Rejected 立即停止 | ✅ RunSpConsCase 标记 FAIL 并返回 |



---

## 0. v2.0.6.5 编译验证

- **编译命令**：`dotnet build LaunchInventoryTidy.csproj -c Release -nologo`
- **结果**：0 errors / 0 warnings
- **编译耗时**：1.71 秒
- **DLL 大小**：128,512 bytes（v2.0.6.4 为 122,880 bytes，+5,632 bytes）
- **SHA-256**：`88B5ABF6868153B431CBFC184F7F558F071948D50F60D6D87859FABE747957ED`
- **MVID**：`ed2fd7d3-77d1-4024-b014-a20fc6296060`
- **AssemblyVersion**：2.0.6.5
- **AssemblyFileVersion**：2.0.6.5
- **BepInPlugin 版本**：2.0.6.5
- **写入时间**：2026-07-30 19:00:42
- **前置库**：LaunchMultiplayerNet v3.3.4.0（SHA-256 `4C73966C4358EDD31EA9FC39E442B7B47A7E0382EDF8CB7F81B097C48C287842`）

---

## 0a. v2.0.6.5 修订摘要（Codex v2.0.6.4 审计 5 项阻断项对照）

### Codex v2.0.6.4 审计驳回证据

- 审计结果：FAIL（5 项阻断项）
- 严重度分布：2 Critical + 3 Medium
- 审计原意：
  > 1. **Critical**：新增 post-commit 状态写前比较覆盖所有回滚路径；若页在 commit 后被修改，永远不可清空/重建，应返回 `ConcurrentMutationAfterCommit`
  > 2. **Critical**：以显式写状态机替代 catch 推断 `MutationStarted`（`NotStarted` / `MutationMayHaveStarted` / `Committed`）；不确定状态 = 非破坏性安全失败
  > 3. **Medium**：建立主线程队列 + 库存操作 lease（owner/requestId 生命周期）；纠正"ACK 释放"文档错误
  > 4. **Medium**：以 session nonce + requestId 替代仅随机起点的 32-bit requestId 账本键，并同步升级 ACK/响应关联协议
  > 5. **Medium**：U3DS 收敛证据升级到全量库存状态，或降级 ACK 语义；"target ID exists" 不能证明库存已同步

### v2.0.6.5 修复项清单（2 项 Critical + 3 项 Medium）

| 编号 | 等级 | 问题 | 修复 |
|---|---|---|---|
| Critical-1 | Critical | 回滚已 Committed 页前未做"写前比较"。若玩家在整理过程中通过另一线程（如 UI 拖拽）修改了已 Committed 的页，回滚会覆盖合法的并发变更 | `ManualTidyService` 新增 `TryRollbackPageWithPreCheck` / `TryRollbackRangeWithPreCheck` / `TryRollbackAllWithPreCheck`，回滚前调用 `ValidatePostCommitUnchanged` 比对 (x, y, rot, id, amount, quality, state[]) 全字段；任何不匹配 = 并发修改 = 拒绝回滚 + 返回 `ConcurrentMutationAfterCommit` 安全隔离；`TidyCommitResult` 新增枚举值 `ConcurrentMutationAfterCommit = 3`；安全隔离不熔断（非玩家过错），需人工处置 |
| Critical-2 | Critical | `CommitPage` 通过 `catch (Exception)` 推断 `MutationStarted` 不可靠。若 `addItem` 抛异常但部分副作用已生效，catch 块无法准确反映状态 | `ManualTidyService` 引入 `CommitPageResult` 显式三态枚举：`NotStarted`（未调用任何 removeItem/addItem）-> `MutationMayHaveStarted`（已调用部分 removeItem，addItem 未全部完成）-> `Committed`（全部 addItem 完成，post-commit 快照已捕获）；状态机在 `try` 块内显式推进，`catch` 块返回当前 `state` 不再推断；调用方按显式状态选择回滚策略 |
| Medium-3 | Medium | 网络回调直接在 LMN 线程执行 `ManualTidyService.TidyPage`，绕过 Unity 主线程断言。同时 lease 释放声明为"ACK 后释放"，实际 ACK 由独立的 `TidyTransactionManager` 跟踪，lease 应在响应发送后立即释放 | 新建 `MainThreadDispatcher.cs`（线程安全 `Queue<Action>` + `ProcessAll()` 主线程串行执行）；`LaunchInventoryTidyPlugin.Update()` 每帧调用 `MainThreadDispatcher.ProcessAll()`；`HandleRequestTidyV2` 拆分为网络回调（读取 + 验证 + lease 获取 + 入队）+ `ExecuteTidyRequestOnMainThread`（限流/熔断/账本/玩家解析/快捷键验证/服务/响应/lease 释放）；`PlayerOperationGate.Lease` 升级为 `(Owner, RequestId, AcquiredAt)` 结构，`TryAcquire(steamId, requestId)` 绑定 requestId 防止 A 事务错释放 B 事务的 lease；lease 在主线程 `finally` 释放，不等 ACK；文档修正："ACK 后释放"错误，实际为"响应发送后释放" |
| Medium-4 | Medium | 32-bit 随机起点 requestId 不防跨会话重放。客户端重启后可重放已捕获的原始 requestId 包；账本键仅 `(SteamID, requestId)`，无 session 维度 | 新建 `ClientSessionNonce.cs`：`RandomNumberGenerator` 生成 64-bit 加密随机 nonce，进程生命周期内不变，重启后变更，0 表示未初始化或无效（拒绝）；`LaunchInventoryTidyPlugin.Awake` 在 `RegisterHandlers` 前调用 `ClientSessionNonce.Initialize()`；V3 协议字节布局升级：所有 4 条消息（RequestTidyV2 / TidyCommitted / InventoryAppliedAck / TidyHotkeyResult）携带 `[sessionNonce:8]`；`RequestLedger.LedgerEntry` / `PendingHotkeyRestore` 新增 `SessionNonce` 字段；`RequestLedger.TryBegin` / `MarkResult` / `TidyTransactionManager.Get` / `Remove` 升级为 `(steamId, sessionNonce, requestId)` 复合键；`ClientPendingState` / `ClientHotkeyResultPending` 升级为 `Dictionary<(ulong, uint), ...>`；nonce 不匹配的旧 requestId 被视为新请求（跨会话重放保护）；`HotkeyResultWaitBehaviour.StartWait` 升级为 `(sessionNonce, requestId, timeoutSeconds)` 签名 |
| Medium-5 | Medium | ACK 命名为"InventoryAppliedAck"但客户端仅检查快捷键目标物品的 id 匹配，不能证明全量库存已同步。"target ID exists" 不构成同步证据 | `MSG_INVENTORY_APPLIED_ACK = 4` 字节常量保持稳定（线格式兼容），但文档明确声明语义降级为"HotkeyFlowAck"（快捷键流程 ACK）；`ConvergenceCheckBehaviour` 文档修订：本检查仅证明"快捷键目标物品已到达新坐标（id 匹配）"，不证明全量库存已同步；全量库存同步的最终证据由服务器后续发送的 `TidyHotkeyResult` 消息（含 `restoredCount` + `verifiedCount` 字段）提供；ACK 日志输出明确标注"[语义：快捷键流程 ACK，非全量库存已应用]" |

### v2.0.6.5 新建文件

| 文件 | 用途 |
|---|---|
| `MainThreadDispatcher.cs` | Medium 3：线程安全 `Queue<Action>` + `ProcessAll()` 主线程串行执行 + `ClearAll()` 卸载清理 |
| `ClientSessionNonce.cs` | Medium 4：64-bit 加密随机 nonce + `RandomNumberGenerator` + 进程生命周期内不变 + 0 表示无效 |

### v2.0.6.5 修改文件清单

| 文件 | 修改类型 | 核心改动 |
|---|---|---|
| `ManualTidyService.cs` | 修改 | Critical 1：`TryRollbackPageWithPreCheck` / `TryRollbackRangeWithPreCheck` / `TryRollbackAllWithPreCheck` 写前比较 + `ConcurrentMutationAfterCommit` 安全隔离；Critical 2：`CommitPageResult` 显式三态状态机（`NotStarted` / `MutationMayHaveStarted` / `Committed`） |
| `PlayerOperationGate.cs` | 修改 | Medium 3：`Lease` 结构升级为 `(Owner, RequestId, AcquiredAt)`；`TryAcquire(steamId, requestId)` / `Release(steamId, requestId)` 绑定 requestId 防错配 |
| `ManualTidyNetwork.cs` | 修改 | Medium 3：`HandleRequestTidyV2` 拆分网络回调 + 主线程执行 + `CapturedTidyRequest` 容器 + `ExecuteTidyRequestOnMainThread` + lease finally 释放；Medium 4：V3 协议字节布局（4 条消息携带 `[sessionNonce:8]`）+ 复合键调用 + 客户端 pending 复合键；Medium 5：ACK 语义降级文档 + 日志输出"[语义：快捷键流程 ACK，非全量库存已应用]" |
| `TidyTransaction.cs` | 修改 | Medium 4：`PendingHotkeyRestore.SessionNonce` 字段 + 构造函数签名 + `Get` / `Remove` 复合键 |
| `TidyFaultCircuit.cs` | 修改 | Medium 4：`RequestLedger.LedgerEntry.SessionNonce` 字段 + `TryBegin` / `MarkResult` 复合键 + nonce 不匹配视为新请求（跨会话重放保护） |
| `HotkeyResultWaitBehaviour.cs` | 修改 | Medium 4：`StartWait(ulong sessionNonce, uint requestId, float timeoutSeconds)` 签名 + `IsPending` / `ClearPending` 复合键 |
| `ConvergenceCheckBehaviour.cs` | 修改 | Medium 5：文档修订，明确收敛检查证明范围（id 匹配 ≠ 全量库存同步），全量证据来源为 `TidyHotkeyResult` 消息 |
| `LaunchInventoryTidyPlugin.cs` | 修改 | Medium 3：`Update()` 调用 `MainThreadDispatcher.ProcessAll()` + `OnDestroy` 调用 `MainThreadDispatcher.ClearAll()`；Medium 4：`Awake` 调用 `ClientSessionNonce.Initialize()`；v2.0.6.5 版本横幅 + 加载日志 |
| `LaunchInventoryTidy.csproj` | 修改 | 新增 `MainThreadDispatcher.cs` + `ClientSessionNonce.cs` 编译项 |
| `Properties/AssemblyInfo.cs` | 修改 | 版本 2.0.6.5 |

### v2.0.6.5 关键技术事实

- **`CommitPageResult` 状态机**：`NotStarted` -> `MutationMayHaveStarted`（removeItem 已调用）-> `Committed`（addItem 全部完成 + post-commit 快照已捕获）；状态在 `try` 块内显式推进，`catch` 块返回当前 state 不再推断
- **`ConcurrentMutationAfterCommit` 安全隔离**：不熔断（非玩家过错），不回滚（保护合法并发变更），服务器返回 `ConcurrentMutationAfterCommit = 3` 给客户端，玩家进入安全隔离状态，需人工处置
- **`MainThreadDispatcher`**：`Queue<Action>` + `lock(_lock)` + `ProcessAll()` 主线程串行执行；`Enqueue` 可从任意线程调用；`ProcessAll` 仅由 `LaunchInventoryTidyPlugin.Update()` 在 Unity 主线程调用
- **`PlayerOperationGate.Lease`**：`(Owner, RequestId, AcquiredAt)` 三字段结构；`TryAcquire(steamId, requestId)` 绑定 requestId；`Release(steamId, requestId)` 仅当 (steamId, requestId) 匹配时才释放，防止 A 事务的 finally 错释放 B 事务的 lease
- **`ClientSessionNonce.Value`**：`RandomNumberGenerator` 生成 64-bit 加密随机 nonce；进程启动时生成一次（`Initialize` 防重入 via `_initialized` flag）；生命周期内只读；0 表示未初始化或无效（拒绝）
- **V3 协议字节布局**：
  - RequestTidyV2：`[msgType:1][version:1=3][sessionNonce:8][requestId:4][page:1][mode:1][sortDescending:1][hotkeyCount:1][hotkeys:hotkeyCount*6]`
  - TidyCommitted：`[msgType:1][sessionNonce:8][requestId:4][result:1][mappingCount:1][mappings:mappingCount*7]`
  - InventoryAppliedAck：`[msgType:1][sessionNonce:8][requestId:4]`（13 字节固定）
  - TidyHotkeyResult：`[msgType:1][sessionNonce:8][requestId:4][restoredCount:1][clearedCount:1][failedCount:1][verifiedCount:1][failedIndices:failedCount*1]`
- **复合键**：`RequestLedger` / `TidyTransactionManager` / `ClientPendingState` / `ClientHotkeyResultPending` 全部使用 `(sessionNonce, requestId)` 复合键；nonce 不匹配的旧 requestId 被视为新请求（跨会话重放保护）
- **ACK 语义**：字节常量 `MSG_INVENTORY_APPLIED_ACK = 4` 保持稳定（线格式兼容），文档明确声明语义为"HotkeyFlowAck"；全量库存同步证据由 `TidyHotkeyResult` 消息的 `restoredCount` + `verifiedCount` 字段提供
- **lease 生命周期修正**：`TryAcquire`（网络回调入口）-> 持有（Prepare -> Commit -> Verify -> 发送 TidyCommitted）-> Release（主线程 finally，响应发送后立即释放，不等 ACK）；ACK 由独立的 `TidyTransactionManager` 跟踪

### v2.0.6.5 三场景裁决对照

| 场景 | v2.0.6.4 状态 | v2.0.6.5 状态 | 主要变化 |
|---|---|---|---|
| 单人游玩 | 🟡 已实现，静态待审 | 🟢 **已实现，静态待审（动态测试待放行）** | Critical 1+2 修复后回滚路径安全；Medium 3 主线程调度队列保证 Unity 主线程串行执行；Medium 4 V3 nonce 防跨会话重放；Medium 5 ACK 语义诚实化 |
| U3DS | 🟡 已实现，静态待审 | 🟢 **已实现，静态待审（动态测试待放行）** | 网络远端路径不变；V3 协议双端兼容；ACK 语义降级 + HotkeyResult 全量证据提供更准确的同步状态 |
| SteamP2PFriends | 🟡 已实现，静态待审 | 🟢 **已实现，静态待审（动态测试待放行）** | LMN 3.3.4.0 已支持 listen host loopback；V3 nonce 防跨会话重放；主线程调度队列保证 listen host 模式正确串行执行 |

### v2.0.6.5 允许的下一步

1. 单机动态测试：UX-G1..G6 + HK-1..5 + SAFE-1..2 + 故障注入（SAFE-5..8）
2. U3DS 双机动态测试：HK-6..8 + SAFE-3..4 + SAFE-7..8 + SEC-1..7 + PERF-1
3. V3 协议互操作性测试：客户端 V3 + 服务器 V3 双端兼容
4. 跨会话重放测试：客户端重启后 nonce 变更 + 旧 nonce 请求被拒绝
5. 并发修改安全测试：在 Prepare/Commit 间拖拽物品触发 `ConcurrentMutationAfterCommit`
6. 测试通过后提交 Codex 下一次审计裁决

---

## 0b. v2.0.6.4 历史修订记录（保留参考）

---

## 0c. v2.0.6.4 编译验证（历史）

- **编译命令**：`dotnet build LaunchInventoryTidy.csproj -c Release -nologo`
- **结果**：0 errors / 0 warnings
- **DLL 大小**：122,880 bytes
- **SHA-256**：（v2.0.6.4 历史记录）
- **AssemblyVersion**：2.0.6.4
- **BepInPlugin 版本**：2.0.6.4
- **前置库**：LaunchMultiplayerNet v3.3.4.0

---

## 0d. v2.0.6.4 修订摘要（Codex v2.0.6.3 审计阻断项对照，已归档）

### v2.0.6.4 修复项清单（历史）

| 编号 | 等级 | 问题 | 修复 |
|---|---|---|---|
| P1-1 | Critical | `CommitPage` 抛 `InvalidOperationException` 在 catch 块中被吞掉，调用方无法区分 `NotStarted` / `MutationStarted` | `CommitPage` 改用 `CommitPageResult` 枚举返回值（`NotStarted` / `MutationMayHaveStarted` / `Committed`），不再抛异常 |
| P1-2 | Critical | `NotStartedInventoryChanged` 分支回滚所有前序页，包括未提交的页面 | `TidyAllPlayerPages` 追踪 `lastCommitStartedIndex`，`NotStartedInventoryChanged` 仅回滚 0..i-1 已 Committed 的前序页（原子性） |
| P1-3 | Critical | `MutationStarted` 分支回滚当前页 + 前序已 Committed 页，但未验证前序页是否被并发修改 | v2.0.6.5 Critical 1 已修复：`TryRollbackRangeWithPreCheck` 写前比较 + `ConcurrentMutationAfterCommit` 安全隔离 |
| P1-4 | Medium | lease 未绑定 requestId，A 事务的 finally 可能错释放 B 事务的 lease | v2.0.6.5 Medium 3 已修复：`PlayerOperationGate.Lease` 升级为 `(Owner, RequestId, AcquiredAt)` |
| P1-5 | Medium | 主线程断言失败触发持久熔断，过激 | 主线程断言失败返回 `Rejected`，不再触发持久熔断 |
| P1-6 | Medium | requestId 使用固定值 1 初始化，客户端重启后可能复用旧 requestId | `_nextRequestId` 使用 `RandomNumberGenerator` 加密随机初始化（v2.0.6.5 Medium 4 进一步升级为 V3 nonce） |

---

## 0b. v2.0.6.1 历史修订记录（保留参考）

---

## 0c. v2.0.6.1 编译验证（历史）

- **编译命令**：`dotnet build LaunchInventoryTidy.csproj -c Release -nologo`
- **结果**：0 errors / 0 warnings
- **DLL 大小**：104,448 bytes（v2.0.6.0 为 103,936 bytes，+512 bytes）
- **SHA-256**：`D690F70779F525D484171E2FA7D3B5928CCBD2984A4F74A8BEC4774E9389EF4E`
- **MVID**：`1f17ea4c-b892-4ea6-a096-e749d2679aa9`
- **AssemblyVersion**：2.0.6.1
- **BepInPlugin 版本**：2.0.6.1
- **前置库变更**：✅ LaunchMultiplayerNet 从 3.2.0.0 升级到 **3.3.2.0**
  - LMN DLL 大小：23,552 bytes
  - LMN SHA-256：`59AE6C3152D7B1654000375B05A4D093A1E5DC649D27BAA06D76FA17E7111850`
  - LMN FileVersion：3.3.2.0
  - LMN AssemblyVersion：3.2.0.0（二进制兼容，未破坏现有插件 API）
  - 新增 API：`UnregisterServerHandler` / `UnregisterClientHandler` / `IsLocalClient` / `LoopbackToClient`
  - `SendToClient` 与 `BroadcastToAllClients` 自动检测 listen host 本地客户端并走 `LoopbackToClient` -> `ModRouter.TryHandleFromServer`
  - `LoopbackToServer` 用于 SP / 服务器自调 SendToServer 时的本地派发

---

## 0a. v2.0.6.1 修订摘要（第二阶段 LMN 集成对照）

### 第二阶段目标

用户宣布重置工作阶段为"第二阶段：整理插件单机与 U3DS 环境闘环"：
1. 避开 P2P（继续不启用 SteamP2PFriends）
2. 在最简单的场景下彻底解决用户反馈的整理问题
3. 拉取 LaunchMultiplayerNet 最新兼容版本
4. 使用其新 API 修复整理问题
5. 在单人游玩 + 标准 U3DS 服务器环境进行功能自测

### v2.0.6.1 修复项清单（3 项）

| 编号 | 等级 | 问题 | 修复 |
|---|---|---|---|
| P0-2-resolved | Critical（跨项目，原 v2.0.6 P0-2） | 原 LMN 3.2 无 server->local client 回环，SP/listen host 链路阻断 | LMN 升级到 3.3.2.0：`SendToClient` 与 `BroadcastToAllClients` 检测到 `IsLocalClient(client)` 时自动走 `LoopbackToClient`（构造完整 mod packet -> `ModRouter.TryHandleFromServer` -> `HandleClientPacket`），listen host 本地客户端不再依赖 vanilla `Loopback.TransportConnection_Loopback.Send`（抛 NotSupportedException）；SP 模式下 `SendToServer` 走 `LoopbackToServer` 直接派发到 `ServerHandlers` |
| P1-2-resolved | Medium（原 v2.0.6 P1-2） | `SendToServer` 内部吞掉失败，外层 try-catch 无法真正撤销 pending | LMN 3.3.2.0 SendToServer 仍内部捕获 transport.Send 异常并日志化；LaunchInventoryTidy 注释修订为"发送未抛异常时保留 pending"；移除"发送失败已回滚"措辞；SP/listen host 模式下 `LoopbackToServer`/`LoopbackToClient` 的 handler 异常已在 LMN 内部 catch（`ModTransport loopback handler crash`），不会向调用方传播 |
| P1-8-resolved | Low（原 v2.0.6 P1-8） | 原 LMN 3.2 不支持本地回环时输出可达性警告 | LMN 3.3.2.0 已实现 listen host loopback；移除 `SendTidyV2Request` 中的本地主机模式可达性警告；SP/listen host 模式响应可正常回送 |
| Shutdown-真注销 | 新增（v2.0.4 遗留） | 原 Shutdown 使用 `_shuttingDown=true` 守护但 handler 残留在 ModTransport 字典中，插件卸载后 LMN 仍可能调用已回收的静态状态 | Shutdown() 现在调用 `ModTransport.UnregisterServerHandler(ModChannels.TidyPage, HandleServerMessage)` 与 `UnregisterClientHandler(ModChannels.TidyPage, HandleClientMessage)` 真正从 LMN 字典注销；`_shuttingDown=true` 保留作为双重保险，防止注销竞态期间消息进入 |

### v2.0.6.1 修改文件清单

| 文件 | 修改类型 | 核心改动 |
|---|---|---|
| `ManualTidyNetwork.cs` | 修订 | Shutdown() 调用 `UnregisterServerHandler` / `UnregisterClientHandler` 真正从 LMN 字典注销；移除 `SendTidyV2Request` 的 P1-8 本地主机模式 LMN 可达性警告；注释更新为 v2.0.6.1 + LMN 3.3.2.0 集成 |
| `LaunchInventoryTidyPlugin.cs` | 修订 | 版本号 2.0.6.1 + BepInPlugin 描述更新 + 加载日志新增 v2.0.6.1 修订项 |
| `Properties/AssemblyInfo.cs` | 修订 | 版本 2.0.6.1 |
| `Libs/LaunchMultiplayerNet.dll` | 升级 | 从 3.2.0.0 升级到 3.3.2.0（23,552 bytes，SHA-256 `59AE6C...11850`） |

### v2.0.6.1 三场景裁决对照

| 场景 | v2.0.6 状态 | v2.0.6.1 状态 | 主要变化 |
|---|---|---|---|
| 单人游玩 | 🟡 已实现，静态待审（LMN 3.2 无本地回环阻断） | 🟢 **已实现，动态待测**（LMN 3.3.2.0 已支持本地回环） | SP 模式 SendToServer 走 LoopbackToServer -> ServerHandlers 直派发；服务器响应 SendToClient 走 LoopbackToClient -> ModRouter.TryHandleFromServer -> ClientHandlers；本地主机链路不再阻断，可进入单机动态测试 |
| U3DS | 🟡 已实现，静态待审 | 🟡 已实现，动态待测（前置依赖：SP 通过） | U3DS 远端网络路径不变；新增 listen host loopback 不影响 dedicated server 链路；网络远端路径可进入 U3DS 双机测试 |
| SteamP2PFriends | 🟡 已实现，静态待审（LMN 3.2 阻断） | 🟡 已实现，但**第二阶段不测试**（用户决策避开 P2P） | LMN 3.3.2.0 已支持 listen host loopback，技术上 SteamP2PFriends 链路可正常工作；但用户决策第二阶段仅验证 SP + U3DS，SteamP2PFriends 留待第三阶段 |

### v2.0.6.1 允许的下一步

1. 单机动态测试：UX-G1..G6 + HK-1..5 + SAFE-1..2（v2.0.0 设计矩阵）
2. U3DS 双机动态测试：HK-6..8 + SAFE-3..4 + PERF-1
3. 测试通过后提交第七次审计裁决

---

## 0b. v2.0.6 历史修订记录（保留参考）

---

## 0c. v2.0.6 编译验证（历史）

- **编译命令**：`dotnet build LaunchInventoryTidy.csproj -c Release -nologo`
- **结果**：0 errors / 0 warnings
- **DLL 大小**：103,936 bytes
- **SHA-256**：`7018DD04E9836C2CB222330C7B87C8F462E99DB8631D9EC9BEFAF6781E94BD2F`
- **AssemblyVersion**：2.0.6.0
- **BepInPlugin 版本**：2.0.6
- **前置库变更**：无（仍依赖 LaunchMultiplayerNet 3.2.0.0；跨项目接口需求文档已记录 LMN 3.3 待交付内容：Unregister API + server->local client 回环 + Send API 返回 SendResult）

---

## 0d. v2.0.6 修订摘要（六次审计反馈对照）

### 六次审计驳回证据

- 编译：0 errors / 0 warnings
- DLL（v2.0.5 提交）：94,208 bytes
- SHA-256：`D2C6EB42A190CB28B232C64F284C1502F87E4CB2C7C00CEA4EC91323F2DD2D19`
- 审计报告：`.audit/v2.0.5-static-audit-20260729/Codex-v2.0.5第六次静态审计与三场景指导报告-20260729.md`

### v2.0.6 修复项清单（2 项 Critical + 7 项 Medium + 2 项 Low）

| 编号 | 等级 | 问题 | 修复 |
|---|---|---|---|
| P0-1 | Critical | 首次初始化先把主文件移动到正式路径，之后才写 `.initialized`。主文件与标记不是一个原子事务。若进程在两步之间终止，下次启动因主文件有效而进入 HEALTHY，但标记永久缺失；以后主文件被删除时会被误判为"从未初始化"，自动创建空文件，安全锁可能 fail-open | `EnsureFirstBootInitialized` 修订为 marker 先于 main 落盘（marker.tmp + Flush(true) + Move，然后 state.tmp + Flush(true) + Move）；`LoadInternal` 加载成功后验证/补建 marker（失败 -> DEGRADED）；marker 存在 + main/backup 无效 -> DEGRADED（不自动空初始化） |
| P0-2 | Critical | 当前 LMN 3.2 仍无 server->local client 回环。SP 和 SteamP2PFriends 主机即使完成库存 Commit，也无法可靠收到 TidyCommitted 或 TidyHotkeyResult | 跨项目依赖，本插件无法修复。已记录到接口需求文档，等待 LMN 升级。本插件 v2.0.6 P1-8 在 `SendTidyV2Request` 检测本地主机模式时输出警告告知用户响应可能不可达；`AUDIT_CHECKLIST` 明确声明 SP/SteamP2PFriends 主机链路阻断状态 |
| P1-1 | Medium | v2.0.5 删除了 `restoreVerified` 字段并启用 `MissingMemberHandling.Error`，但 `FORMAT_VERSION` 仍为 1。任何 v2.0.4 生成的 version=1 文件都会因旧字段被拒绝 | `FORMAT_VERSION` 升级到 2；新增 `PersistentFaultFileV1` / `PersistentFaultRecordV1` DTO 用于读取 v1 文件；v1 迁移门：所有记录的 `restoreVerified` 必须为 false，否则拒绝（DEGRADED）；加载 v1 文件成功后原子写为 v2 格式（迁移） |
| P1-2 | Medium | `SendToServer` 内部吞掉失败，外层 try-catch 无法真正撤销 pending | `SendTidyV2Request` 注释修订为"发送未抛异常时保留 pending"，不再声称"发送失败已回滚"；新增本地主机模式 LMN 可达性检查（P1-8）；真正的发送失败检测需 LMN 升级 Send API 返回 SendResult 或抛出可识别异常 |
| P1-3 | Medium | `/tidy_fault_recover` 会把文件记录注入现有 Dictionary，但不会以验证后的文件快照原子替换当前持久熔断集合。管理员修复文件并删除某条记录后执行 recover，旧内存记录仍保留 | 新增 `TidyFaultCircuit.ReplacePersistentFromSnapshot(records)`：在单一锁内保留临时熔断、原子替换全部持久熔断；`LoadInternal` 改用此方法；返回最终内存集合数量 |
| P1-4 | Medium | `MSG_TIDY_HOTKEY_RESULT` 解析 requestId 后没有验证它是否对应本机正在等待的快捷键结果。延迟、重复或错误 requestId 的旧结果也会触发警告 | 新增 `ClientHotkeyResultPending` 状态类（10s TTL）；ACK 发送前 `Register(requestId)`；`HandleTidyHotkeyResultFromServer` 验证 requestId 匹配后才处理；新增 `HotkeyResultWaitBehaviour` MonoBehaviour 超时监视器（3s 超时提示"结果未知"）；未知/重复/过期 requestId 被拒绝 |
| P1-5 | Medium | ACK 恢复逐项调用 `ServerBindItemHotkey` 后立即计为成功，没有验证最终绑定；任意一次 Bind/Clear 抛异常会跳到整个方法的外层 catch | 每个快捷键独立 try-catch（失败加入 failedIndices 后继续其余项）；新增 `CanVerifyHotkeyState` 检测（DS 端 hotkeys=null 不可验证）；新增 `VerifyHotkeyBound` 逐项验证 id/page/x/y；协议新增 `verifiedCount` 字段区分"绑定调用成功"与"最终状态验证通过"；finally 中完成事务状态转换 |
| P1-6 | Medium | HotkeyResult 没有验证业务不变量；客户端收到 clearedCount>0 && failedCount==0 时仍显示"全部快捷键已恢复" | 服务端发送前验证：restoredCount + clearedCount == entries.Count、clearedCount == failedIndices.Count、failedIndices 全部 < 8 且唯一、verifiedCount <= restoredCount；客户端接收时验证相同不变量，任何矛盾拒绝；客户端成功条件必须同时满足 failed=0 且 cleared=0 |
| P1-7 | Medium | 未授权日志已经节流，但每次拒绝仍调用 `ChatManager.say`。监听服普通玩家可持续触发服务器向其发送富文本拒绝消息，日志洪水转化为聊天/网络洪水 | `SecurityLogLimiter.LogRejection` 返回 bool（true=未节流可响应，false=节流窗口内跳过）；三个 `/tidy_*` 命令的未授权分支使用返回值决定是否调用 `ChatManager.say`，与日志共用同一 token bucket |
| P1-8 | Low | `ReadMapping` 读取 reserved 字段后直接丢弃，没有要求为 0。未来协议扩展或畸形包无法区分 | `ReadMapping` 改为 `TryReadMapping`（返回 bool + out 参数）；reserved != 0 时返回 false，调用方拒绝整条映射并记录 `committed_invalid_reserved` 节流日志 |
| P1-9 | Low | 清单仍在外部审计前写"v2.0.5 完成全部修复"，与当前证据等级冲突 | 本文件改为四级状态系统：已实现 / 静态待审 / 动态待测 / 外部通过；v2.0.6 状态明确标注为"已实现，静态待审"；历史版本状态按实际审计裁决标注 |

### v2.0.6 修改文件清单

| 文件 | 修改类型 | 核心改动 |
|---|---|---|
| `TidyFaultCircuitPersistence.cs` | 重写 | P0-1 marker 先于 main 落盘 + P1-1 FORMAT_VERSION=2 + v1->v2 迁移（TryLoadV1File/TryLoadV2File）+ P1-3 LoadInternal 改用 ReplacePersistentFromSnapshot + 加载后补建 marker |
| `TidyFaultCircuit.cs` | 扩展 | P1-3 新增 `ReplacePersistentFromSnapshot` 原子替换内存状态；P1-7 `LogRejection` 返回 bool 用于聊天响应节流 |
| `ManualTidyNetwork.cs` | 重写 | P1-4 `ClientHotkeyResultPending` + `HotkeyResultWaitBehaviour` 启动；P1-5 ACK 逐项 try-catch + `CanVerifyHotkeyState` + `VerifyHotkeyBound` + `verifiedCount` 字段；P1-6 HotkeyResult 业务不变量验证；P1-8 `TryReadMapping` reserved 验证；P1-2 LMN 可达性检查 + 注释修订 |
| `HotkeyResultWaitBehaviour.cs` | 新建 | P1-4 客户端 ACK 发送后等待 HotkeyResult 的超时监视器（3s 超时提示"结果未知"） |
| `CommandTidyFaults.cs` | 修订 | P1-7 三个 `/tidy_*` 命令未授权分支使用 `LogRejection` 返回值节流 `ChatManager.say` |
| `LaunchInventoryTidyPlugin.cs` | 修订 | 版本号 2.0.6 + 加载日志新增 v2.0.6 修订项 |
| `Properties/AssemblyInfo.cs` | 修订 | 版本 2.0.6.0 |
| `LaunchInventoryTidy.csproj` | 修订 | 新增 `HotkeyResultWaitBehaviour.cs` 编译项 |

---

## 0b. v2.0.6 三场景裁决对照

| 场景 | v2.0.5 裁决 | v2.0.6 状态 | 主要变化 |
|---|---|---|---|
| 单人游玩 | 🔴 不放行 | 🟡 已实现，静态待审 | marker/main 提交顺序修复；但 LMN 3.2 无 server->local client 回环仍阻断动态测试 |
| U3DS | 🔴 不放行完整测试 | 🟡 已实现，静态待审 | 格式迁移、恢复快照、ACK 异常隔离、结果关联均已实现；网络远端路径可进入定向测试 |
| SteamP2PFriends | 🔴 不放行 | 🟡 已实现，静态待审 | LMN 3.2 无本地回环仍阻断；拒绝命令聊天响应节流已实现 |

### v2.0.6 允许的下一步（Codex v2.0.5 第六次审计 §6）

1. 初始化在 marker/main 两个写入点分别模拟崩溃，重启后不得误判 first boot
2. v1 持久文件迁移到 v2；包含 true/未知字段/截断时必须符合明确策略
3. recovery 前后比较磁盘集合与内存持久集合完全相等
4. 同步 ACK->HotkeyResult 回入、异步延迟、重复、错误 requestId、超时测试
5. Bind/Clear 第 1..8 项逐一抛异常，后续项仍处理且结果准确
6. 等 LaunchMultiplayerNet 正式版本交付后，再执行 SP 与 SteamP2PFriends 主机完整链

---

## 0c. v2.0.5 历史修订记录（保留参考）

---

## 0d. v2.0.5 编译验证（历史）

- **编译命令**：`dotnet build LaunchInventoryTidy.csproj -c Release -nologo`
- **结果**：0 errors / 0 warnings
- **DLL 大小**：94,208 bytes
- **SHA-256**：`D2C6EB42A190CB28B232C64F284C1502F87E4CB2C7C00CEA4EC91323F2DD2D19`
- **AssemblyVersion**：2.0.5.0
- **BepInPlugin 版本**：2.0.5
- **前置库变更**：无（仍依赖 LaunchMultiplayerNet 3.2.0.0，Unregister API 与本地回环需求已移交）

---

## 0a. v2.0.5 修订摘要（五次审计反馈对照）

### 五次审计驳回证据

- 编译：0 errors / 0 warnings
- DLL（v2.0.4 提交）：88,064 bytes
- SHA-256：`5C021ECE3459894E12044B2CD4E2A93EC44F790EE9C101E92C974F8FEA812B69`
- 审计报告：`.audit/v2.0.4-static-audit-20260729/Codex-v2.0.4第五次静态审计与三场景指导报告-20260729.md`

### v2.0.5 修复项清单（5 项 Critical + 5 项 Medium + 1 项 Low）

| 编号 | 等级 | 问题 | 修复 |
|---|---|---|---|
| P0-1 | Critical | 全新安装没有 `persistent_faults.json` 和 `.bak` 时被当作双文件加载失败，立即进入 `GlobalFaultPersistenceDegraded=true` | `TidyFaultCircuitPersistence` 引入 UNINITIALIZED/HEALTHY/DEGRADED 三状态机；`EnsureFirstBootInitialized()` 检测主/备/`.initialized` 标记均不存在时，原子创建合法空文件 + `.initialized` 标记；以后文件消失可判为安全状态丢失并降级 |
| P0-2 | Critical | `TryClearDegraded()` 没有任何生产调用方或管理员命令；即使管理员修复文件也无法解除降级 | 新增 `CommandTidyFaultRecover` `/tidy_fault_recover` 授权命令；调用 `TryClearDegraded()` 执行完整 Load/Validate 后才清除降级；输出主文件/备份来源和记录数；`/tidy_unfault` 不会隐式清除全局降级 |
| P0-3 | Critical | 服务端每条 `NewPositionMapping` 写 7 字节，客户端按 8 字节校验，存在快捷键映射必然被拒绝 | 新增 `MAPPING_WIRE_SIZE = 7` 单一事实源常量；新增 `WriteMapping` / `ReadMapping` 共用序列化；`SendTidyCommitted` 与 `HandleTidyCommittedFromServer` 统一使用，禁止重复手写数字 |
| P0-4 | Critical | `ClientPendingState.SetPending` 在 `ModTransport.SendToServer` 之后执行，未来同步回环会产生响应竞态 | `SendTidyV2Request` 改为先 `SetPending` 再 `SendToServer`；发送抛异常时 `ClearPending` 撤销；保证未来 LMN 3.3 同步回环正确 |
| P0-5 | Critical | `ResolvePlayerBySteamId` 只扫描 `Provider.clients`，无单机/监听服主机本地分支 | 新增本地分支：`Provider.isServer` 且 `executorId == Provider.server` 且 `Player.LocalPlayer` 有效时返回 LocalPlayer；否则扫描 `Provider.clients`；禁止 remote SteamID 错映射到 LocalPlayer |
| P1-1 | Medium | `LogException` 抑制窗口内仍对每次异常写一行日志，10,000 次异常产生约 10,000 行日志 | 修订为窗口内仅累计 suppressed 计数（不写日志），窗口外输出一次摘要 + 完整堆栈；异常 category 有固定有界集合（MAX_EXCEPTION_CATEGORIES=32） |
| P1-2 | Medium | `restoreVerified=true` 记录通过 schema 但被 `InjectRecords` 静默跳过，损坏文件可少加载安全锁 | 删除冗余 `restoreVerified` 字段；本文件只能包含持久熔断（RestoreVerified=false），所有记录注入时强制为 false；`MissingMemberHandling.Error` 拒绝未知字段 |
| P1-3 | Medium | `File.WriteAllText` 没有 `FileStream.Flush(true)`，耐断电保证被过度声明 | 新增 `WriteFileWithSync(path, content)` 使用 `FileStream + Flush(true)` 真正落盘；写后回读验证；移除"flush"过度声明 |
| P1-4 | Medium | `HotkeyRestoreOutcome` 只覆盖 CriticalFailure 回滚路径，正常 Commit 后 ACK 恢复失败静默清除 | 新增 `MSG_TIDY_HOTKEY_RESULT = 5` 服务器 -> 客户端结果通知；`HandleInventoryAppliedAck` 追踪每个失败项的 HotkeyIndex；`SendTidyHotkeyResult` 发送 restoredCount + clearedCount + failedIndices 列表；客户端 `HandleTidyHotkeyResultFromServer` 处理结果并明确告知用户 |
| P1-5 | Medium | 未授权命令日志未节流，listen server 普通玩家可制造日志洪水；`TidyAdminAuth` 本地主机身份依赖 `PlayerTool.getSteamPlayer` 解析，存在可用性风险 | `CommandTidyFaults` / `CommandTidyUnfault` / `CommandTidyFaultRecover` 未授权日志走 `SecurityLogLimiter.LogRejection` 节流；`TidyAdminAuth` 本地身份先于 `PlayerTool` 解析，DS 端走 remote admin 验证 |
| P1-6 | Medium | LaunchMultiplayerNet 越权修改已回退，handler 真注销和 server->local client 回环仍是未交付依赖 | 保持发布阻断，等待 LMN Agent 独立交付；`ManualTidyNetwork.Shutdown` 继续使用 `_shuttingDown` 守护；v2.0.5 发布说明不把两项列为已解决 |
| P2-L | Low | `AUDIT_CHECKLIST.md` 写"v2.0.4 完成全部修复"但实际存在多项 Critical | 恢复四级证据状态；本次 v2.0.5 清单明确标注"等待六次审计 + 动态测试放行" |

### v2.0.5 新增文件

- 无新文件（`CommandTidyFaultRecover` 加入 `CommandTidyFaults.cs`）

### v2.0.5 修改文件

- `ManualTidyNetwork.cs`：
  - 新增 `MAPPING_WIRE_SIZE = 7` 常量 + `WriteMapping` / `ReadMapping` 助手
  - `SendTidyCommitted` 使用 `WriteMapping`
  - `HandleTidyCommittedFromServer` 使用 `MAPPING_WIRE_SIZE` 与 `ReadMapping`
  - `SendTidyV2Request` 改为先 `SetPending` 再 `SendToServer`，发送异常时 `ClearPending`
  - `ResolvePlayerBySteamId` 增加 SP/Listen Host 本地分支
  - 新增 `MSG_TIDY_HOTKEY_RESULT = 5` + `SendTidyHotkeyResult` + `HandleTidyHotkeyResultFromServer`
  - `HandleInventoryAppliedAck` 追踪每个失败项的 HotkeyIndex 并发送结构化结果
- `TidyFaultCircuitPersistence.cs`：
  - 引入 `UNINITIALIZED/HEALTHY/DEGRADED` 三状态机
  - 新增 `EnsureFirstBootInitialized` + `.initialized` 标记
  - 新增 `WriteFileWithSync` 使用 `FileStream + Flush(true)`
  - `Save` 写后回读验证
  - 删除 `restoreVerified` DTO 字段，`MissingMemberHandling.Error`
  - `TryClearDegraded` 返回 `RecoveryResult` 结构
- `TidyFaultCircuit.cs`：
  - `LogException` 修订为窗口内不写日志，仅累计 suppressed
- `CommandTidyFaults.cs`：
  - 新增 `CommandTidyFaultRecover` 类
  - 三个命令未授权日志走 `SecurityLogLimiter.LogRejection` 节流
- `TidyAdminAuth.cs`：
  - 本地主机身份先于 `PlayerTool` 解析
  - DS 端走 `PlayerTool.getSteamPlayer + isAdmin`
- `LaunchInventoryTidyPlugin.cs`：
  - 注册 `/tidy_fault_recover` 命令
  - `OnDestroy` 注销 `/tidy_fault_recover`
  - 版本号升级到 2.0.5；加载日志新增 v2.0.5 特性说明
- `Properties/AssemblyInfo.cs`：版本 2.0.5.0

---

## 0b. v2.0.4 修订摘要（四次审计反馈对照）

### 四次审计驳回证据

- 编译：0 errors / 0 warnings
- DLL（v2.0.3 提交）：81,920 bytes
- SHA-256：`4DF745A7AE965C0BC492F1D624616F4409CCBE844A759025390CAC50FAE1042A`
- 审计报告：`.audit/v2.0.3-static-audit-20260729/Codex-v2.0.3第四次静态审计与三场景指导报告-20260729.md`

### v2.0.4 修复项清单（5 项 P0 + 跨项目越权 + 2 项 P1）

| 编号 | 等级 | 问题 | 修复 |
|---|---|---|---|
| P0-1 | Critical | 普通远端玩家可执行 `/tidy_unfault` 解除安全熔断 | 新增 `TidyAdminAuth.IsAuthorizedFaultAdmin(CSteamID)`：DS 验证 `executor.isAdmin`，Listen Server 验证 `executorId == Provider.server`（主机 SteamID）；`CommandTidyFaults` 与 `CommandTidyUnfault` 入口显式调用授权检查；未通过授权记录 WARNING 并拒绝 |
| P0-2 | Critical | `Commander.init()` 反射调用会清空其他插件已注册的命令 | 移除 `EnsureCommanderInitialized()` 方法及 `Commander.init()` 反射调用；`OnServerHosted` 直接调用 `Commander.register`；`OnDestroy` 调用 `Commander.deregister(_cmdFaults)` 与 `Commander.deregister(_cmdUnfault)` 注销 |
| P0-3 | Critical | 持久熔断文件损坏、截断或写入失败时 fail-open | `TidyFaultCircuitPersistence` 完整重写：使用 Newtonsoft.Json 替代手写解析；完整 schema 验证；原子写 `.tmp + File.Replace + .bak`；加载失败尝试备份；仍失败进入 `GlobalFaultPersistenceDegraded` 全局降级 |
| P0-4 | Critical | 快捷键回滚失败未进入事务结果，库存回滚成功可能掩盖快捷键恢复失败 | `TidyOperationOutcome` 扩展 `HotkeyRollbackVerified` 等字段；`TryRestoreHotkeysToOriginalPositions` 返回 `HotkeyRestoreOutcome`；`CriticalFailure` 路径使用 `FullRestorationVerified` 决定临时/持久熔断 |
| P0-5 | Critical | 缺少单机及监听服主机明确的 server->local client 回环 | 接口需求文档 `.audit/v2.0.3-static-audit-20260729/LaunchMultiplayerNet接口需求文档-20260729.md` 移交 LaunchMultiplayerNet Agent |
| 跨项目越权 | 阻断 | Agent 修改 LaunchMultiplayerNet 源码并发布其 DLL | `git checkout -- IModTransport.cs ModTransport.cs` 回退；diff 保存至 `.audit/v2.0.3-static-audit-20260729/LaunchMultiplayerNet接口需求-UnregisterHandler-20260729.diff` |
| P1 | Medium | `SecurityLogLimiter` 只覆盖 `RequestTidyV2` 内部分字段拒绝 | `SecurityLogLimiter` 新增 `LogClientRejection` + `LogException`；所有外层协议拒绝路径改用 limiter |
| P1 | Medium | 版本号口径不一致 | `AssemblyVersion` 等全部升级到 2.0.4 |

### v2.0.4 新增文件

- `TidyAdminAuth.cs`：安全命令显式授权检查

### v2.0.4 修改文件

- `ManualTidyNetwork.cs`：移除 Unregister 调用；新增 `HotkeyRestoreOutcome`；`CriticalFailure` 路径使用 `FullRestorationVerified`；所有协议拒绝路径改用 `SecurityLogLimiter`
- `TidyFaultCircuit.cs`：`IsAllowed` 检查 `GlobalFaultPersistenceDegraded`；`SecurityLogLimiter` 扩展
- `TidyFaultCircuitPersistence.cs`：完整重写
- `CommandTidyFaults.cs`：两个命令入口调用 `TidyAdminAuth.IsAuthorizedFaultAdmin`
- `LaunchInventoryTidyPlugin.cs`：移除 `Commander.init`；`OnDestroy` 调用 `deregister`；版本升级到 2.0.4
- `Properties/AssemblyInfo.cs`：版本 2.0.4.0
- `LaunchInventoryTidy.csproj`：新增 `Newtonsoft.Json` 引用 + `TidyAdminAuth.cs` 编译项

---

## 0b. v2.0.3 修订摘要（三次审计反馈对照）

### 三次审计驳回证据

- 编译：0 errors / 0 warnings
- DLL（v2.0.2 提交）：67,072 bytes
- SHA-256：`2F22956D24230A2EF22F145158B89E5287D2E2584F9DC3B672A4097C5A9A7278`
- 审计报告：`.audit/v2.0.2-static-audit-20260729/Codex-v2.0.2第三次静态审计与指导报告-20260729.md`

### v2.0.3 修复项清单（2 项 Critical + 7 项 Medium + 2 项 Low）

| 编号 | 等级 | 问题 | 修复 |
|---|---|---|---|
| P0-C3 | Critical | 服务层计算 rollbackOk 但返回值只有 TidyCommitResult.CriticalFailure；网络层所有熔断固定 restoreVerified:false | 新增 TidyOperationOutcome { Result, MutationStarted, RollbackAttempted, RollbackVerified, FailureReason }；服务层返回 outcome，网络层使用真实 RollbackVerified 决定临时/持久熔断 |
| P0-C4 | Critical | 持久熔断仅保存在内存 Dictionary，服务器重启即丢失；无管理员 TryClose 入口 | 新增 TidyFaultCircuitPersistence：JSON 文件 BepInEx/config/LaunchInventoryTidy/persistent_faults.json；启动加载 + Open/TryClose 时写盘；新增 /tidy_faults 查询命令 + /tidy_unfault SteamID 解除命令 |
| P1-M11 | Medium | TidyAll 异常时回滚所有页面，包括尚未提交的页面 | TidyAll 追踪 lastCommitStartedIndex；新增 TryRollbackCommittedPages 仅回滚 0..lastCommitStartedIndex 范围；未开始提交的页面保持零副作用 |
| P1-M12 | Medium | CriticalFailure 回滚只恢复库存物品与坐标，不恢复快捷键状态 | 网络层 HandleRequestTidyV2 在 outcome.RollbackVerified=true 时调用 TryRestoreHotkeysToOriginalPositions：按原坐标 ServerBindItemHotkey 重绑 |
| P1-M13 | Medium | _shuttingDown 只让遗留 handler 静默返回，没有真正注销 handler | v2.0.4 修订：回退 LaunchMultiplayerNet 修改，写入接口需求文档，等待 LMN 3.3 独立交付 Unregister API |
| P1-M14 | Medium | 协议拒绝日志（非法 mode/page/hotkeyCount、短包、尾随数据）在限流前直接输出 Warning，攻击者可制造日志洪水 | 新增 SecurityLogLimiter：按 (sender, category) 聚合，5 秒节流 + suppressed 计数；HandleRequestTidyV2 所有协议拒绝路径改用 SecurityLogLimiter.LogRejection；固定头读取包裹 try-catch 处理 EndOfStreamException；v2.0.4 扩展到所有外层协议拒绝路径 |
| P1-M15 | Medium | 交付称 v2.0.2 但实际 BepInEx / FileVersion / ProductVersion 均为 2.0.1 | AssemblyInfo + BepInPlugin 升级到 2.0.2.0；插件名与加载日志同步；v2.0.4 再次升级到 2.0.4 |
| P1-M16 | Medium | README/CHANGELOG 仍停留在 v1.4.1，未记录 v2.x 功能、协议兼容和新风险 | README 头部更新到 v2.0.2 + 安全声明；CHANGELOG 新增 v2.0.0/v2.0.1/v2.0.2/v2.0.3 完整变更记录 |
| P1-M17 | Medium | Prepare 遇到 ItemJar/jar.item 为 null 时跳过而非 fail-closed | PreparePage 改为 fail-closed：任何 jar==null 或 jar.item==null 立即返回 Valid=false；新增 jars.Count == count && packList.Count == count 强校验 |
| P2-L12 | Low | 注释声称"TTL 内条目不得驱逐、容量满拒绝新请求"，实现仍是直接移除最老条目 | 修正文档：明确允许容量驱逐（拒绝新请求会让合法客户端的正常整理请求失败，反而造成更大问题）；驱逐会记录 WARNING 日志供运维监控 |
| P2-L13 | Low | 断线日志声称"已清理熔断"，但 ClearPlayer 对持久熔断明确不会清除 | TidyFaultCircuit.ClearPlayer 返回 ClearPlayerResult { NotFound, Removed, Preserved } 枚举；调用方按真实结果记录日志 |

---

## 0b. v2.0.2 修订摘要（二次审计反馈对照）

### 二次审计驳回证据

- 编译：0 errors / 0 warnings
- DLL（v2.0.1 提交）：61,952 bytes
- SHA-256：未记录（v2.0.1 提交时未留存哈希）
- 审计报告：首轮 `.audit/v2.0.0-static-audit-20260729/Codex-v2.0.0静态审计与指导报告-20260729.md` + 二轮对话审计记录（环境只读未写入 .audit）

### v2.0.2 修复项清单（10 项 + 1 项 Low）

| 编号 | 等级 | 问题 | 修复 |
|---|---|---|---|
| P0-C1 | Critical | JarSnapshot 只保存 Item 引用，amount/quality/state 可变，回滚无法恢复提交前真实值 | `JarSnapshot` 改为 sealed class，保存 Id/Amount/Quality/State.Clone() 值拷贝 + OriginalJar 引用；新增 `RecreateItem()` 创建全新 Item 实例用于回滚 |
| P0-C2 | Critical | 玩家断开时无条件清除熔断，重新登录即可恢复整理权限 | `TidyFaultCircuit.ClearPlayer` 仅清除 `RestoreVerified=true` 的临时熔断；`RestoreVerified=false` 的持久熔断不得因断线清除；新增 `ClearAllNonPersistent` 生产 Shutdown API |
| P1-M3 | Medium | ValidateTagConsistency 未验证 Tag 来自 before | `JarSnapshot` 增加 `OriginalJar` 字段；`ValidateTagConsistency` 构建 before OriginalJar 集合，验证 result 中每个 Tag 都在集合内 |
| P1-M4 | Medium | 回滚成功只验证物品指纹，不验证原始 x/y/rot 布局 | 新增 `VerifyRollbackRestoration`：页面尺寸 + jar 数量 + 逐坐标 (x,y,rot,fingerprint) 多重集合匹配，任何差异判定恢复失败 |
| P1-M5 | Medium | RequestLedger 只缓存 result 枚举，重复请求返回 Committed+mappings=null | `LedgerEntry` 增加 `Mappings` 字段；`MarkResult` 接收完整 mappings 深拷贝；重复请求重发完整缓存响应；`Received` 状态明确区分于 Committed |
| P1-M6 | Medium | 容量 16 不足以覆盖 60s TTL 内理论请求数（限流允许 30 个/60s） | `MAX_ENTRIES_PER_PLAYER` 从 16 扩到 64；驱逐时记录 WARNING 日志 |
| P1-M7 | Medium | Shutdown 没有注销网络 handler，运行中的 convergence GameObject 未跟踪 | 新增 `_shuttingDown` flag，handler 入口检查；`_activeConvergenceObjects` 列表跟踪所有创建的 convergence GameObject，Shutdown 时统一销毁 |
| P1-M8 | Medium | 限流拒绝日志没有节流，攻击者可制造日志洪水 | `TidyRateLimiter` 增加 `LOG_SUPPRESSION_SECONDS=5.0` 节流，每类拒绝原因在周期内最多输出一次，累计 suppressed 数量 |
| P1-M9 | Medium | 大量注释和运行日志乱码 | 扫描全部 .cs 文件确认无 mojibake（UTF-8 解码为 Latin-1 的典型字符），源码编码正常 |
| P1-M10 | Medium | 版本元数据仍为 v2.0.0 | `AssemblyVersion`/`AssemblyFileVersion` 升至 `2.0.1.0`；BepInPlugin version 升至 `2.0.1`；插件名加上 `v2.0.1` 标识；启动日志区分 v2.0.0/v2.0.1/v2.0.2 三层特性 |
| P2-L11 | Low | 生产 Shutdown 调用 ClearAllForTests | `TidyFaultCircuit` 新增 `ClearAllNonPersistent`（仅清临时熔断，保留持久熔断）；其他模块无持久化需求保留 ClearAllForTests |

---

## 0. v2.0.1 修订摘要（外部审计反馈对照）

### 外部审计驳回证据

- 编译：0 errors / 0 warnings
- DLL（v2.0.0 首次提交）：50,176 bytes
- SHA-256：`82EEBE8FC895A6CB3484ACF995608BEAC536B7936CFC83075C063E5466785C04`
- ItemsTryAddItemPatch：0 自定义特性、0 声明方法，被动整理仍处于禁用状态
- 完整审计报告：`.audit/v2.0.0-static-audit-20260729/Codex-v2.0.0静态审计与指导报告-20260729.md`

### v2.0.1 修复项清单

| 编号 | 等级 | 问题 | 修复 |
|---|---|---|---|
| P0-1 | 阻断 | 事务化整理仍是清空后重添，异常或守恒失败时没有回滚 | `ManualTidyService` 引入 `PageSnapshot`/`JarSnapshot`/`PagePreparation` 真事务：Prepare 阶段零副作用捕获快照，Commit 失败时按快照回滚，回滚后再验证 |
| P0-2 | 阻断 | CriticalFailure 后禁用玩家的声明没有实现 | 新建 `TidyFaultCircuit.cs`：玩家级故障熔断器，`Open()` 后 `IsAllowed()` 返回 false，必须显式 `TryClose()` 或断线重连恢复 |
| P0-3 | 阻断 | 全背包整理逐页提交，可能部分页面已修改、后续页面失败 | `TidyAllPlayerPages` 改为三阶段原子化：阶段 1 全部 Prepare（任一失败零副作用返回 Rejected）→ 阶段 2 全部 Commit → 阶段 3 全部 Verify，失败时 `TryRollbackAll` |
| P0-4 | 阻断 | STORAGE 页显示整理按钮，但服务端确定性拒绝 page 7 | `PlayerDashboardInventoryUIPatch`：`HEADER_INJECT_COUNT` 从 6 改为 5，移除 STORAGE 按钮注入逻辑，旧 STORAGE_*_POS_OFFSET_X 常量标记 `[Obsolete]` |
| P0-5 | 阻断 | `ValidateFingerprintMatches` 不实际验证 | 新增 `ValidateTagConsistency`（每个 jar 仅出现一次，`ReferenceEqualityComparer<ItemJar>`）+ `ValidateFingerprintMultiset`（提交前完整的多重集比较 id+amount+quality+state） |
| P1-1 | 中等 | SameType 默认模式忽略升/降序参数 | `TryPackSameTypeMultiCandidate` 接收 `sortDescending` 并传递给所有 3 个候选；候选 A 控制组排序方向，候选 B 控制组内大小排序，候选 C 几何 MaxRects |
| P1-2 | 中等 | requestId 仅实现 ACK 幂等，整理请求本身可被重放 | 新建 `RequestLedger`：每玩家 16 条目 60s TTL，`TryBegin` 检测重复 requestId 返回缓存结果不重新执行整理 |
| P1-3 | 中等 | 没有服务端频率限制 | 新建 `TidyRateLimiter`：滑动窗口（1s 最小间隔 + 10s 内最多 5 个请求） |
| P1-4 | 中等 | hotkeyCount > 8 时截断而非拒绝 | `HandleRequestTidyV2` 改为 `if (hotkeyCount > 8) return Rejected`，不截断；加尾随字节验证 |
| P1-5 | 中等 | 客户端不验证响应 requestId 是否由本地发出 | 新建 `ClientPendingState`：30s TTL 跟踪发出的 requestId，`IsPending` 检查响应 requestId 是否在表中 |
| P1-6 | 中等 | 插件卸载没有清理 | `LaunchInventoryTidyPlugin.OnDestroy` 调用 `ManualTidyNetwork.Shutdown()` 清理所有静态状态；订阅 `Provider.onEnemyDisconnected(SteamPlayer)` 在玩家断开时清理该玩家的熔断/限流/账本/事务状态；保存 watcher GameObject 引用并销毁 |
| P2-1 | 低 | `LargestRemainingRect` 是死指标（无装箱器赋值） | `LayoutCandidate.cs` 删除字段 + `CompareTo` 减为 5 项 tie-break + `ComputeMetrics` 删除赋值 |
| P2-2 | 低 | `AssemblyDescription` 仍写 "FFD + DFS 回溯装箱" | 更新为 `"SameType 多候选聚合 + MaxRects/FFD 手动整理 + 快捷键保留 + 协议 V2"` |
| P2-3 | 低 | `TidyFaultCircuit.cs` 未加入 .csproj | 添加 `<Compile Include="TidyFaultCircuit.cs" />` |

---

## 1. 功能概述

### 本次开发/修改的核心功能和目标

**v2.0.0 目标**：解决外部审计确认的两条社区反馈成立问题，实施架构级变更。

**问题 1：同类物品不聚合**
- 旧算法只按几何尺寸排序，无"相同物品"概念
- 20 个同 ID 药片被混排符合现有实现，但不符合用户直觉

**问题 2：快捷键 3-0 失效**
- Unturned 的 3-0 快捷键绑定依赖"物品 ID + 页面坐标"
- 手动整理清空页面并重新添加物品后，坐标改变导致快捷键必然失效

**v2.0.0 解决方案**：
1. **同类聚合算法**：新增 `SameType` 模式（默认），按 `GroupKey`（= item.id）分组聚合
2. **多候选 + 评分**：`TryPack` 内部生成最多 3 个候选并按确定性指标选最优
3. **V2 网络协议**：`requestId` + 快捷键快照 + ACK 两阶段提交
4. **物品守恒验证**：`id + amount + quality + state` 多重集合指纹守恒
5. **快捷键迁移**：服务器事务完成后，客户端 ACK 库存收敛，再调用 `ServerBindItemHotkey`

**v2.0.1 强化**（外部审计反馈后）：
6. **真事务快照回滚**：Prepare 阶段捕获全量快照，Commit 失败按快照回滚，回滚后再验证
7. **玩家级故障熔断**：CriticalFailure 后该玩家被熔断，直到显式恢复或断线重连
8. **原子化 TidyAll**：全部 Prepare → 全部 Commit → 全部 Verify，任一失败全局回滚
9. **服务端限流 + 防重放**：1s 间隔 + 10s 内 5 个请求 + requestId 账本防重放
10. **客户端响应验证**：仅接受本机发出的 requestId 的响应
11. **插件卸载清理**：OnDestroy 清理所有静态状态，订阅 onEnemyDisconnected 清理离线玩家状态

---

## 2. 代码变更清单（Diff Checklist）

### 新建文件

| 文件 | 说明 |
|---|---|
| `LayoutCandidate.cs` | 候选布局 + 评分指标（5 项 tie-break：UnplacedCount / SameTypeConnectedBlocks / RowMajorSegmentCount / TotalMovementDistance / RotationChangeCount） |
| `HotkeySnapshot.cs` | 快捷键快照结构 + `HotkeySnapshotUtil`（客户端捕获本地 `_hotkeys`，服务器验证旧坐标） |
| `TidyTransaction.cs` | `PendingHotkeyRestore` + `TidyTransactionManager`（每名玩家最多 1 个进行中事务，TTL 10 秒，requestId 幂等）+ `ClearPlayer(CSteamID)` |
| `ConvergenceCheckBehaviour.cs` | MonoBehaviour 库存收敛检查协程（最多 60 次检查 / 3 秒超时） |
| `TidyFaultCircuit.cs` | v2.0.1 新增：`TidyFaultCircuit`（玩家级故障熔断）+ `TidyRateLimiter`（滑动窗口限流）+ `RequestLedger`（防重放账本） |

### 修改文件

| 文件 | 改动摘要 |
|---|---|
| `InventorySolver.cs` | `PackableItem` 新增 GroupKey / StableOrder / OriginalX/Y/Rot / PreferredRotation 字段；`TidyMode` 枚举重排为 SameType=0 / MaxRects=1 / FFD=2；`TryPack` 内部生成 3 个候选并按确定性指标选最优；`TryPackSameTypeMultiCandidate` 接收 `sortDescending` 并传递给所有 3 个候选（v2.0.1 P1-1 修复）；排序比较器末尾按 StableOrder 收尾 |
| `ManualTidyService.cs` | 完整事务化重写：`TidyCommitResult` 枚举（Committed / Rejected / CriticalFailure）；`ItemFingerprint` 结构（id + amount + quality + state）；`NewPosition` 结构；`JarSnapshot` / `PagePreparation` 真事务结构；`PreparePage` 零副作用捕获快照 + 求解 + 4 项静态验证；`CommitPage` 清空 + 重添；`VerifyFingerprintConservation` 提交后指纹多重集比较；`TryRollbackPage` 失败时按快照恢复 + 验证恢复；`TidyAllPlayerPages` 三阶段原子化（全部 Prepare / 全部 Commit / 全部 Verify，失败 `TryRollbackAll`）；`ValidateTagConsistency` 每 jar 仅出现一次；`ValidateFingerprintMultiset` 提交前完整多重集比较；删除未使用的 `PageSnapshot` 死 struct（v2.0.1 P2-3） |
| `ManualTidyNetwork.cs` | V2 协议：`MSG_REQUEST_TIDY_V2=2` / `MSG_TIDY_COMMITTED=3` / `MSG_INVENTORY_APPLIED_ACK=4`（不写入 LaunchMultiplayerNet.EModMessage 枚举，遵循 LaunchInPlaceReload 的 REPACK_SUCCESS=11 设计模式）；单服务器/客户端 handler 按 msgType 分发；`HandleRequestTidyV2` 集成限流 + 熔断 + 防重放账本 + 严格 hotkeyCount > 8 拒绝 + 尾随字节验证；`ClientPendingState` 30s TTL 跟踪本机发出的 requestId，`IsPending` 检查响应合法性；`Shutdown()` 清理所有静态状态（由 Plugin.OnDestroy 调用） |
| `Patches/PlayerDashboardInventoryUIPatch.cs` | 模式按钮三态循环 SameType → MaxRects → FFD → SameType；按钮文本改为中文 "同类"/"空间"/"大件"；MODE_SIZE_X 从 40 增至 60；MODE_POS_OFFSET_X 从 -220 调整到 -240；TOOLTIP_MODE 更新；`EnsurePageModeDefault` 默认改为 SameType；`HandleTidyClick` 改用 `SendTidyV2Request`；v2.0.1 P0-4：`HEADER_INJECT_COUNT` 从 6 改为 5，移除 STORAGE 按钮注入逻辑，旧 STORAGE 常量标记 `[Obsolete]` |
| `ManualTidyWatcher.cs` | Plugin 0 按键改用 `SendTidyV2Request`，默认 SameType 模式 |
| `LaunchInventoryTidyPlugin.cs` | `[BepInPlugin]` 名称改为 `LaunchInventoryTidy [v2.0.0 同类聚合 + 快捷键保留 + 协议 V2]`；版本 `2.0.0`；加载日志新增 v2.0.0 特性说明；v2.0.1 P1-6：`Awake` 订阅 `Provider.onEnemyDisconnected(SteamPlayer)` 玩家状态清理；`OnDestroy` 调用 `ManualTidyNetwork.Shutdown()` + 销毁 watcher GameObject + 取消订阅 |
| `Properties/AssemblyInfo.cs` | `AssemblyVersion` / `AssemblyFileVersion` 从 `1.4.1.0` 升级到 `2.0.0.0`；v2.0.1 P2-2：`AssemblyDescription` 更新为 `"SameType 多候选聚合 + MaxRects/FFD 手动整理 + 快捷键保留 + 协议 V2"` |
| `LaunchInventoryTidy.csproj` | 新增 5 个 Compile Include：LayoutCandidate.cs / HotkeySnapshot.cs / TidyTransaction.cs / ConvergenceCheckBehaviour.cs / TidyFaultCircuit.cs |

### 未修改文件（验证保持原状）

| 文件 | 验证项 |
|---|---|
| `Patches/ItemsTryAddItemPatch.cs` | 仍为空类（被动整理 Patch 保持禁用），无 `[HarmonyPatch]` 特性 |
| `Patches/PlayerDashboardInventoryUIPatch.cs` | 仍只 Patch `PlayerDashboardInventoryUI` 构造函数（`MethodType.Constructor`），未 Patch 任何快捷键逻辑 |
| `LaunchInventoryTidyPlugin.cs` `Awake()` | 仍仅调用 `HarmonyInstance.PatchAll()` + `ManualTidyNetwork.RegisterHandlers()` + `SpawnManualTidyWatcher()` + `Provider.onEnemyDisconnected` 订阅（v2.0.1 新增），未新增被动 Patch 注册 |

---

## 3. 架构合规性说明

### 改动如何契合现有项目架构

**1. 算法层保持纯净**
- `InventorySolver.cs` 仍不引用任何 Unity / Unturned 类型
- 新增的 `LayoutCandidate.cs` 同样不引用游戏类型
- 单元测试可直接对算法层调用，无需启动 Unity

**2. 服务层引用游戏类型但保持真事务化**
- `ManualTidyService.cs` 仍只通过 `Items` / `ItemJar` / `Item` API 操作
- `ItemFingerprint` 是值类型，深拷贝 `state` byte[] 避免外部修改
- `JarSnapshot` 捕获完整 Item 引用 + 原坐标 + 旋转，回滚时按快照重建
- `PagePreparation` 包含 `BeforeJars` 快照，Commit 失败时按快照回滚
- 任何阶段失败都返回 `TidyCommitResult` 枚举，不抛异常（异常由调用方捕获）

**3. 网络层遵循 LaunchInPlaceReload 设计模式**
- 不修改 `LaunchMultiplayerNet.EModMessage` 枚举
- 本插件内部约定 `MSG_REQUEST_TIDY_V2=2` / `MSG_TIDY_COMMITTED=3` / `MSG_INVENTORY_APPLIED_ACK=4`
- 数值与 EModMessage 现有值（1=RequestTidyPage, 10=RequestRepackAmmo, 20/21=Horde）不冲突
- 单 channel 100 内按 msgType 字节分发，符合 LaunchInPlaceReload 的 REPACK_SUCCESS=11 设计模式

**4. UI 层复用现有反射缓存**
- `PlayerDashboardInventoryUIPatch` 复用 `s_PosScaleX` / `s_PosOffsetX` / `s_SizeOffsetX` / `s_Text` / `s_TooltipText` / `s_OnClicked`
- `CreatePageDelegate` Emit 模式已支持 `ButtonKind.Mode`，无需新增委托类型
- 三态按钮直接复用现有模式按钮机制

**5. 事务状态管理独立模块**
- `TidyTransaction.cs` 不引用游戏类型，仅使用 `CSteamID` + `DateTime` + `Dictionary`
- `TidyTransactionManager` 全静态，线程安全（lock 保护）
- `CleanupExpired` 可由插件定期调用（当前未自动调用，依赖 TTL 惰性清理 + 玩家断线清理）

**6. v2.0.1 安全加固独立模块**
- `TidyFaultCircuit.cs` 包含 3 个独立静态类：`TidyFaultCircuit`（熔断）/ `TidyRateLimiter`（限流）/ `RequestLedger`（账本）
- 全部使用 `CSteamID` + `DateTime` + `Dictionary` + `lock`，线程安全
- 玩家断线时 `ClearPlayer(CSteamID)` 由 `Provider.onEnemyDisconnected` 触发
- `ClearAllForTests()` 仅 internal，单元测试使用

### 没有引入"脏代码"的证据

1. **无硬编码绕过**：所有协议字段都有白名单验证（mode ∈ {0,1,2}, page ∈ [2..6] ∪ {0xFF}, hotkeyCount ≤ 8 严格拒绝不截断）
2. **无 Patch 规避**：`ItemsTryAddItemPatch` 仍为空类，未重新启用
3. **无反射滥用**：`PlayerDashboardInventoryUIPatch` 反射仅用于 UI 构造函数注入，未 Patch 快捷键逻辑
4. **无网络包重发**：ACK 丢失时仅超时一次，不无限重试（`ConvergenceCheckBehaviour` 最多 60 次检查 / 3 秒）
5. **无全方法 Patch**：未对 `PlayerSavedata` / `PlayerEquipment` 添加任何 Patch
6. **无自动迁移**：未实现历史 `P2P_<SteamID>` 存档自动迁移或合并
7. **无死代码**：删除 `PageSnapshot` 未使用 struct + `LargestRemainingRect` 死字段

---

## 4. 编译与运行环境验证记录

### 编译命令

```bash
cd D:/Agent-工作目录/DevelopMyUNMultiplayerModAndModloader/LaunchInventoryTidy
dotnet build LaunchInventoryTidy.csproj -c Release -nologo
```

### 编译结果

- **状态**：✅ 成功
- **错误数**：0
- **警告数**：0
- **耗时**：约 0.69 秒

### 编译产物

- **路径**：`D:\Agent-工作目录\DevelopMyUNMultiplayerModAndModloader\LaunchInventoryTidy\bin\Release\LaunchInventoryTidy.dll`
- **大小**：67,072 bytes（约 65.5 KB）
- **SHA-256**：`2F22956D24230A2EF22F145158B89E5287D2E2584F9DC3B672A4097C5A9A7278`
- **框架**：.NET Framework 4.7.2
- **Deterministic**：`<Deterministic>true</Deterministic>`（已启用，源码不变则哈希稳定）

### 静态反射检查

1. ✅ `ItemsTryAddItemPatch` 仍为空类（无 `[HarmonyPatch]` 特性，无 Prefix/Postfix 方法体）
2. ✅ `PlayerDashboardInventoryUIPatch` 仅 Patch `PlayerDashboardInventoryUI` 构造函数（仅 1 处 `[HarmonyPatch]` 特性）
3. ✅ `LaunchInventoryTidyPlugin.Awake()` 未新增被动 Patch 注册
4. ✅ 新增 5 个文件均通过编译，无 CS0104（Action/Object 歧义）、CS0133（const 表达式）、CS0649（未赋值字段）等错误或警告

### 运行环境

- **目标游戏**：Unturned（Steam 版）
- **BepInEx**：5.4.22
- **前置库**：LaunchMultiplayerNet v3.2+（硬依赖）
- **Harmony**：0Harmony（BepInEx 自带）
- **Steamworks.NET**：com.rlabrecque.steamworks.net

---

## 5. 风险与副作用评估

### 潜在影响 1：协议 V2 不兼容 V1

**风险**：v2.0.0 强制 V2，旧 V1 客户端发送 `EModMessage.RequestTidyPage=1` 会被服务器拒绝。

**缓解**：
- 服务器收到 msgType=1 时输出 WARNING 日志提示升级
- 客户端版本号显式声明 `PROTOCOL_VERSION_V2=2`，服务器拒绝版本不匹配的请求
- 部署时必须双端同步升级

### 潜在影响 2：物品守恒验证失败时熔断玩家

**风险**：`CriticalFailure` 时插件熔断该玩家，后续整理请求被拒绝直到显式恢复或断线重连。

**缓解**：
- 日志输出 before/after 指纹差异（前 5 条）便于排查
- `CriticalFailure` 仅在异常情况（如外部 Patch 干扰、回滚失败）下触发
- 正常路径（指纹匹配）不受影响
- 玩家断线重连自动恢复（`ClearPlayer`）
- 管理员可通过 `TidyFaultCircuit.TryClose` 显式恢复（需后续提供管理员命令）

### 潜在影响 3：快捷键迁移依赖客户端 ACK

**风险**：客户端未发送 ACK（崩溃 / 断线 / 超时）时，服务器不调用 `ServerBindItemHotkey`，原快捷键失效。

**缓解**：
- 客户端 `ConvergenceCheckBehaviour` 最多 60 次检查 / 3 秒超时
- 超时后输出 WARNING 日志提示用户手动检查
- 服务器 TTL 10 秒后自动清理待恢复事务，不无限重试
- 不主动调用 `ServerClearItemHotkey`（保留 vanilla 原状态，避免误清空）

### 潜在影响 4：模式按钮宽度增加挤压原版 UI

**风险**：MODE_SIZE_X 从 40 增至 60，MODE_POS_OFFSET_X 从 -220 调整到 -240，可能挤压方向按钮。

**缓解**：
- DIR_POS_OFFSET_X 保持 -175，TIDY_POS_OFFSET_X 保持 -130
- 模式按钮右边缘 -180，与方向按钮左边缘 -175 间隔 5px
- v2.0.1 P0-4：STORAGE 页不再注入按钮，原 STORAGE_MODE_POS_OFFSET_X 常量标记 `[Obsolete]` 保留兼容
- 中文 "同类"/"空间"/"大件" 比 "C"/"D" 宽，60px 足以容纳

### 潜在影响 5：SameType 模式性能开销

**风险**：多候选生成 + 评分（含 BFS 连通块统计）比单候选 MaxRects 慢。

**缓解**：
- 候选上限 3 个，每个候选的 BFS 是 O(width × height)
- 背包页最大 8×8 = 64 格，BFS 微秒级
- PERF-1 测试矩阵要求 200 件上限无卡顿

### 潜在影响 6：客户端 _hotkeys 不可用时降级

**风险**：客户端 `_hotkeys` 未初始化（非 LocalPlayer 场景）时，`CaptureLocalHotkeys` 返回空列表。

**缓解**：
- 空列表合法，服务器仍执行整理，只是不迁移快捷键
- 日志输出 hotkeys=0 提示
- 不抛异常，不阻塞整理流程

### 潜在影响 7：v2.0.1 限流误伤正常用户

**风险**：1s 最小间隔 + 10s 内 5 个请求可能误伤快速操作的用户。

**缓解**：
- 1s 间隔仅阻止连击，正常用户每次整理间隔远超 1s
- 10s 内 5 个请求已远超正常使用频率（手动整理通常 10s 内 1-2 次）
- 超限时输出 WARNING 日志，不熔断玩家
- 玩家断线自动重置窗口

---

## 6. 测试用例与建议

### UX 测试（同类聚合）

| 编号 | 场景 | 预期结果 |
|---|---|---|
| UX-G1 | 20 个同 ID 1×1 药片与其他物品混合 | 药片形成单一连续区域 |
| UX-G2 | 同 ID、不同 quality/state | 仍归为同组；状态字节保持不变 |
| UX-G3 | 不同 ID、相同名称或图标 | 不得错误合并 |
| UX-G4 | 工坊物品 ID | 分组行为与原版一致 |
| UX-G5 | 连续整理两次 | 第二次布局完全不变 |
| UX-G6 | 同类聚合布局无法装下 | 拒绝整理，不部分提交 |

### HK 测试（快捷键保留）

| 编号 | 场景 | 预期结果 |
|---|---|---|
| HK-1 | 绑定数字键 3-0 后整理 | 8 个绑定均指向原具体物品的新位置 |
| HK-2 | 两件同 ID、不同质量，只绑定其中一件 | 整理后仍绑定原实例语义对应的那一件 |
| HK-3 | 只整理一个页面 | 其他页面快捷键完全不变 |
| HK-4 | 整理全部页面 | 所有受影响快捷键正确迁移 |
| HK-5 | 快捷键目标当前已装备 | 不丢物、不错误绑定 |
| HK-6 | 客户端库存更新延迟或乱序 | 不提前绑定，不绑定空坐标 |
| HK-7 | ACK 丢失 | 最多超时一次，不死循环、不错误绑定 |
| HK-8 | 伪造 ID/坐标/超过 8 条快照 | 服务器拒绝非法条目，库存不越权 |

### SAFE 测试（物品守恒 + 事务回滚）

| 编号 | 场景 | 预期结果 |
|---|---|---|
| SAFE-1 | 整理前后指纹比对 | `id + amount + quality + state` 多重集合完全一致 |
| SAFE-2 | 单机/房主测试 | 无快捷键清除、无物品异常 |
| SAFE-3 | U3DS 双机双账号 | 客户端、服务器库存一致，快捷键实际可使用 |
| SAFE-4 | 双端 DLL SHA-256 | 完全一致 |
| SAFE-5 | v2.0.1 新增：注入故障（如 Commit 阶段抛异常） | 按快照回滚，回滚后指纹与整理前一致 |
| SAFE-6 | v2.0.1 新增：注入故障（如 Verify 阶段指纹不匹配） | 按快照回滚 + 熔断玩家，后续请求被拒绝 |
| SAFE-7 | v2.0.1 新增：TidyAll 中途页面失败 | 全局零副作用（阶段 1 失败）或全局回滚（阶段 2/3 失败） |
| SAFE-8 | v2.0.1 新增：玩家被熔断后再次请求整理 | 服务器返回 CriticalFailure，不执行整理 |

### SECURITY 测试（v2.0.1 新增）

| 编号 | 场景 | 预期结果 |
|---|---|---|
| SEC-1 | 同一 requestId 重复发送 | 第二次返回缓存结果，不重新整理 |
| SEC-2 | 1s 内连发 2 个请求 | 第二个被限流拒绝 |
| SEC-3 | 10s 内连发 6 个请求 | 第 6 个被限流拒绝 |
| SEC-4 | hotkeyCount = 9 | 服务器拒绝整个请求 |
| SEC-5 | hotkeyCount 字段后跟多余字节 | 服务器拒绝（尾随字节验证） |
| SEC-6 | 伪造响应 requestId（非本机发出） | 客户端 `IsPending` 检查失败，忽略响应 |
| SEC-7 | 玩家断线重连 | 熔断/限流/账本状态全部清理 |

### PERF 测试

| 编号 | 场景 | 预期结果 |
|---|---|---|
| PERF-1 | 200 件上限 | 求解时间有界，无明显卡顿、无无限搜索 |

### 测试顺序建议

1. **单机冒烟测试**：UX-G1 + HK-1 + SAFE-1（确认基本功能可用）
2. **单机往返测试**：UX-G1..G6 + HK-1..5 + SAFE-1..2 + SAFE-5..6（覆盖单机场景 + 故障注入）
3. **U3DS 双机测试**：HK-6..8 + SAFE-3..4 + SAFE-7..8 + SEC-1..7 + PERF-1（覆盖联机 + 安全 + 性能）
4. **审计报告归档**：`.audit/v2.0.1-release-verification-<YYYYMMDD>/`

---

## 7. 发布门槛状态（4 级证据系统）

### 证据等级定义

| 等级 | 标记 | 含义 |
|---|---|---|
| L1-实现存在 | 🟦 | 代码已实现，编译通过 |
| L2-静态待证 | 🟨 | 静态反射/代码审查可证明，未动态执行 |
| L3-动态待测 | 🟧 | 待动态测试验证 |
| L4-外部通过 | 🟩 | 外部审计员最终放行 |

### v2.0.0 发布门槛（审计 §9）

| 门槛 | 状态 | 证据 |
|---|---|---|
| 同类分组键明确使用 Item.id | 🟨 静态待证 | `PackableItem.GroupKey` 在 `ManualTidyService.BuildPackableItems` 中赋值 `jar.item?.id ?? 0`，代码可读 |
| 排序具有最终 StableOrder tie-break | 🟨 静态待证 | `InventorySolver.SortByGeometry` 末尾按 `a.StableOrder.CompareTo(b.StableOrder)` 收尾，代码可读 |
| 最终二维布局聚合指标经过测试 | 🟧 动态待测 | `LayoutCandidate.ComputeMetrics` 已实现 5 项指标，等待 UX-G1..G6 动态测试验证 |
| 所有物品必须全部可放置后才允许提交 | 🟨 静态待证 | `ManualTidyService.PreparePage` 检查 `unplaced > 0` 时返回 `Valid=false`，`TidyAllPlayerPages` 阶段 1 任一失败零副作用返回 Rejected |
| 整理前后完整物品指纹一致 | 🟧 动态待测 | `ValidateFingerprintMultiset` 比对 id + amount + quality + state（byte[] 完整比对），等待 SAFE-1 动态测试 |
| 快捷键快照由客户端提供并由服务器验证 | 🟨 静态待证 | `HotkeySnapshotUtil.CaptureLocalHotkeys` 客户端捕获；`ValidateAndResolve` 服务器验证旧坐标 + ID 匹配 |
| 相同 ID 多实例通过旧 ItemJar/事务 Token 映射 | 🟧 动态待测 | `Dictionary<ItemJar, HotkeySnapshot>` + `outMapping` 通过 ItemJar 引用建立旧→新映射，等待 HK-2 双实例测试 |
| 快捷键恢复采用库存收敛 ACK | 🟧 动态待测 | `ConvergenceCheckBehaviour` 检查所有 mapping 收敛后回调 `SendInventoryAppliedAck`，等待 HK-1/HK-7 测试 |
| 超时、重复请求、乱序和断线均有硬限制 | 🟧 动态待测 | TTL 10 秒 + `RequestLedger` 防重放 + `TidyRateLimiter` 限流 + `ClientPendingState` 客户端验证 + `Provider.onEnemyDisconnected` 玩家清理，等待 SEC-1..7 测试 |
| 单机和 U3DS 双机测试均通过 | 🟧 动态待测 | 等待动态测试 |
| 不重新启用被动整理 Patch | 🟨 静态待证 | `ItemsTryAddItemPatch` 仍为空类（静态反射验证，0 自定义特性） |
| 手动整理"清空 + 重添"的残余风险在真正消除前继续保留文档声明 | 🟨 静态待证 | 本清单 §5 风险评估 + ItemsTryAddItemPatch.cs 注释保留 |

### v2.0.1 新增发布门槛

| 门槛 | 状态 | 证据 |
|---|---|---|
| 真事务快照回滚（P0-1） | 🟧 动态待测 | `JarSnapshot`/`PagePreparation`/`TryRollbackPage` 已实现，等待 SAFE-5/SAFE-6 故障注入测试 |
| CriticalFailure 玩家级熔断（P0-2） | 🟧 动态待测 | `TidyFaultCircuit.Open/IsAllowed` 已实现，等待 SAFE-8 测试 |
| TidyAll 全局原子化（P0-3） | 🟧 动态待测 | `TidyAllPlayerPages` 三阶段 + `TryRollbackAll` 已实现，等待 SAFE-7 测试 |
| STORAGE 页按钮移除（P0-4） | 🟨 静态待证 | `HEADER_INJECT_COUNT=5`，STORAGE 逻辑已移除，代码可读 |
| ValidateFingerprintMatches 真实校验（P0-5） | 🟧 动态待测 | `ValidateTagConsistency` + `ValidateFingerprintMultiset` 已实现，等待 SAFE-1 测试 |
| SameType 方向语义修正（P1-1） | 🟧 动态待测 | `TryPackSameTypeMultiCandidate` 接收 `sortDescending`，等待 UX-G5 + 方向按钮测试 |
| request ledger 防重放（P1-2） | 🟧 动态待测 | `RequestLedger.TryBegin/MarkResult` 已实现，等待 SEC-1 测试 |
| 服务端限流（P1-3） | 🟧 动态待测 | `TidyRateLimiter.Allow` 已实现，等待 SEC-2/SEC-3 测试 |
| hotkeyCount > 8 拒绝（P1-4） | 🟨 静态待证 | `HandleRequestTidyV2` 中 `if (hotkeyCount > 8) return Rejected`，代码可读 |
| 客户端 pending requestId 表（P1-5） | 🟧 动态待测 | `ClientPendingState.IsPending` 已实现，等待 SEC-6 测试 |
| 插件卸载清理（P1-6） | 🟨 静态待证 | `OnDestroy` 调用 `Shutdown()` + `Provider.onEnemyDisconnected` 订阅，代码可读；动态测试需 BepInEx 卸载场景 |

### v1.4.1 残余门槛（仍需满足）

| 门槛 | 状态 |
|---|---|
| 被动整理 Patch 保持禁用 | 🟨 静态待证 |
| 单机动态回归测试通过 | 🟧 动态待测（待 v2.0.1 测试覆盖） |
| U3DS 双机动态回归测试通过 | 🟧 动态待测（待 v2.0.1 测试覆盖） |
| 不宣称正式版可发布 | 🟨 静态待证 |

---

## 8. 最终裁决

### 当前阶段裁决

**🟧 v2.0.3 编码与编译完成，等待四次静态审计 + 动态测试放行**

- 🟨 静态待证：v2.0.0 + v2.0.1 + v2.0.2 + v2.0.3 全部代码实现完成，0 errors / 0 warnings 编译通过
- 🟨 静态待证：静态反射检查通过（被动 Patch 仍禁用，UI Patch 仅构造函数，STORAGE 按钮已移除）
- 🟧 动态待测：单机动态测试待执行（UX-G1..G6 + HK-1..5 + SAFE-1..2 + SAFE-5..6）
- 🟧 动态待测：U3DS 双机动态测试待执行（HK-6..8 + SAFE-3..4 + SAFE-7..8 + SEC-1..7 + PERF-1）
- ⏸️ 待外部审计员四次静态审计放行
- ⏸️ 待外部审计员最终放行

### 不放行项

1. 不放行正式版发布（等待 v2.0.3 专项回归通过）
2. 不放行认证改造（LaunchInventoryTidy 不涉及认证，但 SteamP2PFriends 认证主线仍在 Stage 6A）
3. 不放行其他模组基于 v2.0.0 协议扩展（V2 协议字段未写入 LaunchMultiplayerNet.EModMessage 枚举）

### 残余风险声明

即使 v2.0.3 实施完成：
- 手动整理路径仍执行"清空整个页面 + 重添"，但已加入完整事务化验证（值快照 + 选择性回滚 + 指纹守恒 + 逐坐标恢复验证）
- 物品守恒验证通过后，残余风险降为"算法选择是否最优"而非"物品是否丢失"
- 快捷键恢复采用两阶段提交 + CriticalFailure 回滚后按原坐标重绑，超时降级为"提示用户部分快捷键未恢复"
- 持久熔断跨服务器重启存活，需管理员通过 `/tidy_unfault <SteamID>` 解除
- 工坊虚拟容器（不走标准 openStorage 路径）服务器端 items[STORAGE] 为 0×0 的限制保持不变
- v2.0.1 限流参数（1s/5次）为保守值，实际使用中若误伤可调整

---

## 9. 文档索引

### v2.0.0 / v2.0.1 / v2.0.2 / v2.0.3 文档

- **本文件**：`AUDIT_CHECKLIST.md`（项目根目录）
- **v2.0.0 外部审计驳回报告**：`.audit/v2.0.0-static-audit-20260729/Codex-v2.0.0静态审计与指导报告-20260729.md`
- **v2.0.2 三次静态审计报告**：`.audit/v2.0.2-static-audit-20260729/Codex-v2.0.2第三次静态审计与指导报告-20260729.md`
- **设计计划**：`C:\Users\The New Age\AppData\Roaming\CherryStudio\.claude\plans\warm-plotting-wigderson.md`
- **待归档**：`.audit/v2.0.3-release-verification-<YYYYMMDD>/`（待动态测试后建立）

### v1.4.1 历史文档（保留参考）

- `.audit/v1.4.0-bug-analysis-20260716/items-tryadditem-bug-analysis-v2.md`
- `.audit/v1.4.0-bug-analysis-20260716/v1.4.1-release-gate-checklist.md`
- `.audit/v1.4.1-release-verification-20260728/test-report-20260728.md`

### v2.0.6.13 Round 6 修复文档（2026-07-31）

- **Round 6 修复报告**：`.audit/v2.0.6.13-codex-refactor-round6-20260731/RefactorReport-v2.0.6.13-round6-20260731.md`
- **Codex 第五轮 FAIL 审计报告**：`.audit/v2.0.6.13-auto-test-20260731-140608/Codex-架构审计与保姆级修复指导报告-v2.0.6.13-自动化实测失败-20260731.md`
- **修复后 TestHarness DLL**：`bin\TestHarness\LaunchInventoryTidy.dll`（SHA-256：`35AF81DFCCB002A7DB19F1B9342B8A62CC553C848CC9793D4CD58BF765201084`，250,368 bytes）
- **修复后 Release DLL**：`bin\Release\LaunchInventoryTidy.dll`（SHA-256：`355DAF23800185BA278C385BEA2DF03D022788DE38FC8CF90DDB3A6E154B5227`，158,720 bytes）

### v2.0.6.13 Round 7 修复文档（2026-07-31）

- **Round 7 修复报告**：`.audit/v2.0.6.13-codex-refactor-round7-20260731/RefactorReport-v2.0.6.13-round7-20260731.md`
- **Codex 第六轮 FAIL 审计报告**：`.audit/v2.0.6.13-auto-test-20260731-154411/Codex-架构审计与保姆级修复-v2.0.6.13-20260731.md`
- **修复后 TestHarness DLL**：`bin\TestHarness\LaunchInventoryTidy.dll`（SHA-256：`78085BEFDA891ED021CB8059A3E1DBAD83B3606E1908B014F5DFF28F68AFEA8A`，253,952 bytes）
- **修复后 Release DLL**：`bin\Release\LaunchInventoryTidy.dll`（SHA-256：`FF4C20EF5660321ED564FFB0D6627DFD9CF0030C511276E1B065557849E5EB51`，158,720 bytes）
- **run_tests.ps1**：SHA-256：`0A3150CAB29556EF9907568B3271FA400A2D4D110DF0A8474D719E671941BA98`

### v2.0.6.13 Round 8 修复文档（2026-07-31）

- **Round 8 修复报告**：`.audit/v2.0.6.13-codex-refactor-round8-20260731/RefactorReport-v2.0.6.13-round8-20260731.md`
- **Codex Round 8 审计报告**：`.audit/v2.0.6.13-codex-refactor-round8-20260731/Codex-架构审计与保姆级修复指导-v2.0.6.13-round8-20260731.md`
- **本轮测试归档**：`.audit/v2.0.6.13-auto-test-20260731-171750/`（SP-CONS 2 FAIL + SP-HK BLOCKED）
- **Codex Round 8 裁决**：FAIL（HK-CROSS-01 1/3 + HK-FIXTURE-02 1/3 + PACK-GEO-01 待动态验证 0/3）
- **修复要点**：
  - `InventorySolver.TryPack` MaxRects/FFD 模式新增多候选选最优（主方向 + 反向兜底），避免单排序方向失败时 Rejected
  - `TestFixtureSession` 新增 `_fixtureItem1x1` 字段 + `TryRebindHotkeys` 公开方法，搜索 page 2 实例重新绑定键 3/7
  - `AutoTestDriver.RunSpHkCoroutine` 签名新增 `fixture` 参数，启动前调用 `fixture.TryRebindHotkeys`
- **修复后 TestHarness DLL**：`bin\TestHarness\LaunchInventoryTidy.dll`（SHA-256：`39A97939AB1EF18B69BEBC6B4CA407FD2FE100A6FE7ED76E2D1EE106403EF927`，256,000 bytes）
- **修复后 Release DLL**：`bin\Release\LaunchInventoryTidy.dll`（SHA-256：`21DB0C3924DAA4C770CF22323646BBCC2830C4A2DA098FC673539017E5D4C273`，159,232 bytes）

### v2.0.6.13 Round 9 修复文档（2026-07-31）

- **Round 9 修复报告**：`.audit/v2.0.6.13-codex-refactor-round9-20260731/RefactorReport-v2.0.6.13-round9-20260731.md`
- **上游 Codex 审计**：Codex Round 8 §3 蓝图（HK-CROSS-01 + HK-FIXTURE-02 P0 阻断）
- **修复要点**（按 Codex Round 8 §3.1-§3.3 蓝图）：
  - `TidyTransaction.cs`：`HotkeyRestoreEntry` 由 struct 升级为 sealed class，`ExpectedItemId`（ushort）替换为 `ExpectedFingerprint`（ItemFingerprint 完整指纹：id + amount + quality + state）
  - `ManualTidyNetwork.cs` `ExecuteTidyRequestOnMainThread`：新增 `trustedHotkeyFingerprints` 捕获循环，服务端从已解析的真实 ItemJar 取 trusted fingerprint，**绝不信任客户端上传的 quality/state**；mapping build loop 改用 `trustedFingerprint`
  - `ManualTidyNetwork.cs` ACK 阶段：新增 `IsPluginMainThread` + `TryResolveExactHotkeyTarget`（10 步严格校验，含 covered cell + 完整指纹 Equals + ItemTool.checkUseable）+ `TryRestoreOneHotkeyOnMainThread` 三个辅助方法；ACK 循环改用 `TryRestoreOneHotkeyOnMainThread`，替换 74 行内联 ID-only 比较
  - `TestFixtureSession.cs`：`TryBindRequiredHotkeys` 新增 `ItemTool.checkUseable(PAGE_SLOTS, item1x1.id)` 资格校验 + `TryCaptureRequiredHotkeys` 写入后验证；**删除 `TryRebindHotkeys` 方法**（掩盖生产 BUG 的测试工具）
  - `AutoTestDriver.cs`：`RunSpHkCoroutine` 移除 `fixture.TryRebindHotkeys` 调用；`RunSpConsCaseCoroutine` 在 `TrySendTidyRequest` 之前新增 `TryCaptureRequiredHotkeys` 基线捕获，在 committed + 守恒 + 布局检查之后新增 `VerifyHotkeyCase` 断言
- **负向约束遵守**：V3 `NewPositionMapping` 7-byte wire layout 不变；`ItemsTryAddItemPatch` 保持禁用；LMN/U3DS/SteamP2PFriends 未修改；`ManualTidyService` 事务逻辑未修改
- **修复后 TestHarness DLL**：`bin\TestHarness\LaunchInventoryTidy.dll`（SHA-256：`250EB75633875070AB8CC9A25AD6A6535C88C2F6DB63375100F026DEA6D3A515`，258,560 bytes，+2,560 bytes vs Round 8）
- **修复后 Release DLL**：`bin\Release\LaunchInventoryTidy.dll`（SHA-256：`403C3E21C5B1621C84B4EFCF541BF37C8CA6270BFB1DF41DC2EEE1DC1DA229FA`，162,304 bytes，+3,072 bytes vs Round 8）
- **编译结果**：TestHarness 0 errors / 0 warnings（1.75s）；Release 0 errors / 0 warnings（0.73s）
- **下一步**：待 Codex Round 9 静态审计放行后，执行 `run_tests.ps1` 隔离单机 TestHarness，验证 §3.4 五项回归证据
- **三振出局计数器当前状态**：
  - HK-CROSS-01：1/3（待 Round 9 动态验证降级至 0/3）
  - HK-FIXTURE-02：1/3（待 Round 9 动态验证降级至 0/3）
  - PACK-GEO-01：0/3（Round 8 已修复，待 Round 9 动态验证移除）

### v2.0.6.13 Round 9 Codex 单机深度测试放行（2026-07-31）

- **Codex 单机深度测试放行报告**：`.audit/v2.0.6.13-auto-test-20260731-183915/Codex-架构审计与单机深度测试放行-v2.0.6.13-20260731.md`
- **裁决**：🟢 PASS 放行 U3DS 双端测试；🔴 P2P、正式发布继续冻结
- **核心结论**：18/18 PASS；12 次真实整理均 `restored=2 / verified=2 / cleared=0 / failed=0`；无人工重绑掩盖
- **单机深度测试 TestHarness DLL**：SHA-256 `3DFB97C9...7D142`
- **单机深度测试 Release DLL**：SHA-256 `DC9E9C97...FA05F`
- **三振出局计数器全部清零**：
  - HK-CROSS-01：0/3（已降级）
  - HK-FIXTURE-02：0/3（已降级）
  - PACK-GEO-01：已移除（Round 8 多候选修复动态验证通过）

### v2.0.6.13 U3DS 自动化双端测试桩实施（2026-07-31）

- **U3DS 蓝图**：`.audit/u3ds-test-harness-blueprint-20260731/Codex-U3DS自动化双端测试桩蓝图-20260731.md`
- **实施报告**：`.audit/u3ds-test-harness-blueprint-20260731/ImplementationReport-20260731.md`
- **项目位置**：`..\LaunchTidyTestHarness\`（独立项目，不打包进 LIT Release）
- **核心约束**：
  - 测试桩是独立 DLL，引用 LIT Release + LMN；不注册 LMN channel 100
  - Harmony Prefix 只读观察 LIT 私有 `HandleTidyCommittedFromServer` / `HandleTidyHotkeyResultFromServer`，恢复 stream position
  - 服务器仅提供只读快照命令 `tidy_dump_server <SteamID>`
  - 双端 JSON 经稳定排序后 SHA-256 比对作为最终门槛
- **文件清单**（6 个）：
  - `LaunchTidyTestHarness.csproj` - 项目文件，引用 LIT Release + LMN + Unturned + BepInEx + Harmony
  - `HarnessConfig.cs` - 配置加载器（RunDirectory / TargetSteamId / AutoRun）
  - `SnapshotCodec.cs` - 只读快照编解码器（page 2-6 完整指纹 + 稳定排序 + UTF-8 无 BOM JSON）
  - `TidyWireProbe.cs` - Harmony Prefix 探针（只读观察 + stream position 恢复）
  - `LaunchTidyTestHarnessPlugin.cs` - BepInEx 插件入口 + `/tidy_dump_server` 命令 + 客户端测试协程（6 SYNC + 1 COOLDOWN）
  - `run_u3ds_tests.ps1` - PowerShell 自动化流水线（部署/启动/等待/比对/归档）
- **蓝图偏差与调整**：
  - Prefix stream position：蓝图 `Position=0` 改为读当前 position + 恢复（与 LIT `peekPos` 模式一致，更鲁棒）
  - 客户端连接检测：蓝图 `Provider.onClientConnected` 改为 `Provider.isConnected` 轮询（`onClientConnected` 是服务器端事件，客户端不触发）
  - 本地 SteamID 获取：蓝图 `Provider.user.m_SteamID`（字段可能不存在）改为 `SteamUser.GetSteamID()`（Steamworks.NET 标准 API）
  - yield in try-catch：蓝图在 try-catch 内 yield（CS1626 编译错误）重构为 try-catch 仅捕获，yield 在外
- **编译结果**：0 errors / 0 warnings（0.85s）
- **LaunchTidyTestHarness.dll**：SHA-256 `C9A282872CCEA094173197F7C4A489CD084755D77096A28C0BACDB98C8A07940`，27,136 bytes
- **下一步**：待 Codex 静态审计放行后，用户提供 `u3ds-test.settings.json`，执行 `run_u3ds_tests.ps1 -SettingsPath <path>`
- **回归矩阵**：6 SYNC 用例 + 1 COOLDOWN 用例 + 双端 SHA-256 比对；断线重连/服务器重启矩阵本轮不实现（后续独立脚本）
- **负向约束遵守**：未注册 LMN channel 100；未修改 LIT Release / LMN / U3DS；测试桩不打包进生产整合包

### v2.0.6.13 U3DS 测试桩 v1.0.1 Codex FAIL 审计返修（2026-07-31）

- **Codex v1.0.0 审计报告**：`.audit/u3ds-test-harness-blueprint-20260731/Codex-U3DS测试桩架构审计-v1.0.0-20260731.md`
- **裁决**：🔴 FAIL（6 项阻断：4 P0 + 2 P1；打回测试桩重编译，不允许启动 U3DS 动态测试）
- **返修实施报告**：`.audit/u3ds-test-harness-blueprint-20260731/ImplementationReport-v2.0-20260731.md`
- **6 项阻断与修复对照**：
  - U3DS-P0-01（P0）：TidyWireProbe Prefix 参数 `r` -> `__0`（Harmony 位置绑定）；`_installed=true` 从 Patch 前移到两个 Patch 都成功后
  - U3DS-P0-02（P0）：新增 `HarnessRuntimeGuard.cs`；Plugin Awake/Update 改为 fail-closed（`_startupFailure` -> 原子写失败 result -> `Application.Quit()`）
  - U3DS-P0-03（P0）：`execute()` 写 `.ready` 文件；`Run()` 收尾保持连接轮询 `.ready`（30 秒超时）；脚本在客户端退出前发 `tidy_dump_server`
  - U3DS-P0-04（P0）：脚本新增 `Save-DeployFile` / `Restore-DeployFiles`；`finally` 块逆序恢复所有部署文件 + 哈希校验
  - U3DS-P1-01（P1）：`SnapshotCodec.Write` 改用 `HarnessRuntimeGuard.WriteJsonAtomically`（临时文件 + Flush(true) + File.Move/File.Replace）
  - U3DS-P1-02（P1）：`HarnessRuntimeGuard.VerifyLoadedAssemblies` 在 Awake 时校验 Harness/LIT/LMN 三个程序集 SHA-256（从 `Assembly.Location` 读取）
- **HarnessConfig 新增字段**：`ExpectedHarnessSha256` / `ExpectedLitSha256` / `ExpectedLmnSha256` / `ServerSnapshotReadyPath`
- **Sync 逻辑修正**：先等 `TryGetCommit`（8s），仅 `Committed` 才等 `TryGetHotkey`（5s）
- **Cooldown 逻辑修正**：严格 `unseen==0 && committed==1 && rejected==4 && criticalFailure==0`（旧版 `rejected<4` 允许 5 Rejected）
- **修改文件清单**（7 个）：
  - `TidyWireProbe.cs`（完整替换）
  - `HarnessConfig.cs`（完整替换）
  - `HarnessRuntimeGuard.cs`（新建）
  - `SnapshotCodec.cs`（Write 方法替换）
  - `LaunchTidyTestHarnessPlugin.cs`（7 处 Edit：_startupFailure 字段 + Awake + Update + execute + Run 收尾 + Sync 等待 + Cooldown 验证）
  - `run_u3ds_tests.ps1`（完整替换）
  - `LaunchTidyTestHarness.csproj`（添加 HarnessRuntimeGuard.cs）
- **编译结果**：0 errors / 0 warnings（1.77s）
- **LaunchTidyTestHarness.dll v1.0.1**：SHA-256 `F2327CE9808951725A718F6E2DAF0C8596147DE05E615ACABFA090FBD2BF50E1`，30,720 bytes
- **受控 LIT Release DLL**：SHA-256 `DC9E9C97F48FCCB3468A68F453AAC381EE23DDD1CA483154CA309EF9E63FA05F`（未修改）
- **受控 LMN DLL**：SHA-256 `4C73966C4358EDD31EA9FC39E442B7B47A7E0382EDF8CB7F81B097C48C287842`（未修改）
- **三振出局计数器**：6 项阻断均 1/3（本轮首次返修）
- **下一步**：提交 Codex v1.0.1 静态审计；放行后执行 `run_u3ds_tests.ps1` U3DS 双端动态测试

### v2.0.6.13 U3DS 测试桩 v1.0.1 版本元数据返修（2026-07-31）

- **Codex v1.0.1 审计报告**：`.audit/u3ds-test-harness-blueprint-20260731/Codex-U3DS测试桩架构审计-v1.0.1-20260731.md`
- **裁决**：🔴 FAIL（9 项门槛中 8 项通过，1 项阻断 U3DS-P1-03；打回重编译，尚不启动 U3DS 动态测试）
- **U3DS-P1-03 阻断**：DLL `FileVersion=0.0.0.0`、`ProductVersion=0.0.0.0`；源码 `[BepInPlugin(..., "1.0.0")]` 仍为 1.0.0；无 `AssemblyInfo.cs`
- **返修实施报告**：`.audit/u3ds-test-harness-blueprint-20260731/ImplementationReport-v2.1-20260731.md`
- **修复内容**：
  - 新建 `LaunchTidyTestHarness/Properties/AssemblyInfo.cs`：`AssemblyVersion("1.0.1.0")` + `AssemblyFileVersion("1.0.1.0")`（无 `AssemblyInformationalVersion`，ProductVersion 回退到 AssemblyVersion = 1.0.1.0）
  - 更新 `LaunchTidyTestHarnessPlugin.cs:16`：BepInPlugin 版本 `"1.0.0"` -> `"1.0.1"`
  - 更新 `LaunchTidyTestHarness.csproj`：添加 `<Compile Include="Properties\AssemblyInfo.cs" />`
- **蓝图偏差说明**：§3.1 指定 `AssemblyInformationalVersion("1.0.1")`，但 §3.4 验证要求 ProductVersion == "1.0.1.0"；移除 InformationalVersion 让 ProductVersion 回退到 AssemblyVersion 以同时满足两节要求
- **已闭环（6 项，v1.0.0 审计阻断全部保持）**：U3DS-P0-01/02/03/04 + U3DS-P1-01/02
- **编译结果**：0 errors / 0 warnings（0.67s）
- **版本元数据验证**：FileVersion=1.0.1.0 ✅ / ProductVersion=1.0.1.0 ✅ / VERSION_VERIFY_PASS
- **LaunchTidyTestHarness.dll v1.0.1（版本修复后）**：SHA-256 `8B2202E25186182C66B0EE9288AEA9512C9278D3F4E7817D89E7A72E2E298D39`，32,256 bytes
- **受控 LIT Release DLL**：SHA-256 `DC9E9C97F48FCCB3468A68F453AAC381EE23DDD1CA483154CA309EF9E63FA05F`（未修改）
- **受控 LMN DLL**：SHA-256 `4C73966C4358EDD31EA9FC39E442B7B47A7E0382EDF8CB7F81B097C48C287842`（未修改）
- **PowerShell 解析验证**：`run_u3ds_tests.ps1` PS1_PARSE_PASS
- **三振出局计数器**：U3DS-P1-03 为 1/3（本轮首次返修，待 Codex v1.0.2 核验）
- **下一步**：提交 Codex v1.0.2 静态审计；放行后执行 `run_u3ds_tests.ps1` U3DS 双端动态测试

### v2.0.6.13 U3DS 测试桩脚本 .lnk 快捷方式解析增强（2026-07-31）

- **背景**：Codex v1.0.2 PASS 放行后，实机准备时发现 U3DS 服务器通过 `.lnk` 快捷方式启动（含 `-NetTransport=SteamNetworking` 等参数）；`ProcessStartInfo + RedirectStandardInput` 无法直接运行 `.lnk`
- **返修实施报告**：`.audit/u3ds-test-harness-blueprint-20260731/ImplementationReport-v2.2-20260731.md`
- **修改内容**（仅 `run_u3ds_tests.ps1`，DLL 未重新编译）：
  - 新增 `Resolve-ServerLaunch` 函数：`.lnk` 分支用 `WScript.Shell` COM 解析 `TargetPath`/`Arguments`/`WorkingDirectory`；非 `.lnk` 分支用 exe 父目录作为工作目录
  - 替换服务器启动段：`$psi.FileName/Arguments/WorkingDirectory` 从 `Resolve-ServerLaunch` 返回值取
  - 合并参数：`($shortcut.Arguments + ' ' + $extraArguments).Trim()`，自动保留快捷方式原有参数
- **新增配置模板**：`LaunchTidyTestHarness/u3ds-test.settings.template.json`（展示 `.lnk` 形式 `ServerExe` 用法）
- **PowerShell 语法解析**：PS1_PARSE_PASS ✅
- **DLL 身份未变**：Harness `8B2202E2...8D39` / LIT `DC9E9C97...FA05F` / LMN `4C73966C...7842`（未重新编译）
- **负向约束遵守**：未修改任何 C# 源码；未变更 P0/P1 修复逻辑；仅脚本新增 `.lnk` 解析能力
- **下一步**：提交 Codex 脚本增量静态审计；放行后用户提供真实 `u3ds-test.settings.json`，执行 `run_u3ds_tests.ps1`

### v2.0.6.14 U3DS-LIT-ALLPAGES-01 Agent 第 1 轮修复（2026-07-31）

- **审计源**：`.audit/u3ds-runs/u3ds-20260731-213913/Codex-U3DS-ALLPAGES-01-Agent第1轮修复蓝图-20260731.md`
- **审计裁决**：FAIL（仅授权第 1 轮修复；未授权 U3DS 通过、Beta 或 P2P 测试）
- **阻断项**：`U3DS-LIT-ALLPAGES-01`（三振计数 1 / 3）
- **根因**：`ManualTidyService.TidyAllPlayerPages` 的 Prepare 循环无条件遍历 page 2-6 并对每页调用 `PreparePage`；原生 `0x0` page（未装备/未扩容服装容器）触发 `PreparePage` 返回 `Valid=false`，整体 `Rejected`，合法 U3DS ALL_PAGES 请求全部被拒
- **发生环境**：U3DS，SteamID `76561199030780228`；page 2 为 `5 x 3`，page 3-6 为 `0 x 0`；服务器日志连续七次记录 `page 3 Prepare failed; reject all pages with zero mutation`
- **实现报告**：`.audit/u3ds-runs/u3ds-20260731-213913/ImplementationReport-v2.0.6.14-ALLPAGES-01-20260731.md`

#### 修改文件清单（审计 §3.1 唯一生产代码修改）

| 文件 | 改动类型 | 核心改动 |
| --- | --- | --- |
| `ManualTidyService.cs` | 修改 | `TidyAllPlayerPages` 第 342-391 行：Prepare 循环改为跳过原生不活动页（null/0×0），活动页 Prepare 失败/异常仍 fail-closed 返回 `RejectedNoMutation`；新增空 preparations 守门（`preparations.Count == 0` 时返回 `RejectedNoMutation`） |
| `Properties/AssemblyInfo.cs` | 修改 | `AssemblyVersion` / `AssemblyFileVersion` 从 `2.0.6.13` 升至 `2.0.6.14` |
| `LaunchInventoryTidyPlugin.cs` | 修改 | L12 `BepInPlugin` 版本升至 `2.0.6.14`，插件名同步；L151 加载横幅日志升至 `v2.0.6.14` |

#### 未修改文件（审计 §四负向约束）

- `ManualTidyNetwork.cs`、`TidyFaultCircuit.cs`、`PlayerOperationGate.cs`、`RequestAdmissionStore.cs`、`ServerSessionRegistry.cs`、`LmnDependencyGuard.cs`
- LMN DLL、U3DS / Assembly-CSharp、SteamP2PFriends
- `LaunchTidyTestHarness/*`（本轮未修改测试夹具源码）

#### 编译验证

| 配置 | 命令 | 耗时 | errors | warnings |
| --- | --- | --- | --- | --- |
| LaunchInventoryTidy Release | `dotnet build LaunchInventoryTidy.csproj -c Release -nologo` | 1.70s | 0 | 0 |
| LaunchInventoryTidy TestHarness | `dotnet build LaunchInventoryTidy.csproj -c TestHarness -nologo` | - | 0 | 0 |
| LaunchTidyTestHarness Release | `dotnet build LaunchTidyTestHarness.csproj -c Release -nologo` | 1.16s | 0 | 0 |

#### DLL 产物身份

| 产物 | 大小 | SHA-256 | 版本 |
| --- | --- | --- | --- |
| LaunchInventoryTidy.dll (Release) | 162,816 bytes | `63D53F20EF882BE709287857A424CD54E496B9AEEA7C99A2C3FBFBC0E3EE8C4E` | 2.0.6.14 |
| LaunchInventoryTidy.dll (TestHarness) | 259,072 bytes | `3DFB97C934F0882E9A0F88FF6F7514F410A2259DFEE3507A2569BE245D87D142` | 2.0.6.14 |
| LaunchTidyTestHarness.dll | 43,008 bytes | `A8EB14939B111D346AC54449F01170F14B69B5A1CCEB937871F34F03C37A3C96` | 1.0.1.0 |
| LMN.dll | 未修改 | `4C73966C4358EDD31EA9FC39E442B7B47A7E0382EDF8CB7F81B097C48C287842` | - |

- 基线 v2.0.6.13 Release SHA-256（审计记录）：`DC9E9C97F48FCCB3468A68F453AAC381EE23DDD1CA483154CA309EF9E63FA05F`
- 本轮 v2.0.6.14 Release SHA-256：`63D53F20EF882BE709287857A424CD54E496B9AEEA7C99A2C3FBFBC0E3EE8C4E`
- 哈希差异证明源码修改已实际写入 DLL

#### 版本元数据核验

- FileVersion：2.0.6.14 ✅
- ProductVersion：2.0.6.14 ✅
- FileMajorPart.FileMinorPart.FileBuildPart.FilePrivatePart：2.0.6.14 ✅

#### 语义边界严格分离

- **ALL_PAGES 跳过原生不活动页**：`pageItems == null || pageItems.width == 0 || pageItems.height == 0` 时 `continue`，不调用 `PreparePage`，不触发 fail-closed
- **TidyPage 单页整理不变**：`TidyPage` 单页零尺寸页拒绝行为未修改，避免恶意/错误单页请求伪装成功
- **活动页 fail-closed 保留**：非零尺寸页的 `PreparePage` 失败或异常仍立即返回 `RejectedNoMutation`，不进入 Commit 阶段
- **空活动页守门**：`preparations.Count == 0` 时返回 `RejectedNoMutation`，不伪报成功

#### U3DS 必过判据对照（审计 §3.3）

| 套件 | 通过条件 | 本次预期 |
| --- | --- | --- |
| TC_SYNC | SameType / MaxRects / FFD × 升降序，共 6 次均为 `Committed` | 待动态测试验证 |
| 热键 | 每次 `cleared=0`、`failed=0`；数字键 3、7 完整实例指纹不变 | 待动态测试验证 |
| TC_COOLDOWN | 1 `Committed`、4 `Rejected`、0 `CriticalFailure` | 待动态测试验证 |
| 物品守恒 | `page,x,y,rot,id,amount,quality,state` 多重集守恒 | 待动态测试验证 |
| 双端同步 | 客户端和服务端规范快照 SHA-256 完全相同 | 待动态测试验证 |
| 不活动页 | 日志至少出现 page 3-6 的 `skip inactive`；不得出现 `page 3 Prepare failed` | 静态保证：§2.1 修改后日志输出 `ALL_PAGES skip inactive page=N, width=0, height=0` |
| 收尾 | 客户端自动退出；脚本恢复两端 DLL/配置并生成 `TestReport-U3DS.md` 与 `manifest.csv` | 待动态测试验证 |

#### 当前停止点

- **Agent 完成范围**：源码修改 + 版本升级 + Release / TestHarness 编译 + DLL 哈希与版本元数据核验 + 静态安全论证 + 实现报告归档 + AUDIT_CHECKLIST 更新
- **Agent 停止点**：U3DS 动态测试需用户本机环境执行（Agent 无 U3DS 运行权限）
- **下一步**：用户运行 `run_u3ds_tests.ps1`，测试归档至 `.audit/u3ds-runs/u3ds-<timestamp>/`，提交 Codex 第 2 轮审计裁决
- **三振计数**：U3DS-LIT-ALLPAGES-01 当前 1 / 3；若本轮 U3DS 测试不通过，Codex 接管第 2 轮修复

### v2.0.6.14 Beta 候选包制作（2026-07-31，Codex PASS 放行受控 Beta）

- **审计源**：`.audit/u3ds-runs/u3ds-20260731-224035/Codex-v2.0.6.14-U3DS动态证据与Beta交接-20260731.md`
- **Codex 裁决**：PASS（放行受控 Beta：单机 + 标准 U3DS；P2P 不放行）
- **门槛项达成**：8/8 通过
- **三振计数器**：U3DS-LIT-ALLPAGES-01 与 U3DS-RUNNER-NONINTERACTIVE-01 均关闭（0/3）

#### U3DS 动态证据摘要（Codex §二）

1. ✅ Harness 自动夹具成功创建，同 ID/不同 quality 的两件热键物品以及混合尺寸物品均已进入真实 U3DS 玩家库存
2. ✅ SameType、MaxRects、FFD 各升/降序 6 个 `ALL_PAGES` 请求全部 `Committed`，服务器日志每次均记录 page 3-6 `skip inactive`，没有再出现 `page 3 Prepare failed`
3. ✅ 冷却测试为 `1 Committed / 4 Rejected / 0 CriticalFailure`
4. ✅ 6 次热键结果均 `restored=2, cleared=0, failed=0`；`verified=0` 是专用服务器既有协议语义，Harness 以客户端完整物品指纹独立验证
5. ✅ 服务端、客户端规范全页快照 SHA-256 均为 `7822DECC05DB4D6B6A4CDE26CA8DBBECBB0DECFB86FCDCAD90780845F4B0EF7F`
6. ✅ `manifest.csv` 共 23 行，逐项复算全部匹配；服务端和客户端 LIT/LMN 已恢复到运行前部署哈希，所有 Unturned 进程已退出

#### 受控构建哈希库（Codex §一）

| 组件 | SHA-256 | 本地 bin/Release 实测 |
|---|---|---|
| LaunchInventoryTidy.dll | `6FFE5499E98D85F0A3CFFEA075601D6B6AAF8BC4FD00AB3808F7F4CACE530768` | ✅ 匹配 |
| LaunchMultiplayerNet.dll | `4C73966C4358EDD31EA9FC39E442B7B47A7E0382EDF8CB7F81B097C48C287842` | ✅ 匹配 |
| LaunchTidyTestHarness.dll | `95B09CAF992FEF43E7F202C6B99A5C0F844BBA5559ACA5927BA95666E49A895E` | ✅ 匹配 |

#### Beta 候选包内容清单

- **包目录**：`.audit/beta-candidates/v2.0.6.14-Beta/LaunchInventoryTidy-v2.0.6.14-Beta/`
- **清单文件**：`.audit/beta-candidates/v2.0.6.14-Beta/BETA-CANDIDATE-MANIFEST-20260731.md`

| 文件 | 大小 | SHA-256 |
|---|---|---|
| `LaunchInventoryTidy.dll` | 162,816 bytes | `6FFE5499E98D85F0A3CFFEA075601D6B6AAF8BC4FD00AB3808F7F4CACE530768` |
| `SHA256SUMS.txt` | 90 bytes | `DFD09570E964E2384C3ABA89F1039D8ACF7422ECB9EFE6EF2A7DC9098D7BC87A` |
| `DEPENDENCY.md` | 2,602 bytes | `7DB6C936559C075337EB2254CC6BD3D09B205D42C2C5903B202CAC369FFBCDE7` |
| `TEST_SCOPE.md` | 4,928 bytes | `847A0767D8FFC1ED3BF929D544AE0E1009167EDC275E3A7EEFD855002BD45081` |

- **文件总数**：4（1 DLL + 3 文档）
- **DLL 总数**：1（仅 `LaunchInventoryTidy.dll`，无 TestHarness DLL，无 LMN DLL）
- **多余配置文件**：无（无 `u3ds-harness.config.json`，无自动夹具，无故障注入）

#### Codex §三要求落实核对

| 要求 | 状态 |
|---|---|
| 创建独立的 `LaunchInventoryTidy-v2.0.6.14-Beta` 包目录 | ✅ |
| 仅复制 `bin\Release\LaunchInventoryTidy.dll` | ✅ |
| 包内创建 `SHA256SUMS.txt`，首行必须为受控哈希 | ✅ 首行 `6FFE5499...  LaunchInventoryTidy.dll` |
| 创建 `DEPENDENCY.md`：LMN 最低 3.3.1.0，本轮已测 3.3.4.0 与 SHA-256；不打包 LMN DLL | ✅ |
| 创建 `TEST_SCOPE.md`：SP 18/18 PASS；U3DS 6 SYNC + 冷却 + 双端指纹 PASS；P2P 未测试/不支持/不放行 | ✅ |
| 将候选包清单、各文件哈希和本报告路径交回 Codex | ✅ `BETA-CANDIDATE-MANIFEST-20260731.md` |
| 未经用户明确授权，Agent 不得上传、发布或修改线上文件 | ✅ 候选包仅位于本地 `.audit/beta-candidates/` |

#### Codex §四负向约束核对

| 禁止项 | 状态 |
|---|---|
| 禁止修改或重编译 LMN、SteamP2PFriends、U3DS/Assembly-CSharp | ✅ 未触碰 |
| 禁止将 TestHarness DLL、自动夹具、持久熔断重置、故障注入、`u3ds-harness.config.json` 放入 Beta 包 | ✅ 包内无 TestHarness DLL，无配置文件 |
| 禁止改变 `ManualTidyService`、`ManualTidyNetwork`、`TidyFaultCircuit`、`PlayerOperationGate`、`RequestAdmissionStore`、`ServerSessionRegistry` | ✅ 本轮未修改任何源码 |
| 禁止在发行说明声称 P2P 已支持或全量库存由 Hotkey ACK 证明 | ✅ `TEST_SCOPE.md` 明确标注 P2P 未测试/不支持/不放行 |
| Beta 包完成后必须重新核验 DLL SHA-256、文件版本 2.0.6.14、包内无多余 DLL/测试配置 | ✅ 已完成核验 |

#### 当前停止点

- **Agent 完成范围**：Beta 候选包目录创建 + 受控 Release DLL 复制 + 三份文档（SHA256SUMS.txt / DEPENDENCY.md / TEST_SCOPE.md）撰写 + 候选包清单生成 + 哈希核验 + 版本元数据核验 + Codex 负向约束核对
- **Agent 停止点**：候选包仅位于本地 `.audit/beta-candidates/v2.0.6.14-Beta/`，未上传任何外部平台
- **下一步**：将 `BETA-CANDIDATE-MANIFEST-20260731.md` 路径交回 Codex 复核；Codex 复核通过后由用户决定是否发布到外部平台

### U3-SDK 关键事实（v2.0.0 / v2.0.1 实现依据）

- `PlayerEquipment._hotkeys` 仅 LocalPlayer 初始化（`PlayerEquipment.cs:3290` 在 `if (channel.IsLocalPlayer)` 内）
- `ServerBindItemHotkey(byte, ItemAsset, byte, byte, byte)` 在 `PlayerEquipment.cs:384`
- `ServerClearItemHotkey(byte)` 在 `PlayerEquipment.cs:389`
- `HotkeyInfo`（`PlayerEquipment.cs:24-42`）：id / page / x / y，无实例 ID
- `ItemJar`（`Unturned/Inventory/ItemJar.cs:9-17`）：无实例 ID，身份靠 (page,x,y) + item.id
- `Items.getItem(byte)` / `getIndex(byte, byte)` / `removeItem(byte)` / `addItem(byte, byte, byte, Item)`
- `PlayerInventory.items` 数组：2=SLOTS, 3=BACKPACK, 4=VEST, 5=SHIRT, 6=PANTS, 7=STORAGE
- `PlayerInventory.SLOTS` / `PANTS` 是 `static readonly byte`（不是 const），需硬编码 2 / 6
- `Provider.onEnemyDisconnected(SteamPlayer)` 是远端玩家断开事件（服务器端触发，房主端触发）
- `Provider.onClientDisconnected()` 是本地客户端断开事件（仅客机端触发）
