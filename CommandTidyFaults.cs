using System.Text;
using Steamworks;
using SDG.Unturned;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// v2.0.3 P0-C4 新增：/tidy_faults 管理员命令 - 查询当前所有持久熔断记录。
    ///
    /// v2.0.4 P0-1 授权：显式调用 TidyAdminAuth.IsAuthorizedFaultAdmin，
    /// 不依赖 vanilla ChatManager.process 的 admin 守门（listen server 中普通远端玩家也能通过）。
    ///
    /// v2.0.5 P1-5：未授权日志走 SecurityLogLimiter 节流，防止未授权玩家制造日志洪水。
    ///
    /// 用法：/tidy_faults
    /// 输出：列出所有持久熔断玩家的 SteamID + 原因 + 时间
    /// </summary>
    public class CommandTidyFaults : Command
    {
        protected override void execute(CSteamID executorID, string parameter)
        {
            if (!Provider.isServer) return;

            // v2.0.4 P0-1：显式授权检查（fail-closed）
            if (!TidyAdminAuth.IsAuthorizedFaultAdmin(executorID))
            {
                // v2.0.6 P1-6：未授权日志 + 聊天响应共用 token bucket
                // Codex v2.0.5 第六次审计 §2 Medium 指出：原实现日志节流但聊天响应不节流，
                // 监听服普通玩家可持续触发服务器向其发送富文本拒绝消息，日志洪水转化为聊天/网络洪水。
                // 修订：LogRejection 返回 false 表示节流窗口内，跳过 ChatManager.say。
                bool respond = SecurityLogLimiter.LogRejection(executorID, "unauthorized_cmd_tidy_faults",
                    $"/tidy_faults 未授权拒绝：executor={(ulong)executorID} isListenHost={Provider.isClient}");
                if (respond)
                {
                    ChatManager.say(executorID,
                        "<color=#ff6666>[整理熔断] 未授权：仅主机或管理员可执行此命令</color>",
                        Palette.SERVER, true);
                }
                return;
            }

            var persistent = TidyFaultCircuit.GetPersistentSnapshot();
            if (persistent.Count == 0)
            {
                ChatManager.say(executorID,
                    "<color=#88ff88>[整理熔断] 当前无持久熔断记录</color>",
                    Palette.SERVER, true);
                return;
            }

            var sb = new StringBuilder(256);
            sb.Append("<color=#ffcc00>[整理熔断]</color> ");
            sb.Append("<color=#ff6666>持久熔断 ").Append(persistent.Count).Append(" 条：</color>\n");

            for (int i = 0; i < persistent.Count && i < 10; i++)
            {
                var r = persistent[i];
                string age = FormatAge(r.OpenedAt);
                sb.Append("<color=#aaaaaa>• </color>");
                sb.Append("<color=#ffffff>").Append(r.SteamId).Append("</color>");
                sb.Append("<color=#888888> (").Append(age).Append("前)</color>\n");
                string reason = r.Reason ?? "unknown";
                if (reason.Length > 80) reason = reason.Substring(0, 80) + "...";
                sb.Append("<color=#ff8888>  ").Append(reason).Append("</color>\n");
            }

            if (persistent.Count > 10)
            {
                sb.Append("<color=#888888>... 还有 ").Append(persistent.Count - 10).Append(" 条未显示</color>\n");
            }

            sb.Append("<color=#888888>使用 /tidy_unfault &lt;SteamID&gt; 解除</color>");

            ChatManager.say(executorID, sb.ToString(), Palette.SERVER, true);
        }

        private static string FormatAge(System.DateTime openedAt)
        {
            var delta = System.DateTime.UtcNow - openedAt;
            if (delta.TotalMinutes < 1) return $"{(int)delta.TotalSeconds}s";
            if (delta.TotalHours < 1) return $"{(int)delta.TotalMinutes}m";
            if (delta.TotalDays < 1) return $"{(int)delta.TotalHours}h";
            return $"{(int)delta.TotalDays}d";
        }

        public CommandTidyFaults(Local newLocalization)
        {
            localization = newLocalization;
            _command = "tidy_faults";
            _info = "查询当前所有持久整理熔断记录";
            _help = "用法: /tidy_faults - 列出所有 RestoreVerified=false 的持久熔断";
        }
    }

    /// <summary>
    /// v2.0.3 P0-C4 新增：/tidy_unfault &lt;SteamID&gt; 管理员命令 - 解除指定玩家的持久熔断。
    ///
    /// v2.0.4 P0-1 授权：显式调用 TidyAdminAuth.IsAuthorizedFaultAdmin，
    /// 不依赖 vanilla ChatManager.process 的 admin 守门。
    /// v2.0.5 P1-5：未授权日志走 SecurityLogLimiter 节流。
    ///
    /// 用法：/tidy_unfault 76561198000000000
    /// </summary>
    public class CommandTidyUnfault : Command
    {
        protected override void execute(CSteamID executorID, string parameter)
        {
            if (!Provider.isServer) return;

            // v2.0.4 P0-1：显式授权检查（fail-closed）
            if (!TidyAdminAuth.IsAuthorizedFaultAdmin(executorID))
            {
                // v2.0.6 P1-6：未授权日志 + 聊天响应共用 token bucket
                bool respond = SecurityLogLimiter.LogRejection(executorID, "unauthorized_cmd_tidy_unfault",
                    $"/tidy_unfault 未授权拒绝：executor={(ulong)executorID} isListenHost={Provider.isClient}");
                if (respond)
                {
                    ChatManager.say(executorID,
                        "<color=#ff6666>[整理熔断] 未授权：仅主机或管理员可执行此命令</color>",
                        Palette.SERVER, true);
                }
                return;
            }

            string param = (parameter ?? "").Trim();
            if (string.IsNullOrEmpty(param))
            {
                ChatManager.say(executorID,
                    "<color=#ff8888>[整理熔断] 用法: /tidy_unfault &lt;SteamID&gt;</color>",
                    Palette.SERVER, true);
                return;
            }

            // 解析 SteamID
            if (!ulong.TryParse(param, out ulong steamIdNum))
            {
                ChatManager.say(executorID,
                    $"<color=#ff8888>[整理熔断] 无效的 SteamID: {param}</color>",
                    Palette.SERVER, true);
                return;
            }

            CSteamID target = new CSteamID(steamIdNum);
            if (TidyFaultCircuit.TryClose(target, out var wasOpen))
            {
                ChatManager.say(executorID,
                    $"<color=#88ff88>[整理熔断] 已解除玩家 {steamIdNum} 的持久熔断</color>\n" +
                    $"<color=#aaaaaa>原熔断原因: {wasOpen.Reason}</color>",
                    Palette.SERVER, true);
            }
            else
            {
                ChatManager.say(executorID,
                    $"<color=#ff8888>[整理熔断] 玩家 {steamIdNum} 无持久熔断记录</color>",
                    Palette.SERVER, true);
            }
        }

        public CommandTidyUnfault(Local newLocalization)
        {
            localization = newLocalization;
            _command = "tidy_unfault";
            _info = "解除指定玩家的持久整理熔断";
            _help = "用法: /tidy_unfault <SteamID> - 清除该玩家的持久熔断记录";
        }
    }

    /// <summary>
    /// v2.0.5 P0-5 新增：/tidy_fault_recover 管理员命令 - 显式清除全局持久化降级状态。
    ///
    /// Codex v2.0.4 第五次静态审计 §2 P0 指出：
    ///   - TryClearDegraded 没有任何生产调用方或管理员命令
    ///   - 即使管理员修复文件，进程内降级状态也无法显式解除，只能重启
    ///   - 首次安装进入降级后同样没有恢复入口
    ///
    /// 本命令调用 TidyFaultCircuitPersistence.TryClearDegraded()，
    /// 执行完整 Load/Validate 后才清除降级。
    /// 禁止 /tidy_unfault 隐式清除全局降级（必须使用本命令）。
    ///
    /// v2.0.5 P1-5：未授权日志走 SecurityLogLimiter 节流。
    ///
    /// 用法：/tidy_fault_recover
    /// </summary>
    public class CommandTidyFaultRecover : Command
    {
        protected override void execute(CSteamID executorID, string parameter)
        {
            if (!Provider.isServer) return;

            // 显式授权检查（fail-closed）
            if (!TidyAdminAuth.IsAuthorizedFaultAdmin(executorID))
            {
                // v2.0.6 P1-6：未授权日志 + 聊天响应共用 token bucket
                bool respond = SecurityLogLimiter.LogRejection(executorID, "unauthorized_cmd_tidy_fault_recover",
                    $"/tidy_fault_recover 未授权拒绝：executor={(ulong)executorID} isListenHost={Provider.isClient}");
                if (respond)
                {
                    ChatManager.say(executorID,
                        "<color=#ff6666>[整理熔断] 未授权：仅主机或管理员可执行此命令</color>",
                        Palette.SERVER, true);
                }
                return;
            }

            // 检查当前是否处于降级状态
            if (!TidyFaultCircuitPersistence.GlobalFaultPersistenceDegraded)
            {
                ChatManager.say(executorID,
                    "<color=#88ff88>[整理熔断] 当前未处于持久化降级状态，无需恢复</color>",
                    Palette.SERVER, true);
                return;
            }

            // 调用 TryClearDegraded 执行完整 Load/Validate
            var result = TidyFaultCircuitPersistence.TryClearDegraded();
            if (result.Success)
            {
                ChatManager.say(executorID,
                    $"<color=#88ff88>[整理熔断] 已从持久化降级恢复</color>\n" +
                    $"<color=#aaaaaa>来源: {(result.FromBackup ? "备份文件" : "主文件")}，加载 {result.LoadedCount} 条持久熔断</color>",
                    Palette.SERVER, true);
            }
            else
            {
                ChatManager.say(executorID,
                    $"<color=#ff6666>[整理熔断] 恢复失败：{result.FailureReason ?? "未知原因"}</color>\n" +
                    $"<color=#ff8888>请检查 BepInEx/config/LaunchInventoryTidy/fault_scopes/ 下的 scope 文件后重试</color>",
                    Palette.SERVER, true);
            }
        }

        public CommandTidyFaultRecover(Local newLocalization)
        {
            localization = newLocalization;
            _command = "tidy_fault_recover";
            _info = "清除全局持久化降级状态（需先修复持久化文件）";
            _help = "用法: /tidy_fault_recover - 重新加载持久熔断文件，成功后解除全局降级";
        }
    }
}
