#if TIDY_TEST_HARNESS
using System;
using System.Collections.Generic;
using System.Text;
using SDG.Unturned;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// v2.0.6.11 重写（Codex v2.0.6.10 审计 §三 P0-1/P0-2 + P1-1 修复）：
    /// 故障注入测试框架 - 仅在 TIDY_TEST_HARNESS 构建中生效。
    ///
    /// Release 构建中本文件不会被编译，ManualTidyService 中的 hook 调用也会被 #if 屏蔽为 no-op。
    /// 生产 DLL 不含任何能对真实背包抛故障的代码路径。
    ///
    /// v2.0.6.11 关键修订：
    ///   1. P0-2：引入 FaultPlan（TargetPage + TargetCommitOrdinal + Kind + Step）精确触发
    ///      - 旧版 NotifyCommitPageStart 每页重置 _currentRemoveStep，导致 faultAfterRemoveStep=0
    ///        在第一页第一次 removeItem 后即触发，永远测不到"第二页故障"
    ///      - 新版只有目标页 + 目标序号匹配后才递增计数器并触发
    ///   2. P1-1：测试结果改为联合断言，维护不可逆 Triggered/FailureReason 标志
    ///      - 只有 Triggered && CriticalFailure && RollbackAttempted && RollbackVerified && 指纹匹配 才 PASS
    ///      - 任何断言失败立即 FAIL 并停止后续用例
    ///      - 物品不足时标记 SKIPPED_INSUFFICIENT_FIXTURE，不是 PASS
    ///   3. P0-1：整个文件用 #if TIDY_TEST_HARNESS 包裹，Release 不编译
    /// </summary>
    internal static class FaultInjectionTestRunner
    {
        // ===== 故障注入计划（FaultPlan） =====
        private static FaultPlan _plan;
        private static bool _armed = false;

        // CommitPage 调用计数（1-based，第一页为 1，第二页为 2）
        private static int _commitOrdinal = 0;
        // 当前 CommitPage 内的写入步骤计数（0-based）
        private static int _removeStepInPage = 0;
        private static int _addStepInPage = 0;

        public static bool IsArmed => _armed;

        /// <summary>配置故障注入计划。只有 TargetPage + TargetCommitOrdinal 匹配时才触发。</summary>
        public static void Arm(FaultPlan plan)
        {
            _plan = plan;
            _armed = true;
            _commitOrdinal = 0;
            _removeStepInPage = 0;
            _addStepInPage = 0;
            LaunchInventoryTidyPlugin.Log?.LogInfo(
                $"[FaultInjection] 已 Arm：targetPage={plan.TargetPage} targetOrdinal={plan.TargetCommitOrdinal} " +
                $"kind={plan.Kind} step={plan.Step}");
        }

        public static void Disarm()
        {
            _armed = false;
            _plan = default(FaultPlan);
            _commitOrdinal = 0;
            _removeStepInPage = 0;
            _addStepInPage = 0;
        }

        /// <summary>
        /// v2.0.6.12 新增（Codex v2.0.6.11 单机冒烟复盘 §3.5）：
        /// 原子快照+清理。必须在 finally 内调用。
        /// 旧版 Disarm() 先清空 _plan，调用方再读 _plan.Triggered / _commitOrdinal 永远得到 false/0。
        /// 本方法先复制观测值到 FaultObservation（值类型），再清理静态状态，
        /// 确保即使 finally 后调用方读取 observation 也能拿到故障期间的真实值。
        /// </summary>
        internal readonly struct FaultObservation
        {
            internal readonly bool Triggered;
            internal readonly int CommitOrdinal;
            internal FaultObservation(bool triggered, int ordinal)
            {
                Triggered = triggered;
                CommitOrdinal = ordinal;
            }
        }

        internal static FaultObservation SnapshotAndDisarm()
        {
            FaultObservation snapshot = new FaultObservation(_plan.Triggered, _commitOrdinal);
            _plan = default(FaultPlan);
            _commitOrdinal = 0;
            _removeStepInPage = 0;
            _addStepInPage = 0;
            _armed = false;
            return snapshot;
        }

        /// <summary>CommitPage 入口通知：递增 ordinal，重置页内步骤计数器。</summary>
        public static void NotifyCommitPageStart(byte page)
        {
            _commitOrdinal++;
            _removeStepInPage = 0;
            _addStepInPage = 0;
            if (_armed)
            {
                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[FaultInjection] CommitPage 入口：page={page} ordinal={_commitOrdinal} " +
                    $"(targetPage={_plan.TargetPage} targetOrdinal={_plan.TargetCommitOrdinal})");
            }
        }

        /// <summary>removeItem(0) 调用后钩子：仅当 page+ordinal+step 精确匹配时触发。</summary>
        public static void OnAfterRemoveItem(byte page)
        {
            if (!_armed) return;
            if (_plan.Kind != WriteKind.RemoveItem) return;
            if (page != _plan.TargetPage) return;
            if (_commitOrdinal != _plan.TargetCommitOrdinal) return;

            if (_removeStepInPage == _plan.Step)
            {
                _plan.Triggered = true;
                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[FaultInjection] 触发故障：page={page} ordinal={_commitOrdinal} removeStep={_removeStepInPage}");
                throw new InvalidOperationException(
                    $"[FaultInjection] removeItem(0) post-call fault at page={page} ordinal={_commitOrdinal} step={_removeStepInPage}");
            }
            _removeStepInPage++;
        }

        /// <summary>addItem(...) 调用后钩子：仅当 page+ordinal+step 精确匹配时触发。</summary>
        public static void OnAfterAddItem(byte page)
        {
            if (!_armed) return;
            if (_plan.Kind != WriteKind.AddItem) return;
            if (page != _plan.TargetPage) return;
            if (_commitOrdinal != _plan.TargetCommitOrdinal) return;

            if (_addStepInPage == _plan.Step)
            {
                _plan.Triggered = true;
                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[FaultInjection] 触发故障：page={page} ordinal={_commitOrdinal} addStep={_addStepInPage}");
                throw new InvalidOperationException(
                    $"[FaultInjection] addItem post-call fault at page={page} ordinal={_commitOrdinal} step={_addStepInPage}");
            }
            _addStepInPage++;
        }

        // ===== 测试结果结构 =====
        public enum TestVerdict : byte
        {
            Pending = 0,
            Pass = 1,
            Fail = 2,
            SkippedInsufficientFixture = 3,
        }

        public sealed class TestResult
        {
            public string TestName;
            public TestVerdict Verdict;
            public string FailureReason;  // 不可逆：一旦写入非空，不会被覆盖为 PASS
            public bool Triggered;         // 不可逆：一旦为 true，不会被重置
            public bool ExpectedPageReached;
            public bool RollbackAttempted;
            public bool RollbackVerified;
            public List<(byte x, byte y, byte rot, ushort id, byte amount, byte quality, byte[] state)> BeforeFingerprint;
            public List<(byte x, byte y, byte rot, ushort id, byte amount, byte quality, byte[] state)> AfterFingerprint;
        }

        /// <summary>运行全部故障注入测试。首例 FAIL 立即停止后续用例。</summary>
        public static List<TestResult> RunAllTests(PlayerInventory inv, bool sortDescending = true)
        {
            var results = new List<TestResult>();
            if (inv == null)
            {
                results.Add(new TestResult
                {
                    TestName = "RunnerSelfCheck",
                    Verdict = TestVerdict.Fail,
                    FailureReason = "PlayerInventory is null",
                });
                return results;
            }

            // 用例 1：单页 removeItem 第 0 步故障
            var r1 = RunSinglePageTest(inv, PlayerInventory.SLOTS, sortDescending,
                targetStep: 0, kind: WriteKind.RemoveItem,
                testName: "SinglePage_RemoveItemFaultAt_Step0");
            results.Add(r1);
            if (r1.Verdict == TestVerdict.Fail) return results;

            // 用例 2：单页 removeItem 中间步故障
            var r2 = RunSinglePageTest(inv, PlayerInventory.SLOTS, sortDescending,
                targetStep: 2, kind: WriteKind.RemoveItem,
                testName: "SinglePage_RemoveItemFaultAt_Step2");
            results.Add(r2);
            if (r2.Verdict == TestVerdict.Fail) return results;

            // 用例 3：单页 addItem 第 0 步故障
            var r3 = RunSinglePageTest(inv, PlayerInventory.SLOTS, sortDescending,
                targetStep: 0, kind: WriteKind.AddItem,
                testName: "SinglePage_AddItemFaultAt_Step0");
            results.Add(r3);
            if (r3.Verdict == TestVerdict.Fail) return results;

            // 用例 4：单页 addItem 中间步故障
            var r4 = RunSinglePageTest(inv, PlayerInventory.SLOTS, sortDescending,
                targetStep: 2, kind: WriteKind.AddItem,
                testName: "SinglePage_AddItemFaultAt_Step2");
            results.Add(r4);
            if (r4.Verdict == TestVerdict.Fail) return results;

            // 用例 5：多页第二页故障
            var r5 = RunMultiPageSecondPageFaultTest(inv, sortDescending);
            results.Add(r5);

            return results;
        }

        /// <summary>单页故障注入测试：精确匹配 page+ordinal+step。
        /// v2.0.6.13 第三轮 §3.3：改为 internal，允许 SP-FI 逐例调用并在前后做 IndependentSnapshot。</summary>
        internal static TestResult RunSinglePageTest(PlayerInventory inv, byte page, bool sortDescending,
            int targetStep, WriteKind kind, string testName)
        {
            var result = new TestResult { TestName = testName, Verdict = TestVerdict.Pending };

            try
            {
                Items items = inv.items[page];
                if (items == null || items.getItemCount() == 0)
                {
                    result.Verdict = TestVerdict.SkippedInsufficientFixture;
                    result.FailureReason = $"page {page} 无物品，跳过（不是 PASS）";
                    LaunchInventoryTidyPlugin.Log?.LogWarning(
                        $"[FaultInjection] {testName} SKIPPED：物品不足");
                    return result;
                }

                // Step2 要求至少 targetStep+1 件物品（removeItem 路径）
                if (kind == WriteKind.RemoveItem && items.getItemCount() < targetStep + 1)
                {
                    result.Verdict = TestVerdict.SkippedInsufficientFixture;
                    result.FailureReason = $"page {page} 物品数 {items.getItemCount()} 不足以测试 Step{targetStep}";
                    return result;
                }

                result.BeforeFingerprint = CaptureFullFingerprint(items, page);

                Arm(new FaultPlan
                {
                    TargetPage = page,
                    TargetCommitOrdinal = 1,
                    Kind = kind,
                    Step = targetStep,
                });

                TidyCommitResult observedResult;
                bool observedMutationStarted = false;
                bool observedRollbackAttempted = false;
                bool observedRollbackVerified = false;
                FaultObservation observation;

                try
                {
                    var outcome = ManualTidyService.TidyAllPlayerPages(inv, sortDescending,
                        TidyMode.SameType, null);
                    observedResult = outcome.Result;
                    observedMutationStarted = outcome.MutationStarted;
                    observedRollbackAttempted = outcome.RollbackAttempted;
                    observedRollbackVerified = outcome.RollbackVerified;
                }
                catch (Exception ex) when (IsFaultInjectionException(ex))
                {
                    result.Verdict = TestVerdict.Fail;
                    result.FailureReason = $"故障异常泄漏到测试运行器：{ex.Message}（应被 ManualTidyService 捕获）";
                    return result;
                }
                finally
                {
                    // v2.0.6.12：必须先快照再清理，旧版 Disarm() 后读 _plan.Triggered 永远为 false
                    observation = SnapshotAndDisarm();
                }

                result.Triggered = observation.Triggered;
                result.RollbackAttempted = observedRollbackAttempted;
                result.RollbackVerified = observedRollbackVerified;
                result.ExpectedPageReached = true;

                result.AfterFingerprint = CaptureFullFingerprint(items, page);

                bool fingerprintOk = FingerprintsMatch(result.BeforeFingerprint, result.AfterFingerprint);
                bool resultOk = observedResult == TidyCommitResult.CriticalFailure;

                if (!result.Triggered)
                {
                    result.Verdict = TestVerdict.Fail;
                    result.FailureReason = $"故障未触发（Triggered=false），observedResult={observedResult}";
                    return result;
                }
                if (!resultOk)
                {
                    result.Verdict = TestVerdict.Fail;
                    result.FailureReason = $"预期 CriticalFailure，实际 result={observedResult}，" +
                        $"MutationStarted={observedMutationStarted}, RollbackAttempted={observedRollbackAttempted}, " +
                        $"RollbackVerified={observedRollbackVerified}";
                    return result;
                }
                if (!observedRollbackAttempted)
                {
                    result.Verdict = TestVerdict.Fail;
                    result.FailureReason = "RollbackAttempted=false（应回滚但未尝试）";
                    return result;
                }
                if (!observedRollbackVerified)
                {
                    result.Verdict = TestVerdict.Fail;
                    result.FailureReason = "RollbackVerified=false（回滚未通过验证）";
                    return result;
                }
                if (!fingerprintOk)
                {
                    result.Verdict = TestVerdict.Fail;
                    result.FailureReason = "指纹不匹配：" + BuildFingerprintMismatchMessage(result);
                    return result;
                }

                result.Verdict = TestVerdict.Pass;
                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[FaultInjection] {testName} PASS：Triggered+CriticalFailure+RollbackAttempted+RollbackVerified+FingerprintMatch " +
                    $"({result.BeforeFingerprint.Count} 项)");
                return result;
            }
            catch (Exception e)
            {
                result.Verdict = TestVerdict.Fail;
                result.FailureReason = $"测试异常: {e}";
                return result;
            }
            finally
            {
                Disarm();
            }
        }

        /// <summary>多页第二页故障测试：第一页应按 post-commit 快照回滚。
        /// v2.0.6.13 第三轮 §3.3：改为 internal，允许 SP-FI 逐例调用并在前后做 IndependentSnapshot。</summary>
        internal static TestResult RunMultiPageSecondPageFaultTest(PlayerInventory inv, bool sortDescending)
        {
            var result = new TestResult
            {
                TestName = "MultiPage_SecondPageFault",
                Verdict = TestVerdict.Pending,
            };

            try
            {
                byte page1 = PlayerInventory.SLOTS;
                byte page2 = (byte)(PlayerInventory.SLOTS + 1);
                Items items1 = inv.items[page1];
                Items items2 = inv.items[page2];
                if (items1 == null || items1.getItemCount() == 0 || items2 == null || items2.getItemCount() == 0)
                {
                    result.Verdict = TestVerdict.SkippedInsufficientFixture;
                    result.FailureReason = "page1 或 page2 无物品，跳过（不是 PASS）";
                    return result;
                }

                var beforeFp1 = CaptureFullFingerprint(items1, page1);
                var beforeFp2 = CaptureFullFingerprint(items2, page2);
                result.BeforeFingerprint = beforeFp1;

                // P0-2：目标页 = page2，目标 ordinal = 2（第二页 CommitPage 时触发）
                Arm(new FaultPlan
                {
                    TargetPage = page2,
                    TargetCommitOrdinal = 2,
                    Kind = WriteKind.RemoveItem,
                    Step = 0,
                });

                TidyCommitResult observedResult = TidyCommitResult.Committed;
                bool observedRollbackAttempted = false;
                bool observedRollbackVerified = false;
                FaultObservation observation;

                try
                {
                    var outcome = ManualTidyService.TidyAllPlayerPages(inv, sortDescending,
                        TidyMode.SameType, null);
                    observedResult = outcome.Result;
                    observedRollbackAttempted = outcome.RollbackAttempted;
                    observedRollbackVerified = outcome.RollbackVerified;
                }
                catch (Exception ex) when (IsFaultInjectionException(ex))
                {
                    result.Verdict = TestVerdict.Fail;
                    result.FailureReason = $"故障异常泄漏到测试运行器：{ex.Message}";
                    return result;
                }
                finally
                {
                    // v2.0.6.12：必须先快照再清理，旧版 Disarm() 后读 _plan.Triggered/_commitOrdinal 永远为 false/0
                    observation = SnapshotAndDisarm();
                }

                result.Triggered = observation.Triggered;
                result.RollbackAttempted = observedRollbackAttempted;
                result.RollbackVerified = observedRollbackVerified;
                result.ExpectedPageReached = observation.CommitOrdinal >= 2;

                var afterFp1 = CaptureFullFingerprint(items1, page1);
                var afterFp2 = CaptureFullFingerprint(items2, page2);
                result.AfterFingerprint = afterFp1;

                bool fp1Ok = FingerprintsMatch(beforeFp1, afterFp1);
                bool fp2Ok = FingerprintsMatch(beforeFp2, afterFp2);
                bool resultOk = observedResult == TidyCommitResult.CriticalFailure;

                if (!result.ExpectedPageReached)
                {
                    result.Verdict = TestVerdict.Fail;
                    result.FailureReason = $"未到达目标页（commitOrdinal={observation.CommitOrdinal}，预期 >=2）";
                    return result;
                }
                if (!result.Triggered)
                {
                    result.Verdict = TestVerdict.Fail;
                    result.FailureReason = "故障未触发（Triggered=false），第一页可能未正常 commit";
                    return result;
                }
                if (!resultOk)
                {
                    result.Verdict = TestVerdict.Fail;
                    result.FailureReason = $"预期 CriticalFailure，实际 result={observedResult}";
                    return result;
                }
                if (!observedRollbackAttempted)
                {
                    result.Verdict = TestVerdict.Fail;
                    result.FailureReason = "RollbackAttempted=false";
                    return result;
                }
                if (!observedRollbackVerified)
                {
                    result.Verdict = TestVerdict.Fail;
                    result.FailureReason = "RollbackVerified=false（回滚未通过验证）";
                    return result;
                }
                if (!fp1Ok)
                {
                    result.Verdict = TestVerdict.Fail;
                    result.FailureReason = "page1 指纹不匹配（第一页 post-commit 回滚失败）：" + BuildFingerprintMismatchMessage(result);
                    return result;
                }
                if (!fp2Ok)
                {
                    result.Verdict = TestVerdict.Fail;
                    result.FailureReason = "page2 指纹不匹配（第二页回滚失败）";
                    return result;
                }

                result.Verdict = TestVerdict.Pass;
                LaunchInventoryTidyPlugin.Log?.LogInfo(
                    $"[FaultInjection] {result.TestName} PASS：第一页+第二页指纹全量匹配 + 联合断言通过");
                return result;
            }
            catch (Exception e)
            {
                result.Verdict = TestVerdict.Fail;
                result.FailureReason = $"测试异常: {e}";
                return result;
            }
            finally
            {
                Disarm();
            }
        }

        // ===== 辅助方法 =====

        private static bool IsFaultInjectionException(Exception ex)
        {
            return ex is InvalidOperationException && ex.Message != null &&
                   ex.Message.Contains("[FaultInjection]");
        }

        private static List<(byte x, byte y, byte rot, ushort id, byte amount, byte quality, byte[])>
            CaptureFullFingerprint(Items items, byte page)
        {
            var list = new List<(byte, byte, byte, ushort, byte, byte, byte[])>();
            if (items == null) return list;
            byte count = items.getItemCount();
            for (byte i = 0; i < count; i++)
            {
                ItemJar jar = items.getItem(i);
                if (jar?.item == null) continue;
                byte[] stateCopy = jar.item.state == null ? null : (byte[])jar.item.state.Clone();
                list.Add((jar.x, jar.y, jar.rot, jar.item.id, jar.item.amount, jar.item.quality, stateCopy));
            }
            list.Sort(CompareFingerprint);
            return list;
        }

        private static int CompareFingerprint(
            (byte x, byte y, byte rot, ushort id, byte amount, byte quality, byte[] state) a,
            (byte x, byte y, byte rot, ushort id, byte amount, byte quality, byte[] state) b)
        {
            int c = a.id.CompareTo(b.id);
            if (c != 0) return c;
            c = a.amount.CompareTo(b.amount);
            if (c != 0) return c;
            c = a.quality.CompareTo(b.quality);
            if (c != 0) return c;
            c = a.x.CompareTo(b.x);
            if (c != 0) return c;
            c = a.y.CompareTo(b.y);
            if (c != 0) return c;
            c = a.rot.CompareTo(b.rot);
            if (c != 0) return c;
            return CompareStateBytes(a.state, b.state);
        }

        private static int CompareStateBytes(byte[] a, byte[] b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            int len = Math.Min(a.Length, b.Length);
            for (int i = 0; i < len; i++)
            {
                int c = a[i].CompareTo(b[i]);
                if (c != 0) return c;
            }
            return a.Length.CompareTo(b.Length);
        }

        private static bool FingerprintsMatch(
            List<(byte x, byte y, byte rot, ushort id, byte amount, byte quality, byte[] state)> a,
            List<(byte x, byte y, byte rot, ushort id, byte amount, byte quality, byte[] state)> b)
        {
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                var ai = a[i];
                var bi = b[i];
                if (ai.id != bi.id) return false;
                if (ai.amount != bi.amount) return false;
                if (ai.quality != bi.quality) return false;
                if (ai.x != bi.x) return false;
                if (ai.y != bi.y) return false;
                if (ai.rot != bi.rot) return false;
                if (!StateBytesEqual(ai.state, bi.state)) return false;
            }
            return true;
        }

        private static bool StateBytesEqual(byte[] a, byte[] b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static string BuildFingerprintMismatchMessage(TestResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"before.Count={result.BeforeFingerprint?.Count ?? -1}, " +
                          $"after.Count={result.AfterFingerprint?.Count ?? -1}");
            if (result.BeforeFingerprint != null && result.AfterFingerprint != null)
            {
                int minCount = Math.Min(result.BeforeFingerprint.Count, result.AfterFingerprint.Count);
                for (int i = 0; i < minCount; i++)
                {
                    var b = result.BeforeFingerprint[i];
                    var a = result.AfterFingerprint[i];
                    if (b.id != a.id || b.amount != a.amount || b.quality != a.quality ||
                        b.x != a.x || b.y != a.y || b.rot != a.rot ||
                        !StateBytesEqual(b.state, a.state))
                    {
                        sb.AppendLine($"首个不匹配 index={i}: " +
                                      $"before(id={b.id},amt={b.amount},q={b.quality},x={b.x},y={b.y},rot={b.rot}) " +
                                      $"vs after(id={a.id},amt={a.amount},q={a.quality},x={a.x},y={a.y},rot={a.rot})");
                        break;
                    }
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>故障注入计划：精确指定目标页、目标 Commit 序号、写入类型、步骤。</summary>
    internal struct FaultPlan
    {
        public byte TargetPage;
        public int TargetCommitOrdinal;
        public WriteKind Kind;
        public int Step;
        public bool Triggered;
    }

    internal enum WriteKind : byte
    {
        RemoveItem = 0,
        AddItem = 1,
    }
}
#endif
