using System;
using System.Collections.Generic;
using System.IO;
using LaunchMultiplayerNet;
using SDG.Unturned;
using Steamworks;
using UnityEngine;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// 快捷键迁移映射：整理完成后，原 (oldX, oldY) 的物品已迁移到 (newX, newY)，
    /// 客户端据此做库存收敛检查，服务器据此调用 ServerBindItemHotkey。
    /// </summary>
    public struct NewPositionMapping
    {
        public byte HotkeyIndex;
        public byte NewPage;
        public byte NewX;
        public byte NewY;
        public ushort ExpectedItemId;

        public NewPositionMapping(byte hotkeyIndex, byte newPage, byte newX, byte newY, ushort expectedItemId)
        {
            HotkeyIndex = hotkeyIndex;
            NewPage = newPage;
            NewX = newX;
            NewY = newY;
            ExpectedItemId = expectedItemId;
        }
    }

    /// <summary>
    /// 背包整理 V2 网络协议（v2.0.2 整改）。
    ///
    /// v2.0.2 安全加固：
    ///   - 服务端限流（每玩家最小间隔 1s + 10s 窗口内最多 5 次 + 日志节流）
    ///   - 玩家级故障熔断（CriticalFailure 后拒绝直到显式恢复；持久熔断不得因断线清除）
    ///   - request ledger 防重放（重复 requestId 返回完整缓存响应含 mappings）
    ///   - 严格 hotkeyCount 上限校验（>8 直接拒绝，不截断）
    ///   - 客户端 pending requestId 表（仅接受本机发出的响应）
    ///   - Shutdown flag + convergence GameObject 跟踪（卸载时清理生命周期）
    /// </summary>
    public static class ManualTidyNetwork
    {
        public const byte ALL_PAGES = 0xFF;

        public const byte MSG_REQUEST_TIDY_V2 = 2;
        public const byte MSG_TIDY_COMMITTED = 3;
        /// <summary>
        /// v2.0.6.5 语义降级（Codex v2.0.6.4 审计 §五阻断项 5）：
        /// 原命名"InventoryAppliedAck"暗示"全量库存已应用"，但客户端仅检查快捷键目标物品的 id 匹配，
        /// 不能证明全量库存已同步。重命名为"HotkeyFlowAck"（快捷键流程 ACK）以准确描述语义。
        /// 字节常量保持不变（=4）以维持 V3 协议线格式稳定。
        ///
        /// v2.0.6.6 修订（Codex v2.0.6.5 审计 §三 Medium 4 修复）：
        ///   - 删除"TidyHotkeyResult 提供全量库存同步证据"的错误表述
        ///   - TidyHotkeyResult 仅含快捷键绑定统计（restoredCount/verifiedCount），不证明全量库存同步
        ///   - 全量库存同步证据必须由外部双端测试前后导出的全页
        ///     (x, y, rot, id, amount, quality, state) 多重集合对照证明
        ///   - 协议内不提供全量库存同步确认；若需要，应新增服务器权威库存 revision/hash 并对照客户端快照
        /// </summary>
        public const byte MSG_INVENTORY_APPLIED_ACK = 4;  // 语义：HotkeyFlowAck
        /// <summary>
        /// v2.0.5 P1-4：服务器 -> 客户端的快捷键恢复结果通知。
        /// v2.0.6.6 修订（Codex v2.0.6.5 审计 §三 Medium 4 修复）：
        ///   本消息仅含快捷键绑定统计（restoredCount/verifiedCount/clearedCount），
        ///   不证明全量库存同步。全量库存同步证据必须由外部双端测试
        ///   前后导出的全页 (x,y,rot,id,amount,quality,state) 多重集合对照证明。
        /// </summary>
        public const byte MSG_TIDY_HOTKEY_RESULT = 5;

        /// <summary>
        /// v2.0.6.8 新增（Codex v2.0.6.7 审计 §三 Medium 3 模板 C 修复）：
        /// 服务器 -> 客户端的会话 challenge。
        ///
        /// 玩家连接时（Provider.onEnemyConnected 或 OnServerHosted 本地分支），
        /// 服务端生成 64-bit 随机 token，通过本消息发送给客户端。
        /// 客户端收到后调用 ClientSessionNonce.ReplaceWithServerChallenge 替换临时 nonce。
        ///
        /// 布局：[MSG_SESSION_CHALLENGE:1][token:8]
        /// </summary>
        public const byte MSG_SESSION_CHALLENGE = 6;

        private const byte PROTOCOL_VERSION_V2 = 2;
        /// <summary>v2.0.6.5 新增：V3 协议版本，引入 64-bit session nonce 防跨会话重放。</summary>
        private const byte PROTOCOL_VERSION_V3 = 3;

        /// <summary>
        /// v2.0.5 P0-1：TidyCommitted 中每条 NewPositionMapping 的网络字节大小（单一事实源）。
        /// 布局：HotkeyIndex(1) + NewPage(1) + NewX(1) + NewY(1) + ExpectedItemId(2) + reserved(1) = 7 字节。
        /// Codex v2.0.4 第五次审计 §2 P0 指出：服务端写 7 字节、客户端按 8 字节校验，
        /// 只要存在快捷键映射响应必然被拒绝。本常量统一两端，禁止重复手写数字。
        /// </summary>
        private const int MAPPING_WIRE_SIZE = 7;

        /// <summary>v2.0.5 P0-1：单条快捷键映射写入（与 TryReadMapping 共用同一布局）。</summary>
        private static void WriteMapping(BinaryWriter w, NewPositionMapping m)
        {
            w.Write(m.HotkeyIndex);
            w.Write(m.NewPage);
            w.Write(m.NewX);
            w.Write(m.NewY);
            w.Write(m.ExpectedItemId);
            w.Write((byte)0);  // reserved
        }

        /// <summary>
        /// v2.0.6 P1-7 修订：单条快捷键映射读取，reserved 字节必须为 0。
        /// Codex v2.0.5 第六次审计 §2 Low 指出：原实现读取 reserved 后直接丢弃，
        /// 未来协议扩展或畸形包无法区分。修订为 reserved!=0 时返回 false 拒绝整条映射。
        /// </summary>
        private static bool TryReadMapping(BinaryReader r, out NewPositionMapping mapping)
        {
            byte hi = r.ReadByte();
            byte p = r.ReadByte();
            byte x = r.ReadByte();
            byte y = r.ReadByte();
            ushort id = r.ReadUInt16();
            byte reserved = r.ReadByte();
            if (reserved != 0)
            {
                mapping = default;
                return false;
            }
            mapping = new NewPositionMapping(hi, p, x, y, id);
            return true;
        }

        /// <summary>事务 TTL：10 秒后丢弃。</summary>
        private static readonly TimeSpan TransactionTtl = TimeSpan.FromSeconds(10);

        /// <summary>v2.0.2：Shutdown flag，handler 入口检查，卸载后拒绝处理新消息。</summary>
        private static volatile bool _shuttingDown;

        /// <summary>v2.0.2：跟踪所有活跃的 convergence GameObject，OnDestroy 时统一销毁。</summary>
        private static readonly List<GameObject> _activeConvergenceObjects = new List<GameObject>();
        private static readonly object _convLock = new object();

        /// <summary>由 Plugin.Awake 调用，注册服务器端 + 客户端通道处理器。</summary>
        public static void RegisterHandlers()
        {
            _shuttingDown = false;
            ModTransport.RegisterServerHandler(ModChannels.TidyPage, HandleServerMessage);
            ModTransport.RegisterClientHandler(ModChannels.TidyPage, HandleClientMessage);
            LaunchInventoryTidyPlugin.Log?.LogInfo(
                "[TidyNet] 已注册 channel=" + ModChannels.TidyPage + " V2 双端处理器");
        }

        /// <summary>
        /// v2.0.6.11 新增（Codex v2.0.6.10 审计 §三 P1-2 修复）：
        /// 卸载第一阶段：仅设置 _shuttingDown=true，handler 立即拒绝新请求。
        /// 不清空 ledger/gate/pending，保证 MainThreadDispatcher.Shutdown drain 期间
        /// CancelNew 仍能找到条目并执行补偿事务（释放 lease + MarkResult Failed + 回送 Rejected）。
        ///
        /// 调用顺序：BeginQuiesce -> MainThreadDispatcher.Shutdown -> CompleteShutdown
        /// </summary>
        public static void BeginQuiesce()
        {
            _shuttingDown = true;
            LaunchInventoryTidyPlugin.Log?.LogInfo(
                "[TidyNet] BeginQuiesce：_shuttingDown=true，handler 拒绝新请求，ledger/gate/pending 保留");
        }

        /// <summary>
        /// v2.0.6.11 新增（Codex v2.0.6.10 审计 §三 P1-2 修复）：
        /// 卸载第三阶段：注销 handler + 销毁 convergence GameObject + 清空所有静态状态。
        /// 必须在 MainThreadDispatcher.Shutdown 完成后调用（drain + Cancel 已执行完毕）。
        /// </summary>
        public static void CompleteShutdown()
        {
            // v2.0.6.1：LaunchMultiplayerNet 3.3.2.0 已交付 UnregisterServerHandler/UnregisterClientHandler。
            try
            {
                bool unregServer = ModTransport.UnregisterServerHandler(ModChannels.TidyPage, HandleServerMessage);
                bool unregClient = ModTransport.UnregisterClientHandler(ModChannels.TidyPage, HandleClientMessage);
                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[TidyNet] CompleteShutdown handler 注销：server={unregServer}, client={unregClient}");
            }
            catch (Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogWarning(
                    "[TidyNet] CompleteShutdown handler 注销异常: " + e.Message);
            }

            // 销毁所有活跃的 convergence GameObject
            lock (_convLock)
            {
                for (int i = 0; i < _activeConvergenceObjects.Count; i++)
                {
                    try
                    {
                        if (_activeConvergenceObjects[i] != null)
                            UnityEngine.Object.Destroy(_activeConvergenceObjects[i]);
                    }
                    catch { }
                }
                _activeConvergenceObjects.Clear();
            }

            // v2.0.6.11：此时 dispatcher 已 drain 完成，所有 queued request 的 Cancel 已执行
            // CancelNew 可正常更新 ledger 并发送 Rejected，现在安全清空静态状态
            TidyTransactionManager.ClearAllForTests();
            TidyFaultCircuit.ClearAllNonPersistent();   // 仅清临时熔断，保留持久熔断
            TidyRateLimiter.ClearAllForTests();
            RequestLedger.ClearAllForTests();
            ClientPendingState.ClearAll();
            ClientHotkeyResultPending.ClearAll();
            PlayerOperationGate.ClearAll();
            LaunchInventoryTidyPlugin.Log?.LogInfo(
                "[TidyNet] CompleteShutdown 已清理所有静态状态（持久熔断保留）");
        }

        /// <summary>
        /// v2.0.2：由 Plugin.OnDestroy 调用，清理静态状态 + 生命周期对象。
        /// v2.0.6.11 修订（Codex v2.0.6.10 审计 §三 P1-2 修复）：
        ///   - 生产路径应使用 BeginQuiesce -> MainThreadDispatcher.Shutdown -> CompleteShutdown
        ///   - 此方法保留为向后兼容，仅用于测试或无法分阶段关闭的场景
        ///   - 注意：直接调用此方法会导致 CancelNew 找不到条目（补偿事务失效）
        /// </summary>
        public static void Shutdown()
        {
            _shuttingDown = true;

            try
            {
                bool unregServer = ModTransport.UnregisterServerHandler(ModChannels.TidyPage, HandleServerMessage);
                bool unregClient = ModTransport.UnregisterClientHandler(ModChannels.TidyPage, HandleClientMessage);
                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[TidyNet] handler 注销：server={unregServer}, client={unregClient}");
            }
            catch (Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogWarning(
                    "[TidyNet] handler 注销异常: " + e.Message);
            }

            lock (_convLock)
            {
                for (int i = 0; i < _activeConvergenceObjects.Count; i++)
                {
                    try
                    {
                        if (_activeConvergenceObjects[i] != null)
                            UnityEngine.Object.Destroy(_activeConvergenceObjects[i]);
                    }
                    catch { }
                }
                _activeConvergenceObjects.Clear();
            }

            TidyTransactionManager.ClearAllForTests();
            TidyFaultCircuit.ClearAllNonPersistent();
            TidyRateLimiter.ClearAllForTests();
            RequestLedger.ClearAllForTests();
            ClientPendingState.ClearAll();
            ClientHotkeyResultPending.ClearAll();
            PlayerOperationGate.ClearAll();
            LaunchInventoryTidyPlugin.Log?.LogInfo("[TidyNet] 已清理所有静态状态（持久熔断保留）");
        }

        // ─────────────────────────────────────────────────────────────
        // 服务器端：消息分发
        // ─────────────────────────────────────────────────────────────

        private static void HandleServerMessage(CSteamID sender, BinaryReader reader)
        {
            if (_shuttingDown) return;  // v2.0.2：卸载后拒绝处理
            try
            {
                if (reader.BaseStream.Length < 1)
                {
                    SecurityLogLimiter.LogRejection(sender, "empty_packet",
                        $"服务器收到空包，忽略 sender={(ulong)sender}");
                    return;
                }

                long peekPos = reader.BaseStream.Position;
                byte msgType = reader.ReadByte();
                reader.BaseStream.Position = peekPos;

                switch (msgType)
                {
                    case MSG_REQUEST_TIDY_V2:
                        HandleRequestTidyV2(sender, reader);
                        break;
                    case MSG_INVENTORY_APPLIED_ACK:
                        HandleInventoryAppliedAck(sender, reader);
                        break;
                    case (byte)EModMessage.RequestTidyPage:
                        SecurityLogLimiter.LogRejection(sender, "v1_protocol",
                            $"收到 V1 协议（msgType=1），v2.0.0 已强制 V2，拒绝 sender={(ulong)sender}");
                        break;
                    default:
                        SecurityLogLimiter.LogRejection(sender, "unknown_msg_type",
                            $"服务器收到未知消息类型 {msgType}，忽略 sender={(ulong)sender}");
                        break;
                }
            }
            catch (Exception e)
            {
                SecurityLogLimiter.LogException("server_msg_crash",
                    $"HandleServerMessage crash sender={(ulong)sender}", e);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 客机端：消息分发
        // ─────────────────────────────────────────────────────────────

        private static void HandleClientMessage(BinaryReader reader)
        {
            if (_shuttingDown) return;  // v2.0.2：卸载后拒绝处理
            try
            {
                if (reader.BaseStream.Length < 1)
                {
                    SecurityLogLimiter.LogClientRejection("empty_packet",
                        "客机收到空包，忽略");
                    return;
                }

                long peekPos = reader.BaseStream.Position;
                byte msgType = reader.ReadByte();
                reader.BaseStream.Position = peekPos;

                switch (msgType)
                {
                    case MSG_TIDY_COMMITTED:
                        HandleTidyCommittedFromServer(reader);
                        break;
                    case MSG_TIDY_HOTKEY_RESULT:
                        HandleTidyHotkeyResultFromServer(reader);
                        break;
                    case MSG_SESSION_CHALLENGE:
                        HandleSessionChallengeFromServer(reader);
                        break;
                    default:
                        SecurityLogLimiter.LogClientRejection("unknown_msg_type",
                            $"客机收到未知消息类型 {msgType}，忽略");
                        break;
                }
            }
            catch (Exception e)
            {
                SecurityLogLimiter.LogException("client_msg_crash",
                    "HandleClientMessage crash", e);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // 客机端：发送 RequestTidyV2
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// v2.0.6.9 新增（Codex v2.0.6.8 审计 §三 Medium 3 修复）：
        /// 客户端发送入口闸门。检查 IsReady（已收到服务端 challenge）后才允许发送。
        ///
        /// 行为：
        ///   - IsReady=false（未收到 challenge 或 RNG 失败）：返回 false，不建 pending，不发包
        ///   - IsReady=true：生成 requestId，SetPending，发送，返回 true
        ///
        /// 调用方：ManualTidyWatcher / UI 按钮
        /// 失败处理：调用方应提示用户"未收到服务端会话 challenge，请稍后再试"
        /// </summary>
        public static bool TrySendTidyRequest(byte page, TidyMode mode, bool sortDescending, out uint requestId)
        {
            requestId = 0;

            // v2.0.6.10：Codex v2.0.6.9 审计 §三 Medium / §五阻断项 3 修复：
            //   - 禁止分别读取 IsReady + Value（存在 TOCTOU 窗口）
            //   - 使用 TryGetServerIssuedToken 原子读 API，在同一临界区验证 ready + 复制 token
            //   - 32-bit 运行时不假定 ulong 读写原子
            //   - 网络回调未证明必在 Unity 主线程，裸读写允许读到"已签发=true + 旧临时 token"或不一致状态
            ulong sessionNonce;
            if (!ClientSessionNonce.TryGetServerIssuedToken(out sessionNonce))
            {
                LaunchInventoryTidyPlugin.Log?.LogWarning(
                    $"[TidyNet] TrySendTidyRequest 拒绝：客户端未收到有效服务端 session challenge，" +
                    $"不建 pending，不发包。请等待 MSG_SESSION_CHALLENGE 或检查 RNG 是否失败。");
                return false;
            }

            requestId = ClientSessionNonce.NextRequestId();
            var hotkeys = HotkeySnapshotUtil.CaptureLocalHotkeys();

            byte[] payload;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(MSG_REQUEST_TIDY_V2);
                w.Write(PROTOCOL_VERSION_V3);
                w.Write(sessionNonce);
                w.Write(requestId);
                w.Write(page);
                w.Write((byte)mode);
                w.Write(sortDescending);
                w.Write((byte)hotkeys.Count);
                for (int i = 0; i < hotkeys.Count; i++)
                {
                    w.Write(hotkeys[i].HotkeyIndex);
                    w.Write(hotkeys[i].ExpectedItemId);
                    w.Write(hotkeys[i].OldPage);
                    w.Write(hotkeys[i].OldX);
                    w.Write(hotkeys[i].OldY);
                }
                payload = ms.ToArray();
            }

            ClientPendingState.SetPending(sessionNonce, requestId, page, mode, sortDescending);

            try
            {
                ModTransport.SendToServer(ModChannels.TidyPage, payload, reliable: true);
            }
            catch (Exception e)
            {
                ClientPendingState.ClearPending(sessionNonce, requestId);
                SecurityLogLimiter.LogException("send_to_server_failed",
                    $"TrySendTidyRequest SendToServer 抛异常 reqId={requestId}", e);
                requestId = 0;
                return false;
            }

            LaunchInventoryTidyPlugin.Log?.LogInfo(
                $"[TidyNet] -> 服务器: RequestTidyV3(reqId={requestId}, nonce={sessionNonce:X16}, page={page}, mode={mode}, desc={sortDescending}, hotkeys={hotkeys.Count})");

            return true;
        }

        /// <summary>
        /// v2.0.6.9 保留（向后兼容）：旧 SendTidyV2Request API。
        /// 内部调用 TrySendTidyRequest。若 IsReady=false，返回 0（调用方应检查）。
        /// 新代码应直接使用 TrySendTidyRequest。
        /// </summary>
        public static uint SendTidyV2Request(byte page, TidyMode mode, bool sortDescending)
        {
            if (!TrySendTidyRequest(page, mode, sortDescending, out uint requestId))
            {
                return 0;
            }
            return requestId;
        }

        // ─────────────────────────────────────────────────────────────
        // 服务器端：处理 RequestTidyV2
        // ─────────────────────────────────────────────────────────────

        private static void HandleRequestTidyV2(CSteamID sender, BinaryReader reader)
        {
            uint requestId = 0;

            // v2.0.6.5 修订（Codex v2.0.6.4 审计 §五阻断项 3）：
            // 网络回调只做：参数读取 + 协议验证 + lease 获取 + 入队到主线程
            // 主线程执行端做：RequestLedger + TidyTransactionManager + ManualTidyService + 响应
            //
            // Codex 审计原意：
            //   - 建立唯一的主线程调度入口；网络回调只验证和入队
            //   - 主线程执行端取得带 owner/requestId 生命周期的库存操作 lease
            //   - 修正"ACK 后释放"的错误声明（lease 在响应发送后即释放，不等 ACK）

            byte msgType;
            byte version;
            ulong sessionNonce;  // v2.0.6.5：V3 协议新增 64-bit nonce
            byte page;
            byte modeByte;
            bool sortDescending;
            byte hotkeyCount;
            List<HotkeySnapshot> hotkeySnapshots;

            // v2.0.6.4 P1：固定头读取，短包走 SecurityLogLimiter 节流
            try
            {
                msgType = reader.ReadByte();
                if (msgType != MSG_REQUEST_TIDY_V2) return;

                version = reader.ReadByte();
                // v2.0.6.5：V3 协议在 version 后插入 sessionNonce（8 字节）
                sessionNonce = reader.ReadUInt64();
                requestId = reader.ReadUInt32();
                page = reader.ReadByte();
                modeByte = reader.ReadByte();
                sortDescending = reader.ReadBoolean();
                hotkeyCount = reader.ReadByte();
            }
            catch (System.IO.EndOfStreamException)
            {
                SecurityLogLimiter.LogRejection(sender, "short_packet",
                    $"短包（固定头读取失败），拒绝 sender={(ulong)sender}");
                return;
            }
            catch (System.Exception ex)
            {
                SecurityLogLimiter.LogRejection(sender, "header_read_error",
                    $"固定头读取异常: {ex.Message}，拒绝 sender={(ulong)sender}");
                return;
            }

            // v2.0.6.5：协议版本检查 - V2 拒绝（强制升级），V3 通过
            if (version != PROTOCOL_VERSION_V3)
            {
                SecurityLogLimiter.LogRejection(sender, "version_mismatch",
                    $"协议版本不匹配（收到 {version}，要求 {PROTOCOL_VERSION_V3}），忽略 sender={(ulong)sender}");
                return;
            }

            // v2.0.6.5：nonce 必须非零（0 表示未初始化或伪造）
            if (sessionNonce == 0)
            {
                SecurityLogLimiter.LogRejection(sender, "invalid_nonce",
                    $"sessionNonce=0，拒绝 sender={(ulong)sender} reqId={requestId}");
                return;
            }

            // mode 白名单
            if (modeByte > 2)
            {
                SecurityLogLimiter.LogRejection(sender, "invalid_mode",
                    $"非法 mode={modeByte}，拒绝 sender={(ulong)sender} reqId={requestId}");
                return;
            }
            TidyMode mode = (TidyMode)modeByte;

            // page 范围（v2.0.1：拒绝 STORAGE=7，仅允许 2..6 或 0xFF）
            if (page != ALL_PAGES && (page < PlayerInventory.SLOTS || page > PlayerInventory.PANTS))
            {
                SecurityLogLimiter.LogRejection(sender, "invalid_page",
                    $"非法 page={page}（V2 仅支持 2..6 或 0xFF），拒绝 sender={(ulong)sender} reqId={requestId}");
                return;
            }

            // 严格 hotkeyCount 上限校验（> 8 直接拒绝，不截断）
            if (hotkeyCount > HotkeySnapshotUtil.HOTKEY_COUNT)
            {
                SecurityLogLimiter.LogRejection(sender, "hotkey_count_overflow",
                    $"hotkeyCount={hotkeyCount} 超上限（>{HotkeySnapshotUtil.HOTKEY_COUNT}），拒绝整个请求 sender={(ulong)sender} reqId={requestId}");
                return;
            }

            // 校验剩余字节长度，禁止尾随数据
            long expectedPayloadEnd = reader.BaseStream.Position + (hotkeyCount * 6);
            if (expectedPayloadEnd > reader.BaseStream.Length)
            {
                SecurityLogLimiter.LogRejection(sender, "hotkey_bytes_insufficient",
                    $"声明 hotkeyCount={hotkeyCount} 但剩余字节不足，拒绝 sender={(ulong)sender} reqId={requestId}");
                return;
            }

            hotkeySnapshots = new List<HotkeySnapshot>(hotkeyCount);
            for (int i = 0; i < hotkeyCount; i++)
            {
                byte hi = reader.ReadByte();
                ushort itemId = reader.ReadUInt16();
                byte p = reader.ReadByte();
                byte x = reader.ReadByte();
                byte y = reader.ReadByte();
                hotkeySnapshots.Add(new HotkeySnapshot(hi, itemId, p, x, y));
            }

            // 校验：若还有尾随字节，拒绝
            if (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                SecurityLogLimiter.LogRejection(sender, "trailing_data",
                    $"收到尾随数据，拒绝 sender={(ulong)sender} reqId={requestId}");
                return;
            }

            // v2.0.6.8：Codex v2.0.6.7 审计 §三 Critical 2 模板 B 修复：
            //   使用 RequestAdmissionStore.TryAdmit 进行单锁原子准入决策。
            //   修复 v2.0.6.7 的阻断项：
            //     - v2.0.6.7 在判定 BusyDifferent 之前为每个不同 requestId 新增 Received
            //     - 攻击者可发送 65+ 个不同 ID 驱逐 in-flight 条目（MAX_ENTRIES_PER_PLAYER=64）
            //     - 驱逐的通常就是正在执行的原请求，导致原请求无法写缓存结果
            //   v2.0.6.8 新流程：
            //     1. TryAdmit 在单一 _gate 锁内完成：会话验证 + 账本查询 + lease 检查 + 容量检查 + 创建条目 + 获取 lease
            //     2. BusyDifferent 不创建 ledger 条目（防止账本被攻击者填满）
            //     3. 容量满时不驱逐 Received 条目（防止 in-flight 被驱逐）
            //     4. New 状态调用 TryEnqueue；若失败调用 CancelNew 执行补偿事务
            var admission = RequestAdmissionStore.TryAdmit(sender, sessionNonce, requestId, out var cachedEntry);
            switch (admission)
            {
                case RequestAdmissionStore.AdmissionKind.InFlight:
                    // 原请求仍在处理中（Received），静默丢弃（不回送响应）
                    LaunchInventoryTidyPlugin.Log?.LogInfo(
                        $"[TidyNet] 收到重复 (nonce={sessionNonce:X16}, reqId={requestId}) 原请求仍在处理中（Received），静默丢弃（不回送响应）sender={(ulong)sender}");
                    return;

                case RequestAdmissionStore.AdmissionKind.Cached:
                    // cached (Committed/Failed/Expired) - 重发完整缓存响应
                    LaunchInventoryTidyPlugin.Log?.LogInfo(
                        $"[TidyNet] 收到重复 requestId={requestId} nonce={sessionNonce:X16}，返回缓存结果 state={cachedEntry.State} result={cachedEntry.Result} mappings={(cachedEntry.Mappings?.Count ?? 0)} sender={(ulong)sender}");
                    SendTidyCommitted(sender, sessionNonce, requestId, cachedEntry.Result, cachedEntry.Mappings);
                    return;

                case RequestAdmissionStore.AdmissionKind.BusyDifferent:
                    // 不同请求且玩家已有进行中事务，回送 Rejected（不建 ledger 条目）
                    SecurityLogLimiter.LogRejection(sender, "admission_busy_different",
                        $"玩家 {(ulong)sender} 已有进行中整理事务（不同 requestId），拒绝 nonce={sessionNonce:X16} reqId={requestId}（v2.0.6.8：不建 ledger 条目，防止账本被填满）");
                    SendTidyCommitted(sender, sessionNonce, requestId, TidyCommitResult.Rejected, null);
                    return;

                case RequestAdmissionStore.AdmissionKind.Rejected:
                    // 会话无效（token 未注册/不匹配/重放）或账本容量满，回送 Rejected
                    SecurityLogLimiter.LogRejection(sender, "admission_rejected",
                        $"准入被拒绝 nonce={sessionNonce:X16} reqId={requestId} sender={(ulong)sender}（会话无效或账本容量满）");
                    SendTidyCommitted(sender, sessionNonce, requestId, TidyCommitResult.Rejected, null);
                    return;

                case RequestAdmissionStore.AdmissionKind.New:
                    // v2.0.6.8：入队到主线程调度器；使用 TryEnqueue 感知失败
                    // 若 TryEnqueue 失败（队列满或 Shutdown），调用 CancelNew 执行补偿事务
                    // v2.0.6.10：Codex v2.0.6.9 审计 §三 Medium / §五阻断项 4 修复：
                    //   - 入队对象改为 QueuedTidyRequest，携带 Cancel 回调
                    //   - 插件卸载时 MainThreadDispatcher.Shutdown 会对每个 queued request 调用 Cancel
                    //   - Cancel 回调执行 RequestAdmissionStore.CancelNew + 尝试发送终态 Rejected
                    //   - 不再由 Queue.Clear() 静默丢弃已入队请求
                    var captured = new CapturedTidyRequest(
                        sender, sessionNonce, requestId, page, mode, sortDescending, hotkeySnapshots);
                    var queuedRequest = new QueuedTidyRequest
                    {
                        Work = () => ExecuteTidyRequestOnMainThread(captured),
                        Cancel = () =>
                        {
                            // v2.0.6.10：Shutdown 期间被 drain 的任务，执行补偿事务
                            RequestAdmissionStore.CancelNew(sender, sessionNonce, requestId);
#if TIDY_TEST_HARNESS
                            // v2.0.6.13 Codex 第二轮 §3.4：SP-SD 真实事务屏障 - 记录 Cancel 调用
                            ShutdownBarrier.RecordCancelInvocation(requestId);
#endif
                            try
                            {
                                SendTidyCommitted(sender, sessionNonce, requestId, TidyCommitResult.Rejected, null);
                            }
                            catch (Exception ex)
                            {
                                // transport 可能已不可用，至少 ledger 终态已通过 CancelNew 设置
                                LaunchInventoryTidyPlugin.Log?.LogWarning(
                                    $"[TidyNet] Shutdown Cancel SendTidyCommitted 失败（transport 可能已不可用），" +
                                    $"ledger 终态已设置 nonce={sessionNonce:X16} reqId={requestId}: {ex.Message}");
                            }
                            SecurityLogLimiter.LogRejection(sender, "shutdown_cancel",
                                $"插件卸载期间 drain 任务，已 CancelNew + 尝试发送 Rejected " +
                                $"nonce={sessionNonce:X16} reqId={requestId} sender={(ulong)sender}");
                        },
                        Tag = $"TidyRequest nonce={sessionNonce:X16} reqId={requestId} sender={(ulong)sender}",
                    };
                    if (!MainThreadDispatcher.TryEnqueue(queuedRequest))
                    {
                        // v2.0.6.8：TryEnqueue 失败，执行补偿事务
                        // CancelNew 会释放 lease + 标记 ledger Failed
                        RequestAdmissionStore.CancelNew(sender, sessionNonce, requestId);
                        SecurityLogLimiter.LogRejection(sender, "enqueue_failed",
                            $"TryEnqueue 失败（队列满或 Shutdown），已 CancelNew nonce={sessionNonce:X16} reqId={requestId} sender={(ulong)sender}");
                        SendTidyCommitted(sender, sessionNonce, requestId, TidyCommitResult.Rejected, null);
                    }
#if TIDY_TEST_HARNESS
                    else
                    {
                        // v2.0.6.13 Codex 第二轮 §3.4：SP-SD 真实事务屏障 - 记录真实入队的 requestId
                        ShutdownBarrier.RecordQueuedRequest(requestId);
                    }
#endif
                    return;
            }
        }

        /// <summary>
        /// v2.0.6.5 新增（Codex v2.0.6.4 审计 §五阻断项 3）：
        /// 主线程执行端。网络回调已完成：参数读取 + ledger 登记（Received）+ lease 获取 + 入队。
        /// 本方法在 Unity 主线程中执行（由 LaunchInventoryTidyPlugin.Update -> MainThreadDispatcher.ProcessAll 调用），
        /// 负责：限流 + 熔断 + 会话 + 玩家解析 + 快捷键验证 + ManualTidyService + 响应 + lease 释放。
        ///
        /// v2.0.6.7 修订（Codex v2.0.6.6 审计 §三 Critical 2 修复）：
        ///   - ledger TryBegin 已移到网络回调阶段，本方法不再重复调用
        ///   - 所有拒绝路径（限流/熔断/会话/解析失败）必须 MarkResult(Failed, ...) + 释放 lease
        ///   - ConcurrentMutationAfterCommit 路径新增：打开持久熔断（restoreVerified: false）
        ///     强制管理员通过 /tidy_unfault 显式恢复，提供可观察/可恢复的 fail-closed 流程
        ///
        /// lease 生命周期（修正后）：
        ///   - TryAcquire：网络回调入口（ledger 之后）
        ///   - 持有：Prepare -> Commit -> Verify -> 发送 TidyCommitted 响应
        ///   - Release：主线程 finally（响应发送后立即释放，不等 ACK）
        ///   - ACK 由独立的 TidyTransactionManager 跟踪
        /// </summary>
        private static void ExecuteTidyRequestOnMainThread(CapturedTidyRequest req)
        {
            CSteamID sender = req.Sender;
            ulong sessionNonce = req.SessionNonce;  // v2.0.6.5：复合键的一部分
            uint requestId = req.RequestId;
            try
            {
                // v2.0.6.7：限流检查（ledger 已在网络回调阶段登记，此处仅消耗新请求的限流配额）
                // 限流拒绝时 MarkResult(Failed, Rejected)，便于后续相同 nonce + reqId 重复包走缓存重发
                if (!TidyRateLimiter.Allow(sender))
                {
                    RequestLedger.MarkResult(sender, sessionNonce, requestId,
                        RequestLedger.RequestState.Failed, TidyCommitResult.Rejected, null);
                    SendTidyCommitted(sender, sessionNonce, requestId, TidyCommitResult.Rejected, null);
                    return;
                }

                // v2.0.1：故障熔断检查
                if (!TidyFaultCircuit.IsAllowed(sender))
                {
                    SecurityLogLimiter.LogRejection(sender, "fault_circuit",
                        $"玩家 {(ulong)sender} 已被熔断，拒绝整理 reqId={requestId}");
                    RequestLedger.MarkResult(sender, sessionNonce, requestId,
                        RequestLedger.RequestState.Failed, TidyCommitResult.CriticalFailure, null);
                    SendTidyCommitted(sender, sessionNonce, requestId, TidyCommitResult.CriticalFailure, null);
                    return;
                }

                // v2.0.6.8：会话验证已移至 RequestAdmissionStore.TryAdmit（模板 B）
                //   - TryAdmit 在 _gate 锁内调用 ServerSessionRegistry.ValidateRequest
                //   - 若会话无效（token 未注册/不匹配/重放），TryAdmit 返回 Rejected，
                //     不创建 ledger 条目，不获取 lease，直接回送 Rejected
                //   - 主线程执行端无需重复验证（disconnect 路径已清理 lease + ledger + session）

                Player player = ResolvePlayerBySteamId(sender);
                if (player?.inventory == null)
                {
                    SecurityLogLimiter.LogRejection(sender, "no_player",
                        $"sender {(ulong)sender} 无对应 Player");
                    RequestLedger.MarkResult(sender, sessionNonce, requestId, RequestLedger.RequestState.Failed, TidyCommitResult.Rejected, null);
                    SendTidyCommitted(sender, sessionNonce, requestId, TidyCommitResult.Rejected, null);
                    return;
                }

                // 每名玩家最多 1 个进行中事务（快捷键 pending）
                if (TidyTransactionManager.HasActiveTransaction(sender))
                {
                    SecurityLogLimiter.LogRejection(sender, "active_transaction",
                        $"sender {(ulong)sender} 已有进行中事务，拒绝新请求 reqId={requestId}");
                    RequestLedger.MarkResult(sender, sessionNonce, requestId, RequestLedger.RequestState.Failed, TidyCommitResult.Rejected, null);
                    SendTidyCommitted(sender, sessionNonce, requestId, TidyCommitResult.Rejected, null);
                    return;
                }

                // 验证并解析快捷键快照
                var resolvedHotkeys = HotkeySnapshotUtil.ValidateAndResolve(player.inventory, req.HotkeySnapshots);
                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[TidyNet] sender={(ulong)sender} reqId={requestId} nonce={sessionNonce:X16} hotkey snapshots: {req.HotkeySnapshots.Count} 个上传，{resolvedHotkeys.Count} 个通过验证");

                // v2.0.6.13 Round 9（Codex Round 8 §3.1）：捕获服务端可信的快捷键恢复目标指纹。
                // 绝不信任客户端上传的 quality/state；服务端从已解析的真实 ItemJar 取指纹。
                // 后续 ACK 阶段按完整指纹校验目标，避免同 ID 不同实例错位绑定。
                var trustedHotkeyFingerprints = new Dictionary<ItemJar, ItemFingerprint>(
                    ReferenceEqualityComparer<ItemJar>.Instance);

                foreach (KeyValuePair<ItemJar, HotkeySnapshot> pair in resolvedHotkeys)
                {
                    ItemJar jar = pair.Key;
                    if (jar == null || jar.item == null)
                    {
                        LaunchInventoryTidyPlugin.Log?.LogWarning(
                            $"[TidyNet] reqId={requestId} hotkey={pair.Value.HotkeyIndex} 解析后 jar/item 为 null，拒绝整理");
                        RequestLedger.MarkResult(sender, sessionNonce, requestId,
                            RequestLedger.RequestState.Failed, TidyCommitResult.Rejected, null);
                        SendTidyCommitted(sender, sessionNonce, requestId, TidyCommitResult.Rejected, null);
                        return;
                    }

                    trustedHotkeyFingerprints[jar] = new ItemFingerprint(jar.item);
                }

                // 执行事务化整理（v2.0.1 真事务 + 回滚）
                // v2.0.6.5：主线程执行，ManualTidyService 的主线程断言将通过
                var outMapping = new Dictionary<ItemJar, NewPosition>();
                TidyOperationOutcome outcome;
                try
                {
                    if (req.Page == ALL_PAGES)
                    {
                        outcome = ManualTidyService.TidyAllPlayerPages(
                            player.inventory, req.SortDescending, req.Mode, outMapping);
                    }
                    else
                    {
                        outcome = ManualTidyService.TidyPage(
                            player.inventory.items[req.Page], req.Page, req.SortDescending, req.Mode, outMapping);
                    }
                }
                catch (Exception e)
                {
                    SecurityLogLimiter.LogException("tidy_service_crash",
                        $"整理异常 reqId={requestId}", e);
                    // 服务层未捕获异常：无法确认回滚状态，按持久熔断处理
                    TidyFaultCircuit.Open(sender, reason: $"整理异常: {e.Message}", restoreVerified: false);
                    RequestLedger.MarkResult(sender, sessionNonce, requestId, RequestLedger.RequestState.Failed, TidyCommitResult.CriticalFailure, null);
                    SendTidyCommitted(sender, sessionNonce, requestId, TidyCommitResult.CriticalFailure, null);
                    return;
                }

                if (outcome.Result == TidyCommitResult.CriticalFailure)
                {
                    // v2.0.4 P0-4：CriticalFailure 时，若库存已回滚则尝试恢复快捷键到原坐标
                    // 快捷键恢复结果纳入 outcome，决定最终熔断类型
                    if (outcome.RollbackVerified && resolvedHotkeys.Count > 0)
                    {
                        var hkOutcome = TryRestoreHotkeysToOriginalPositions(player, resolvedHotkeys);
                        outcome.HotkeyRestoreAttempted = hkOutcome.Attempted;
                        outcome.HotkeyRestoreSucceeded = hkOutcome.Succeeded;
                        outcome.HotkeyRestoreFailed = hkOutcome.Failed;
                        outcome.HotkeyRollbackVerified = hkOutcome.AllVerified;
                    }
                    else
                    {
                        // 无快捷键需要恢复或库存未回滚
                        outcome.HotkeyRollbackVerified = (resolvedHotkeys.Count == 0) && outcome.RollbackVerified;
                    }

                    // v2.0.4 P0-4：使用 FullRestorationVerified 决定熔断类型
                    bool fullRestored = outcome.FullRestorationVerified;
                    TidyFaultCircuit.Open(sender,
                        reason: outcome.FailureReason ?? "CriticalFailure during tidy",
                        restoreVerified: fullRestored);

                    if (!fullRestored)
                    {
                        LaunchInventoryTidyPlugin.Log?.LogError(
                            $"[TidyNet] reqId={requestId} 不完整恢复：inventory={outcome.RollbackVerified}, hotkey={outcome.HotkeyRollbackVerified} ({outcome.HotkeyRestoreSucceeded}/{outcome.HotkeyRestoreAttempted}) -> 持久熔断");
                    }

                    RequestLedger.MarkResult(sender, sessionNonce, requestId, RequestLedger.RequestState.Failed, TidyCommitResult.CriticalFailure, null);
                    SendTidyCommitted(sender, sessionNonce, requestId, outcome.Result, null);
                    return;
                }

                // v2.0.6.5：ConcurrentMutationAfterCommit - 安全隔离
                // v2.0.6.7：Codex v2.0.6.6 审计 §三 Medium 4 修复：
                //   - 打开持久熔断（restoreVerified: false），强制管理员通过 /tidy_unfault 显式恢复
                //   - 提供可观察（/tidy_faults 查询）、可恢复（/tidy_unfault 解除、/tidy_fault_recover 降级恢复）的 fail-closed 流程
                //   - 防止未知库存状态下玩家继续整理造成更大破坏
                // v2.0.6.8：Codex v2.0.6.7 审计 §三 Medium 4 修复：
                //   - 删除"玩家进入 fail-closed 安全隔离"的错误表述
                //   - 持久熔断只阻止后续整理请求（本插件），不冻结原版背包、物品使用或存档
                //   - 未知库存状态下玩家仍可继续操作和保存；本插件不再写入
                //   - 不声称"玩家被冻结"
                if (outcome.Result == TidyCommitResult.ConcurrentMutationAfterCommit)
                {
                    LaunchInventoryTidyPlugin.Log?.LogError(
                        $"[TidyNet] reqId={requestId} ConcurrentMutationAfterCommit：已提交页被并发修改或异常路径状态未知，回滚被拒绝以保护合法并发变更。" +
                        $"本插件已停止为该玩家继续整理（持久熔断，需管理员通过 /tidy_unfault 显式恢复）。" +
                        $"注意：原版背包、物品使用和存档不受本插件冻结，玩家仍可继续操作和保存；未知库存状态下本插件不再写入。");
                    TidyFaultCircuit.Open(sender,
                        reason: $"ConcurrentMutationAfterCommit (reqId={requestId}): unknown inventory state detected during rollback, rollback refused; only this plugin's tidy requests are blocked, vanilla inventory is not frozen",
                        restoreVerified: false);
                    RequestLedger.MarkResult(sender, sessionNonce, requestId, RequestLedger.RequestState.Failed, TidyCommitResult.ConcurrentMutationAfterCommit, null);
                    SendTidyCommitted(sender, sessionNonce, requestId, outcome.Result, null);
                    return;
                }

                if (outcome.Result == TidyCommitResult.Rejected)
                {
                    LaunchInventoryTidyPlugin.Log?.LogInfo(
                        $"[TidyNet] 整理被拒绝（未修改任何物品）: reqId={requestId}");
                    RequestLedger.MarkResult(sender, sessionNonce, requestId, RequestLedger.RequestState.Failed, TidyCommitResult.Rejected, null);
                    SendTidyCommitted(sender, sessionNonce, requestId, outcome.Result, null);
                    return;
                }

                // v2.0.6.13 Round 9（Codex Round 8 §3.1）：构建快捷键恢复映射 + 待恢复事务
                // 使用服务端可信指纹（trustedHotkeyFingerprints），不信任客户端上传的 quality/state。
                var entries = new List<HotkeyRestoreEntry>();
                var mappings = new List<NewPositionMapping>();

                foreach (KeyValuePair<ItemJar, HotkeySnapshot> pair in resolvedHotkeys)
                {
                    ItemJar originalJar = pair.Key;
                    HotkeySnapshot snapshot = pair.Value;

                    NewPosition newPosition;
                    ItemFingerprint trustedFingerprint;
                    if (!outMapping.TryGetValue(originalJar, out newPosition) ||
                        !trustedHotkeyFingerprints.TryGetValue(originalJar, out trustedFingerprint))
                    {
                        // 已提交但缺 mapping 时不能谎称快捷键恢复成功；响应仍可发送，测试将对此 FAIL。
                        LaunchInventoryTidyPlugin.Log?.LogWarning(
                            $"[TidyNet] reqId={requestId} hotkey={snapshot.HotkeyIndex} 缺少可信恢复映射");
                        continue;
                    }

                    entries.Add(new HotkeyRestoreEntry(
                        snapshot.HotkeyIndex,
                        newPosition.Page,
                        newPosition.X,
                        newPosition.Y,
                        trustedFingerprint));

                    // 协议仍保持既有 7-byte mapping ABI；它仅用于客户端收敛定位。
                    mappings.Add(new NewPositionMapping(
                        snapshot.HotkeyIndex,
                        newPosition.Page,
                        newPosition.X,
                        newPosition.Y,
                        trustedFingerprint.Id));
                }

                // v2.0.6.5：PendingHotkeyRestore 携带 sessionNonce
                var pending = new PendingHotkeyRestore(requestId, sessionNonce, entries, TransactionTtl);
                TidyTransactionManager.Store(sender, pending);

                // v2.0.2：账本缓存完整 mappings（深拷贝），重复请求时重发完整响应
                RequestLedger.MarkResult(sender, sessionNonce, requestId, RequestLedger.RequestState.Committed, TidyCommitResult.Committed, mappings);

                SendTidyCommitted(sender, sessionNonce, requestId, TidyCommitResult.Committed, mappings);

                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[TidyNet] 服务器: 整理提交 reqId={requestId} nonce={sessionNonce:X16} page={req.Page} mode={req.Mode} mappings={mappings.Count}");
            }
            catch (Exception e)
            {
                SecurityLogLimiter.LogException("request_tidy_v2_crash",
                    $"ExecuteTidyRequestOnMainThread crash reqId={requestId}", e);
                if (requestId != 0)
                {
                    // 服务层之外的异常：无法确认回滚状态，按持久熔断处理
                    TidyFaultCircuit.Open(sender, reason: $"ExecuteTidyRequestOnMainThread crash: {e.Message}", restoreVerified: false);
                    RequestLedger.MarkResult(sender, sessionNonce, requestId, RequestLedger.RequestState.Failed, TidyCommitResult.CriticalFailure, null);
                    SendTidyCommitted(sender, sessionNonce, requestId, TidyCommitResult.CriticalFailure, null);
                }
            }
            finally
            {
                // v2.0.6.5：lease 在主线程 finally 释放，绑定 requestId 防止错配。
                // 注意：lease 不等 ACK。ACK 由独立的 TidyTransactionManager 跟踪。
                // 文档修正：原"ACK 后释放"描述错误，实际为"响应发送后释放"。
                PlayerOperationGate.Release(sender, requestId);
            }
        }

        /// <summary>
        /// v2.0.6.5 新增：主线程执行的整理请求参数容器。
        /// 网络回调读取并验证所有字段后，构建此容器并入队到 MainThreadDispatcher。
        /// 主线程执行端从此容器取出所有参数，不再访问 BinaryReader。
        /// v2.0.6.5：新增 SessionNonce 字段用于 V3 协议复合键。
        /// </summary>
        private struct CapturedTidyRequest
        {
            public CSteamID Sender;
            public ulong SessionNonce;  // v2.0.6.5：V3 协议 64-bit nonce
            public uint RequestId;
            public byte Page;
            public TidyMode Mode;
            public bool SortDescending;
            public List<HotkeySnapshot> HotkeySnapshots;

            public CapturedTidyRequest(CSteamID sender, ulong sessionNonce, uint requestId, byte page,
                TidyMode mode, bool sortDescending, List<HotkeySnapshot> hotkeySnapshots)
            {
                Sender = sender;
                SessionNonce = sessionNonce;
                RequestId = requestId;
                Page = page;
                Mode = mode;
                SortDescending = sortDescending;
                HotkeySnapshots = hotkeySnapshots;
            }
        }

        /// <summary>
        /// v2.0.3 P1-M12 新增：CriticalFailure 回滚成功后，按原坐标恢复快捷键绑定。
        /// 回滚已将物品放回原 (OldPage, OldX, OldY) 位置，因此使用快照中的 OLD 坐标重新绑定。
        /// v2.0.4 P0-4 修订：返回 HotkeyRestoreOutcome 结构，调用方据此决定熔断类型。
        /// </summary>
        private struct HotkeyRestoreOutcome
        {
            public int Attempted;
            public int Succeeded;
            public int Failed;
            public bool AllVerified => Failed == 0 && Attempted == Succeeded;
        }

        private static HotkeyRestoreOutcome TryRestoreHotkeysToOriginalPositions(Player player,
            Dictionary<ItemJar, HotkeySnapshot> resolvedHotkeys)
        {
            var outcome = new HotkeyRestoreOutcome();
            if (player?.equipment == null || resolvedHotkeys == null) return outcome;

            outcome.Attempted = resolvedHotkeys.Count;
            foreach (var kv in resolvedHotkeys)
            {
                HotkeySnapshot snap = kv.Value;
                try
                {
                    // 回滚后物品应位于原坐标，再次校验
                    Items pageItems = player.inventory.items[snap.OldPage];
                    if (pageItems == null) { outcome.Failed++; continue; }
                    byte jarIdx = pageItems.getIndex(snap.OldX, snap.OldY);
                    if (jarIdx == byte.MaxValue) { outcome.Failed++; continue; }
                    ItemJar jar = pageItems.getItem(jarIdx);
                    if (jar?.item == null || jar.item.id != snap.ExpectedItemId) { outcome.Failed++; continue; }

                    ItemAsset asset = jar.GetAsset();
                    if (asset == null || !ItemTool.checkUseable(snap.OldPage, asset.id))
                    {
                        outcome.Failed++;
                        continue;
                    }

                    player.equipment.ServerBindItemHotkey(snap.HotkeyIndex, asset, snap.OldPage, snap.OldX, snap.OldY);
                    outcome.Succeeded++;
                }
                catch (Exception e)
                {
                    LaunchInventoryTidyPlugin.Log?.LogWarning(
                        $"[TidyNet] 回滚后恢复快捷键 {snap.HotkeyIndex} 异常: {e.Message}");
                    outcome.Failed++;
                }
            }

            LaunchInventoryTidyPlugin.Log?.LogInfo(
                $"[TidyNet] CriticalFailure 回滚后快捷键恢复：attempted={outcome.Attempted}, succeeded={outcome.Succeeded}, failed={outcome.Failed}");
            return outcome;
        }

        // ─────────────────────────────────────────────────────────────
        // 服务器端：发送 TidyCommitted
        // ─────────────────────────────────────────────────────────────

        private static void SendTidyCommitted(CSteamID target, ulong sessionNonce, uint requestId,
            TidyCommitResult result, List<NewPositionMapping> mappings)
        {
            byte[] payload;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(MSG_TIDY_COMMITTED);
                w.Write(sessionNonce);  // v2.0.6.5：V3 协议 nonce
                w.Write(requestId);
                w.Write((byte)result);
                int count = mappings?.Count ?? 0;
                if (count > 255) count = 255;
                w.Write((byte)count);
                if (mappings != null)
                {
                    for (int i = 0; i < count; i++)
                    {
                        WriteMapping(w, mappings[i]);
                    }
                }
                payload = ms.ToArray();
            }
            ModTransport.SendToClient(target, ModChannels.TidyPage, payload, reliable: true);
            LaunchInventoryTidyPlugin.Log?.LogInfo(
                $"[TidyNet] -> 客机 {target}: TidyCommitted(reqId={requestId}, nonce={sessionNonce:X16}, result={result}, mappings={mappings?.Count ?? 0})");
        }

        // ─────────────────────────────────────────────────────────────
        // 客机端：处理 TidyCommitted + 库存收敛检查 + 发送 ACK
        // ─────────────────────────────────────────────────────────────

        private static void HandleTidyCommittedFromServer(BinaryReader reader)
        {
            try
            {
                byte msgType;
                ulong sessionNonce = 0;  // v2.0.6.5：V3 协议 nonce
                uint requestId = 0;
                byte resultByte;
                byte mappingCount;

                // v2.0.4 P1：固定头读取，短包走 client limiter 节流
                try
                {
                    msgType = reader.ReadByte();
                    if (msgType != MSG_TIDY_COMMITTED) return;
                    sessionNonce = reader.ReadUInt64();  // v2.0.6.5：V3 nonce
                    requestId = reader.ReadUInt32();
                    resultByte = reader.ReadByte();
                    mappingCount = reader.ReadByte();
                }
                catch (System.IO.EndOfStreamException)
                {
                    SecurityLogLimiter.LogClientRejection("short_committed_packet",
                        $"短包（TidyCommitted 固定头读取失败），忽略");
                    return;
                }
                catch (System.Exception ex)
                {
                    SecurityLogLimiter.LogClientRejection("committed_header_read_error",
                        $"TidyCommitted 固定头读取异常: {ex.Message}，忽略");
                    return;
                }

                TidyCommitResult result = (TidyCommitResult)resultByte;

                // v2.0.6.5：nonce 必须非零（V3 协议）
                if (sessionNonce == 0)
                {
                    SecurityLogLimiter.LogClientRejection("committed_invalid_nonce",
                        $"TidyCommitted sessionNonce=0，忽略 reqId={requestId}");
                    return;
                }

                // v2.0.5 P0-1：使用 MAPPING_WIRE_SIZE 单一事实源，禁止手写数字
                long expectedPayloadEnd = reader.BaseStream.Position + (mappingCount * MAPPING_WIRE_SIZE);
                if (expectedPayloadEnd > reader.BaseStream.Length)
                {
                    SecurityLogLimiter.LogClientRejection("committed_bytes_insufficient",
                        $"声明 mappingCount={mappingCount} 但剩余字节不足，忽略 reqId={requestId}");
                    return;
                }

                var mappings = new List<NewPositionMapping>(mappingCount);
                for (int i = 0; i < mappingCount; i++)
                {
                    // v2.0.6 P1-7：reserved 字节必须为 0
                    if (!TryReadMapping(reader, out var m))
                    {
                        SecurityLogLimiter.LogClientRejection("committed_invalid_reserved",
                            $"TidyCommitted 第 {i} 条映射 reserved 字节非 0，忽略 reqId={requestId}");
                        return;
                    }
                    mappings.Add(m);
                }

                // 校验：若还有尾随字节，拒绝
                if (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    SecurityLogLimiter.LogClientRejection("committed_trailing_data",
                        $"收到尾随数据，忽略 reqId={requestId}");
                    return;
                }

                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[TidyNet] <- 服务器: TidyCommitted(reqId={requestId}, nonce={sessionNonce:X16}, result={result}, mappings={mappings.Count})");

                // v2.0.6.5：客户端验证 (sessionNonce, requestId) 是否由本机发出
                if (!ClientPendingState.IsPending(sessionNonce, requestId))
                {
                    SecurityLogLimiter.LogClientRejection("unexpected_request_id",
                        $"收到未发出的 (nonce={sessionNonce:X16}, reqId={requestId}) 响应，忽略（可能是旧响应或伪造）");
                    return;
                }

#if TIDY_TEST_HARNESS
                // v2.0.6.13 Codex 架构审计 §3.3：网络回环探针，记录协议校验通过的 Commit 回包
                NetworkTestProbe.RecordCommit(requestId, result);
#endif

                if (result != TidyCommitResult.Committed)
                {
                    ClientPendingState.ClearPending(sessionNonce, requestId);
                    if (result == TidyCommitResult.CriticalFailure)
                    {
                        LaunchInventoryTidyPlugin.Log?.LogError(
                            $"[TidyNet] 服务器报告 CriticalFailure，本玩家整理能力已被熔断，需联系管理员或重新登录");
                    }
                    return;
                }

                Player player = Player.LocalPlayer;
                if (player?.inventory == null)
                {
                    LaunchInventoryTidyPlugin.Log?.LogWarning(
                        "[TidyNet] LocalPlayer.inventory 为 null，跳过收敛检查");
                    ClientPendingState.ClearPending(sessionNonce, requestId);
                    return;
                }

                StartBoundedConvergenceCheck(player, sessionNonce, requestId, mappings);
            }
            catch (Exception e)
            {
                SecurityLogLimiter.LogException("client_committed_crash",
                    $"HandleTidyCommittedFromServer crash", e);
            }
        }

        /// <summary>
        /// v2.0.5 P1-4 新增，v2.0.6 P1-3/P1-4/P1-5 修订，v2.0.6.5 V3 协议 nonce 升级：处理服务器发来的快捷键恢复结果。
        /// 布局：[MSG_TIDY_HOTKEY_RESULT:1][sessionNonce:8][requestId:4][restoredCount:1][clearedCount:1][failedCount:1][verifiedCount:1][failedHotkeyIndices:failedCount*1]
        ///
        /// v2.0.6 P1-3：requestId 必须与本机等待中的 HotkeyResultPending 匹配，否则拒绝。
        /// v2.0.6 P1-4：verifiedCount 表示实际验证通过的快捷键数量（DS 端可能为 0）。
        /// v2.0.6 P1-5：业务不变量验证（restored+failed=期望, cleared==failed, 索引唯一0..7）。
        /// v2.0.6.5：复合键升级为 (sessionNonce, requestId)，nonce 必须非零且与本机 pending 匹配。
        ///
        /// v2.0.6.6 修订（Codex v2.0.6.5 审计 §三 Medium 4 修复）：
        ///   本消息仅含快捷键绑定统计（restoredCount/verifiedCount/clearedCount），
        ///   不证明全量库存同步。全量库存同步证据必须由外部双端测试前后导出的
        ///   全页 (x, y, rot, id, amount, quality, state) 多重集合对照证明。
        /// </summary>
        private static void HandleTidyHotkeyResultFromServer(BinaryReader reader)
        {
            try
            {
                byte msgType;
                ulong sessionNonce = 0;  // v2.0.6.5：V3 协议 nonce
                uint requestId;
                byte restoredCount;
                byte clearedCount;
                byte failedCount;
                byte verifiedCount;

                try
                {
                    msgType = reader.ReadByte();
                    if (msgType != MSG_TIDY_HOTKEY_RESULT) return;
                    sessionNonce = reader.ReadUInt64();  // v2.0.6.5：V3 nonce
                    requestId = reader.ReadUInt32();
                    restoredCount = reader.ReadByte();
                    clearedCount = reader.ReadByte();
                    failedCount = reader.ReadByte();
                    verifiedCount = reader.ReadByte();
                }
                catch (System.IO.EndOfStreamException)
                {
                    SecurityLogLimiter.LogClientRejection("short_hotkey_result_packet",
                        "短包（TidyHotkeyResult 固定头读取失败），忽略");
                    return;
                }
                catch (System.Exception ex)
                {
                    SecurityLogLimiter.LogClientRejection("hotkey_result_header_read_error",
                        $"TidyHotkeyResult 固定头读取异常: {ex.Message}，忽略");
                    return;
                }

                // v2.0.6.5：nonce 必须非零
                if (sessionNonce == 0)
                {
                    SecurityLogLimiter.LogClientRejection("hotkey_result_invalid_nonce",
                        $"TidyHotkeyResult sessionNonce=0，忽略 reqId={requestId}");
                    return;
                }

                // v2.0.6 P1-3 + v2.0.6.5：(sessionNonce, requestId) 关联验证
                if (!ClientHotkeyResultPending.IsPending(sessionNonce, requestId))
                {
                    SecurityLogLimiter.LogClientRejection("unexpected_hotkey_result_request_id",
                        $"收到未等待的 HotkeyResult (nonce={sessionNonce:X16}, reqId={requestId})，忽略（可能是延迟/重复/伪造响应）");
                    return;
                }

                // 校验：剩余字节必须等于 failedCount
                long expectedEnd = reader.BaseStream.Position + failedCount;
                if (expectedEnd > reader.BaseStream.Length)
                {
                    SecurityLogLimiter.LogClientRejection("hotkey_result_bytes_insufficient",
                        $"声明 failedCount={failedCount} 但剩余字节不足，忽略 reqId={requestId}");
                    return;
                }

                var failedIndices = new List<byte>(failedCount);
                for (int i = 0; i < failedCount; i++)
                {
                    failedIndices.Add(reader.ReadByte());
                }

                // 校验尾随数据
                if (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    SecurityLogLimiter.LogClientRejection("hotkey_result_trailing_data",
                        $"TidyHotkeyResult 收到尾随数据，忽略 reqId={requestId}");
                    return;
                }

                // v2.0.6 P1-5：业务不变量验证
                // 1. failedIndices.Count == failedCount（已由读取逻辑保证）
                // 2. clearedCount == failedCount
                if (clearedCount != failedCount)
                {
                    SecurityLogLimiter.LogClientRejection("hotkey_result_invariant_cleared_failed",
                        $"TidyHotkeyResult 不变量失败：clearedCount={clearedCount} != failedCount={failedCount}，忽略 reqId={requestId}");
                    return;
                }
                // 3. failedIndices 全部 < 8
                for (int i = 0; i < failedIndices.Count; i++)
                {
                    if (failedIndices[i] >= 8)
                    {
                        SecurityLogLimiter.LogClientRejection("hotkey_result_invariant_index_range",
                            $"TidyHotkeyResult 不变量失败：failedIndices[{i}]={failedIndices[i]} >= 8，忽略 reqId={requestId}");
                        return;
                    }
                }
                // 4. failedIndices 唯一
                var uniqueCheck = new HashSet<byte>(failedIndices);
                if (uniqueCheck.Count != failedIndices.Count)
                {
                    SecurityLogLimiter.LogClientRejection("hotkey_result_invariant_index_unique",
                        $"TidyHotkeyResult 不变量失败：failedIndices 存在重复，忽略 reqId={requestId}");
                    return;
                }
                // 5. verifiedCount <= restoredCount（验证通过数不超过绑定调用成功数）
                if (verifiedCount > restoredCount)
                {
                    SecurityLogLimiter.LogClientRejection("hotkey_result_invariant_verified_range",
                        $"TidyHotkeyResult 不变量失败：verifiedCount={verifiedCount} > restoredCount={restoredCount}，忽略 reqId={requestId}");
                    return;
                }

                // v2.0.6 P1-3 + v2.0.6.5：消费 pending（HotkeyResultWaitBehaviour 会检测到并自毁）
                ClientHotkeyResultPending.ClearPending(sessionNonce, requestId);

#if TIDY_TEST_HARNESS
                // v2.0.6.13 Codex 架构审计 §3.3：网络回环探针，记录协议校验通过的 HotkeyResult 回包
                NetworkTestProbe.RecordHotkey(requestId, restoredCount, verifiedCount, clearedCount, failedCount);
#endif

                // v2.0.6 P1-4/P1-5：客户端成功条件必须同时满足 failed=0 且 cleared=0
                // verifiedCount < restoredCount 时表示服务器无法完全验证，提示但不警告
                if (failedCount > 0)
                {
                    // 有失败：明确告知用户部分快捷键未恢复
                    var sb = new System.Text.StringBuilder(128);
                    sb.Append("[TidyNet] ⚠ 整理完成，但 ");
                    sb.Append(failedCount).Append(" 个快捷键未能恢复（已清除绑定）：");
                    for (int i = 0; i < failedIndices.Count && i < 8; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(failedIndices[i]);
                    }
                    LaunchInventoryTidyPlugin.Log?.LogWarning(sb.ToString());
                    ShowHotkeyRestoreWarning();
                }
                else if (verifiedCount < restoredCount)
                {
                    // 绑定调用成功但部分无法验证（DS 端常见）
                    LaunchInventoryTidyPlugin.Log?.LogInfo(
                        $"[TidyNet] <- 服务器: TidyHotkeyResult(nonce={sessionNonce:X16}, reqId={requestId}, restored={restoredCount}, verified={verifiedCount}, cleared=0, failed=0) 绑定调用成功，部分最终状态无法验证");
                }
                else
                {
                    LaunchInventoryTidyPlugin.Log?.LogInfo(
                        $"[TidyNet] <- 服务器: TidyHotkeyResult(nonce={sessionNonce:X16}, reqId={requestId}, restored={restoredCount}, verified={verifiedCount}, cleared=0, failed=0) 全部快捷键已恢复并验证");
                }
            }
            catch (Exception e)
            {
                SecurityLogLimiter.LogException("client_hotkey_result_crash",
                    "HandleTidyHotkeyResultFromServer crash", e);
            }
        }

        private static void StartBoundedConvergenceCheck(Player player, ulong sessionNonce, uint requestId, List<NewPositionMapping> mappings)
        {
            // v2.0.2：卸载中不再创建新的 convergence 对象
            if (_shuttingDown)
            {
                LaunchInventoryTidyPlugin.Log?.LogWarning(
                    $"[TidyNet] Shutdown 中，跳过 convergence check reqId={requestId}");
                return;
            }

            var go = new GameObject("LaunchInventoryTidy_ConvergenceCheck");
            UnityEngine.Object.DontDestroyOnLoad(go);

            // v2.0.2：跟踪到活跃列表，OnDestroy 时统一销毁
            lock (_convLock)
            {
                _activeConvergenceObjects.Add(go);
            }

            var checker = go.AddComponent<ConvergenceCheckBehaviour>();
            checker.StartCheck(player, requestId, mappings, maxChecks: 60, timeoutSeconds: 3f,
                onSuccess: () =>
                {
                    // v2.0.6 P1-3 + v2.0.6.5：ACK 发送前注册 HotkeyResultPending，建立 (nonce, requestId) 关联
                    ClientHotkeyResultPending.Register(sessionNonce, requestId);
                    SendInventoryAppliedAck(sessionNonce, requestId);
                    ClientPendingState.ClearPending(sessionNonce, requestId);
                    RemoveConvergenceObject(go);
                    UnityEngine.Object.Destroy(go);

                    // v2.0.6 P1-3 + v2.0.6.5：启动 HotkeyResult 等待监视器
                    if (!_shuttingDown)
                    {
                        var waitGo = new GameObject("LaunchInventoryTidy_HotkeyResultWait");
                        UnityEngine.Object.DontDestroyOnLoad(waitGo);
                        lock (_convLock)
                        {
                            _activeConvergenceObjects.Add(waitGo);
                        }
                        var waiter = waitGo.AddComponent<HotkeyResultWaitBehaviour>();
                        waiter.StartWait(sessionNonce, requestId, timeoutSeconds: 3f);
                    }
                },
                onFailure: () =>
                {
                    LaunchInventoryTidyPlugin.Log?.LogWarning(
                        $"[TidyNet] 库存收敛超时 reqId={requestId}，部分快捷键可能未恢复");
                    ShowHotkeyRestoreWarning();
                    ClientPendingState.ClearPending(sessionNonce, requestId);
                    RemoveConvergenceObject(go);
                    UnityEngine.Object.Destroy(go);
                });
        }

        private static void RemoveConvergenceObject(GameObject go)
        {
            lock (_convLock)
            {
                _activeConvergenceObjects.Remove(go);
            }
        }

        /// <summary>
        /// v2.0.6.5 修订（Codex v2.0.6.4 审计 §五阻断项 5）：
        /// ACK 语义降级为"HotkeyFlowAck"（快捷键流程 ACK）。
        /// 客户端仅在收敛检查通过后（快捷键目标物品的 id 在新坐标匹配）后发送此 ACK，
        /// 表示"客户端已观察快捷键目标物品到达新坐标，可进行快捷键绑定"。
        /// 不再声称"全量库存已应用"。
        ///
        /// v2.0.6.6 修订（Codex v2.0.6.5 审计 §三 Medium 4 修复）：
        ///   - 删除"全量同步证据由服务器后续 TidyHotkeyResult 消息提供"的错误表述
        ///   - TidyHotkeyResult 仅含快捷键绑定统计（restoredCount/verifiedCount），不证明全量库存同步
        ///   - 全量库存同步证据必须由外部双端测试前后导出的全页
        ///     (x, y, rot, id, amount, quality, state) 多重集合对照证明
        ///
        /// 布局：[MSG_INVENTORY_APPLIED_ACK:1][sessionNonce:8][requestId:4]
        /// </summary>
        private static void SendInventoryAppliedAck(ulong sessionNonce, uint requestId)
        {
            byte[] payload;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(MSG_INVENTORY_APPLIED_ACK);
                w.Write(sessionNonce);  // v2.0.6.5：V3 nonce
                w.Write(requestId);
                payload = ms.ToArray();
            }
            ModTransport.SendToServer(ModChannels.TidyPage, payload, reliable: true);
            LaunchInventoryTidyPlugin.Log?.LogInfo(
                $"[TidyNet] -> 服务器: HotkeyFlowAck(nonce={sessionNonce:X16}, reqId={requestId})  [语义：快捷键流程 ACK，非全量库存已应用]");
        }

        private static void ShowHotkeyRestoreWarning()
        {
            LaunchInventoryTidyPlugin.Log?.LogWarning(
                "[TidyNet] ⚠ 整理完成，但部分快捷键未能恢复，请手动检查 3-0 数字键绑定");
        }

        // ─────────────────────────────────────────────────────────────
        // 服务器端：处理 InventoryAppliedAck
        // ─────────────────────────────────────────────────────────────

        private static void HandleInventoryAppliedAck(CSteamID sender, BinaryReader reader)
        {
            uint requestId = 0;
            ulong sessionNonce = 0;  // v2.0.6.5：V3 协议 nonce
            try
            {
                byte msgType;
                // v2.0.4 P1 + v2.0.6.5：固定头读取（含 nonce），短包走 server limiter 节流
                try
                {
                    msgType = reader.ReadByte();
                    if (msgType != MSG_INVENTORY_APPLIED_ACK) return;
                    sessionNonce = reader.ReadUInt64();  // v2.0.6.5：V3 nonce
                    requestId = reader.ReadUInt32();
                }
                catch (System.IO.EndOfStreamException)
                {
                    SecurityLogLimiter.LogRejection(sender, "short_ack_packet",
                        $"短包（ACK 固定头读取失败），拒绝 sender={(ulong)sender}");
                    return;
                }
                catch (System.Exception ex)
                {
                    SecurityLogLimiter.LogRejection(sender, "ack_header_read_error",
                        $"ACK 固定头读取异常: {ex.Message}，拒绝 sender={(ulong)sender}");
                    return;
                }

                // v2.0.6.5：nonce 必须非零
                if (sessionNonce == 0)
                {
                    SecurityLogLimiter.LogRejection(sender, "ack_invalid_nonce",
                        $"ACK sessionNonce=0，拒绝 sender={(ulong)sender} reqId={requestId}");
                    return;
                }

                // 校验：若还有尾随字节，拒绝（ACK 固定 13 字节：msgType+nonce+requestId）
                if (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    SecurityLogLimiter.LogRejection(sender, "ack_trailing_data",
                        $"ACK 收到尾随数据，拒绝 sender={(ulong)sender} reqId={requestId}");
                    return;
                }

                // v2.0.6.5：复合键查找事务
                var pending = TidyTransactionManager.Get(sender, sessionNonce, requestId);
                if (pending == null)
                {
                    LaunchInventoryTidyPlugin.Log?.LogInfo(
                        $"[TidyNet] 服务器收到 ACK (nonce={sessionNonce:X16}, reqId={requestId}) 但事务不存在或已过期，幂等忽略");
                    return;
                }

                Player player = ResolvePlayerBySteamId(sender);
                if (player?.inventory == null)
                {
                    SecurityLogLimiter.LogRejection(sender, "ack_no_player",
                        $"ACK 时找不到 Player sender={(ulong)sender}");
                    TidyTransactionManager.Remove(sender, sessionNonce, requestId);
                    return;
                }

                // v2.0.6.13 Round 9（Codex Round 8 §3.2）：ACK 恢复逐项异常隔离 + 完整指纹校验
                // 修订：
                //   - 使用 TryRestoreOneHotkeyOnMainThread 替换原 ID-only 比较
                //   - 每条 entry 独立 try-catch，失败加入 failedIndices 后继续其余项
                //   - 失败后立即 ServerClearItemHotkey（安全降级）
                //   - restoredCount = 绑定调用成功数；verifiedCount = 实际验证通过数
                int restoredCount = 0;
                int verifiedCount = 0;
                int clearedCount = 0;
                var failedHotkeyIndices = new List<byte>(pending.Entries.Count);
                bool canVerify = CanVerifyHotkeyState(player);

                for (int i = 0; i < pending.Entries.Count; i++)
                {
                    var entry = pending.Entries[i];
                    bool verified;
                    string restoreReason;

                    try
                    {
                        bool restored = TryRestoreOneHotkeyOnMainThread(player, entry, canVerify,
                            out verified, out restoreReason);

                        if (restored)
                        {
                            restoredCount++;
                            if (verified) verifiedCount++;
                        }
                        else
                        {
                            LaunchInventoryTidyPlugin.Log?.LogWarning(
                                $"[TidyNet] ACK hotkey={entry.HotkeyIndex} restore failed: {restoreReason}");
                            try { player.equipment.ServerClearItemHotkey(entry.HotkeyIndex); }
                            catch (Exception clearException)
                            {
                                LaunchInventoryTidyPlugin.Log?.LogWarning(
                                    $"[TidyNet] ACK hotkey={entry.HotkeyIndex} clear failed: {clearException.Message}");
                            }
                            clearedCount++;
                            failedHotkeyIndices.Add(entry.HotkeyIndex);
                        }
                    }
                    catch (Exception ex)
                    {
                        // v2.0.6 P1-4：逐项异常隔离，不中断后续项
                        LaunchInventoryTidyPlugin.Log?.LogWarning(
                            $"[TidyNet] ACK 恢复快捷键 {entry.HotkeyIndex} 异常: {ex.Message}");
                        try { player.equipment.ServerClearItemHotkey(entry.HotkeyIndex); }
                        catch (Exception clearException)
                        {
                            LaunchInventoryTidyPlugin.Log?.LogWarning(
                                $"[TidyNet] ACK hotkey={entry.HotkeyIndex} clear failed: {clearException.Message}");
                        }
                        clearedCount++;
                        failedHotkeyIndices.Add(entry.HotkeyIndex);
                    }
                }

                // finally：事务状态转换（无论上面是否有异常都必须执行）
                TidyTransactionManager.Remove(sender, sessionNonce, requestId);
                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[TidyNet] 服务器: ACK 处理完成 nonce={sessionNonce:X16} reqId={requestId} restored={restoredCount} verified={verifiedCount} cleared={clearedCount} canVerify={canVerify}");

                // v2.0.6 P1-5：业务不变量验证后发送
                // restoredCount + clearedCount == pending.Entries.Count
                // clearedCount == failedHotkeyIndices.Count
                // failedHotkeyIndices 全部 < 8 且唯一
                if (clearedCount != failedHotkeyIndices.Count)
                {
                    SecurityLogLimiter.LogException("ack_invariant_cleared_failed_mismatch",
                        $"ACK 不变量失败：clearedCount={clearedCount} != failedIndices.Count={failedHotkeyIndices.Count}", null);
                }
                if (restoredCount + clearedCount != pending.Entries.Count)
                {
                    SecurityLogLimiter.LogException("ack_invariant_total_mismatch",
                        $"ACK 不变量失败：restoredCount={restoredCount} + clearedCount={clearedCount} != entries.Count={pending.Entries.Count}", null);
                }
                var invariantCheck = new HashSet<byte>(failedHotkeyIndices);
                if (invariantCheck.Count != failedHotkeyIndices.Count)
                {
                    SecurityLogLimiter.LogException("ack_invariant_index_duplicate",
                        "ACK 不变量失败：failedHotkeyIndices 存在重复", null);
                }
                for (int i = 0; i < failedHotkeyIndices.Count; i++)
                {
                    if (failedHotkeyIndices[i] >= 8)
                    {
                        SecurityLogLimiter.LogException("ack_invariant_index_range",
                            $"ACK 不变量失败：failedHotkeyIndices[{i}]={failedHotkeyIndices[i]} >= 8", null);
                        break;
                    }
                }

                // v2.0.5 P1-4 + v2.0.6.5：发送结构化结果给客户端，携带 sessionNonce
                // 即使 clearedCount=0 也发送（明确告知"全部成功"），让客户端 UI 状态确定
                SendTidyHotkeyResult(sender, sessionNonce, requestId, restoredCount, verifiedCount, clearedCount, failedHotkeyIndices);
            }
            catch (Exception e)
            {
                SecurityLogLimiter.LogException("server_ack_crash",
                    $"HandleInventoryAppliedAck crash reqId={requestId}", e);
            }
        }

        /// <summary>
        /// v2.0.6 P1-4 新增：检测服务器端是否能验证快捷键最终绑定状态。
        /// DS 端 PlayerEquipment._hotkeys 为 null（仅本地玩家初始化），
        /// 此时只能报告"绑定调用成功"而非"已验证恢复"。
        /// </summary>
        private static bool CanVerifyHotkeyState(Player player)
        {
            if (player?.equipment == null) return false;
            try
            {
                // PlayerEquipment.hotkeys 是 HotkeyInfo[]，仅本地玩家初始化
                // DS 端服务器上的 player.equipment.hotkeys 为 null
                var hotkeys = player.equipment.hotkeys;
                return hotkeys != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// v2.0.6 P1-4 新增：验证快捷键最终绑定状态。
        /// 比对 hotkeys[hotkeyIndex] 的 id/page/x/y 与期望值。
        /// </summary>
        private static bool VerifyHotkeyBound(Player player, byte hotkeyIndex,
            byte expectedPage, byte expectedX, byte expectedY, ushort expectedItemId)
        {
            if (player?.equipment == null) return false;
            var hotkeys = player.equipment.hotkeys;
            if (hotkeys == null || hotkeyIndex >= hotkeys.Length) return false;
            var hk = hotkeys[hotkeyIndex];
            // HotkeyInfo.id == 0 表示未绑定
            if (hk.id != expectedItemId) return false;
            if (hk.page != expectedPage) return false;
            if (hk.x != expectedX) return false;
            if (hk.y != expectedY) return false;
            return true;
        }

        // ─────────────────────────────────────────────────────────────
        // v2.0.6.13 Round 9（Codex Round 8 §3.2）：ACK 阶段主线程断言 + 完整指纹校验 + 逐项诊断
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// v2.0.6.13 Round 9 新增：断言当前线程为插件主线程。
        /// ACK 处理必须在主线程执行（库存与快捷键 API 非线程安全）。
        /// </summary>
        private static bool IsPluginMainThread(out string reason)
        {
            int expected = LaunchInventoryTidyPlugin.MainThreadId;
            int current = System.Threading.Thread.CurrentThread.ManagedThreadId;
            if (expected != 0 && expected == current)
            {
                reason = null;
                return true;
            }

            reason = $"must run on LIT main thread (expected={expected}, current={current})";
            return false;
        }

        /// <summary>
        /// v2.0.6.13 Round 9 新增：按完整指纹解析并验证快捷键恢复目标。
        ///
        /// 校验链（任一失败即返回 false，reason 描述具体原因）：
        ///   1. inventory/items 非 null
        ///   2. hotkeyIndex 在 0..HOTKEY_COUNT-1 范围
        ///   3. NewPage 在可整理页范围且不越界 inventory.items.Length
        ///   4. NewX/NewY 在 page 范围内
        ///   5. page.getIndex(NewX, NewY) != byte.MaxValue（目标坐标有 ItemJar）
        ///   6. candidate.jar/item 非 null
        ///   7. candidate.x == NewX && candidate.y == NewY（不是被覆盖格）
        ///   8. 完整指纹匹配（id + amount + quality + state）
        ///   9. ItemAsset 非 null
        ///  10. ItemTool.checkUseable(NewPage, asset.id) 为 true
        /// </summary>
        private static bool TryResolveExactHotkeyTarget(PlayerInventory inventory,
            HotkeyRestoreEntry entry, out ItemJar jar, out ItemAsset asset, out string reason)
        {
            jar = null;
            asset = null;
            reason = null;

            if (inventory == null || inventory.items == null)
            {
                reason = "inventory/items is null";
                return false;
            }
            if (entry == null || entry.HotkeyIndex >= HotkeySnapshotUtil.HOTKEY_COUNT)
            {
                reason = "entry is null or hotkey index is out of range";
                return false;
            }
            if (entry.NewPage < HotkeySnapshotUtil.TIDYABLE_PAGE_MIN ||
                entry.NewPage > HotkeySnapshotUtil.TIDYABLE_PAGE_MAX ||
                entry.NewPage >= inventory.items.Length)
            {
                reason = "target page is out of range";
                return false;
            }

            Items page = inventory.items[entry.NewPage];
            if (page == null || entry.NewX >= page.width || entry.NewY >= page.height)
            {
                reason = "target coordinate is outside page";
                return false;
            }

            byte index = page.getIndex(entry.NewX, entry.NewY);
            if (index == byte.MaxValue)
            {
                reason = "no ItemJar at target coordinate";
                return false;
            }

            ItemJar candidate = page.getItem(index);
            if (candidate == null || candidate.item == null)
            {
                reason = "target ItemJar/item is null";
                return false;
            }
            // getIndex may identify a multi-cell jar from a covered cell. Binding must always use its origin.
            if (candidate.x != entry.NewX || candidate.y != entry.NewY)
            {
                reason = $"target is covered cell; jar origin=({candidate.x},{candidate.y})";
                return false;
            }

            ItemFingerprint actual = new ItemFingerprint(candidate.item);
            if (!actual.Equals(entry.ExpectedFingerprint))
            {
                reason = $"fingerprint mismatch expected=id:{entry.ExpectedFingerprint.Id}/amt:{entry.ExpectedFingerprint.Amount}/q:{entry.ExpectedFingerprint.Quality}, " +
                         $"actual=id:{actual.Id}/amt:{actual.Amount}/q:{actual.Quality}";
                return false;
            }

            ItemAsset candidateAsset = candidate.GetAsset();
            if (candidateAsset == null)
            {
                reason = "ItemAsset is null";
                return false;
            }
            if (!ItemTool.checkUseable(entry.NewPage, candidateAsset.id))
            {
                reason = $"ItemTool.checkUseable rejected id={candidateAsset.id} on page={entry.NewPage}";
                return false;
            }

            jar = candidate;
            asset = candidateAsset;
            return true;
        }

        /// <summary>
        /// v2.0.6.13 Round 9 新增：在主线程上恢复单条快捷键绑定。
        ///
        /// 流程：
        ///   1. 断言主线程
        ///   2. 断言 player/equipment 非 null
        ///   3. 调用 TryResolveExactHotkeyTarget 解析并校验目标
        ///   4. 调用 ServerBindItemHotkey
        ///   5. 若 canVerify，调用 VerifyHotkeyBound 验证最终绑定状态
        /// </summary>
        private static bool TryRestoreOneHotkeyOnMainThread(Player player, HotkeyRestoreEntry entry,
            bool canVerify, out bool verified, out string reason)
        {
            verified = false;
            reason = null;

            string threadReason;
            if (!IsPluginMainThread(out threadReason))
            {
                reason = threadReason;
                return false;
            }
            if (player == null || player.equipment == null)
            {
                reason = "player/equipment is null";
                return false;
            }

            ItemJar jar;
            ItemAsset asset;
            if (!TryResolveExactHotkeyTarget(player.inventory, entry, out jar, out asset, out reason))
                return false;

            player.equipment.ServerBindItemHotkey(
                entry.HotkeyIndex, asset, entry.NewPage, entry.NewX, entry.NewY);

            if (!canVerify)
                return true;

            verified = VerifyHotkeyBound(player, entry.HotkeyIndex,
                entry.NewPage, entry.NewX, entry.NewY, entry.ExpectedFingerprint.Id);
            if (!verified)
            {
                reason = "ServerBindItemHotkey returned but HotkeyInfo did not match expected id/page/x/y";
                return false;
            }
            return true;
        }

        /// <summary>
        /// v2.0.5 P1-4 新增，v2.0.6 P1-4 修订，v2.0.6.5 V3 协议 nonce 升级：发送快捷键恢复结果给客户端。
        /// 布局：[MSG_TIDY_HOTKEY_RESULT:1][sessionNonce:8][requestId:4][restoredCount:1][clearedCount:1][failedCount:1][verifiedCount:1][failedHotkeyIndices:failedCount*1]
        /// v2.0.6 P1-4：新增 verifiedCount 字段，区分"绑定调用成功"与"最终状态验证通过"。
        /// v2.0.6.5：新增 sessionNonce 字段，客户端据此定位 (nonce, requestId) 复合键。
        /// </summary>
        private static void SendTidyHotkeyResult(CSteamID target, ulong sessionNonce, uint requestId,
            int restoredCount, int verifiedCount, int clearedCount, List<byte> failedHotkeyIndices)
        {
            byte[] payload;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(MSG_TIDY_HOTKEY_RESULT);
                w.Write(sessionNonce);  // v2.0.6.5：V3 nonce
                w.Write(requestId);
                w.Write((byte)Math.Min(restoredCount, 255));
                w.Write((byte)Math.Min(clearedCount, 255));
                int failedCount = failedHotkeyIndices?.Count ?? 0;
                if (failedCount > 255) failedCount = 255;
                w.Write((byte)failedCount);
                w.Write((byte)Math.Min(verifiedCount, 255));
                if (failedHotkeyIndices != null)
                {
                    for (int i = 0; i < failedCount; i++)
                    {
                        w.Write(failedHotkeyIndices[i]);
                    }
                }
                payload = ms.ToArray();
            }
            ModTransport.SendToClient(target, ModChannels.TidyPage, payload, reliable: true);
            LaunchInventoryTidyPlugin.Log?.LogInfo(
                $"[TidyNet] -> 客机 {target}: TidyHotkeyResult(nonce={sessionNonce:X16}, reqId={requestId}, restored={restoredCount}, verified={verifiedCount}, cleared={clearedCount}, failed={failedHotkeyIndices?.Count ?? 0})");
        }

        // ─────────────────────────────────────────────────────────────
        // v2.0.6.8 新增（Codex v2.0.6.7 审计 §三 Medium 3 模板 C 修复）：
        // 服务器 -> 客户端的会话 challenge
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// v2.0.6.8 新增（模板 C）：服务器向客户端发送 session challenge。
        ///
        /// 由 ServerSessionRegistry.BeginSession 调用，在玩家连接时发送服务端生成的 64-bit token。
        /// 客户端收到后调用 ClientSessionNonce.ReplaceWithServerChallenge 替换临时 nonce。
        ///
        /// 布局：[MSG_SESSION_CHALLENGE:1][token:8]
        ///
        /// SP / listen host 本地分支：ModTransport.SendToClient 内部检测 IsLocalClient 并走 loopback，
        /// 同一进程内回环到客户端 handler，无需特殊处理。
        /// </summary>
        public static void SendSessionChallenge(CSteamID target, ulong token)
        {
            if (target == CSteamID.Nil) return;
            if (token == 0) return;

            byte[] payload;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(MSG_SESSION_CHALLENGE);
                w.Write(token);
                payload = ms.ToArray();
            }

            try
            {
                ModTransport.SendToClient(target, ModChannels.TidyPage, payload, reliable: true);
                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[TidyNet] -> 客机 {target}: SessionChallenge(token={token:X16})");
            }
            catch (Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogWarning(
                    $"[TidyNet] SendSessionChallenge 异常 target={(ulong)target} token={token:X16}: {e.Message}");
            }
        }

        /// <summary>
        /// v2.0.6.8 新增（模板 C）：客户端处理服务器的 session challenge。
        ///
        /// 收到服务端生成的 64-bit token，调用 ClientSessionNonce.ReplaceWithServerChallenge 替换临时 nonce。
        /// 后续整理请求将使用服务端签发的 token。
        /// </summary>
        private static void HandleSessionChallengeFromServer(BinaryReader reader)
        {
            try
            {
                byte msgType;
                ulong token;

                try
                {
                    msgType = reader.ReadByte();
                    if (msgType != MSG_SESSION_CHALLENGE) return;
                    token = reader.ReadUInt64();
                }
                catch (System.IO.EndOfStreamException)
                {
                    SecurityLogLimiter.LogClientRejection("short_session_challenge",
                        "短包（SessionChallenge 固定头读取失败），忽略");
                    return;
                }
                catch (System.Exception ex)
                {
                    SecurityLogLimiter.LogClientRejection("session_challenge_header_read_error",
                        $"SessionChallenge 固定头读取异常: {ex.Message}，忽略");
                    return;
                }

                // 校验尾随数据
                if (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    SecurityLogLimiter.LogClientRejection("session_challenge_trailing_data",
                        "SessionChallenge 收到尾随数据，忽略");
                    return;
                }

                // 替换客户端 nonce 为服务端签发 token
                ClientSessionNonce.ReplaceWithServerChallenge(token);

                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[TidyNet] <- 服务器: SessionChallenge(token={token:X16})，已替换为服务端签发 token");
            }
            catch (Exception e)
            {
                SecurityLogLimiter.LogException("client_session_challenge_crash",
                    "HandleSessionChallengeFromServer crash", e);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // CSteamID -> Player 反查
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// v2.0.5 P0-3：CSteamID -> Player 反查，含单机/监听服主机本地分支。
        ///
        /// Codex v2.0.4 第五次审计 §2 P0 指出：原实现只扫描 Provider.clients，
        /// 没有 Provider.server + Player.LocalPlayer 本地分支。即使 LaunchMultiplayerNet
        /// 正确实现本地客户端回环，单机和 SteamP2PFriends 主机的请求解析、ACK 处理仍可能找不到本地 Player。
        ///
        /// 解析顺序：
        /// 1. 若目标 SteamID 等于本机权威身份（Provider.server），且 Player.LocalPlayer 有效，返回 LocalPlayer
        /// 2. 否则扫描 Provider.clients 匹配远端玩家
        /// 3. 禁止把 remote SteamID 错映射到 LocalPlayer
        /// </summary>
        /// <summary>
        /// v2.0.5 P0-3：本地分支 - 单机或监听服主机
        /// Provider.isServer 且 Player.LocalPlayer 有效时，先验证目标是否为本机权威身份
        /// v2.0.6.10：从 private 改为 public，供 CommandTidyFaultInjectionTest 复用。
        /// </summary>
        public static Player ResolvePlayerBySteamId(CSteamID steamId)
        {
            if (steamId == CSteamID.Nil) return null;
            ulong targetId = (ulong)steamId;

            // v2.0.5 P0-3：本地分支 - 单机或监听服主机
            // Provider.isServer 且 Player.LocalPlayer 有效时，先验证目标是否为本机权威身份
            try
            {
                if (Provider.isServer)
                {
                    CSteamID localAuthorityId = Provider.server;
                    if (localAuthorityId != CSteamID.Nil && (ulong)localAuthorityId == targetId)
                    {
                        Player localPlayer = Player.LocalPlayer;
                        if (localPlayer != null)
                        {
                            return localPlayer;
                        }
                        // 本地玩家无效时继续扫描 clients（兜底）
                    }
                }
            }
            catch
            {
                // 任何异常都继续走 clients 扫描
            }

            // 远端分支：扫描 Provider.clients
            var clients = Provider.clients;
            if (clients == null) return null;

            for (int i = 0; i < clients.Count; i++)
            {
                SteamPlayer sp = clients[i];
                if (sp == null) continue;

                SteamPlayerID pid = sp.playerID;
                if (ReferenceEquals(pid, null)) continue;

                if ((ulong)pid.steamID == targetId)
                    return sp.player;
            }
            return null;
        }

        // ─────────────────────────────────────────────────────────────
        // 客户端 pending requestId 表
        // ─────────────────────────────────────────────────────────────

        internal static class ClientPendingState
        {
            private struct PendingEntry
            {
                public DateTime CreatedAt;
                public byte Page;
                public TidyMode Mode;
                public bool SortDescending;
            }

            /// <summary>v2.0.6.5：复合键 (sessionNonce, requestId)。</summary>
            private static readonly Dictionary<(ulong, uint), PendingEntry> _pending =
                new Dictionary<(ulong, uint), PendingEntry>();
            private static readonly object _lock = new object();

            private static readonly TimeSpan ENTRY_TTL = TimeSpan.FromSeconds(30);

            public static void SetPending(ulong sessionNonce, uint requestId, byte page, TidyMode mode, bool sortDescending)
            {
                lock (_lock)
                {
                    CleanupExpired();
                    _pending[(sessionNonce, requestId)] = new PendingEntry
                    {
                        CreatedAt = DateTime.UtcNow,
                        Page = page,
                        Mode = mode,
                        SortDescending = sortDescending,
                    };
                }
            }

            public static bool IsPending(ulong sessionNonce, uint requestId)
            {
                lock (_lock)
                {
                    CleanupExpired();
                    return _pending.ContainsKey((sessionNonce, requestId));
                }
            }

            public static void ClearPending(ulong sessionNonce, uint requestId)
            {
                lock (_lock) _pending.Remove((sessionNonce, requestId));
            }

            public static void ClearAll()
            {
                lock (_lock) _pending.Clear();
            }

            private static void CleanupExpired()
            {
                DateTime now = DateTime.UtcNow;
                var expired = new List<(ulong, uint)>();
                foreach (var kv in _pending)
                    if ((now - kv.Value.CreatedAt).TotalSeconds > ENTRY_TTL.TotalSeconds)
                        expired.Add(kv.Key);
                for (int i = 0; i < expired.Count; i++)
                    _pending.Remove(expired[i]);
            }
        }

        // ─────────────────────────────────────────────────────────────
        // v2.0.6 P1-3：客户端 HotkeyResult 等待状态
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// v2.0.6 P1-3 新增：客户端 ACK 发送后等待 HotkeyResult 的 requestId 关联状态。
        ///
        /// Codex v2.0.5 第六次审计 §2 Medium 指出：原实现 ACK 发送后立即清除 pending，
        /// 没有等待服务器 HotkeyResult 的状态关联。延迟、重复或错误 requestId 的旧结果
        /// 也会触发"快捷键未恢复"警告。
        ///
        /// 修订流程：
        ///   1. 客户端收敛成功 -> Register(requestId) -> 发送 ACK
        ///   2. 服务器返回 HotkeyResult -> 验证 requestId 匹配 -> ClearPending
        ///   3. HotkeyResultWaitBehaviour 超时 -> ClearPending + 提示"结果未知"
        ///   4. 未知/重复/过期 requestId 的 HotkeyResult 被拒绝
        /// </summary>
        internal static class ClientHotkeyResultPending
        {
            /// <summary>v2.0.6.5：复合键 (sessionNonce, requestId)。</summary>
            private static readonly Dictionary<(ulong, uint), DateTime> _pending =
                new Dictionary<(ulong, uint), DateTime>();
            private static readonly object _lock = new object();
            private static readonly TimeSpan TTL = TimeSpan.FromSeconds(10);

            public static void Register(ulong sessionNonce, uint requestId)
            {
                lock (_lock)
                {
                    CleanupExpired();
                    _pending[(sessionNonce, requestId)] = DateTime.UtcNow;
                }
            }

            public static bool IsPending(ulong sessionNonce, uint requestId)
            {
                lock (_lock)
                {
                    CleanupExpired();
                    return _pending.ContainsKey((sessionNonce, requestId));
                }
            }

            public static void ClearPending(ulong sessionNonce, uint requestId)
            {
                lock (_lock) _pending.Remove((sessionNonce, requestId));
            }

            public static void ClearAll()
            {
                lock (_lock) _pending.Clear();
            }

            private static void CleanupExpired()
            {
                DateTime now = DateTime.UtcNow;
                var expired = new List<(ulong, uint)>();
                foreach (var kv in _pending)
                    if ((now - kv.Value).TotalSeconds > TTL.TotalSeconds)
                        expired.Add(kv.Key);
                for (int i = 0; i < expired.Count; i++)
                    _pending.Remove(expired[i]);
            }
        }
    }
}
