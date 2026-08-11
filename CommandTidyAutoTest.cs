#if TIDY_TEST_HARNESS
using System.Text;
using Steamworks;
using SDG.Unturned;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// v2.0.6.13 新增（Codex v2.0.6.12 勘误复审与单机深测放行 §4 单机深度测试项目核对清单）：
    /// /tidy_auto_test 管理员命令 - 一键运行 SP-CONS/SP-HK/SP-FI/SP-SD 四类受控单机深度测试。
    ///
    /// 测试范围：
    ///   - SP-CONS：全页物品守恒（page 2-6 × 三模式 × 升降序，独立只读采样器）
    ///   - SP-HK：快捷键回归（三模式 × 升降序 × outMapping 验证）
    ///   - SP-FI：TestHarness 隔离故障注入（复用 FaultInjectionTestRunner.RunAllTests）
    ///   - SP-SD：关闭在途请求（MainThreadDispatcher + BeginQuiesce + CompleteShutdown）
    ///
    /// 测试完成后自动：
    ///   1. 写入 .audit/temp_states/auto_test_summary.json
    ///   2. 写入每个用例的 before/after JSON 快照
    ///   3. 调用 Provider.disconnect() + Application.Quit() 退出游戏
    ///
    /// 用法：/tidy_auto_test
    /// 权限：仅主机或管理员可执行（fail-closed）
    /// </summary>
    public class CommandTidyAutoTest : Command
    {
        public CommandTidyAutoTest(Local newLocalization)
        {
            localization = newLocalization;
            _command = "tidy_auto_test";
            _info = "一键运行 SP-CONS/SP-HK/SP-FI/SP-SD 四类受控单机深度测试（Codex v2.0.6.12 §4）";
            _help = "用法：/tidy_auto_test";
        }

        protected override void execute(CSteamID executorID, string parameter)
        {
            if (!Provider.isServer) return;

            // 显式授权检查（fail-closed）
            if (!TidyAdminAuth.IsAuthorizedFaultAdmin(executorID))
            {
                bool respond = SecurityLogLimiter.LogRejection(executorID, "unauthorized_cmd_tidy_auto_test",
                    $"/tidy_auto_test 未授权拒绝：executor={(ulong)executorID} isListenHost={Provider.isClient}");
                if (respond)
                {
                    ChatManager.say(executorID,
                        "<color=#ff6666>[自动化测试] 未授权：仅主机或管理员可执行此命令</color>",
                        Palette.SERVER, true);
                }
                return;
            }

            // 使用 ManualTidyNetwork.ResolvePlayerBySteamId 统一解析 Player
            Player player = ManualTidyNetwork.ResolvePlayerBySteamId(executorID);
            if (player == null || player.inventory == null)
            {
                ChatManager.say(executorID,
                    "<color=#ff6666>[自动化测试] 未找到执行者的 PlayerInventory</color>",
                    Palette.SERVER, true);
                return;
            }

            // 主线程检查
            int currentThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            int mainThreadId = LaunchInventoryTidyPlugin.MainThreadId;
            if (currentThreadId != mainThreadId)
            {
                LaunchInventoryTidyPlugin.Log?.LogError(
                    $"[AutoTest] /tidy_auto_test 必须在主线程调用（current={currentThreadId}, main={mainThreadId}）");
                ChatManager.say(executorID,
                    "<color=#ff6666>[自动化测试] 必须在主线程调用</color>",
                    Palette.SERVER, true);
                return;
            }

            ChatManager.say(executorID,
                "<color=#ffcc00>[自动化测试] 开始运行 SP-CONS/SP-HK/SP-FI/SP-SD 四类测试，请查看日志...</color>",
                Palette.SERVER, true);

            LaunchInventoryTidyPlugin.Log?.LogInfo(
                "[AutoTest] ===============================================");
            LaunchInventoryTidyPlugin.Log?.LogInfo(
                "[AutoTest] /tidy_auto_test 启动 - Codex v2.0.6.13 协程化 + 网络回环");
            LaunchInventoryTidyPlugin.Log?.LogInfo(
                $"[AutoTest] 执行者 SteamID={(ulong)executorID} 主线程={currentThreadId}");
            LaunchInventoryTidyPlugin.Log?.LogInfo(
                "[AutoTest] ===============================================");

            // v2.0.6.13：协程化启动，避免阻塞主线程
            AutoTestDriver.StartAllSuites(player, suites =>
            {
                int totalPass = 0, totalFail = 0, totalSkip = 0, totalBlock = 0;
                var sb = new StringBuilder(1024);
                sb.Append("<color=#ffcc00>[自动化测试结果]</color>\n");
                foreach (var s in suites)
                {
                    string color = s.Verdict == "PASS" ? "<color=#88ff88>"
                        : s.Verdict == "SKIPPED" ? "<color=#ffcc66>"
                        : s.Verdict == "BLOCKED" ? "<color=#ff9966>"
                        : "<color=#ff6666>";
                    sb.Append(color).Append("• ").Append(s.SuiteName).Append(": ").Append(s.Verdict)
                      .Append($" (pass={s.Passed}/fail={s.Failed}/skip={s.Skipped}/block={s.Blocked})</color>\n");
                    totalPass += s.Passed;
                    totalFail += s.Failed;
                    totalSkip += s.Skipped;
                    totalBlock += s.Blocked;
                }
                sb.Append($"<color=#ffcc00>总计：{totalPass} PASS / {totalFail} FAIL / {totalSkip} SKIPPED / {totalBlock} BLOCKED</color>");
                ChatManager.say(executorID, sb.ToString(), Palette.SERVER, true);

                LaunchInventoryTidyPlugin.Log?.LogInfo("[AutoTest] ===============================================");
                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[AutoTest] 全部套件完成：{totalPass} PASS / {totalFail} FAIL / {totalSkip} SKIPPED / {totalBlock} BLOCKED");
                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    "[AutoTest] 测试摘要已写入 .lit_autotest/auto_test_summary.json");
                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    "[AutoTest] completion.marker 已写入（供 run_tests.ps1 检测）");
                LaunchInventoryTidyPlugin.Log?.LogInfo("[AutoTest] ===============================================");
            });
        }
    }
}
#endif
