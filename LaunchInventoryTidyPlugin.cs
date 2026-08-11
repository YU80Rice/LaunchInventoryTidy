using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Logging;
using BepInEx;
using HarmonyLib;
using LaunchMultiplayerNet;
using SDG.Unturned;
using Steamworks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LaunchInventoryTidy
{
    [BepInPlugin("com.yu80rice.launchinventorytidy",
        "LaunchInventoryTidy [v3.0.1 / scoped P2P fault isolation]",
        "3.0.1")]
    [BepInDependency(LaunchMultiplayerNetPlugin.Guid, BepInDependency.DependencyFlags.HardDependency)]
    public class LaunchInventoryTidyPlugin : BaseUnityPlugin
    {
        public const string HARMONY_ID = "com.yu80rice.launchinventorytidy";

        public static LaunchInventoryTidyPlugin Instance { get; private set; }

        public static ManualLogSource Log { get; private set; }

        public Harmony HarmonyInstance { get; private set; }

        /// <summary>
        /// v2.0.6.3 新增：Unity 主线程 ID（在 Awake 阶段缓存）。
        /// 整理事务入口（ManualTidyService.TidyPage / TidyAllPlayerPages）会校验当前线程是否匹配，
        /// 防止任何非主线程的背包物品增删操作破坏 Unity 原版 API 的线程安全性假设。
        /// </summary>
        public static int MainThreadId { get; private set; }

        /// <summary>
        /// v2.0.6.3 新增：Dependency Guard 是否通过。
        /// false 时插件已完成最小化初始化但拒绝注册整理处理器，防止用户部署旧版 LMN 触发 MissingMethodException。
        /// </summary>
        public static bool DependencyGuardPassed { get; private set; }

        private GameObject _watcherObject;
        private bool _disconnectHooked;
        private bool _connectedHooked;
        private bool _serverHostedHooked;
        private CommandTidyFaults _cmdFaults;
        private CommandTidyUnfault _cmdUnfault;
        private CommandTidyFaultRecover _cmdRecover;
#if TIDY_TEST_HARNESS
        private CommandTidyFaultInjectionTest _cmdFaultInjectionTest;
        private CommandTidyAutoTest _cmdAutoTest;
#endif

        // P0-LIT-01：Listen Host 本地会话延迟建立。
        // OnServerHosted 回调内 Provider.server 可能尚未稳定，禁止急切 BeginSession。
        // _localSessionPending=true 表示已收到 OnServerHosted，等待主线程 Update 检测身份稳定后建立会话。
        // _localSessionStarted=true 表示本地主机会话已建立，避免重复 BeginSession。
        private bool _localSessionPending;
        private bool _localSessionStarted;

        // P0-LIT-02 R2：会话作用域隔离 - 跟踪当前已初始化的持久熔断 scope。
        // scope 转换由 TrySwitchFaultScope 唯一权威门控制：
        //   - 无副作用校验（ValidateScopeArguments）通过后才允许清空运行时熔断 + InitializeForScope + Load
        //   - 任何转换失败都 fail-closed（清空运行时 + 重置 scope 字段 + SetDegraded）
        //   - 不再因 _currentScopeMode=="p2p" 永久阻止单机重载（修复 P0-LIT-02-A）
        // 静态字段：供 public static BeginScope 直接访问，避免依赖 Instance 生命周期。
        private static readonly object _scopeLock = new object();
        private const string SingleplayerScopeMode = "singleplayer";
        private const string P2PScopeMode = "p2p";
        private static string _currentScopeMode;
        private static string _currentScopeMap;
        private static int _currentScopeSlot;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            MainThreadId = Thread.CurrentThread.ManagedThreadId;

            // v2.0.6.13 Codex 第二轮复审：删除 TidyThreadAndRateGuard 死代码（双轨问题）。
            // 主线程断言 + 限流已由 ManualTidyService.IsMainThread + TidyRateLimiter 单一权威实现。
            DontDestroyOnLoad(this.gameObject);
            this.gameObject.hideFlags = HideFlags.HideAndDontSave;

            // v2.0.6.3 P0：前置库最低版本强约束（Dependency Guard）。
            // v3.0.0 升级：最低版本提升至 4.0.0.0，对齐 LMN v4.0.0 breaking change。
            // 在任何 LMN API 调用前验证 LMN AssemblyFileVersion >= 4.0.0.0，
            // 低于此版本时拒绝完成初始化，防止 MissingMethodException 触发整理请求持续熔断。
            DependencyGuardPassed = LmnDependencyGuard.Verify(Logger);
            if (!DependencyGuardPassed)
            {
                Logger.LogError("[Tidy] Dependency Guard 未通过，LaunchInventoryTidy 拒绝完成初始化（整理功能不可用）");
                Logger.LogError("[Tidy] 请升级 BepInEx/plugins/LaunchMultiplayerNet.dll 至 v4.0.0.0 或更高版本后重启游戏");
                // 故意不 return：仍允许 HarmonyInstance.PatchAll 等基础初始化，但 ManualTidyNetwork.RegisterHandlers
                // 不会执行，因此整理通道不会注册，玩家发起整理请求将被服务器拒绝（无 handler）。
                return;
            }

            HarmonyInstance = new Harmony(HARMONY_ID);
            HarmonyInstance.PatchAll();

            // v2.0.6.5：初始化客户端会话 nonce（V3 协议防跨会话重放）
            // 必须在 ManualTidyNetwork.RegisterHandlers 之前完成，确保首条请求可携带 nonce
            try
            {
                ClientSessionNonce.Initialize();
            }
            catch (System.Exception e)
            {
                Logger.LogWarning("[Tidy] ClientSessionNonce.Initialize 异常: " + e.Message);
            }

            // LaunchMultiplayerNetPlugin.Awake 已自动 ModTransport.Initialize()，
            // 此处仅注册本插件的 V2 双端通道处理器（服务器端 + 客户端）。
            ManualTidyNetwork.RegisterHandlers();

            SpawnManualTidyWatcher();

            // P0-LIT-02：持久熔断磁盘持久化改为会话作用域隔离，不再在 Awake 中固定 Initialize + Load。
            // InitializeForScope 必须在世界、模式、slot 已稳定的会话边界执行：
            //   - 单人模式：Update 中 TryInitializeSingleplayerScope 自动检测
            //     Provider.serverID.StartsWith("Singleplayer_") + Provider.map 稳定 + Characters.selected 稳定后调用
            //   - P2P 模式：由 SteamP2PFriends 在 Stage 6A 已确认的上下文中显式调用 BeginScope("p2p", ...)
            // 切换 scope 前必须先清空运行时持久熔断状态（ReplacePersistentFromSnapshot 空列表），然后才 Load()。
            // LIT 不通过 SteamP2PFriends 类型、反射或程序集引用自行猜测"当前是不是 P2P"。
            // 旧全局 persistent_faults.json 已废弃，新逻辑不得读取它。

            // v2.0.3 P0-C4：订阅 Provider.onServerHosted，在服务器启动后注册管理员命令
            try
            {
                Provider.onServerHosted += OnServerHosted;
                _serverHostedHooked = true;
                Logger.LogInfo("[Tidy] 已订阅 Provider.onServerHosted（管理员命令注册）");
            }
            catch (System.Exception e)
            {
                Logger.LogWarning("[Tidy] 订阅 Provider.onServerHosted 失败: " + e.Message);
            }

            // v2.0.1 P1-6：订阅远端玩家断开事件，清理该玩家的熔断/限流/账本状态。
            // vanilla Provider.onEnemyDisconnected(SteamPlayer) 在服务器端触发（远端玩家离开）。
            // 本地客户端断开（Provider.onClientDisconnected）由 Shutdown() 统一清理，无需单独订阅。
            try
            {
                Provider.onEnemyDisconnected += OnEnemyDisconnected;
                _disconnectHooked = true;
                Logger.LogInfo("[Tidy] 已订阅 Provider.onEnemyDisconnected（玩家状态清理）");
            }
            catch (System.Exception e)
            {
                Logger.LogWarning("[Tidy] 订阅 Provider.onEnemyDisconnected 失败: " + e.Message);
            }

            // v2.0.6.8：Codex v2.0.6.7 审计 §三 Medium 3 模板 C 修复：
            // 订阅 Provider.onEnemyConnected，在玩家连接时建立服务端会话并发送 session challenge。
            // 服务端生成 64-bit token，通过 MSG_SESSION_CHALLENGE 发送给客户端。
            // 客户端收到后替换临时 nonce 为服务端签发 token。
            // 断线时 ClearPlayer 删除会话，旧 token 立即失效，重放被拒绝。
            try
            {
                Provider.onEnemyConnected += OnEnemyConnected;
                _connectedHooked = true;
                Logger.LogInfo("[Tidy] 已订阅 Provider.onEnemyConnected（会话 challenge 发送）");
            }
            catch (System.Exception e)
            {
                Logger.LogWarning("[Tidy] 订阅 Provider.onEnemyConnected 失败: " + e.Message);
            }

            Logger.LogInfo("===============================================");
            Logger.LogInfo($" LaunchInventoryTidy v3.0.0 已加载（主版本升级 对齐 LMN v4.0.0 消除文件后缀 安全箱事务+多环境自适应）");
            Logger.LogInfo($" Unity 主线程 ID = {MainThreadId}（整理事务入口将断言当前线程匹配）");
            Logger.LogInfo($" 前置库版本检查：LMN {LmnDependencyGuard.LoadedVersion} >= {LmnDependencyGuard.MIN_REQUIRED_VERSION} ✅");
            Logger.LogInfo(" v3.0.0：主版本升级 - LMN 最低版本提升至 4.0.0.0（breaking change 对齐）");
            Logger.LogInfo("         + 消除文件名后缀（bin/Release/ 唯一产物 LaunchInventoryTidy.dll 裸名）");
            Logger.LogInfo("         + 安全箱事务与多环境自适应（沿用 v2.0.6.14 ALL_PAGES 跳过原生不活动页基线）");
            Logger.LogInfo(" v2.0.0：SameType 默认 + 快捷键迁移 + 物品守恒 + V2 协议");
            Logger.LogInfo(" v2.0.1：真事务快照回滚 + 玩家级熔断 + 限流 + 防重放 + 卸载清理");
            Logger.LogInfo(" v2.0.2：值快照 + 持久熔断写盘 + 账本完整缓存 + 日志节流 + 生命周期清理");
            Logger.LogInfo(" v2.0.3：TidyOperationOutcome + 选择性回滚 + CriticalFailure 快捷键恢复");
            Logger.LogInfo("         + Prepare fail-closed + 持久熔断管理员命令 + 协议日志统一节流");
            Logger.LogInfo(" v2.0.4：/tidy_unfault 显式授权 + Commander.deregister + 持久熔断原子写 + ");
            Logger.LogInfo("         + HotkeyRestoreOutcome 纳入 FullRestorationVerified + 跨项目接口回退");
            Logger.LogInfo("         + SecurityLogLimiter 扩展到所有外层协议拒绝路径 + 异常日志低频采样");
            Logger.LogInfo(" v2.0.5：持久化三状态机（UNINITIALIZED/HEALTHY/DEGRADED）+ 协议单一事实源");
            Logger.LogInfo("         + SetPending 前置 + 本地玩家解析分支 + /tidy_fault_recover 恢复命令");
            Logger.LogInfo("         + LogException 真正节流 + ACK 恢复失败结构化结果");
            Logger.LogInfo(" v2.0.6：marker 先于 main 落盘 + FORMAT_VERSION=2 v1迁移 + ReplacePersistentFromSnapshot");
            Logger.LogInfo("         + ClientHotkeyResultPending 关联状态 + ACK 逐项 try-catch + verifiedCount");
            Logger.LogInfo("         + HotkeyResult 业务不变量 + reserved 字节验证 + 聊天响应节流 + LMN 可达性检查");
            Logger.LogInfo(" v2.0.6.1：LMN 3.3.2.0 集成 + Shutdown 真正注销 handler（UnregisterServerHandler/Client）");
            Logger.LogInfo("         + 移除 LMN 可达性警告（v3.3.2.0 已实现 IsLocalClient + LoopbackToClient）");
            Logger.LogInfo("         + SP / listen host 模式响应可正常回送，不再阻塞快捷键恢复");
            Logger.LogInfo(" v2.0.6.2：U3DS 兼容性修复 - 移除 Dedicator.IsDedicatedServer 属性依赖");
            Logger.LogInfo("         + 客户端 Assembly-CSharp.dll 中 Dedicator.IsDedicatedServer 是属性（有 get_IsDedicatedServer）");
            Logger.LogInfo("         + U3DS Assembly-CSharp.dll 中 Dedicator.IsDedicatedServer 是字段（无 getter 方法）");
            Logger.LogInfo("         + 运行时 MissingMethodException 触发整理请求持续熔断");
            Logger.LogInfo("         + 修复：LMN ModTransport.IsLocalClient 改用 Provider.isServer && Provider.isClient");
            Logger.LogInfo("         + 修复：TidyAdminAuth + CommandTidyFaults 三处日志改用 Provider.isClient");
            Logger.LogInfo("         + 逻辑等价（DS 下 Provider.isClient=false 即原 !Dedicator.IsDedicatedServer）");
            Logger.LogInfo(" v2.0.6.3：前置库版本强约束 + 整理事务并发安全加固");
            Logger.LogInfo("         + Dependency Guard：LMN AssemblyFileVersion >= 3.3.1.0，否则拒绝完成初始化");
            Logger.LogInfo("         + 主线程断言：TidyPage / TidyAllPlayerPages 入口校验当前线程 == Unity 主线程");
            Logger.LogInfo("         + 玩家级冷却闸门：最小请求间隔 1.0s -> 1.5s（防高频恶意发包重入）");
            Logger.LogInfo("         + Commit 前二次库存校验：Prepare 与 Commit 之间物品总数/ItemJar 哈希变更则立即 Abort");
            Logger.LogInfo(" v2.0.6.4：Codex v2.0.6.3 审计阻断项修复");
            Logger.LogInfo("         + CommitPage 改用 CommitPageResult 枚举（不再抛 InvalidOperationException）");
            Logger.LogInfo("         + NotStartedInventoryChanged：当前页零回滚，仅回滚已 Committed 的前序页（原子性）");
            Logger.LogInfo("         + MutationStarted：当前页破损，回滚当前页 + 前序已 Committed 页");
            Logger.LogInfo("         + PlayerOperationGate：per-player lease，Prepare->Commit->Verify->响应 全程独占");
            Logger.LogInfo("         + 主线程断言失败返回 Rejected（不再触发持久熔断）");
            Logger.LogInfo("         + requestId 加密随机初始化（RandomNumberGenerator，防重启复用旧 ID 重放）");
            Logger.LogInfo(" v2.0.6.5：Codex v2.0.6.4 审计 5 项阻断项修复");
            Logger.LogInfo("         + Critical 1：post-commit 快照写前比较，回滚前检测并发修改，ConcurrentMutationAfterCommit 安全隔离");
            Logger.LogInfo("         + Critical 2：CommitPage 显式状态机（NotStarted / MutationMayHaveStarted / Committed）");
            Logger.LogInfo("         + Medium 3：主线程调度队列 + lease owner/requestId 生命周期 + 修正 ACK 后释放错误声明");
            Logger.LogInfo("         + Medium 4：V3 协议引入 64-bit session nonce，账本键升级为 (SteamID, nonce, requestId) 复合键");
            Logger.LogInfo("         + Medium 5：ACK 语义降级为 HotkeyFlowAck（快捷键流程 ACK），不再声称全量库存已应用");
            Logger.LogInfo(" v2.0.6.6：Codex v2.0.6.5 审计 5 项阻断项修复");
            Logger.LogInfo("         + Critical 1：mutation journal 替代状态推断，逐步 remove/add 预期中间态验证，未知状态禁止回滚");
            Logger.LogInfo("         + Critical 2：lease BusyDuplicate vs BusyDifferent 区分，同 (nonce, requestId) 重复包静默丢弃");
            Logger.LogInfo("         + Medium 3：ServerSessionRegistry 服务端会话 nonce 绑定，断开立即失效 + requestId 单调性防重放");
            Logger.LogInfo("         + Medium 4：删除 TidyHotkeyResult 全量库存同步证据错误表述，需外部双端全页多重集合证明");
            Logger.LogInfo("         + Medium 5：P2P 不放行，仅申请单机 + U3DS 动态测试");
            Logger.LogInfo(" v2.0.6.7：Codex v2.0.6.6 审计 4 项阻断项修复");
            Logger.LogInfo("         + Critical 1：mutation journal 改为每个写调用前记录 before/after 全量状态");
            Logger.LogInfo("         +   修复 v2.0.6.6 整个 while(removeItem) 循环完成后才记一条的问题");
            Logger.LogInfo("         + Critical 2：重排 ledger/lease/限流 顺序，ledger 提前到网络回调阶段");
            Logger.LogInfo("         +   cached 重发缓存，in-flight 静默，new 才消耗限流+lease");
            Logger.LogInfo("         +   修复 lease 释放后重复包被限流拒绝回送 Rejected 清掉 pending 的问题");
            Logger.LogInfo("         + Medium 3：nonce mismatch 改为拒绝（不自动轮换），需断线重连才能注册新 nonce");
            Logger.LogInfo("         + Medium 4：ProcessAll 主线程断言 + 队列容量上限 200 + 每帧任务上限 10");
            Logger.LogInfo("         +   ConcurrentMutationAfterCommit 打开持久熔断 + /tidy_unfault 显式恢复路径");
            Logger.LogInfo(" v2.0.6.8：Codex v2.0.6.7 审计 5 项阻断项修复（模板 A/B/C/D）");
            Logger.LogInfo("         + Critical 1（模板 A）：MainThreadDispatcher.TryEnqueue 返回 bool，队列满时执行补偿事务");
            Logger.LogInfo("         +   + Shutdown() 优雅关闭：_shuttingDown=true 阻止新任务入队");
            Logger.LogInfo("         + Critical 2（模板 B）：RequestAdmissionStore.TryAdmit 单锁原子准入");
            Logger.LogInfo("         +   + BusyDifferent 不再创建 ledger 条目（防攻击者填满 ledger）");
            Logger.LogInfo("         +   + Received 条目不被驱逐（防 in-flight 驱逐攻击）");
            Logger.LogInfo("         + Medium 3（模板 C）：服务端签发 64-bit token + MSG_SESSION_CHALLENGE");
            Logger.LogInfo("         +   + 客户端先收 challenge 才能发请求（不再自动注册）");
            Logger.LogInfo("         +   + 断线 ClearPlayer 删除会话，旧 token 立即失效");
            Logger.LogInfo("         + Medium 4：ConcurrentMutationAfterCommit 措辞修正 - 只阻塞本插件写入");
            Logger.LogInfo("         +   + 不再声称 \"玩家进入 fail-closed 安全隔离\"，原版背包/物品使用/存档不受冻结");
            Logger.LogInfo("         + Medium 5（模板 D）：post-call state verification - 每次 removeItem/addItem 后验证");
            Logger.LogInfo("         +   + ExpectedStateAfter 在调用前/后均比对，vanilla API 静默失败立即抛异常");
            Logger.LogInfo(" v2.0.6.9：Codex v2.0.6.8 审计 4 项阻断项修复（模板 1+2+§6）");
            Logger.LogInfo("         + Critical 1（模板 1）：准入顺序修复 - token-only -> ledger 幂等 -> HasLease -> TryReserveNextRequestId");
            Logger.LogInfo("         +   + 修复 v2.0.6.8 ValidateRequest 在 TryLookup 之前检查 requestId 单调性的阻断项");
            Logger.LogInfo("         +   + 拆分 ServerSessionRegistry 为 ValidateTokenOnly + TryReserveNextRequestId");
            Logger.LogInfo("         + Medium 2（模板 2）：TryCreateReceivedNonEvicting - 容量检查+驱逐+插入合并为单一 API");
            Logger.LogInfo("         +   + 修复 v2.0.6.8 HasCapacityForNew + CreateReceivedEntry 分离导致的 MAX 突破");
            Logger.LogInfo("         +   + 强制不变量：Received 不被驱逐，list.Count 不超过 MAX_ENTRIES_PER_PLAYER");
            Logger.LogInfo("         + Medium 3：ClientSessionNonce.IsReady + TrySendTidyRequest 客户端入口闸门");
            Logger.LogInfo("         +   + 修复 v2.0.6.8 注释称\"收到 challenge 前禁用整理\"但 SendTidyV2Request 未检查的阻断项");
            Logger.LogInfo("         +   + 未收到 challenge 时返回 false，不建 pending，不发包");
            Logger.LogInfo("         + Medium 4：RNG fail-closed - 不再降级为时间戳");
            Logger.LogInfo("         +   + ServerSessionRegistry.GenerateTokenOrFail：RNG 失败抛 CryptographicException");
            Logger.LogInfo("         +   + ClientSessionNonce.Initialize：RNG 失败标记 _initializationFailed=true");
            Logger.LogInfo("         +   + 64-bit token 作为明确的临时测试限制，禁止安全声明");
            Logger.LogInfo("         +   + 升级到 V4 128-bit 应作为协议大版本升级单独审计门处理");
            Logger.LogInfo(" v2.0.6.11：Codex v2.0.6.10 审计 P0+P1 阻断项修复");
            Logger.LogInfo("         + P0-1 Critical：Release 移除真实背包故障注入命令与抛异常 hook");
            Logger.LogInfo("         +   + 所有故障注入代码用 #if TIDY_TEST_HARNESS 包裹");
            Logger.LogInfo("         +   + 新增 TestHarness 构建配置（DefineConstants TIDY_TEST_HARNESS）");
            Logger.LogInfo("         +   + Release/DLL 不含能对真实背包抛故障的聊天命令或 hook");
            Logger.LogInfo("         + P0-2 Critical：修正多页注入选择器");
            Logger.LogInfo("         +   + 引入 FaultPlan（TargetPage + TargetCommitOrdinal + Kind + Step）");
            Logger.LogInfo("         +   + 旧版 NotifyCommitPageStart 每页重置计数器导致第一页就触发");
            Logger.LogInfo("         +   + 新版只有目标页+目标序号匹配才递增并触发");
            Logger.LogInfo("         +   + 多页测试前断言第一页已 commit，断言第二页才触发故障");
            Logger.LogInfo("         + P1-1 Medium：测试结论机改为联合断言");
            Logger.LogInfo("         +   + 维护不可逆 Triggered/FailureReason 标志，不可被覆盖为 PASS");
            Logger.LogInfo("         +   + 只有 Triggered && CriticalFailure && RollbackAttempted && RollbackVerified && 指纹匹配 才 PASS");
            Logger.LogInfo("         +   + 物品不足标记 SKIPPED_INSUFFICIENT_FIXTURE，不是 PASS");
            Logger.LogInfo("         +   + 首例 FAIL 立即停止后续用例");
            Logger.LogInfo("         + P1-2 Medium：实现两阶段关闭 BeginQuiesce -> drain -> CompleteShutdown");
            Logger.LogInfo("         +   + 旧顺序先清 ledger 再 drain，CancelNew 找不到条目，补偿失效");
            Logger.LogInfo("         +   + 新顺序 BeginQuiesce 仅设 _shuttingDown，保留 ledger/gate/pending");
            Logger.LogInfo("         +   + MainThreadDispatcher.Shutdown drain 期间 CancelNew 可正常工作");
            Logger.LogInfo("         +   + CompleteShutdown 在 drain 完成后清空静态状态");
            Logger.LogInfo("         + P0-1 保留：PagePreparation sealed class（v2.0.6.10 已修复）");
            Logger.LogInfo("         + P1-3 保留：ClientSessionNonce 锁保护原子读 API（v2.0.6.10 已修复）");
            Logger.LogInfo("         + P1-4 保留：QueuedTidyRequest 可取消任务对象（v2.0.6.10 已修复）");
            Logger.LogInfo(" v2.0.6.12：Codex v2.0.6.11 单机冒烟复盘 P0+P1 修复");
            Logger.LogInfo("         + P0 Critical：StateMatches 内部双侧规范化排序（消除 currentSorted 隐式契约）");
            Logger.LogInfo("         +   + 旧版只排序 expected，信任调用方预排序 currentSorted");
            Logger.LogInfo("         +   + vanilla Items.removeItem(0) 交换末尾元素，容器顺序非集合顺序");
            Logger.LogInfo("         +   + CommitPage 两个调用点未排序，页面物品 >=3 时假阳性 CriticalFailure");
            Logger.LogInfo("         +   + 新版对 current 与 expected 都做副本规范化排序，不修改原列表");
            Logger.LogInfo("         +   + 比较字段 (x,y,rot,fp) 不放宽，任一差异仍返回 false");
            Logger.LogInfo("         + P1 Medium：TestHarness SnapshotAndDisarm 观测快照修复");
            Logger.LogInfo("         +   + 旧版 finally { Disarm(); } 后读 _plan.Triggered/_commitOrdinal 永远 false/0");
            Logger.LogInfo("         +   + 新增 FaultObservation readonly struct + SnapshotAndDisarm() 方法");
            Logger.LogInfo("         +   + 先复制观测值到值类型，再清理静态状态，finally 内调用");
#if TIDY_TEST_HARNESS
            Logger.LogInfo(" 管理员命令：/tidy_faults, /tidy_unfault, /tidy_fault_recover, /tidy_fault_injection_test, /tidy_auto_test (TestHarness)");
#else
            Logger.LogInfo(" 管理员命令：/tidy_faults 查询, /tidy_unfault <SteamID> 解除, /tidy_fault_recover 恢复降级");
            Logger.LogInfo(" 注意：故障注入测试命令与一键自动化测试命令仅在 TestHarness 构建中可用（Release 不注册）");
#endif
            Logger.LogInfo(" 注意：被动整理 Patch 仍禁用，请用 [整理] 按钮或 Plugin 0 按键手动整理");
            Logger.LogInfo("===============================================");
        }

        private void SpawnManualTidyWatcher()
        {
            _watcherObject = new GameObject("LaunchInventoryTidy_ManualTidyWatcher");
            Object.DontDestroyOnLoad(_watcherObject);
            _watcherObject.AddComponent<ManualTidyWatcher>();
        }

        private static void OnEnemyDisconnected(SteamPlayer player)
        {
            try
            {
                if (player == null) return;
                CSteamID steamId = player.playerID.steamID;

                // v2.0.3 P2-L13：ClearPlayer 返回真实处理结果，按结果记录日志
                var faultResult = TidyFaultCircuit.ClearPlayer(steamId);
                TidyRateLimiter.ClearPlayer(steamId);
                RequestLedger.ClearPlayer(steamId);
                TidyTransactionManager.ClearPlayer(steamId);
                SecurityLogLimiter.ClearPlayer(steamId);
                PlayerOperationGate.ClearPlayer(steamId);  // v2.0.6.4：清理玩家操作 lease
                ServerSessionRegistry.ClearPlayer(steamId);  // v2.0.6.6：清理会话 nonce 注册

                string faultMsg = faultResult switch
                {
                    TidyFaultCircuit.ClearPlayerResult.Removed => "已清理临时熔断",
                    TidyFaultCircuit.ClearPlayerResult.Preserved => "持久熔断保留（需管理员解除）",
                    _ => "无熔断记录",
                };
                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[Tidy] 玩家 {(ulong)steamId} 断开：{faultMsg}，已清理限流/账本/事务状态");
            }
            catch (System.Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogWarning(
                    "[Tidy] OnEnemyDisconnected 异常: " + e.Message);
            }
        }

        // v2.0.6.8：Codex v2.0.6.7 审计 §三 Medium 3 模板 C 修复：
        // 玩家连接时由服务端生成 64-bit token 并发送 MSG_SESSION_CHALLENGE。
        // 客户端收到后替换临时 nonce 为服务端签发 token。
        // 断线时 ClearPlayer 删除会话，旧 token 立即失效，重放被拒绝（不再自动注册）。
        //
        // v2.0.6.9：Codex v2.0.6.8 审计 §三 Medium 4 修复：
        //   - RNG 失败时 BeginSession 抛 CryptographicException，不再降级为时间戳
        //   - 此处捕获异常并记录错误日志，整理功能对该玩家禁用（fail-closed）
        private static void OnEnemyConnected(SteamPlayer player)
        {
            try
            {
                if (player == null) return;
                CSteamID steamId = player.playerID.steamID;
                if (steamId == CSteamID.Nil) return;
                try
                {
                    ServerSessionRegistry.BeginSession(steamId);
                }
                catch (System.Security.Cryptography.CryptographicException ce)
                {
                    Log?.LogError(
                        $"[Tidy] OnEnemyConnected BeginSession RNG 失败（fail-closed），整理功能对该玩家禁用 steamId={(ulong)steamId}: {ce.Message}");
                }
            }
            catch (System.Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogWarning(
                    "[Tidy] OnEnemyConnected 异常: " + e.Message);
            }
        }

        private void OnServerHosted()
        {
            // P0-LIT-01：下次 host 开始时复位两个字段，并清除上一会话遗留的本地 SteamID nonce。
            // 不得遗留 pending 或旧 nonce 到下一会话。
            try
            {
                if (_localSessionPending || _localSessionStarted)
                {
                    try
                    {
                        CSteamID prevServerId = Provider.server;
                        if (prevServerId != CSteamID.Nil)
                        {
                            ServerSessionRegistry.ClearPlayer(prevServerId);
                        }
                    }
                    catch { }
                    _localSessionPending = false;
                    _localSessionStarted = false;
                }
            }
            catch { }

            // v2.0.4 P0-2：移除破坏性 Commander.init() 反射调用
            // Codex v2.0.3 第四次审计 §2 P0 指出：Commander.init() 第一步是 commands = new List<Command>()，
            // 会清空其他插件已注册的命令，不是幂等安全。
            // 正确做法：直接 Commander.register，Commander 由 vanilla 在更早时机初始化。
            try
            {
                if (_cmdFaults == null)
                {
                    _cmdFaults = new CommandTidyFaults(new Local());
                    Commander.register(_cmdFaults);
                    Logger.LogInfo("[Tidy] 已注册 /tidy_faults 命令");
                }
                if (_cmdUnfault == null)
                {
                    _cmdUnfault = new CommandTidyUnfault(new Local());
                    Commander.register(_cmdUnfault);
                    Logger.LogInfo("[Tidy] 已注册 /tidy_unfault 命令");
                }
                // v2.0.5 P0-5：新增 /tidy_fault_recover 命令解除全局持久化降级
                if (_cmdRecover == null)
                {
                    _cmdRecover = new CommandTidyFaultRecover(new Local());
                    Commander.register(_cmdRecover);
                    Logger.LogInfo("[Tidy] 已注册 /tidy_fault_recover 命令");
                }
                // v2.0.6.10：Codex v2.0.6.9 审计 §三 P0-2 故障注入测试命令
                // v2.0.6.11：Codex v2.0.6.10 审计 §三 P0-1 修复：#if 包裹，Release 不注册
#if TIDY_TEST_HARNESS
                if (_cmdFaultInjectionTest == null)
                {
                    _cmdFaultInjectionTest = new CommandTidyFaultInjectionTest(new Local());
                    Commander.register(_cmdFaultInjectionTest);
                    Logger.LogInfo("[Tidy] 已注册 /tidy_fault_injection_test 命令（TestHarness 构建）");
                }
                // v2.0.6.13：Codex v2.0.6.12 §4 单机深度测试一键自动化
                if (_cmdAutoTest == null)
                {
                    _cmdAutoTest = new CommandTidyAutoTest(new Local());
                    Commander.register(_cmdAutoTest);
                    Logger.LogInfo("[Tidy] 已注册 /tidy_auto_test 命令（TestHarness 构建，一键 SP-CONS/SP-HK/SP-FI/SP-SD）");
                }
#endif
            }
            catch (System.Exception e)
            {
                Logger.LogWarning("[Tidy] 注册管理员命令失败: " + e.Message);
            }

            // P0-LIT-01：禁止在 Provider.onServerHosted 回调内急切读取 Provider.server 并 BeginSession。
            // 回调触发时 Provider.server / Provider.client 可能尚未稳定对齐，急切 BeginSession 会读到错误身份。
            // 仅置 pending，等待 Update 主线程检测 Provider.isServer && Provider.isClient &&
            // Provider.server == Provider.client 后再建立本地主机会话。
            _localSessionPending = Provider.isServer;
            _localSessionStarted = false;
        }

        private void OnDestroy()
        {
            try
            {
                if (_disconnectHooked)
                {
                    try { Provider.onEnemyDisconnected -= OnEnemyDisconnected; } catch { }
                    _disconnectHooked = false;
                }
                if (_connectedHooked)
                {
                    try { Provider.onEnemyConnected -= OnEnemyConnected; } catch { }
                    _connectedHooked = false;
                }
                if (_serverHostedHooked)
                {
                    try { Provider.onServerHosted -= OnServerHosted; } catch { }
                    _serverHostedHooked = false;
                }
            }
            catch { }
            // v2.0.4 P1：注销管理员命令（CMD-LIFE-1 生命周期测试要求）
            try
            {
                if (_cmdFaults != null)
                {
                    try { Commander.deregister(_cmdFaults); } catch (System.Exception e)
                    {
                        Logger?.LogWarning("[Tidy] deregister /tidy_faults 异常: " + e.Message);
                    }
                    _cmdFaults = null;
                }
                if (_cmdUnfault != null)
                {
                    try { Commander.deregister(_cmdUnfault); } catch (System.Exception e)
                    {
                        Logger?.LogWarning("[Tidy] deregister /tidy_unfault 异常: " + e.Message);
                    }
                    _cmdUnfault = null;
                }
                // v2.0.5 P0-5：注销 /tidy_fault_recover
                if (_cmdRecover != null)
                {
                    try { Commander.deregister(_cmdRecover); } catch (System.Exception e)
                    {
                        Logger?.LogWarning("[Tidy] deregister /tidy_fault_recover 异常: " + e.Message);
                    }
                    _cmdRecover = null;
                }
                // v2.0.6.10：注销 /tidy_fault_injection_test
                // v2.0.6.11：Codex v2.0.6.10 审计 §三 P0-1 修复：#if 包裹
#if TIDY_TEST_HARNESS
                if (_cmdFaultInjectionTest != null)
                {
                    try { Commander.deregister(_cmdFaultInjectionTest); } catch (System.Exception e)
                    {
                        Logger?.LogWarning("[Tidy] deregister /tidy_fault_injection_test 异常: " + e.Message);
                    }
                    _cmdFaultInjectionTest = null;
                }
                // v2.0.6.13：注销 /tidy_auto_test
                if (_cmdAutoTest != null)
                {
                    try { Commander.deregister(_cmdAutoTest); } catch (System.Exception e)
                    {
                        Logger?.LogWarning("[Tidy] deregister /tidy_auto_test 异常: " + e.Message);
                    }
                    _cmdAutoTest = null;
                }
#endif
            }
            catch { }

            // v2.0.6.11：Codex v2.0.6.10 审计 §三 P1-2 修复：
            //   - 卸载顺序改为 BeginQuiesce -> MainThreadDispatcher.Shutdown -> CompleteShutdown
            //   - BeginQuiesce 仅设置 _shuttingDown=true，handler 拒绝新请求但不清 ledger/gate/pending
            //   - MainThreadDispatcher.Shutdown drain 期间 CancelNew 仍能找到条目执行补偿事务
            //   - CompleteShutdown 在 drain 完成后清空静态状态
            //   - 禁止在 CancelNew 前清空账本（旧顺序会导致 CancelNew 找不到条目，补偿失效）
            try
            {
                ManualTidyNetwork.BeginQuiesce();
            }
            catch (System.Exception e)
            {
                Logger?.LogWarning("[Tidy] ManualTidyNetwork.BeginQuiesce 异常: " + e.Message);
            }

            // v2.0.6.8：调用 MainThreadDispatcher.Shutdown 优雅关闭队列
            // v2.0.6.11：必须在 BeginQuiesce 之后、CompleteShutdown 之前调用
            // - 设置 _shuttingDown=true 阻止新的 TryEnqueue 入队
            // - drain 已排队任务，对每个调用 Cancel 回调
            // - CancelNew 可正常更新 ledger 并发送 Rejected（ledger 尚未清空）
            try
            {
                MainThreadDispatcher.Shutdown();
            }
            catch (System.Exception e)
            {
                Logger?.LogWarning("[Tidy] MainThreadDispatcher.Shutdown 异常: " + e.Message);
            }

            try
            {
                ManualTidyNetwork.CompleteShutdown();
            }
            catch (System.Exception e)
            {
                Logger?.LogWarning("[Tidy] ManualTidyNetwork.CompleteShutdown 异常: " + e.Message);
            }

            // v2.0.6.6：清空服务端会话 nonce 注册表
            try
            {
                ServerSessionRegistry.ClearAll();
            }
            catch (System.Exception e)
            {
                Logger?.LogWarning("[Tidy] ServerSessionRegistry.ClearAll 异常: " + e.Message);
            }

            // P0-LIT-01：Shutdown 时复位本地会话状态，不得遗留 pending 或旧 nonce 到下一会话。
            _localSessionPending = false;
            _localSessionStarted = false;

            // P0-LIT-02：Shutdown 时复位 scope 跟踪字段，避免遗留到下一会话。
            // 磁盘上的 scope 文件持久化保留，下次 Awake 后由 TryInitializeSingleplayerScope 或
            // SteamP2PFriends.BeginScope 重新初始化。
            _currentScopeMode = null;
            _currentScopeMap = null;
            _currentScopeSlot = 0;

            try
            {
                if (_watcherObject != null)
                {
                    Object.Destroy(_watcherObject);
                    _watcherObject = null;
                }
            }
            catch { }

            HarmonyInstance?.UnpatchSelf();
            Instance = null;
            Log = null;
        }

        /// <summary>
        /// v2.0.6.5 新增（Codex v2.0.6.4 审计 §五阻断项 3）：
        /// Unity 主线程调度入口。每帧调用 MainThreadDispatcher.ProcessAll()，
        /// 执行网络回调入队的事务任务，保证 Prepare->Commit->Verify 全程在主线程串行执行。
        /// </summary>
        private void Update()
        {
            try
            {
                MainThreadDispatcher.ProcessAll();
            }
            catch (System.Exception e)
            {
                Logger?.LogWarning("[Tidy] MainThreadDispatcher.ProcessAll 异常: " + e.Message);
            }

            // P0-LIT-01：Listen Host 本地会话延迟建立。
            // 必须放在既有 MainThreadDispatcher.ProcessAll() 的 try/catch 后，保持主线程执行。
            TryBeginLocalListenHostSession();

            // P0-LIT-02：单人模式持久熔断 scope 自动初始化。
            // P2P scope 由 SteamP2PFriends 显式调用 BeginScope("p2p", ...)，不在此处理。
            TryInitializeSingleplayerScope();

#if TIDY_TEST_HARNESS
            // v2.0.6.13：Codex v2.0.6.12 §4 一键自动化测试触发器
            // 检查 <插件 DLL 目录>/.lit_autotest/autorun.flag 文件是否存在
            // 存在则删除并等待 Provider.isServer && Player.LocalPlayer != null 后调用 AutoTestDriver.RunAllSuites
            // 文件触发优于 BepInEx config：自清理、易脚本化、易验证
            // 路径锚定到插件 DLL 目录，避免依赖 Unturned 的 CWD（CWD 是游戏安装目录，不是项目目录）
            try
            {
                if (!_autoRunTriggered && _autoRunCheckCooldown <= 0f)
                {
                    _autoRunCheckCooldown = 0.5f; // 每 0.5 秒检查一次文件
                    string pluginDir = System.IO.Path.GetDirectoryName(
                        typeof(LaunchInventoryTidyPlugin).Assembly.Location);
                    string flagPath = System.IO.Path.Combine(pluginDir, ".lit_autotest", "autorun.flag");
                    if (System.IO.File.Exists(flagPath))
                    {
                        try { System.IO.File.Delete(flagPath); } catch { }
                        Logger?.LogInfo($"[AutoTest] 检测到 autorun.flag（{flagPath}），等待游戏加载完成后自动运行测试...");
                        _autoRunArmed = true;
                    }
                }
                else
                {
                    _autoRunCheckCooldown -= Time.deltaTime;
                }

                if (_autoRunArmed && !_autoRunTriggered)
                {
                    // 等待玩家进入地图（Provider.isServer && Player.LocalPlayer != null）
                    Player localPlayer = null;
                    try { localPlayer = Player.LocalPlayer; } catch { }
                    if (Provider.isServer && localPlayer != null && localPlayer.inventory != null)
                    {
                        _autoRunTriggered = true;
                        _autoRunArmed = false;
                        Logger?.LogInfo("[AutoTest] 玩家已加载，3 秒后自动运行测试套件...");
                        // 延迟 3 秒确保库存完全初始化
                        StartCoroutine(DelayedAutoRun(localPlayer));
                    }
                }
            }
            catch (System.Exception e)
            {
                Logger?.LogWarning("[Tidy] AutoRun 触发器异常: " + e.Message);
            }
#endif
        }

        // P0-LIT-01：Listen Host 本地会话延迟建立。
        // 在 Update 主线程检测 Provider.isServer && Provider.isClient && Provider.server == Provider.client 后再建立会话。
        // 语义不得放宽：身份未稳定时保持 pending；不自动重试 CryptographicException。
        private void TryBeginLocalListenHostSession()
        {
            if (!_localSessionPending || _localSessionStarted)
                return;

            if (!Provider.isServer || !Provider.isClient)
                return;

            CSteamID serverId = Provider.server;
            CSteamID clientId = Provider.client;
            if (serverId == CSteamID.Nil ||
                clientId == CSteamID.Nil ||
                serverId != clientId)
                return; // 身份尚未稳定，保持 pending。

            if (Player.LocalPlayer == null)
                return;

            try
            {
                ServerSessionRegistry.BeginSession(serverId);
                _localSessionStarted = true;
                _localSessionPending = false;
                Logger.LogInfo(
                    $"[Tidy] Listen Host local session ready: steamId={(ulong)serverId}");
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                _localSessionPending = false;
                Logger.LogError(
                    "[Tidy] local session nonce generation failed; tidy disabled: " + ex.Message);
            }
            catch (System.Exception ex)
            {
                _localSessionPending = false;
                Logger.LogError(
                    "[Tidy] local session setup failed; tidy disabled: " + ex);
            }
        }

        // P0-LIT-02 R2：单人模式持久熔断 scope 自动初始化。
        // 在 Update 主线程检测 Provider.serverID.StartsWith("Singleplayer_") + Provider.map 稳定 +
        // Player.LocalPlayer 就绪后，调用 TrySwitchFaultScope（唯一权威转换门）。
        // 不再因 _currentScopeMode=="p2p" 永久阻止单机重载（修复 P0-LIT-02-A）。
        // LIT 不通过 SteamP2PFriends 类型、反射或程序集引用自行猜测"当前是不是 P2P"。
        private void TryInitializeSingleplayerScope()
        {
            string serverId;
            string mapName;
            int slot;
            Player localPlayer;

            try
            {
                serverId = Provider.serverID;
                mapName = Provider.map;
                slot = Characters.selected;
                localPlayer = Player.LocalPlayer;
            }
            catch
            {
                return;
            }

            if (string.IsNullOrEmpty(serverId) ||
                !serverId.StartsWith("Singleplayer_", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(mapName) ||
                localPlayer == null)
                return;

            TrySwitchFaultScope(SingleplayerScopeMode, mapName, slot);
        }

        // P0-LIT-02 R2：公共 P2P scope 入口，供 SteamP2PFriends 在 Stage 6A 已确认的 P2P 上下文中显式调用。
        // 只接受 mode=="p2p"；非 P2P 模式被拒绝（单人由 TryInitializeSingleplayerScope 自动处理）。
        // 调用方必须在游戏主线程；ThreadUtil.assertIsGameThread 强制断言。
        // 返回 bool：true 表示 scope 已就绪，false 表示被拒绝或 fail-closed。
        public static bool BeginScope(string mode, string mapName, int saveSlot)
        {
            ThreadUtil.assertIsGameThread();

            if (!string.Equals(mode, P2PScopeMode, StringComparison.Ordinal))
            {
                Log?.LogError("[Tidy] BeginScope rejects non-P2P mode.");
                return false;
            }

            return TrySwitchFaultScope(P2PScopeMode, mapName, saveSlot);
        }

        // P0-LIT-02 R2：唯一权威 scope 转换门。
        // 顺序：ThreadUtil.assertIsGameThread -> ValidateScopeArguments（无副作用）->
        //   加 _scopeLock -> 检测 scope 是否变化 -> ReplacePersistentFromSnapshot(空) ->
        //   InitializeForScope -> Load -> 检测 GlobalFaultPersistenceDegraded -> 更新 scope 字段
        // 任何失败都 fail-closed：清空运行时 + 重置 scope 字段 + SetDegraded + 返回 false。
        // 修复 P0-LIT-02-B：先校验后清空，异常 fail-closed 而非 fail-open。
        private static bool TrySwitchFaultScope(string mode, string mapName, int saveSlot)
        {
            ThreadUtil.assertIsGameThread();

            try
            {
                TidyFaultCircuitPersistence.ValidateScopeArguments(mode, mapName, saveSlot);
            }
            catch (Exception ex)
            {
                TidyFaultCircuitPersistence.SetDegraded("Rejected invalid fault scope: " + ex.Message);
                Log?.LogError("[Tidy] fault scope validation failed: " + ex);
                return false;
            }

            lock (_scopeLock)
            {
                if (_currentScopeMode == mode &&
                    _currentScopeMap == mapName &&
                    _currentScopeSlot == saveSlot)
                    return !TidyFaultCircuitPersistence.GlobalFaultPersistenceDegraded;

                try
                {
                    // Validation above is non-mutating. Only now may the old runtime scope be removed.
                    TidyFaultCircuit.ReplacePersistentFromSnapshot(
                        new List<TidyFaultCircuitPersistence.PersistentRecord>());
                    TidyFaultCircuitPersistence.InitializeForScope(mode, mapName, saveSlot);
                    int loaded = TidyFaultCircuitPersistence.Load();

                    if (TidyFaultCircuitPersistence.GlobalFaultPersistenceDegraded)
                        throw new InvalidOperationException("Scoped persistence load entered degraded state.");

                    _currentScopeMode = mode;
                    _currentScopeMap = mapName;
                    _currentScopeSlot = saveSlot;
                    Log?.LogInfo("[Tidy] fault scope ready: mode=" + mode +
                        " map=" + mapName + " slot=" + saveSlot + " loaded=" + loaded);
                    return true;
                }
                catch (Exception ex)
                {
                    // The old scope must never remain usable after a failed attempted transition.
                    TidyFaultCircuit.ReplacePersistentFromSnapshot(
                        new List<TidyFaultCircuitPersistence.PersistentRecord>());
                    _currentScopeMode = null;
                    _currentScopeMap = null;
                    _currentScopeSlot = 0;
                    TidyFaultCircuitPersistence.SetDegraded("Fault scope transition failed: " + ex.Message);
                    Log?.LogError("[Tidy] fault scope transition failed closed: " + ex);
                    return false;
                }
            }
        }

#if TIDY_TEST_HARNESS
        private bool _autoRunArmed = false;
        private bool _autoRunTriggered = false;
        private float _autoRunCheckCooldown = 0f;

        private System.Collections.IEnumerator DelayedAutoRun(Player player)
        {
            yield return new WaitForSeconds(3f);
            Logger?.LogInfo("[AutoTest] 开始自动运行 AutoTestDriver.RunAllSuitesCoroutine...");
            int pass = 0, fail = 0, skip = 0, block = 0;
            yield return AutoTestDriver.RunAllSuitesCoroutine(player, suites =>
            {
                foreach (var s in suites)
                {
                    pass += s.Passed; fail += s.Failed; skip += s.Skipped; block += s.Blocked;
                }
            });
            Logger?.LogInfo($"[AutoTest] 自动测试完成：{pass} PASS / {fail} FAIL / {skip} SKIPPED / {block} BLOCKED");
        }
#endif
    }
}
