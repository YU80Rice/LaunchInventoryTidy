#if TIDY_TEST_HARNESS
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using SDG.Unturned;
using Steamworks;
using UnityEngine;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// v2.0.6.13 Codex 第二轮复审 §3.3-§3.4 重构：
    /// 严格通过状态机 + 真实事务验证 + 独立全页快照。
    ///
    /// v2.0.6.13 第一轮缺陷（Codex 第二轮 §1 Critical）：
    ///   - allPass 接受 SKIPPED；Rejected 被转换为 SKIPPED；fixture 不足也 SKIPPED
    ///   - SP-HK 未验证键 3/7 绑定，空快捷键可 PASS
    ///   - SP-SD 用测试 lambda Cancel，不是真实 RequestAdmissionStore.CancelNew
    ///   - SP-FI 复用 FaultInjectionTestRunner fingerprint，非独立全页证据
    ///
    /// 第二轮修复：
    ///   - AreAllRequiredSuitesPass：所有必需套件必须 Verdict=PASS + Failed=0 + Skipped=0 + Blocked=0
    ///   - SP-HK：FixtureValidator.TryCaptureRequiredHotkeys 前后对比键 3/7
    ///   - SP-SD：TrySendTidyRequest 真实 admission + ShutdownBarrier 屏障 + 真实 ledger/lease/pending 验证
    ///   - SP-FI：IndependentSnapshot.CaptureAllPages 独立全页证据；FI SKIPPED -> SP-FI BLOCKED
    ///   - Rejected/SendFailed/Timeout -> FAIL（不是 SKIPPED）
    /// </summary>
    internal static class AutoTestDriver
    {
        private const float RequestIntervalSeconds = 2.5f;
        private const float ReplyTimeoutSeconds = 5.0f;
        private const float BarrierWaitTimeoutSeconds = 3.0f;
        private const string StateExportDirName = ".lit_autotest";
        private const string SummaryFileName = "auto_test_summary.json";
        private const string CompletionMarkerFileName = "completion.marker";
        private const string ExpectedHarnessHashFileName = "expected-harness-hash.txt";

        private static string GetPluginDirectory()
        {
            try
            {
                var asm = typeof(LaunchInventoryTidyPlugin).Assembly;
                string codeLocation = asm.Location;
                if (!string.IsNullOrEmpty(codeLocation) && File.Exists(codeLocation))
                {
                    return Path.GetDirectoryName(codeLocation);
                }
            }
            catch { }
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private static string StateExportDir => Path.Combine(GetPluginDirectory(), StateExportDirName);
        private static string SummaryFile => Path.Combine(StateExportDir, SummaryFileName);
        private static string CompletionMarkerFile => Path.Combine(StateExportDir, CompletionMarkerFileName);
        private static string ExpectedHarnessHashFile => Path.Combine(StateExportDir, ExpectedHarnessHashFileName);

        /// <summary>
        /// v2.0.6.13 Round 7 AT-FIX-04：运行时 DLL SHA-256 自校验。
        /// 从 expected-harness-hash.txt 读取期望哈希，与 typeof(LaunchInventoryTidyPlugin).Assembly
        /// 的实际 SHA-256 比较。返回 true 表示身份一致，可以继续测试；返回 false 表示身份不匹配。
        /// </summary>
        private static bool VerifyHarnessIdentity(out string expectedHash, out string actualHash, out string reason)
        {
            expectedHash = null;
            actualHash = null;
            reason = null;

            try
            {
                // 1. 读取期望哈希文件
                if (!File.Exists(ExpectedHarnessHashFile))
                {
                    reason = "期望哈希文件不存在: " + ExpectedHarnessHashFileName;
                    return false;
                }

                string rawExpected = File.ReadAllText(ExpectedHarnessHashFile).Trim();
                if (rawExpected.Length == 0)
                {
                    reason = "期望哈希文件为空";
                    return false;
                }
                expectedHash = rawExpected.ToUpperInvariant();

                // 2. 计算实际程序集 SHA-256
                string assemblyPath = typeof(LaunchInventoryTidyPlugin).Assembly.Location;
                if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
                {
                    reason = "无法定位插件程序集文件";
                    return false;
                }

                using (var sha = SHA256.Create())
                using (var fs = File.OpenRead(assemblyPath))
                {
                    byte[] hash = sha.ComputeHash(fs);
                    actualHash = BitConverter.ToString(hash).Replace("-", "").ToUpperInvariant();
                }

                // 3. 比较（大小写不敏感）
                if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "运行时 DLL SHA-256 与期望值不匹配";
                    return false;
                }

                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[AutoTest] HARNESS-IDENTITY: OK (SHA-256={actualHash})");
                return true;
            }
            catch (Exception e)
            {
                reason = "VerifyHarnessIdentity 异常: " + e.Message;
                return false;
            }
        }

        // ===== 测试结果结构 =====
        public sealed class TestSuiteResult
        {
            public string SuiteName;
            public string Verdict; // PASS / FAIL / SKIPPED / BLOCKED
            public int TotalCases;
            public int Passed;
            public int Failed;
            public int Skipped;
            public int Blocked;
            public List<TestCaseResult> Cases = new List<TestCaseResult>();
            public string FailureReason;
        }

        public sealed class TestCaseResult
        {
            public string CaseName;
            public string Verdict; // PASS / FAIL / SKIPPED / BLOCKED
            public string BeforeJsonPath;
            public string AfterJsonPath;
            public string BeforeSha256;
            public string AfterSha256;
            public bool ConservationPassed;
            public bool LayoutValid;
            public string FailureReason;
            public uint RequestId;
            public string CommitResult; // Committed / Rejected / CriticalFailure / Timeout / SendFailed
            public string HotkeySummary; // restored=X verified=Y cleared=Z failed=W
            // v2.0.6.13 第五轮 §3.2：精确布局回滚 + 稳定内容哈希
            public bool ExactLayoutRestored;
            public string BeforeContentSha256;
            public string AfterContentSha256;
        }

        // ===== 必需套件名称 =====
        private static readonly string[] RequiredSuites = { "SP-CONS", "SP-HK", "SP-FI", "SP-SD" };

        // ===== 公共入口 =====

        public static void StartAllSuites(Player player, Action<List<TestSuiteResult>> onComplete)
        {
            if (player == null)
            {
                onComplete?.Invoke(new List<TestSuiteResult>());
                return;
            }

            var go = new GameObject("LaunchInventoryTidy_AutoTestHost");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var host = go.AddComponent<AutoTestHostBehaviour>();
            host.StartCoroutine(RunAllSuitesCoroutine(player, suites =>
            {
                onComplete?.Invoke(suites);
                UnityEngine.Object.Destroy(go);
            }));
        }

        public static IEnumerator RunAllSuitesCoroutine(Player player, Action<List<TestSuiteResult>> onComplete)
        {
            var suites = new List<TestSuiteResult>();

            // v2.0.6.13 Round 7 AT-FIX-04：运行时 DLL SHA-256 自校验。
            // 脚本写入期望 TestHarness SHA-256；插件从 typeof(LaunchInventoryTidyPlugin).Assembly.Location
            // 重算 SHA-256，不相等时写 HARNESS-IDENTITY: BLOCKED、completion.marker success=false，并直接退出。
            if (!VerifyHarnessIdentity(out string expectedHash, out string actualHash, out string identityReason))
            {
                LaunchInventoryTidyPlugin.Log?.LogError(
                    $"[AutoTest] HARNESS-IDENTITY: BLOCKED - {identityReason} (expected={expectedHash}, actual={actualHash})");
                suites.Add(new TestSuiteResult
                {
                    SuiteName = "HarnessIdentity",
                    Verdict = "BLOCKED",
                    FailureReason = $"HARNESS-IDENTITY: BLOCKED - {identityReason}",
                });
                WriteSummary(suites);
                WriteCompletionMarker(false, "HARNESS-IDENTITY: BLOCKED - " + identityReason);
                yield return QuitGameCoroutine();
                onComplete?.Invoke(suites);
                yield break;
            }

            if (player == null || player.inventory == null)
            {
                suites.Add(new TestSuiteResult
                {
                    SuiteName = "RunnerSelfCheck",
                    Verdict = "FAIL",
                    FailureReason = "Player or PlayerInventory is null",
                });
                WriteSummary(suites);
                WriteCompletionMarker(false, "Player or PlayerInventory is null");
                yield return QuitGameCoroutine();
                onComplete?.Invoke(suites);
                yield break;
            }

            int currentThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            int mainThreadId = LaunchInventoryTidyPlugin.MainThreadId;
            if (currentThreadId != mainThreadId)
            {
                suites.Add(new TestSuiteResult
                {
                    SuiteName = "RunnerSelfCheck",
                    Verdict = "FAIL",
                    FailureReason = $"Not on main thread: current={currentThreadId}, main={mainThreadId}",
                });
                WriteSummary(suites);
                WriteCompletionMarker(false, "Not on main thread");
                yield return QuitGameCoroutine();
                onComplete?.Invoke(suites);
                yield break;
            }

            EnsureStateExportDir();
            NetworkTestProbe.Reset();

            // TestHarness 夹具覆盖所有会触碰玩家库存的用例。这样即使玩家在隔离档中
            // 清空了物品，SP-CONS / SP-HK / SP-FI 仍使用同一套可恢复的受控库存。
            TestFixtureSession fixture = null;
            string fixtureReason = null;
            bool fixtureCreated = false;
            try
            {
                fixtureCreated = TestFixtureSession.TryCreate(player, out fixture, out fixtureReason);
            }
            catch (Exception e)
            {
                fixtureReason = $"TestFixtureSession.TryCreate 异常: {e.GetType().Name}: {e.Message}";
                LaunchInventoryTidyPlugin.Log?.LogError($"[AutoTest] {fixtureReason}");
            }

            if (!fixtureCreated)
            {
                // 夹具建立失败：所有库存相关套件都不得退回到玩家的自然库存。
                AddBlockedSuite(suites, "SP-CONS", "测试夹具建立失败：" + fixtureReason);
                AddBlockedSuite(suites, "SP-HK", "测试夹具建立失败：" + fixtureReason);
                AddBlockedSuite(suites, "SP-FI", "测试夹具建立失败：" + fixtureReason);
            }
            else
            {
                try
                {
                    // 1. SP-CONS：全页物品守恒（网络路径）
                    LogSuiteStart("SP-CONS");
                    yield return RunSpConsCoroutine(player.inventory, suites);
                    LogSuiteEnd("SP-CONS", suites);

                    // 2. SP-HK：快捷键回归（网络路径 + 键 3/7 验证）
                    LogSuiteStart("SP-HK");
                    yield return RunSpHkCoroutine(player, fixture, suites);
                    LogSuiteEnd("SP-HK", suites);

                    // 3. SP-FI：也必须使用受控夹具。若在回收后才执行，清空库存的
                    // 隔离档会把故障注入错误地变成 fixture 不足。
                    LogSuiteStart("SP-FI");
                    var spFi = RunSpFi(player.inventory);
                    suites.Add(spFi);
                    LogSuiteEnd("SP-FI", spFi);
                }
                finally
                {
                    // v2.0.6.13 第五轮 §3.3：恢复原始库存和快捷键，并验证 SameExactLayout
                    bool restored = false;
                    try
                    {
                        restored = fixture.RestoreOriginalInventoryAndHotkeys();
                    }
                    catch (Exception e)
                    {
                        LaunchInventoryTidyPlugin.Log?.LogError(
                            $"[AutoTest] TestFixtureSession.Restore 异常: {e.GetType().Name}: {e.Message}");
                    }

                    try
                    {
                        var afterRestore = IndependentSnapshot.CaptureAllPages(player.inventory);
                        bool layoutMatch = IndependentSnapshot.SameExactLayout(fixture.OriginalSnapshot, afterRestore);
                        if (!restored || !layoutMatch)
                        {
                            LaunchInventoryTidyPlugin.Log?.LogError(
                                $"[AutoTest] TestFixtureSession 回收失败：restored={restored}, layoutMatch={layoutMatch}；隔离存档不得继续使用");
                            MarkFixtureDependentSuitesFailed(
                                suites, "TestFixtureSession 回收失败：原始库存未完全恢复");
                        }
                    }
                    catch (Exception e)
                    {
                        LaunchInventoryTidyPlugin.Log?.LogError(
                            $"[AutoTest] TestFixtureSession 回收验证异常: {e.GetType().Name}: {e.Message}");
                        MarkFixtureDependentSuitesFailed(
                            suites, "TestFixtureSession 回收验证异常：" + e.GetType().Name);
                    }
                }
            }

            // 4. SP-SD：关闭在途请求（真实 admission + ShutdownBarrier）
            LogSuiteStart("SP-SD");
            yield return RunSpSdCoroutine(player, suites);
            LogSuiteEnd("SP-SD", suites);

            WriteSummary(suites);

            // v2.0.6.13 第二轮 §3.3：严格通过状态机
            bool allPass = AreAllRequiredSuitesPass(suites, out string failReason);
            WriteCompletionMarker(allPass, allPass ? "all required suites PASS" : failReason);
            yield return QuitGameCoroutine();
            onComplete?.Invoke(suites);
        }

        // ===== §3.3 严格通过状态机 =====

        /// <summary>
        /// v2.0.6.13 第五轮 §3.3：夹具建立失败时，将套件标记为 BLOCKED。
        /// </summary>
        private static void AddBlockedSuite(List<TestSuiteResult> suites, string suiteName, string reason)
        {
            var suite = new TestSuiteResult
            {
                SuiteName = suiteName,
                Verdict = "BLOCKED",
                FailureReason = reason,
                TotalCases = 1,
                Blocked = 1,
            };
            suite.Cases.Add(new TestCaseResult
            {
                CaseName = "FixtureValidation",
                Verdict = "BLOCKED",
                FailureReason = reason,
            });
            suites.Add(suite);
            LaunchInventoryTidyPlugin.Log?.LogError($"[AutoTest] {suiteName} BLOCKED: {reason}");
        }

        /// <summary>
        /// 夹具恢复失败意味着测试改变了隔离档却无法证明回收完整；所有依赖夹具的
        /// 套件均失效。保留原用例结果，同时追加显式失败用例，避免伪造计数。
        /// </summary>
        private static void MarkFixtureDependentSuitesFailed(List<TestSuiteResult> suites, string reason)
        {
            if (suites == null) return;

            string[] names = { "SP-CONS", "SP-HK", "SP-FI" };
            for (int i = 0; i < names.Length; i++)
            {
                TestSuiteResult suite = suites.Find(s => s != null && s.SuiteName == names[i]);
                if (suite == null)
                {
                    suite = new TestSuiteResult { SuiteName = names[i] };
                    suites.Add(suite);
                }

                suite.Cases.Add(new TestCaseResult
                {
                    CaseName = "FixtureRestore",
                    Verdict = "FAIL",
                    FailureReason = reason,
                });
                suite.TotalCases++;
                suite.Failed++;
                suite.Verdict = "FAIL";
                suite.FailureReason = reason;
            }
        }

        /// <summary>
        /// v2.0.6.13 第二轮 §3.3 蓝图：所有必需套件必须 Verdict=PASS + Failed=0 + Skipped=0 + Blocked=0。
        /// 任何 SKIPPED/BLOCKED/Rejected/Timeout 都令 success=false。
        /// </summary>
        private static bool AreAllRequiredSuitesPass(List<TestSuiteResult> suites, out string failure)
        {
            foreach (string name in RequiredSuites)
            {
                TestSuiteResult suite = suites.Find(s => s.SuiteName == name);
                if (suite == null)
                {
                    failure = name + " suite is missing";
                    return false;
                }
                if (suite.Verdict != "PASS" || suite.Failed != 0 || suite.Skipped != 0 || suite.Blocked != 0)
                {
                    failure = $"{name} is not a complete PASS (verdict={suite.Verdict}, " +
                        $"failed={suite.Failed}, skipped={suite.Skipped}, blocked={suite.Blocked})";
                    return false;
                }
            }
            failure = null;
            return true;
        }

        // ===== SP-CONS：全页物品守恒（网络路径）=====

        private static IEnumerator RunSpConsCoroutine(PlayerInventory inv, List<TestSuiteResult> suites)
        {
            var suite = new TestSuiteResult { SuiteName = "SP-CONS", Verdict = "PASS" };
            suites.Add(suite);

            if (!FixtureValidator.TryValidateAllRequiredShapes(inv, out string fixtureFailure))
            {
                suite.Verdict = "BLOCKED";
                suite.FailureReason = "Fixture 不满足：" + fixtureFailure;
                suite.Cases.Add(new TestCaseResult
                {
                    CaseName = "FixtureValidation",
                    Verdict = "BLOCKED",
                    FailureReason = suite.FailureReason,
                });
                suite.TotalCases = 1;
                suite.Blocked = 1;
                yield break;
            }

            var modes = new[] { TidyMode.SameType, TidyMode.MaxRects, TidyMode.FFD };
            var descOptions = new[] { true, false };

            foreach (var mode in modes)
            {
                foreach (var desc in descOptions)
                {
                    string caseName = $"SPCONS_{mode}_Desc{desc}";
                    var tc = new TestCaseResult { CaseName = caseName, Verdict = "PASS" };
                    yield return RunSpConsCaseCoroutine(inv, mode, desc, caseName, tc);
                    suite.Cases.Add(tc);
                    suite.TotalCases++;
                    UpdateSuiteCounters(suite, tc);
                    yield return new WaitForSecondsRealtime(RequestIntervalSeconds);
                }
            }
        }

        private static IEnumerator RunSpConsCaseCoroutine(PlayerInventory inv, TidyMode mode, bool desc,
            string caseName, TestCaseResult tc)
        {
            var beforeSnap = IndependentSnapshot.CaptureAllPages(inv);
            tc.BeforeJsonPath = IndependentSnapshot.WriteCanonicalJson("SPCONS", caseName + "_before", beforeSnap);
            tc.BeforeSha256 = IndependentSnapshot.ComputeFileSha256(tc.BeforeJsonPath);

            // v2.0.6.13 Round 9（Codex Round 8 §3.3 HK-CROSS-01）：
            // 在 TrySendTidyRequest 之前捕获必需的快捷键 3/7 绑定 + 实例级指纹。
            // 若 fixture 不满足（SP-CONS 整理后快捷键被清除），直接 FAIL，
            // 不通过重绑掩盖已经发生的恢复失败。
            Dictionary<byte, FixtureValidator.BoundItemFingerprint> beforeHotkeys;
            string hotkeyCaptureReason;
            if (!FixtureValidator.TryCaptureRequiredHotkeys(out beforeHotkeys, out hotkeyCaptureReason))
            {
                tc.Verdict = "FAIL";
                tc.CommitResult = "PreconditionLost";
                tc.FailureReason = "SP-CONS before hotkey capture failed: " + hotkeyCaptureReason;
                yield break;
            }

            bool sent = ManualTidyNetwork.TrySendTidyRequest(0xFF, mode, desc, out uint requestId);
            tc.RequestId = requestId;
            if (!sent)
            {
                tc.Verdict = "FAIL";
                tc.CommitResult = "SendFailed";
                tc.FailureReason = "TrySendTidyRequest 返回 false（未收到 challenge 或队列满）";
                yield break;
            }

            yield return WaitForReplyOrTimeout(requestId, ReplyTimeoutSeconds);

            if (!NetworkTestProbe.TryGet(requestId, out var commit, out var hotkey))
            {
                tc.Verdict = "FAIL";
                tc.CommitResult = "Timeout";
                tc.FailureReason = $"等待 {ReplyTimeoutSeconds}s 未收到网络回包";
                yield break;
            }

            tc.CommitResult = commit.ToString();
            tc.HotkeySummary = $"restored={hotkey.RestoredCount} verified={hotkey.VerifiedCount} cleared={hotkey.ClearedCount} failed={hotkey.FailedCount}";

            var afterSnap = IndependentSnapshot.CaptureAllPages(inv);
            tc.AfterJsonPath = IndependentSnapshot.WriteCanonicalJson("SPCONS", caseName + "_after", afterSnap);
            tc.AfterSha256 = IndependentSnapshot.ComputeFileSha256(tc.AfterJsonPath);

            tc.ConservationPassed = IndependentSnapshot.SameItemMultiset(beforeSnap, afterSnap);
            tc.LayoutValid = IndependentSnapshot.AllPagesInBoundsAndNonOverlapping(afterSnap);

            if (commit == TidyCommitResult.CriticalFailure)
            {
                tc.Verdict = "FAIL";
                tc.FailureReason = "服务器返回 CriticalFailure";
                yield break;
            }

            if (commit == TidyCommitResult.Rejected)
            {
                // v2.0.6.13 第二轮 §3.3：Rejected 令必需套件 FAIL（不是 SKIPPED）
                if (!tc.ConservationPassed)
                {
                    tc.Verdict = "FAIL";
                    tc.FailureReason = "Rejected 且物品守恒失败（物品被修改）";
                    yield break;
                }
                tc.Verdict = "FAIL";
                tc.FailureReason = "服务器返回 Rejected（可能 fixture 不足或限流）";
                yield break;
            }

            if (commit != TidyCommitResult.Committed)
            {
                tc.Verdict = "FAIL";
                tc.FailureReason = $"意外 CommitResult={commit}";
                yield break;
            }

            if (!tc.ConservationPassed)
            {
                tc.Verdict = "FAIL";
                tc.FailureReason = "守恒失败：全页 id+amount+quality+state 多重集合不一致";
                yield break;
            }

            if (!tc.LayoutValid)
            {
                tc.Verdict = "FAIL";
                tc.FailureReason = "布局无效：越界或重叠（按真实网格几何验证）";
                yield break;
            }

            // v2.0.6.13 Round 9（Codex Round 8 §3.3 HK-CROSS-01）：
            // 在 committed、守恒和布局检查都成功后，加入跨套件快捷键保留断言。
            // 若生产代码 ACK 恢复路径未能保留快捷键（cleared > 0 或指纹变化），
            // 直接 FAIL，不通过重绑掩盖。
            if (!FixtureValidator.VerifyHotkeyCase(beforeHotkeys, hotkey, out string hotkeyFailure))
            {
                tc.Verdict = "FAIL";
                tc.FailureReason = "cross-suite hotkey preservation failed: " + hotkeyFailure;
                yield break;
            }

            tc.Verdict = "PASS";
        }

        // ===== SP-HK：快捷键回归（网络路径 + 键 3/7 验证）=====

        private static IEnumerator RunSpHkCoroutine(Player player, TestFixtureSession fixture, List<TestSuiteResult> suites)
        {
            var suite = new TestSuiteResult { SuiteName = "SP-HK", Verdict = "PASS" };
            suites.Add(suite);

            // v2.0.6.13 Round 9（Codex Round 8 §3.3 HK-CROSS-01）：
            // 已永久删除 SP-HK 启动前的 fixture.TryRebindHotkeys 调用。
            // 原调用会掩盖 SP-CONS 整理后真实发生的快捷键丢失（cleared=2 failed=2），
            // 使后续套件显示 PASS 但不能证明"用户点击整理后快捷键得到保留"。
            // 现在由 SP-CONS 的 VerifyHotkeyCase 断言直接暴露恢复失败。
            // 若生产代码 ACK 恢复路径（指纹校验）正常工作，SP-CONS 不会清除快捷键，
            // SP-HK 启动时 fixture 自然满足；否则 SP-CONS 已 FAIL，无需 SP-HK 掩盖。

            if (!FixtureValidator.TryValidateHotkeyFixture(player.inventory, out string fixtureFailure))
            {
                suite.Verdict = "BLOCKED";
                suite.FailureReason = "Fixture 不满足：" + fixtureFailure;
                suite.Cases.Add(new TestCaseResult
                {
                    CaseName = "FixtureValidation",
                    Verdict = "BLOCKED",
                    FailureReason = suite.FailureReason,
                });
                suite.TotalCases = 1;
                suite.Blocked = 1;
                yield break;
            }

            // v2.0.6.13 第三轮 §3.2：验证键 3/7 已绑定到相同 ID、不同 quality/state 的两件实例
            if (!FixtureValidator.TryCaptureRequiredHotkeys(out var beforeHotkeys, out string hkFixtureFailure))
            {
                suite.Verdict = "BLOCKED";
                suite.FailureReason = "Hotkey fixture 不满足：" + hkFixtureFailure;
                suite.Cases.Add(new TestCaseResult
                {
                    CaseName = "HotkeyFixtureValidation",
                    Verdict = "BLOCKED",
                    FailureReason = suite.FailureReason,
                });
                suite.TotalCases = 1;
                suite.Blocked = 1;
                yield break;
            }

            var modes = new[] { TidyMode.SameType, TidyMode.MaxRects, TidyMode.FFD };
            var descOptions = new[] { true, false };

            foreach (var mode in modes)
            {
                foreach (var desc in descOptions)
                {
                    string caseName = $"SPHK_{mode}_Desc{desc}";
                    var tc = new TestCaseResult { CaseName = caseName, Verdict = "PASS" };
                    yield return RunSpHkCaseCoroutine(player, mode, desc, caseName, tc, beforeHotkeys);
                    suite.Cases.Add(tc);
                    suite.TotalCases++;
                    UpdateSuiteCounters(suite, tc);
                    yield return new WaitForSecondsRealtime(RequestIntervalSeconds);
                }
            }
        }

        private static IEnumerator RunSpHkCaseCoroutine(Player player, TidyMode mode, bool desc,
            string caseName, TestCaseResult tc, Dictionary<byte, FixtureValidator.BoundItemFingerprint> beforeHotkeys)
        {
            var inv = player.inventory;
            var beforeSnap = IndependentSnapshot.CaptureAllPages(inv);
            tc.BeforeJsonPath = IndependentSnapshot.WriteCanonicalJson("SPHK", caseName + "_before", beforeSnap);
            tc.BeforeSha256 = IndependentSnapshot.ComputeFileSha256(tc.BeforeJsonPath);

            bool sent = ManualTidyNetwork.TrySendTidyRequest(0xFF, mode, desc, out uint requestId);
            tc.RequestId = requestId;
            if (!sent)
            {
                tc.Verdict = "FAIL";
                tc.CommitResult = "SendFailed";
                tc.FailureReason = "TrySendTidyRequest 返回 false";
                yield break;
            }

            yield return WaitForReplyOrTimeout(requestId, ReplyTimeoutSeconds);

            if (!NetworkTestProbe.TryGet(requestId, out var commit, out var hotkey))
            {
                tc.Verdict = "FAIL";
                tc.CommitResult = "Timeout";
                tc.FailureReason = $"等待 {ReplyTimeoutSeconds}s 未收到网络回包";
                yield break;
            }

            tc.CommitResult = commit.ToString();
            tc.HotkeySummary = $"restored={hotkey.RestoredCount} verified={hotkey.VerifiedCount} cleared={hotkey.ClearedCount} failed={hotkey.FailedCount}";

            var afterSnap = IndependentSnapshot.CaptureAllPages(inv);
            tc.AfterJsonPath = IndependentSnapshot.WriteCanonicalJson("SPHK", caseName + "_after", afterSnap);
            tc.AfterSha256 = IndependentSnapshot.ComputeFileSha256(tc.AfterJsonPath);

            tc.ConservationPassed = IndependentSnapshot.SameItemMultiset(beforeSnap, afterSnap);
            tc.LayoutValid = IndependentSnapshot.AllPagesInBoundsAndNonOverlapping(afterSnap);

            if (commit == TidyCommitResult.CriticalFailure)
            {
                tc.Verdict = "FAIL";
                tc.FailureReason = "服务器返回 CriticalFailure";
                yield break;
            }

            if (commit == TidyCommitResult.Rejected)
            {
                tc.Verdict = "FAIL";
                tc.FailureReason = "服务器返回 Rejected（必需套件不得 SKIPPED）";
                yield break;
            }

            if (commit != TidyCommitResult.Committed)
            {
                tc.Verdict = "FAIL";
                tc.FailureReason = $"意外 CommitResult={commit}";
                yield break;
            }

            if (!tc.ConservationPassed)
            {
                tc.Verdict = "FAIL";
                tc.FailureReason = "守恒失败";
                yield break;
            }

            if (!tc.LayoutValid)
            {
                tc.Verdict = "FAIL";
                tc.FailureReason = "布局无效（按真实网格几何验证）";
                yield break;
            }

            // v2.0.6.13 第二轮 §3.2：严格快捷键验证
            if (!FixtureValidator.VerifyHotkeyCase(beforeHotkeys, hotkey, out string hkFailure))
            {
                tc.Verdict = "FAIL";
                tc.FailureReason = "快捷键验证失败：" + hkFailure;
                yield break;
            }

            tc.Verdict = "PASS";
        }

        // ===== SP-FI：TestHarness 隔离故障注入（逐例独立全页快照）=====

        /// <summary>
        /// v2.0.6.13 第三轮 §3.3 蓝图重写：
        /// 每个 FaultPlan 独立 before/after IndependentSnapshot + 独立哈希 + 独立守恒断言。
        /// 不再在整个 RunAllTests 前后只做一次全局对比。
        /// </summary>
        private static TestSuiteResult RunSpFi(PlayerInventory inv)
        {
            var suite = new TestSuiteResult { SuiteName = "SP-FI", Verdict = "PASS" };
            try
            {
                if (inv == null)
                {
                    suite.Verdict = "FAIL";
                    suite.FailureReason = "PlayerInventory is null";
                    suite.Failed++;
                    return suite;
                }

                // v2.0.6.13 第三轮 §3.3：逐例独立 before/after IndependentSnapshot
                var plans = BuildRequiredFaultPlans();
                if (plans.Count == 0)
                {
                    suite.Verdict = "BLOCKED";
                    suite.FailureReason = "未构建出任何 FaultPlan（测试框架异常）";
                    suite.Blocked++;
                    return suite;
                }

                foreach (var plan in plans)
                {
                    IndependentSnapshot.FullInventorySnapshot beforeSnap = IndependentSnapshot.CaptureAllPages(inv);
                    string beforePath = IndependentSnapshot.WriteCanonicalJson("SPFI", plan.TestName + "_before", beforeSnap);
                    string beforeHash = IndependentSnapshot.ComputeFileSha256(beforePath);
                    string beforeContentHash = IndependentSnapshot.ComputeContentSha256(beforeSnap);

                    FaultInjectionTestRunner.TestResult r = plan.Run(inv);

                    IndependentSnapshot.FullInventorySnapshot afterSnap = IndependentSnapshot.CaptureAllPages(inv);
                    string afterPath = IndependentSnapshot.WriteCanonicalJson("SPFI", plan.TestName + "_after", afterSnap);
                    string afterHash = IndependentSnapshot.ComputeFileSha256(afterPath);
                    string afterContentHash = IndependentSnapshot.ComputeContentSha256(afterSnap);

                    bool verdictPass = r.Verdict == FaultInjectionTestRunner.TestVerdict.Pass;
                    bool conservationPass = IndependentSnapshot.SameItemMultiset(beforeSnap, afterSnap);
                    bool layoutPass = IndependentSnapshot.AllPagesInBoundsAndNonOverlapping(afterSnap);
                    // v2.0.6.13 第五轮 §3.2：故障回滚必须证明精确布局恢复
                    bool exactLayoutPass = IndependentSnapshot.SameExactLayout(beforeSnap, afterSnap);

                    var tc = new TestCaseResult
                    {
                        CaseName = plan.TestName,
                        Verdict = MapFiVerdictWithIndependentInvariant(r.Verdict, verdictPass, conservationPass, layoutPass, exactLayoutPass),
                        FailureReason = BuildFiFailureReason(r, verdictPass, conservationPass, layoutPass, exactLayoutPass),
                        ConservationPassed = conservationPass,
                        LayoutValid = layoutPass,
                        ExactLayoutRestored = exactLayoutPass,
                        BeforeJsonPath = beforePath,
                        BeforeSha256 = beforeHash,
                        AfterJsonPath = afterPath,
                        AfterSha256 = afterHash,
                        BeforeContentSha256 = beforeContentHash,
                        AfterContentSha256 = afterContentHash,
                    };
                    suite.Cases.Add(tc);
                    suite.TotalCases++;
                    UpdateSuiteCounters(suite, tc);

                    // v2.0.6.13 第三轮 §3.3：任一用例独立不变量失败立即停止后续用例
                    if (tc.Verdict == "FAIL")
                    {
                        suite.Verdict = "FAIL";
                        suite.FailureReason = $"用例 {plan.TestName} 独立不变量失败：{tc.FailureReason}";
                        return suite;
                    }
                    // v2.0.6.13 第三轮 §3.3：任何 FI SKIPPED 令 SP-FI BLOCKED（不进入 U3DS）
                    if (tc.Verdict == "SKIPPED" || tc.Verdict == "BLOCKED")
                    {
                        if (suite.Verdict != "FAIL")
                        {
                            suite.Verdict = "BLOCKED";
                            suite.FailureReason = $"用例 {plan.TestName} 因 fixture 不足被 {tc.Verdict}";
                            suite.Blocked++;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                suite.Verdict = "FAIL";
                suite.FailureReason = $"SP-FI crashed: {e.GetType().Name}: {e.Message}";
                LaunchInventoryTidyPlugin.Log?.LogError($"[AutoTest] SP-FI crashed: {e}");
            }
            return suite;
        }

        /// <summary>
        /// v2.0.6.13 第三轮 §3.3：构建必需 FaultPlan 列表，每项携带执行委托。
        /// 与 FaultInjectionTestRunner.RunAllTests 内部用例一一对应。
        /// </summary>
        private static List<FaultPlanEntry> BuildRequiredFaultPlans()
        {
            var list = new List<FaultPlanEntry>();
            list.Add(new FaultPlanEntry
            {
                TestName = "SinglePage_RemoveItemFaultAt_Step0",
                Run = (inv) => FaultInjectionTestRunner.RunSinglePageTest(inv, PlayerInventory.SLOTS, true, 0, WriteKind.RemoveItem, "SinglePage_RemoveItemFaultAt_Step0"),
            });
            list.Add(new FaultPlanEntry
            {
                TestName = "SinglePage_RemoveItemFaultAt_Step2",
                Run = (inv) => FaultInjectionTestRunner.RunSinglePageTest(inv, PlayerInventory.SLOTS, true, 2, WriteKind.RemoveItem, "SinglePage_RemoveItemFaultAt_Step2"),
            });
            list.Add(new FaultPlanEntry
            {
                TestName = "SinglePage_AddItemFaultAt_Step0",
                Run = (inv) => FaultInjectionTestRunner.RunSinglePageTest(inv, PlayerInventory.SLOTS, true, 0, WriteKind.AddItem, "SinglePage_AddItemFaultAt_Step0"),
            });
            list.Add(new FaultPlanEntry
            {
                TestName = "SinglePage_AddItemFaultAt_Step2",
                Run = (inv) => FaultInjectionTestRunner.RunSinglePageTest(inv, PlayerInventory.SLOTS, true, 2, WriteKind.AddItem, "SinglePage_AddItemFaultAt_Step2"),
            });
            list.Add(new FaultPlanEntry
            {
                TestName = "MultiPage_SecondPageFault",
                Run = (inv) => FaultInjectionTestRunner.RunMultiPageSecondPageFaultTest(inv, true),
            });
            return list;
        }

        private struct FaultPlanEntry
        {
            public string TestName;
            public Func<PlayerInventory, FaultInjectionTestRunner.TestResult> Run;
        }

        /// <summary>
        /// v2.0.6.13 第三轮 §3.3：综合 FI 内部 Verdict 与独立 IndependentSnapshot 不变量判定最终 Verdict。
        /// v2.0.6.13 第五轮 §3.2：新增 exactLayoutPass 精确布局断言。
        /// - FI Pass + 守恒 + 布局 + 精确布局 -> PASS
        /// - FI SkippedInsufficientFixture -> SKIPPED（SP-FI 整体 BLOCKED）
        /// - FI Fail 或 守恒失败 或 布局失败 或 精确布局失败 -> FAIL
        /// </summary>
        private static string MapFiVerdictWithIndependentInvariant(
            FaultInjectionTestRunner.TestVerdict fiVerdict,
            bool verdictPass, bool conservationPass, bool layoutPass, bool exactLayoutPass)
        {
            if (fiVerdict == FaultInjectionTestRunner.TestVerdict.SkippedInsufficientFixture)
                return "SKIPPED";
            if (fiVerdict != FaultInjectionTestRunner.TestVerdict.Pass)
                return "FAIL";
            if (!verdictPass || !conservationPass || !layoutPass || !exactLayoutPass)
                return "FAIL";
            return "PASS";
        }

        private static string BuildFiFailureReason(
            FaultInjectionTestRunner.TestResult r,
            bool verdictPass, bool conservationPass, bool layoutPass, bool exactLayoutPass)
        {
            var parts = new List<string>();
            if (r.Verdict != FaultInjectionTestRunner.TestVerdict.Pass && r.Verdict != FaultInjectionTestRunner.TestVerdict.SkippedInsufficientFixture)
                parts.Add($"FI内部Verdict={r.Verdict}");
            if (!string.IsNullOrEmpty(r.FailureReason))
                parts.Add($"FI原因={r.FailureReason}");
            if (!verdictPass) parts.Add("FI内部Verdict != Pass");
            if (!conservationPass) parts.Add("独立守恒失败");
            if (!layoutPass) parts.Add("独立布局失败（按真实网格几何验证）");
            if (!exactLayoutPass) parts.Add("精确布局恢复失败（坐标/旋转/指纹未完全恢复）");
            return parts.Count == 0 ? null : string.Join("; ", parts);
        }



        // ===== SP-SD：关闭在途请求（真实 admission + ShutdownBarrier）=====

        private static IEnumerator RunSpSdCoroutine(Player player, List<TestSuiteResult> suites)
        {
            var suite = new TestSuiteResult { SuiteName = "SP-SD", Verdict = "PASS" };
            suites.Add(suite);

            var tc = new TestCaseResult { CaseName = "ShutdownInFlight", Verdict = "PASS" };
            suite.Cases.Add(tc);
            suite.TotalCases = 1;

            // v2.0.6.13 第二轮 §3.4：武装屏障，准备捕获真实入队的 requestId
            ShutdownBarrier.Arm();

            uint requestId = 0;
            bool sent = false;
            try
            {
                sent = ManualTidyNetwork.TrySendTidyRequest(0xFF, TidyMode.SameType, true, out requestId);
            }
            catch (Exception e)
            {
                ShutdownBarrier.Disarm();
                tc.Verdict = "FAIL";
                tc.FailureReason = $"TrySendTidyRequest 抛异常: {e.GetType().Name}: {e.Message}";
                suite.Verdict = "FAIL";
                suite.FailureReason = tc.FailureReason;
                suite.Failed++;
                LaunchInventoryTidyPlugin.Log?.LogError($"[AutoTest] SP-SD TrySendTidyRequest crashed: {e}");
                yield break;
            }

            tc.RequestId = requestId;

            if (!sent)
            {
                ShutdownBarrier.Disarm();
                // v2.0.6.13 第二轮 §3.4：无法创建真实 admission -> BLOCKED（不伪造 PASS）
                tc.Verdict = "BLOCKED";
                tc.CommitResult = "SendFailed";
                tc.FailureReason = "TrySendTidyRequest 返回 false（无服务器 challenge 或会话未就绪），无法创建真实 admission";
                suite.Verdict = "BLOCKED";
                suite.FailureReason = tc.FailureReason;
                suite.Blocked++;
                yield break;
            }

            // 等待真实请求入队（ShutdownBarrier.RecordQueuedRequest 被调用）
            float barrierStart = Time.realtimeSinceStartup;
            bool barrierTriggered = false;
            while (Time.realtimeSinceStartup - barrierStart < BarrierWaitTimeoutSeconds)
            {
                if (ShutdownBarrier.TryGetQueuedRequestId(out uint queuedId) && queuedId == requestId)
                {
                    barrierTriggered = true;
                    break;
                }
                // 如果请求已经处理完成（Commit 回包已到），说明没有在途请求可取消
                if (NetworkTestProbe.HasCommitReply(requestId))
                {
                    break;
                }
                yield return null;
            }

            if (!barrierTriggered)
            {
                ShutdownBarrier.Disarm();
                // 请求未入队或已处理完 -> 无法测试在途取消
                tc.Verdict = "BLOCKED";
                tc.CommitResult = "NoInFlightRequest";
                tc.FailureReason = $"等待 {BarrierWaitTimeoutSeconds}s 真实请求未入队或已被处理，无法测试在途取消";
                suite.Verdict = "BLOCKED";
                suite.FailureReason = tc.FailureReason;
                suite.Blocked++;
                yield break;
            }

            // 记录关闭前状态
            int pendingBeforeShutdown = MainThreadDispatcher.PendingCount;
            int cancelledBefore = MainThreadDispatcher.CancelledDuringShutdownCount;

            // v2.0.6.13 Codex 第三轮 §3.1：在 Armed 捕获到入队请求后，先释放 ProcessAll 冻结，
            // 让后续 Shutdown drain 能取到该请求并触发真实 Cancel 回调。
            // 不解除 Armed：Cancel 回调仍需 RecordCancelInvocation 计数。
            ShutdownBarrier.ReleaseHold();

            // 触发生产 Shutdown drain：BeginQuiesce -> MainThreadDispatcher.Shutdown（同步 drain + Cancel 回调）
            // 注意：CompleteShutdown 必须延后到清理前观测窗口完成之后，否则 ClearAll 会抹掉 ledger/lease/pending 证据。
            try
            {
                ManualTidyNetwork.BeginQuiesce();
                MainThreadDispatcher.Shutdown();
            }
            catch (Exception e)
            {
                ShutdownBarrier.Disarm();
                tc.Verdict = "FAIL";
                tc.FailureReason = $"Shutdown 异常: {e.GetType().Name}: {e.Message}";
                suite.Verdict = "FAIL";
                suite.FailureReason = tc.FailureReason;
                suite.Failed++;
                LaunchInventoryTidyPlugin.Log?.LogError($"[AutoTest] SP-SD shutdown crashed: {e}");
                yield break;
            }

            // 等待 drain 完成（Shutdown 是同步的，但 Cancel 回调内可能异步触发 transport 发包）
            // 最多等 0.5s（约 30 帧 @60fps），等待结束仍 pending 即 FAIL，不依赖 CompleteShutdown 抹掉 pending。
            float drainStart = Time.realtimeSinceStartup;
            bool drainComplete = false;
            while (Time.realtimeSinceStartup - drainStart < 0.5f)
            {
                int cancelNow = ShutdownBarrier.CancelInvocationCount;
                if (cancelNow >= 1)
                {
                    drainComplete = true;
                    break;
                }
                yield return null;
            }

            // 清理前的唯一有效观测窗口：必须在 CompleteShutdown 之前查询
            // CompleteShutdown 会执行 RequestLedger.ClearAllForTests + ClientPendingState.ClearAll + PlayerOperationGate.ClearAll
            // 之后查询到的"不存在"既可能代表成功清理，也可能代表 CancelNew 从未执行，两者不可区分。
            int pendingAfterShutdown = MainThreadDispatcher.PendingCount;
            int cancelledAfter = MainThreadDispatcher.CancelledDuringShutdownCount;
            int cancelInvocationCount = ShutdownBarrier.CancelInvocationCount;

            // 验证 ledger 终态：必须是 Failed（CancelNew 已执行）
            bool ledgerFailed = false;
            string ledgerStateDetail = "未查询";
            ulong capturedNonce = 0;
            CSteamID capturedSteamId = CSteamID.Nil;
            try
            {
                capturedSteamId = player.channel.owner.playerID.steamID;
                if (ClientSessionNonce.TryGetServerIssuedToken(out capturedNonce))
                {
                    if (RequestLedger.TryLookup(capturedSteamId, capturedNonce, requestId, out var entry))
                    {
                        ledgerStateDetail = entry.State.ToString();
                        ledgerFailed = entry.State == RequestLedger.RequestState.Failed;
                    }
                    else
                    {
                        ledgerStateDetail = "条目不存在";
                    }
                }
                else
                {
                    ledgerStateDetail = "nonce 不可用";
                }
            }
            catch (Exception e)
            {
                ledgerStateDetail = "查询异常: " + e.Message;
            }

            // 验证 lease 释放：CancelNew 必须已 Release
            bool leaseReleased = false;
            string leaseDetail = "未查询";
            try
            {
                leaseReleased = !PlayerOperationGate.IsHeld(capturedSteamId);
                leaseDetail = leaseReleased ? "已释放" : "仍被持有";
            }
            catch (Exception e)
            {
                leaseDetail = "查询异常: " + e.Message;
            }

            // 验证客户端 pending（仅作为可选观察，不作为 pre-complete 硬断言）
            // v2.0.6.13 第五轮 §3.1：pendingCleared 移到 post-complete 阶段作为硬断言
            // pre-complete 阶段：服务端补偿不变量必须全部成立（drain + cancel + ledger + lease）
            // post-complete 阶段：CompleteShutdown 后 client pending 不得残留
            bool pendingClearedBeforeComplete = false;
            string pendingDetailBefore = "未查询";
            try
            {
                pendingClearedBeforeComplete = capturedNonce != 0 && !ManualTidyNetwork.ClientPendingState.IsPending(capturedNonce, requestId);
                pendingDetailBefore = pendingClearedBeforeComplete ? "已清除（回环提前到达）" : "仍 pending（等 CompleteShutdown 清理）";
            }
            catch (Exception e)
            {
                pendingDetailBefore = "查询异常: " + e.Message;
            }

            // 写入清理前观测窗口的 JSON 证据
            tc.BeforeJsonPath = WriteSpSdStateJson("before_shutdown", new
            {
                requestId,
                pendingBeforeShutdown,
                cancelledBefore,
            });
            tc.BeforeSha256 = ComputeFileSha256(tc.BeforeJsonPath);

            tc.AfterJsonPath = WriteSpSdStateJson("before_complete_shutdown", new
            {
                phase = "pre_complete_shutdown",
                requestId,
                pendingAfterShutdown,
                cancelledAfter,
                cancelInvocationCount,
                drainComplete,
                ledgerState = ledgerStateDetail,
                ledgerFailed,
                leaseDetail,
                leaseReleased,
                pendingDetailBefore,
                pendingClearedBeforeComplete,
            });
            tc.AfterSha256 = ComputeFileSha256(tc.AfterJsonPath);

            // v2.0.6.13 第五轮 §3.1：pre-complete 服务端补偿不变量（不含 pendingCleared）
            bool serverCompensated = drainComplete
                && pendingAfterShutdown == 0
                && cancelInvocationCount == 1
                && cancelledAfter > cancelledBefore
                && ledgerFailed
                && leaseReleased;

            if (!serverCompensated)
            {
                ShutdownBarrier.Disarm();
                tc.Verdict = "FAIL";
                tc.FailureReason = "关闭前服务端补偿不变量失败：" +
                    $"drain={drainComplete}, queue={pendingAfterShutdown}, cancel={cancelInvocationCount}, " +
                    $"ledgerFailed={ledgerFailed}, leaseReleased={leaseReleased}";
                tc.ConservationPassed = false;
                suite.Verdict = "FAIL";
                suite.FailureReason = tc.FailureReason;
                suite.Failed++;
                yield break;
            }

            // 现在才允许执行 CompleteShutdown 清空所有静态状态
            try
            {
                ManualTidyNetwork.CompleteShutdown();
            }
            catch (Exception e)
            {
                ShutdownBarrier.Disarm();
                tc.Verdict = "FAIL";
                tc.FailureReason = $"CompleteShutdown 异常: {e.GetType().Name}: {e.Message}";
                suite.Verdict = "FAIL";
                suite.FailureReason = tc.FailureReason;
                suite.Failed++;
                LaunchInventoryTidyPlugin.Log?.LogError($"[AutoTest] SP-SD complete shutdown crashed: {e}");
                yield break;
            }

            ShutdownBarrier.Disarm();

            // v2.0.6.13 第五轮 §3.1：post-complete 客户端零残留硬断言
            bool pendingClearedAfterComplete = false;
            string pendingDetailAfter = "未查询";
            try
            {
                pendingClearedAfterComplete = capturedNonce != 0 && !ManualTidyNetwork.ClientPendingState.IsPending(capturedNonce, requestId);
                pendingDetailAfter = pendingClearedAfterComplete ? "已清除" : "仍残留";
            }
            catch (Exception e)
            {
                pendingDetailAfter = "查询异常: " + e.Message;
            }

            if (!pendingClearedAfterComplete)
            {
                tc.Verdict = "FAIL";
                tc.FailureReason = $"CompleteShutdown 后 client pending 仍残留：{pendingDetailAfter}";
                tc.ConservationPassed = false;
                suite.Verdict = "FAIL";
                suite.FailureReason = tc.FailureReason;
                suite.Failed++;
                yield break;
            }

            tc.Verdict = "PASS";
            tc.ConservationPassed = true;
            tc.LayoutValid = true;
            tc.HotkeySummary = pendingClearedBeforeComplete
                ? "Rejected 回环已在 CompleteShutdown 前清除 pending"
                : "由 CompleteShutdown 清除未投递回环的 pending";
            suite.Passed++;
        }

        // ===== 套件计数器更新 =====

        private static void UpdateSuiteCounters(TestSuiteResult suite, TestCaseResult tc)
        {
            if (tc.Verdict == "PASS") suite.Passed++;
            else if (tc.Verdict == "SKIPPED") suite.Skipped++;
            else if (tc.Verdict == "BLOCKED") suite.Blocked++;
            else
            {
                suite.Failed++;
                if (suite.Verdict != "FAIL")
                {
                    suite.Verdict = "FAIL";
                    suite.FailureReason = $"Case {tc.CaseName} failed: {tc.FailureReason}";
                }
            }
        }

        // ===== 协程辅助 =====

        private static IEnumerator WaitForReplyOrTimeout(uint requestId, float timeoutSeconds)
        {
            if (requestId == 0) yield break;
            float start = Time.realtimeSinceStartup;
            while (!NetworkTestProbe.HasValidReply(requestId))
            {
                if (Time.realtimeSinceStartup - start > timeoutSeconds)
                {
                    LaunchInventoryTidyPlugin.Log?.LogWarning(
                        $"[AutoTest] WaitForReply timeout: requestId={requestId}, timeout={timeoutSeconds}s");
                    yield break;
                }
                yield return null;
            }
        }

        // ===== JSON 序列化与文件 IO =====

        internal sealed class PageItemRecord
        {
            public byte page;
            public byte x;
            public byte y;
            public byte rot;
            public ushort id;
            public byte amount;
            public byte quality;
            public byte[] state;
        }

        private static void EnsureStateExportDir()
        {
            try
            {
                if (!Directory.Exists(StateExportDir))
                {
                    Directory.CreateDirectory(StateExportDir);
                    LaunchInventoryTidyPlugin.Log?.LogInfo(
                        $"[AutoTest] 创建状态导出目录: {Path.GetFullPath(StateExportDir)}");
                }
            }
            catch (Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogError($"[AutoTest] 创建状态导出目录失败: {e}");
            }
        }

        private static string WriteFingerprintJson(string caseName, string phase,
            List<(byte x, byte y, byte rot, ushort id, byte amount, byte quality, byte[] state)> fingerprint)
        {
            try
            {
                var records = new List<PageItemRecord>(fingerprint.Count);
                foreach (var f in fingerprint)
                {
                    records.Add(new PageItemRecord
                    {
                        page = 0,
                        x = f.x,
                        y = f.y,
                        rot = f.rot,
                        id = f.id,
                        amount = f.amount,
                        quality = f.quality,
                        state = f.state == null ? Array.Empty<byte>() : (byte[])f.state.Clone(),
                    });
                }
                string fileName = $"{caseName}_{phase}.json";
                string path = Path.Combine(StateExportDir, fileName);
                string json = JsonConvert.SerializeObject(records, Formatting.Indented);
                File.WriteAllText(path, json, new UTF8Encoding(false));
                return path;
            }
            catch (Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogError($"[AutoTest] WriteFingerprintJson failed: {e}");
                return null;
            }
        }

        private static string WriteSpSdStateJson(string phase, object state)
        {
            try
            {
                string fileName = $"SP-SD_{phase}.json";
                string path = Path.Combine(StateExportDir, fileName);
                string json = JsonConvert.SerializeObject(state, Formatting.Indented);
                File.WriteAllText(path, json, new UTF8Encoding(false));
                return path;
            }
            catch (Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogError($"[AutoTest] WriteSpSdStateJson failed: {e}");
                return null;
            }
        }

        private static string ComputeFileSha256(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (var stream = File.OpenRead(path))
                {
                    byte[] hash = sha.ComputeHash(stream);
                    var sb = new StringBuilder(64);
                    for (int i = 0; i < hash.Length; i++)
                        sb.Append(hash[i].ToString("X2"));
                    return sb.ToString();
                }
            }
            catch (Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogError($"[AutoTest] ComputeFileSha256 failed for {path}: {e}");
                return null;
            }
        }

        private static void WriteSummary(List<TestSuiteResult> suites)
        {
            try
            {
                EnsureStateExportDir();
                string json = JsonConvert.SerializeObject(suites, Formatting.Indented);
                File.WriteAllText(SummaryFile, json, new UTF8Encoding(false));
                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[AutoTest] 测试结果摘要已写入: {Path.GetFullPath(SummaryFile)}");

                var sb = new StringBuilder(1024);
                sb.AppendLine("[AutoTest] ===== 测试结果摘要 =====");
                foreach (var s in suites)
                {
                    sb.AppendLine($"  {s.SuiteName}: {s.Verdict} " +
                        $"(total={s.TotalCases}, pass={s.Passed}, fail={s.Failed}, skip={s.Skipped}, block={s.Blocked})");
                    if (!string.IsNullOrEmpty(s.FailureReason))
                        sb.AppendLine($"    failure: {s.FailureReason}");
                }
                sb.AppendLine($"[AutoTest] AreAllRequiredSuitesPass: {AreAllRequiredSuitesPass(suites, out string fail)}" +
                    (fail != null ? $" ({fail})" : ""));
                LaunchInventoryTidyPlugin.Log?.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogError($"[AutoTest] WriteSummary failed: {e}");
            }
        }

        private static void WriteCompletionMarker(bool success, string message)
        {
            try
            {
                EnsureStateExportDir();
                var marker = new
                {
                    completedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    success = success,
                    message = message,
                };
                string json = JsonConvert.SerializeObject(marker, Formatting.Indented);
                File.WriteAllText(CompletionMarkerFile, json, new UTF8Encoding(false));
                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[AutoTest] completion.marker 已写入: {Path.GetFullPath(CompletionMarkerFile)} (success={success})");
            }
            catch (Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogError($"[AutoTest] WriteCompletionMarker failed: {e}");
            }
        }

        private static void LogSuiteStart(string name)
        {
            LaunchInventoryTidyPlugin.Log?.LogInfo($"[AutoTest] ===== 开始执行 {name} 测试套件 =====");
        }

        private static void LogSuiteEnd(string name, TestSuiteResult r)
        {
            LaunchInventoryTidyPlugin.Log?.LogInfo(
                $"[AutoTest] ===== {name} 完成: {r.Verdict} " +
                $"(total={r.TotalCases}, pass={r.Passed}, fail={r.Failed}, skip={r.Skipped}, block={r.Blocked}) =====");
        }

        private static void LogSuiteEnd(string name, List<TestSuiteResult> suites)
        {
            var r = suites.Find(s => s.SuiteName == name);
            if (r != null) LogSuiteEnd(name, r);
        }

        private static IEnumerator QuitGameCoroutine()
        {
            LaunchInventoryTidyPlugin.Log?.LogInfo("[AutoTest] 全部测试完成，3 秒后自动退出游戏...");
            yield return new WaitForSecondsRealtime(3.0f);
            try { Provider.disconnect(); }
            catch (Exception e) { LaunchInventoryTidyPlugin.Log?.LogError($"[AutoTest] Provider.disconnect failed: {e}"); }
            yield return new WaitForSecondsRealtime(0.5f);
            try { Application.Quit(); }
            catch (Exception e) { LaunchInventoryTidyPlugin.Log?.LogError($"[AutoTest] Application.Quit failed: {e}"); }
        }
    }

    internal sealed class AutoTestHostBehaviour : MonoBehaviour
    {
        private void Update() { }
    }
}
#endif
