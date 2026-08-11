#if TIDY_TEST_HARNESS
using System.Text;
using Steamworks;
using SDG.Unturned;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// v2.0.6.10 新增（Codex v2.0.6.9 审计 §三 P0-2 / §五阻断项 2）：
    /// /tidy_fault_injection_test 管理员命令 - 运行故障注入测试套件。
    ///
    /// 测试范围：
    ///   - 单页 removeItem 第 0 步故障
    ///   - 单页 removeItem 第 2 步故障
    ///   - 单页 addItem 第 0 步故障
    ///   - 单页 addItem 第 2 步故障
    ///   - 多页第二页故障（验证第一页按 post-commit 快照回滚）
    ///
    /// 断言：
    ///   - TryRollbackPageWithPreCheck 可见 prep.MutationJournal（非 null）
    ///   - 回滚后 (page, x, y, rot, id, amount, quality, state) 多重集合全量匹配
    ///   - 多页场景：第一页 PostCommitJars 非空，TryRollbackRangeWithPreCheck 按快照回滚
    ///
    /// 用法：/tidy_fault_injection_test
    /// 权限：仅主机或管理员可执行（fail-closed）
    /// </summary>
    public class CommandTidyFaultInjectionTest : Command
    {
        public CommandTidyFaultInjectionTest(Local newLocalization)
        {
            localization = newLocalization;
            _command = "tidy_fault_injection_test";
            _info = "运行故障注入测试套件（Codex v2.0.6.9 §三 P0-2）";
            _help = "用法：/tidy_fault_injection_test";
        }

        protected override void execute(CSteamID executorID, string parameter)
        {
            if (!Provider.isServer) return;

            // v2.0.6.10：显式授权检查（fail-closed）
            if (!TidyAdminAuth.IsAuthorizedFaultAdmin(executorID))
            {
                bool respond = SecurityLogLimiter.LogRejection(executorID, "unauthorized_cmd_tidy_fault_injection_test",
                    $"/tidy_fault_injection_test 未授权拒绝：executor={(ulong)executorID} isListenHost={Provider.isClient}");
                if (respond)
                {
                    ChatManager.say(executorID,
                        "<color=#ff6666>[故障注入测试] 未授权：仅主机或管理员可执行此命令</color>",
                        Palette.SERVER, true);
                }
                return;
            }

            // v2.0.6.10：使用 ManualTidyNetwork.ResolvePlayerBySteamId 统一解析 Player
            // 复用已有本地分支 + 远端扫描逻辑，避免 Provider.player 不存在错误
            Player player = ManualTidyNetwork.ResolvePlayerBySteamId(executorID);
            if (player == null || player.inventory == null)
            {
                ChatManager.say(executorID,
                    "<color=#ff6666>[故障注入测试] 未找到执行者的 PlayerInventory</color>",
                    Palette.SERVER, true);
                return;
            }

            // 主线程检查
            int currentThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            int mainThreadId = LaunchInventoryTidyPlugin.MainThreadId;
            if (currentThreadId != mainThreadId)
            {
                LaunchInventoryTidyPlugin.Log?.LogError(
                    $"[FaultInjection] /tidy_fault_injection_test 必须在主线程调用（current={currentThreadId}, main={mainThreadId}）");
                ChatManager.say(executorID,
                    "<color=#ff6666>[故障注入测试] 必须在主线程调用</color>",
                    Palette.SERVER, true);
                return;
            }

            ChatManager.say(executorID,
                "<color=#ffcc00>[故障注入测试] 开始运行测试套件，请查看日志...</color>",
                Palette.SERVER, true);

            // 运行测试
            var results = FaultInjectionTestRunner.RunAllTests(player.inventory, sortDescending: true);
            int passed = 0;
            int failed = 0;
            int skipped = 0;
            var sb = new StringBuilder(512);
            sb.Append("<color=#ffcc00>[故障注入测试结果]</color>\n");
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                string color;
                string status;
                switch (r.Verdict)
                {
                    case FaultInjectionTestRunner.TestVerdict.Pass:
                        color = "<color=#88ff88>"; status = "PASS"; passed++; break;
                    case FaultInjectionTestRunner.TestVerdict.SkippedInsufficientFixture:
                        color = "<color=#ffcc66>"; status = "SKIPPED"; skipped++; break;
                    default:
                        color = "<color=#ff6666>"; status = "FAIL"; failed++; break;
                }
                sb.Append(color).Append("• ").Append(r.TestName).Append(": ").Append(status).Append("</color>\n");
            }
            sb.Append($"<color=#ffcc00>总计：{passed} PASS / {failed} FAIL / {skipped} SKIPPED</color>");
            ChatManager.say(executorID, sb.ToString(), Palette.SERVER, true);

            // 详细日志
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                if (r.Verdict == FaultInjectionTestRunner.TestVerdict.Pass)
                {
                    LaunchInventoryTidyPlugin.Log?.LogInfo(
                        $"[FaultInjection] {r.TestName} PASS");
                }
                else if (r.Verdict == FaultInjectionTestRunner.TestVerdict.SkippedInsufficientFixture)
                {
                    LaunchInventoryTidyPlugin.Log?.LogWarning(
                        $"[FaultInjection] {r.TestName} SKIPPED: {r.FailureReason}");
                }
                else
                {
                    LaunchInventoryTidyPlugin.Log?.LogError(
                        $"[FaultInjection] {r.TestName} FAIL: {r.FailureReason}");
                }
            }
        }
    }
}
#endif
