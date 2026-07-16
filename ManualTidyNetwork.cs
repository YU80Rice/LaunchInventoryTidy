using System;
using System.IO;
using LaunchMultiplayerNet;
using SDG.Unturned;
using Steamworks;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// 背包整理的双端自适应网络层。
    ///
    /// 协议（Channel 100 = ModChannels.TidyPage）：
    ///   客机 -> 服务器
    ///     [EModMessage.RequestTidyPage: byte][page: byte][sortDescending: bool][mode: byte]
    ///     page = 0xFF 表示整理全部 5 个多格页（SLOTS..PANTS）；
    ///     page ∈ [2..6] 表示仅整理指定页；
    ///     mode = 0 (MaxRects, C 优先) 或 1 (FFD, D 优先)。
    ///
    /// 服务器端处理：
    ///   1) 通过 sender CSteamID 在 Provider.clients 中反查 Player
    ///   2) 调 ManualTidyService.TidyAllPlayerPages 或 TidyPage 修改服务器端 inventory
    ///   3) Items.removeItem/addItem 触发 onItemAdded/onItemRemoved，PlayerInventory 订阅
    ///      这些事件做原生网络同步 -> 客机端自动收到 inventory 更新包，无需我们再发
    ///
    /// 安全性：
    ///   P2P 通道独立于 Unturned 反作弊，但本通道仅做"整理自己的背包"操作，
    ///   服务器只对 sender 自己的 Player.inventory 操作，不会越权修改他人背包。
    /// </summary>
    public static class ManualTidyNetwork
    {
        /// <summary>0xFF = 全部页的哨兵值。与 Unturned 的 page=255 "无指定页"语义一致。</summary>
        public const byte ALL_PAGES = 0xFF;

        /// <summary>由 Plugin.Awake 调用，注册服务器端通道处理器。</summary>
        public static void RegisterHandlers()
        {
            ModTransport.RegisterServerHandler(ModChannels.TidyPage, HandleRequestTidyPage);
            LaunchInventoryTidyPlugin.Log?.LogInfo(
                "[TidyNet] 已注册 channel=" + ModChannels.TidyPage + " 服务器端处理器");
        }

        // ─────────────────────────────────────────────────────────────
        // 客机端：发送请求
        // ─────────────────────────────────────────────────────────────

        /// <summary>客机端：请求服务器帮我整理全部 5 个多格页。</summary>
        public static void SendTidyAllRequest(bool sortDescending = true, TidyMode mode = TidyMode.MaxRects)
        {
            byte[] payload = ModTransport.BuildMessage(EModMessage.RequestTidyPage, w =>
            {
                w.Write(ALL_PAGES);
                w.Write(sortDescending);
                w.Write((byte)mode);
            });
            ModTransport.SendToServer(ModChannels.TidyPage, payload, reliable: true);
            LaunchInventoryTidyPlugin.Log?.LogInfo(
                $"[TidyNet] -> 服务器: RequestTidyPage(ALL, desc={sortDescending}, mode={mode})");
        }

        /// <summary>客机端：请求服务器帮我整理指定页。</summary>
        public static void SendTidyPageRequest(byte page, bool sortDescending = true,
                                               TidyMode mode = TidyMode.MaxRects)
        {
            byte[] payload = ModTransport.BuildMessage(EModMessage.RequestTidyPage, w =>
            {
                w.Write(page);
                w.Write(sortDescending);
                w.Write((byte)mode);
            });
            ModTransport.SendToServer(ModChannels.TidyPage, payload, reliable: true);
            LaunchInventoryTidyPlugin.Log?.LogInfo(
                $"[TidyNet] -> 服务器: RequestTidyPage(page={page}, desc={sortDescending}, mode={mode})");
        }

        // ─────────────────────────────────────────────────────────────
        // 服务器端：处理请求
        // ─────────────────────────────────────────────────────────────

        private static void HandleRequestTidyPage(CSteamID sender, BinaryReader reader)
        {
            try
            {
                // 读取并校验消息类型（BuildMessage 写入了 EModMessage 字节，必须先消费）
                byte msgType = reader.ReadByte();
                if (msgType != (byte)EModMessage.RequestTidyPage)
                {
                    LaunchInventoryTidyPlugin.Log?.LogWarning(
                        $"[TidyNet] 收到未知消息类型 {msgType}，忽略");
                    return;
                }

                byte page = reader.ReadByte();
                bool sortDescending = reader.ReadBoolean();
                // 向后兼容：v1.4 前无 mode 字节。若客户端是 v1.3，读不到 mode 时回退默认。
                TidyMode mode = TidyMode.MaxRects;
                try { mode = (TidyMode)reader.ReadByte(); }
                catch { LaunchInventoryTidyPlugin.Log?.LogWarning("[TidyNet] 客户端协议较旧（无 mode 字节），回退 MaxRects"); }

                Player player = ResolvePlayerBySteamId(sender);
                if (player?.inventory == null)
                {
                    LaunchInventoryTidyPlugin.Log?.LogWarning(
                        $"[TidyNet] 收到 RequestTidyPage 但 sender {(ulong)sender} 无对应 Player");
                    return;
                }

                if (page == ALL_PAGES)
                {
                    ManualTidyService.TidyAllPlayerPages(player.inventory, sortDescending, mode);
                    LaunchInventoryTidyPlugin.Log?.LogInfo(
                        $"[TidyNet] 服务器: 已为 sender={(ulong)sender} 整理全部页 (desc={sortDescending}, mode={mode})");
                }
                else
                {
                    // page ∈ [SLOTS..STORAGE] = [2..7]，其中 page=7=STORAGE 是容器页
                    // （储物箱/展示柜/车辆后备箱）。服务端通过 player.inventory.items[STORAGE]
                    // 获取 Items 实例--该引用指向当前打开的 InteractableStorage.items，
                    // 服务端权威修改后触发 onStateUpdated 自动同步给所有客机。
                    if (page < PlayerInventory.SLOTS || page > PlayerInventory.STORAGE)
                    {
                        LaunchInventoryTidyPlugin.Log?.LogWarning(
                            $"[TidyNet] 收到非法 page={page}，忽略");
                        return;
                    }
                    // TidyPage(byte, bool) 便捷入口使用 Player.LocalPlayer.inventory
                    // 但服务器端 Player.LocalPlayer 不一定是 sender！必须用 sender 的 inventory。
                    // 调用 Items 重载避免误用 LocalPlayer。
                    Items items = player.inventory.items[page];
                    // 诊断日志：打印 items 实际状态，便于排查"容器页整理无效"类问题
                    int itemsW = items?.width ?? 0;
                    int itemsH = items?.height ?? 0;
                    int itemsCount = items?.getItemCount() ?? 0;
                    LaunchInventoryTidyPlugin.Log?.LogInfo(
                        $"[TidyNet] 服务器诊断: sender={(ulong)sender} page={page} " +
                        $"items={(items == null ? "null" : "Items")} width={itemsW} height={itemsH} count={itemsCount}");
                    ManualTidyService.TidyPage(items, page, sortDescending, mode);
                    LaunchInventoryTidyPlugin.Log?.LogInfo(
                        $"[TidyNet] 服务器: 已为 sender={(ulong)sender} 整理 page={page} (desc={sortDescending}, mode={mode})");
                }
            }
            catch (Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogError(
                    $"[TidyNet] HandleRequestTidyPage crash: {e}");
            }
        }

        // ─────────────────────────────────────────────────────────────
        // CSteamID -> Player 反查
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 通过 CSteamID 在 Provider.clients 中反查对应 Player 实例。
        /// 依据：SteamPlayer.playerID.steamID 是 CSteamID（SteamPlayerID.cs:11-12），
        /// CSteamID 仅有显式 ulong 转换（CSteamID.cs:251-256）。
        /// </summary>
        private static Player ResolvePlayerBySteamId(CSteamID steamId)
        {
            var clients = Provider.clients;
            if (clients == null) return null;

            ulong targetId = (ulong)steamId;
            for (int i = 0; i < clients.Count; i++)
            {
                SteamPlayer sp = clients[i];
                if (sp == null) continue;

                // SteamPlayerID 重载了 == 运算符但未做 null 检查（SteamPlayerID.cs:136-139），
                // 直接用 sp.playerID == null 会触发 NRE。必须用 ReferenceEquals 判空。
                SteamPlayerID pid = sp.playerID;
                if (ReferenceEquals(pid, null)) continue;

                if ((ulong)pid.steamID == targetId)
                    return sp.player;
            }
            return null;
        }
    }
}
