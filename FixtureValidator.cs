#if TIDY_TEST_HARNESS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SDG.Unturned;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// v2.0.6.13 Codex 第三轮复审 §3.2 重写：
    /// 测试前置 fixture 验证 - 同 ID 不同 quality/state 实例级指纹 + 完整 fixture 契约。
    ///
    /// 第三轮修订（Codex 第三轮 §3.2 蓝图）：
    ///   - 新增 BoundItemFingerprint：完整 (id, amount, quality, state) 实例级指纹
    ///   - TryCaptureRequiredHotkeys：键 3/7 必须指向相同 ID、不同 quality 或 state 的两件物品
    ///   - VerifyHotkeyCase：整理前后 CaptureBoundFingerprints 序列相等（实例级一致性）
    ///   - 不再要求"两个不同 ID"：这正是坐标映射快捷键恢复的风险点
    ///
    /// 第二轮已有（保留）：
    ///   - HasReply + RestoredCount == 2 + VerifiedCount == 2 + ClearedCount == 0 + FailedCount == 0
    ///   - 不满足 fixture -> BLOCKED（不是 PASS/SKIPPED）
    ///
    /// Unturned PlayerInventory 页面索引（U3-SDK 事实）：
    ///   page 2 = SLOTS, page 3 = BACKPACK, page 4 = VEST, page 5 = SHIRT, page 6 = PANTS, page 7 = STORAGE
    ///
    /// HotkeyIndex 映射：0=键3, 1=键4, 2=键5, 3=键6, 4=键7, 5=键8, 6=键9, 7=键0
    /// </summary>
    internal static class FixtureValidator
    {
        private const byte PAGE_SLOTS = 2;
        private const byte PAGE_BACKPACK = 3;
        private const byte PAGE_VEST = 4;
        private const byte PAGE_SHIRT = 5;
        private const byte PAGE_PANTS = 6;

        /// <summary>HotkeyIndex 0 对应数字键 3。</summary>
        public const byte Key3Index = 0;
        /// <summary>HotkeyIndex 4 对应数字键 7。</summary>
        public const byte Key7Index = 4;

        /// <summary>
        /// v2.0.6.13 第三轮 §3.2：实例级完整指纹。
        /// HotkeyInfo 没有 instance ID，必须靠 (id, amount, quality, state) 完整比对
        /// 才能区分"同 ID 多实例"中具体哪一件。
        /// </summary>
        internal readonly struct BoundItemFingerprint : IEquatable<BoundItemFingerprint>
        {
            internal readonly ushort Id;
            internal readonly byte Amount;
            internal readonly byte Quality;
            internal readonly byte[] State;

            internal BoundItemFingerprint(Item item)
            {
                if (item == null)
                {
                    Id = 0; Amount = 0; Quality = 0; State = Array.Empty<byte>();
                    return;
                }
                Id = item.id;
                Amount = item.amount;
                Quality = item.quality;
                State = item.state == null ? Array.Empty<byte>() : (byte[])item.state.Clone();
            }

            public bool Equals(BoundItemFingerprint other)
            {
                if (Id != other.Id || Amount != other.Amount || Quality != other.Quality)
                    return false;
                if (State == null || other.State == null) return State == other.State;
                return State.SequenceEqual(other.State);
            }

            public override int GetHashCode()
            {
                int hash = (Id << 16) | (Amount << 8) | Quality;
                if (State != null)
                {
                    for (int i = 0; i < State.Length && i < 8; i++)
                        hash = (hash * 31) ^ State[i];
                }
                return hash;
            }

            public override string ToString()
            {
                string stateHex = State == null ? "null" :
                    (State.Length == 0 ? "empty" : BitConverter.ToString(State).Replace("-", ""));
                return $"id={Id},amount={Amount},quality={Quality},state={stateHex}";
            }
        }

        /// <summary>验证 SP-CONS 必需 fixture。返回 false 时 out failure 描述原因。</summary>
        public static bool TryValidateAllRequiredShapes(PlayerInventory inv, out string failure)
        {
            var sb = new StringBuilder();
            if (inv?.items == null)
            {
                failure = "PlayerInventory.items 为 null";
                return false;
            }

            // page 2 必须有 >= 4 件物品（混合尺寸覆盖）
            try
            {
                Items slots = inv.items[PAGE_SLOTS];
                if (slots == null || slots.width == 0 || slots.height == 0)
                {
                    sb.AppendLine("page 2 (SLOTS) items 为 null 或 0×0；");
                }
                else
                {
                    byte count = slots.getItemCount();
                    if (count < 4)
                        sb.AppendLine($"page 2 (SLOTS) 物品数 {count} < 4（必需 >= 4 件）；");

                    // v2.0.6.13 第三轮 §2.3：验证混合尺寸 - 至少有一件非 1×1 物品
                    bool hasNonTrivialSize = false;
                    var sizeSet = new HashSet<string>();
                    for (byte i = 0; i < count; i++)
                    {
                        ItemJar jar = slots.getItem(i);
                        if (jar?.item == null) continue;
                        int sx = jar.size_x, sy = jar.size_y;
                        sizeSet.Add($"{sx}x{sy}");
                        if (sx > 1 || sy > 1) hasNonTrivialSize = true;
                    }
                    if (sizeSet.Count < 2)
                        sb.AppendLine($"page 2 (SLOTS) 物品尺寸单一（{sizeSet.Count} 种），缺乏混合尺寸覆盖；");
                    if (!hasNonTrivialSize)
                        sb.AppendLine("page 2 (SLOTS) 全部为 1×1 物品，缺乏非平凡尺寸覆盖；");

                    // v2.0.6.13 第三轮 §2.3：验证同 ID 不同 quality/state 多实例
                    if (!HasSameIdDifferentQualityOrState(slots, out string idMultiDetail))
                        sb.AppendLine($"page 2 (SLOTS) 缺少同 ID 不同 quality/state 多实例：{idMultiDetail}；");
                }
            }
            catch (Exception e)
            {
                sb.AppendLine($"page 2 (SLOTS) 读取异常: {e.Message}；");
            }

            // page 4 (VEST) 必须非 null
            if (!IsPageAvailable(inv, PAGE_VEST))
                sb.AppendLine("page 4 (VEST) items 为 null 或 0×0（必须装备战术背心）；");

            // page 5 (SHIRT) 必须非 null
            if (!IsPageAvailable(inv, PAGE_SHIRT))
                sb.AppendLine("page 5 (SHIRT) items 为 null 或 0×0（必须装备衬衫）；");

            // page 6 (PANTS) 必须非 null
            if (!IsPageAvailable(inv, PAGE_PANTS))
                sb.AppendLine("page 6 (PANTS) items 为 null 或 0×0（必须装备裤子）；");

            // 深度回归必须至少覆盖一个已满网格页面：这能覆盖无空闲格、
            // 紧凑布局与重排后仍无重叠的边界条件。只读验证，绝不为测试增删物品。
            if (!HasFullyOccupiedValidPage(inv, out string fullPageDetail))
                sb.AppendLine("缺少满页 fixture：" + fullPageDetail + "；");

            if (sb.Length == 0)
            {
                failure = null;
                return true;
            }
            failure = sb.ToString().TrimEnd();
            return false;
        }

        private static bool IsPageAvailable(PlayerInventory inv, byte page)
        {
            try
            {
                Items items = inv.items[page];
                return items != null && items.width > 0 && items.height > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 验证 page 2..6 中至少有一页被合法物品完整占满。
        /// 这是测试 fixture 条件，不是生产背包校验；遇到越界/重叠的 fixture 一律拒绝，
        /// 防止用非法初始状态伪造“满页”覆盖。
        /// </summary>
        private static bool HasFullyOccupiedValidPage(PlayerInventory inv, out string detail)
        {
            detail = "page 2..6 中没有任何一页被合法物品完整占满";
            if (inv?.items == null) return false;

            for (byte page = PAGE_SLOTS; page <= PAGE_PANTS; page++)
            {
                Items items;
                try { items = inv.items[page]; }
                catch { continue; }
                if (items == null || items.width == 0 || items.height == 0) continue;

                bool[,] occupied = new bool[items.width, items.height];
                bool invalidGeometry = false;
                for (byte i = 0; i < items.getItemCount(); i++)
                {
                    ItemJar jar = items.getItem(i);
                    if (jar?.item == null || jar.size_x == 0 || jar.size_y == 0)
                    {
                        invalidGeometry = true;
                        break;
                    }

                    int width = (jar.rot == 1 || jar.rot == 3) ? jar.size_y : jar.size_x;
                    int height = (jar.rot == 1 || jar.rot == 3) ? jar.size_x : jar.size_y;
                    if (jar.x + width > items.width || jar.y + height > items.height)
                    {
                        invalidGeometry = true;
                        break;
                    }

                    for (int dx = 0; dx < width && !invalidGeometry; dx++)
                    for (int dy = 0; dy < height; dy++)
                    {
                        int x = jar.x + dx;
                        int y = jar.y + dy;
                        if (occupied[x, y])
                        {
                            invalidGeometry = true;
                            break;
                        }
                        occupied[x, y] = true;
                    }
                }

                if (invalidGeometry)
                {
                    detail = $"page {page} fixture 存在越界、重叠或零尺寸物品";
                    return false;
                }

                bool full = true;
                for (int x = 0; x < items.width && full; x++)
                for (int y = 0; y < items.height; y++)
                    if (!occupied[x, y]) { full = false; break; }

                if (full)
                {
                    detail = $"page {page} 为合法满页（{items.width}x{items.height}）";
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// v2.0.6.13 第三轮 §2.3：检查页面内是否存在同 ID 不同 quality 或 state 的多实例。
        /// 这覆盖"同 ID 多实例"歧义场景，正是坐标映射快捷键恢复的风险点。
        /// </summary>
        private static bool HasSameIdDifferentQualityOrState(Items pageItems, out string detail)
        {
            detail = "";
            if (pageItems == null) { detail = "pageItems null"; return false; }
            try
            {
                byte count = pageItems.getItemCount();
                var byId = new Dictionary<ushort, List<BoundItemFingerprint>>();
                for (byte i = 0; i < count; i++)
                {
                    ItemJar jar = pageItems.getItem(i);
                    if (jar?.item == null) continue;
                    var fp = new BoundItemFingerprint(jar.item);
                    if (!byId.TryGetValue(fp.Id, out var list)) { list = new List<BoundItemFingerprint>(); byId[fp.Id] = list; }
                    list.Add(fp);
                }
                foreach (var kv in byId)
                {
                    if (kv.Value.Count < 2) continue;
                    for (int a = 0; a < kv.Value.Count; a++)
                        for (int b = a + 1; b < kv.Value.Count; b++)
                        {
                            var fa = kv.Value[a];
                            var fb = kv.Value[b];
                            bool diffQuality = fa.Quality != fb.Quality;
                            bool diffState = !fa.State.SequenceEqual(fb.State);
                            if (diffQuality || diffState)
                            {
                                detail = $"id={kv.Key} 实例 A({fa}) vs B({fb})";
                                return true;
                            }
                        }
                }
                detail = "未找到同 ID 不同 quality/state 多实例";
                return false;
            }
            catch (Exception e)
            {
                detail = "异常: " + e.Message;
                return false;
            }
        }

        /// <summary>验证 SP-HK 必需 fixture：page 2 至少 2 件可用物品可绑定快捷键。</summary>
        public static bool TryValidateHotkeyFixture(PlayerInventory inv, out string failure)
        {
            if (inv?.items == null)
            {
                failure = "PlayerInventory.items 为 null";
                return false;
            }
            try
            {
                Items slots = inv.items[PAGE_SLOTS];
                if (slots == null || slots.width == 0 || slots.height == 0)
                {
                    failure = "page 2 (SLOTS) items 为 null 或 0×0";
                    return false;
                }
                byte count = slots.getItemCount();
                if (count < 2)
                {
                    failure = $"page 2 (SLOTS) 物品数 {count} < 2（快捷键恢复测试必需 >= 2 件）";
                    return false;
                }
                failure = null;
                return true;
            }
            catch (Exception e)
            {
                failure = "page 2 (SLOTS) 读取异常: " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// v2.0.6.13 第三轮 §3.2 蓝图：捕获并验证必需的快捷键 3/7 绑定 + 实例级指纹。
        /// 隔离存档必须先将两个有效物品绑定到数字键 3（HotkeyIndex 0）与 7（HotkeyIndex 4）。
        ///
        /// v2.0.6.13 第三轮修订（覆盖同 ID 多实例风险点）：
        ///   - 键 3 与键 7 必须指向相同物品 ID（不是不同 ID）
        ///   - 该 ID 的两件物品必须 quality 或 state 不同
        ///   - 这样整理后靠坐标映射恢复快捷键时，若错位绑定到另一件同 ID 实例，指纹比对能立即发现
        ///
        /// 返回 Dictionary&lt;byte, BoundItemFingerprint&gt;：HotkeyIndex -> 完整指纹。
        /// </summary>
        public static bool TryCaptureRequiredHotkeys(out Dictionary<byte, BoundItemFingerprint> expected, out string failure)
        {
            expected = new Dictionary<byte, BoundItemFingerprint>();
            try
            {
                Player player = Player.LocalPlayer;
                if (player?.equipment?.hotkeys == null)
                {
                    failure = "LocalPlayer.equipment.hotkeys 为 null";
                    return false;
                }
                PlayerInventory inv = player.inventory;
                if (inv?.items == null)
                {
                    failure = "LocalPlayer.inventory.items 为 null";
                    return false;
                }

                HotkeyInfo[] hotkeys = player.equipment.hotkeys;
                for (byte i = 0; i < hotkeys.Length && i < HotkeySnapshotUtil.HOTKEY_COUNT; i++)
                {
                    HotkeyInfo info = hotkeys[i];
                    if (info == null || info.id == 0) continue;
                    if (i != Key3Index && i != Key7Index) continue;

                    if (info.page < HotkeySnapshotUtil.TIDYABLE_PAGE_MIN ||
                        info.page > HotkeySnapshotUtil.TIDYABLE_PAGE_MAX) continue;

                    Items pageItems = inv.items[info.page];
                    if (pageItems == null) continue;
                    byte jarIdx = pageItems.getIndex(info.x, info.y);
                    if (jarIdx == byte.MaxValue) continue;
                    ItemJar jar = pageItems.getItem(jarIdx);
                    if (jar?.item == null) continue;
                    if (jar.item.id != info.id) continue;

                    expected[i] = new BoundItemFingerprint(jar.item);
                }

                if (expected.Count != 2)
                {
                    failure = "隔离存档必须先将两个有效物品分别绑定到数字键 3 与 7（当前捕获 " +
                              expected.Count + " 个）";
                    return false;
                }

                var fp3 = expected[Key3Index];
                var fp7 = expected[Key7Index];

                // v2.0.6.13 第三轮 §3.2：必须是相同 ID（覆盖同 ID 多实例映射风险）
                if (fp3.Id != fp7.Id)
                {
                    failure = $"数字键 3 与 7 必须绑定到相同物品 ID（当前键3={fp3.Id}, 键7={fp7.Id}），" +
                              "以覆盖同 ID 多实例的坐标映射快捷键恢复风险";
                    return false;
                }

                // v2.0.6.13 第三轮 §3.2：必须 quality 或 state 不同（区分两件不同实例）
                bool diffQuality = fp3.Quality != fp7.Quality;
                bool diffState = !fp3.State.SequenceEqual(fp7.State);
                if (!diffQuality && !diffState)
                {
                    failure = $"数字键 3 与 7 绑定的物品 ID 相同（{fp3.Id}）但 quality 与 state 完全一致，" +
                              "无法区分两件不同实例（键3={fp3}, 键7={fp7}）";
                    return false;
                }

                failure = null;
                return true;
            }
            catch (Exception e)
            {
                failure = "TryCaptureRequiredHotkeys 异常: " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// v2.0.6.13 第三轮 §3.2 蓝图：验证 SP-HK 用例的快捷键恢复结果（实例级指纹一致性）。
        ///
        /// 要求：
        ///   - flow.HasReply == true
        ///   - RestoredCount == 2（必需绑定数）
        ///   - VerifiedCount == 2
        ///   - ClearedCount == 0
        ///   - FailedCount == 0
        ///   - 整理后 CaptureBoundFingerprints 序列与整理前相等（实例级一致性）
        ///     即键 3/7 仍指向完全相同的 (id, amount, quality, state) 指纹
        /// </summary>
        public static bool VerifyHotkeyCase(
            Dictionary<byte, BoundItemFingerprint> before,
            NetworkTestProbe.HotkeyFlowResult flow,
            out string failure)
        {
            if (!flow.HasReply)
            {
                failure = "HotkeyResult 未收到回包";
                return false;
            }
            if (before == null || before.Count != 2)
            {
                failure = "before fixture 不满足（必需 2 个绑定）";
                return false;
            }
            if (flow.RestoredCount != before.Count)
            {
                failure = $"RestoredCount={flow.RestoredCount} != before.Count={before.Count}";
                return false;
            }
            if (flow.VerifiedCount != before.Count)
            {
                failure = $"VerifiedCount={flow.VerifiedCount} != before.Count={before.Count}";
                return false;
            }
            if (flow.ClearedCount != 0)
            {
                failure = $"ClearedCount={flow.ClearedCount} != 0（快捷键被清除）";
                return false;
            }
            if (flow.FailedCount != 0)
            {
                failure = $"FailedCount={flow.FailedCount} != 0（快捷键恢复失败）";
                return false;
            }

            // v2.0.6.13 第三轮 §3.2：整理后再次捕获实例级指纹，与 before 序列相等
            if (!TryCaptureRequiredHotkeys(out var after, out failure)) return false;

            if (after.Count != before.Count)
            {
                failure = $"after.Count={after.Count} != before.Count={before.Count}";
                return false;
            }

            // 键 3 指纹必须完全一致
            if (!after[Key3Index].Equals(before[Key3Index]))
            {
                failure = $"键 3 指纹变化：before={before[Key3Index]}, after={after[Key3Index]}";
                return false;
            }
            // 键 7 指纹必须完全一致
            if (!after[Key7Index].Equals(before[Key7Index]))
            {
                failure = $"键 7 指纹变化：before={before[Key7Index]}, after={after[Key7Index]}";
                return false;
            }

            failure = null;
            return true;
        }
    }
}
#endif
