using SDG.Unturned;
using Steamworks;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// v2.0.4 P0-1：安全命令显式授权检查。
    ///
    /// Codex v2.0.3 第四次静态审计 §5.1 要求：命令入口必须显式解析执行者并强制授权，
    /// 不得依赖 vanilla ChatManager.process 的 admin 守门（listen server 中普通远端玩家也能通过）。
    ///
    /// 授权规则：
    /// - Dedicated Server：仅 executor.isAdmin=true 可用
    /// - Listen Server / 单机：仅主机 SteamID 可用（Provider.server 即主机 CSteamID）
    /// - 查不到执行者、SteamID 为 Nil、授权状态不明确：一律拒绝并审计记录
    ///
    /// v2.0.5 P1-5 修订：Codex v2.0.4 第五次审计 §2 P1 指出：原实现先要求
    /// PlayerTool.getSteamPlayer(executorId) 解析执行者，但本地主机身份可能未被 vanilla
    /// players 列表收录，存在"主机也被拒绝"的可用性风险。
    /// 修订：本地身份先于 PlayerTool 解析，DS 端再走 remote admin 验证。
    ///
    /// v2.0.6.1 第二阶段修订：原实现 `if (!Dedicator.IsDedicatedServer)` 在编译时绑定
    /// 客户端 Assembly-CSharp.dll 中 Dedicator.IsDedicatedServer 属性 getter（get_IsDedicatedServer），
    /// 但 U3DS 服务器 dll 中该成员是字段（field，无 getter 方法），运行时抛 MissingMethodException。
    /// 修订为 `if (Provider.isServer && Provider.isClient)` 识别 listen host / SP 模式：
    /// - Listen host / SP：Provider.isServer=true && Provider.isClient=true -> 本地分支
    /// - DS：Provider.isServer=true && Provider.isClient=false -> PlayerTool 验证
    /// 逻辑等价，无 Dedicator 依赖。
    /// </summary>
    public static class TidyAdminAuth
    {
        /// <summary>
        /// 检查执行者是否有权执行安全命令（/tidy_faults、/tidy_unfault、/tidy_fault_recover）。
        /// </summary>
        /// <param name="executorId">执行命令的玩家 SteamID</param>
        /// <returns>true=授权通过；false=拒绝</returns>
        public static bool IsAuthorizedFaultAdmin(CSteamID executorId)
        {
            if (!Provider.isServer) return false;
            if (executorId == CSteamID.Nil) return false;

            // v2.0.6.1 第二阶段：Listen Server / 单机本地分支优先（用 Provider.isClient 替代 !Dedicator.IsDedicatedServer）
            // 本地主机的 executorId 应等于 Provider.server（即主机 SteamID）。
            // 不依赖 PlayerTool.getSteamPlayer，因为 vanilla 玩家列表可能未收录本地主机。
            if (Provider.isClient)
            {
                try
                {
                    CSteamID hostSteamId = Provider.server;
                    if (hostSteamId != CSteamID.Nil && executorId == hostSteamId)
                    {
                        return true;
                    }
                    // 本地身份不匹配 -> fall through 到 fail-closed
                }
                catch
                {
                    // 任何异常都 fail-closed
                }
                return false;
            }

            // v2.0.5 P1-5：Dedicated Server 走 PlayerTool.getSteamPlayer + isAdmin
            SteamPlayer executor = PlayerTool.getSteamPlayer(executorId);
            if (executor == null) return false;

            return executor.isAdmin;
        }
    }
}
