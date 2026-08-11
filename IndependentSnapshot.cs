#if TIDY_TEST_HARNESS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;
using SDG.Unturned;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// v2.0.6.13 Codex 第二轮复审 §3.1 重写：
    /// 全页独立只读快照 - 真实网格几何（PageGeometry + ItemRecord SizeX/SizeY）。
    ///
    /// v2.0.6.13 第一轮缺陷（Codex 第二轮 §1 Critical）：
    ///   - AllPagesInBoundsAndNonOverlapping 仅比较同一页的左上角 (x,y)
    ///   - byte < 0 永远无意义
    ///   - 未读取 Items.width/height，未使用 jar.size_x/size_y
    ///   - 两件 2×2 物品可拥有不同左上角但重叠三个格子，被误判为合法
    ///
    /// 第二轮修复（§3.1 蓝图）：
    ///   - PageGeometry 记录每页真实 width/height（从 Items 实例读取）
    ///   - ItemRecord 新增 SizeX/SizeY（从 ItemJar.size_x/size_y 读取）
    ///   - HasValidGeometry 按旋转占用所有网格并检查边界 + 重叠
    ///   - 不再使用左上角代替物品占用面积
    /// </summary>
    internal static class IndependentSnapshot
    {
        // PlayerInventory.SLOTS=2, PlayerInventory.PANTS=6（共 5 页：2,3,4,5,6）
        private const byte FIRST_PAGE = 2;
        private const byte LAST_PAGE = 6;

        /// <summary>页面几何（真实宽高，从 Items 实例读取）。</summary>
        public struct PageGeometry
        {
            public byte Page;
            public byte Width;
            public byte Height;
        }

        /// <summary>单件物品记录（值类型，深拷贝）。</summary>
        public struct ItemRecord
        {
            public byte Page;
            public byte X;
            public byte Y;
            public byte Rot;
            public ushort Id;
            public byte Amount;
            public byte Quality;
            public byte SizeX;
            public byte SizeY;
            public byte[] State;  // 深拷贝
        }

        /// <summary>全页快照结果。</summary>
        public sealed class FullInventorySnapshot
        {
            public DateTime CapturedAtUtc;
            public List<ItemRecord> Items;  // 按 (page, x, y) 排序
            public List<PageGeometry> Pages;  // v2.0.6.13 第二轮：真实页面尺寸
        }

        /// <summary>
        /// 一次性捕获 page 2..6 全部物品 + 真实页面几何。
        /// 缺失页面（items=null 或 width=0 或 height=0）跳过，不抛异常。
        /// </summary>
        public static FullInventorySnapshot CaptureAllPages(PlayerInventory inv)
        {
            var snap = new FullInventorySnapshot
            {
                CapturedAtUtc = DateTime.UtcNow,
                Items = new List<ItemRecord>(64),
                Pages = new List<PageGeometry>(5),
            };

            if (inv?.items == null) return snap;

            for (byte page = FIRST_PAGE; page <= LAST_PAGE; page++)
            {
                try
                {
                    Items items = inv.items[page];
                    if (items == null || items.width == 0 || items.height == 0) continue;

                    snap.Pages.Add(new PageGeometry
                    {
                        Page = page,
                        Width = items.width,
                        Height = items.height,
                    });

                    byte count = items.getItemCount();
                    for (byte i = 0; i < count; i++)
                    {
                        ItemJar jar = items.getItem(i);
                        if (jar?.item == null) continue;

                        byte[] stateCopy = jar.item.state == null ? null : (byte[])jar.item.state.Clone();
                        snap.Items.Add(new ItemRecord
                        {
                            Page = page,
                            X = jar.x,
                            Y = jar.y,
                            Rot = jar.rot,
                            Id = jar.item.id,
                            Amount = jar.item.amount,
                            Quality = jar.item.quality,
                            SizeX = jar.size_x,
                            SizeY = jar.size_y,
                            State = stateCopy,
                        });
                    }
                }
                catch (Exception e)
                {
                    LaunchInventoryTidyPlugin.Log?.LogWarning(
                        $"[IndependentSnapshot] CaptureAllPages page={page} 异常: {e.Message}");
                }
            }

            snap.Items.Sort(CompareItemRecord);
            return snap;
        }

        private static int CompareItemRecord(ItemRecord a, ItemRecord b)
        {
            int c = a.Page.CompareTo(b.Page);
            if (c != 0) return c;
            c = a.X.CompareTo(b.X);
            if (c != 0) return c;
            return a.Y.CompareTo(b.Y);
        }

        /// <summary>
        /// 守恒比较：id+amount+quality+state 多重集合完全一致（忽略坐标和旋转）。
        /// state 字节[] 深比较。
        /// </summary>
        public static bool SameItemMultiset(FullInventorySnapshot before, FullInventorySnapshot after)
        {
            if (before == null || after == null) return false;
            if (ReferenceEquals(before, after)) return true;
            if (before.Items.Count != after.Items.Count) return false;

            var beforeKeys = new List<string>(before.Items.Count);
            var afterKeys = new List<string>(after.Items.Count);

            for (int i = 0; i < before.Items.Count; i++)
                beforeKeys.Add(MakeFingerprintKey(before.Items[i]));
            for (int i = 0; i < after.Items.Count; i++)
                afterKeys.Add(MakeFingerprintKey(after.Items[i]));

            beforeKeys.Sort(StringComparer.Ordinal);
            afterKeys.Sort(StringComparer.Ordinal);

            for (int i = 0; i < beforeKeys.Count; i++)
                if (beforeKeys[i] != afterKeys[i]) return false;
            return true;
        }

        private static string MakeFingerprintKey(ItemRecord r)
        {
            string stateHash = r.State == null ? "0" : BitConverter.ToString(r.State).Replace("-", "");
            return $"{r.Id}|{r.Amount}|{r.Quality}|{stateHash}";
        }

        /// <summary>
        /// v2.0.6.13 第二轮 §3.1 重写：真实网格几何验证。
        /// 检查每件物品按旋转占用所有网格：
        ///   - 边界：X + effWidth <= pageWidth, Y + effHeight <= pageHeight
        ///   - 重叠：所有占用的网格格子不得重复
        ///   - 物品尺寸有效：SizeX > 0 且 SizeY > 0
        /// </summary>
        public static bool AllPagesInBoundsAndNonOverlapping(FullInventorySnapshot snap)
        {
            if (snap == null) return false;

            var geometry = new Dictionary<byte, PageGeometry>();
            for (int i = 0; i < snap.Pages.Count; i++)
                geometry[snap.Pages[i].Page] = snap.Pages[i];

            var occupied = new HashSet<string>();
            for (int i = 0; i < snap.Items.Count; i++)
            {
                var r = snap.Items[i];
                if (r.SizeX == 0 || r.SizeY == 0) return false;

                if (!geometry.TryGetValue(r.Page, out PageGeometry page)) return false;

                int effWidth = (r.Rot == 1 || r.Rot == 3) ? r.SizeY : r.SizeX;
                int effHeight = (r.Rot == 1 || r.Rot == 3) ? r.SizeX : r.SizeY;

                if (effWidth <= 0 || effHeight <= 0) return false;
                if (r.X + effWidth > page.Width) return false;
                if (r.Y + effHeight > page.Height) return false;

                for (int dx = 0; dx < effWidth; dx++)
                {
                    for (int dy = 0; dy < effHeight; dy++)
                    {
                        string key = r.Page + ":" + (r.X + dx) + ":" + (r.Y + dy);
                        if (!occupied.Add(key)) return false;
                    }
                }
            }
            return true;
        }

        /// <summary>将快照写入规范化 JSON 文件（UTF-8，无 BOM，缩进）。</summary>
        public static string WriteCanonicalJson(string suitePrefix, string phase, FullInventorySnapshot snap)
        {
            try
            {
                string pluginDir = Path.GetDirectoryName(
                    typeof(LaunchInventoryTidyPlugin).Assembly.Location);
                string exportDir = Path.Combine(pluginDir, ".lit_autotest");
                Directory.CreateDirectory(exportDir);

                string fileName = $"{suitePrefix}_{phase}.json";
                string fullPath = Path.Combine(exportDir, fileName);

                var dto = new
                {
                    capturedAtUtc = snap.CapturedAtUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    pages = ConvertPagesToDto(snap.Pages),
                    itemCount = snap.Items.Count,
                    items = ConvertToDto(snap.Items),
                };

                string json = JsonConvert.SerializeObject(dto, Formatting.Indented);
                File.WriteAllText(fullPath, json, new System.Text.UTF8Encoding(false));
                return fullPath;
            }
            catch (Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogError(
                    $"[IndependentSnapshot] WriteCanonicalJson 异常: {e}");
                throw;
            }
        }

        private static List<object> ConvertPagesToDto(List<PageGeometry> pages)
        {
            var list = new List<object>(pages.Count);
            foreach (var p in pages)
                list.Add(new { page = p.Page, width = p.Width, height = p.Height });
            return list;
        }

        private static List<object> ConvertToDto(List<ItemRecord> items)
        {
            var list = new List<object>(items.Count);
            foreach (var r in items)
            {
                string stateStr = r.State == null ? "" : BitConverter.ToString(r.State).Replace("-", "");
                list.Add(new
                {
                    page = r.Page,
                    x = r.X,
                    y = r.Y,
                    rot = r.Rot,
                    id = r.Id,
                    amount = r.Amount,
                    quality = r.Quality,
                    sizeX = r.SizeX,
                    sizeY = r.SizeY,
                    state = stateStr,
                });
            }
            return list;
        }

        /// <summary>计算文件 SHA-256（大写）。</summary>
        public static string ComputeFileSha256(string path)
        {
            if (!File.Exists(path)) return string.Empty;
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(fs);
                return BitConverter.ToString(hash).Replace("-", "");
            }
        }

        // ─────────────────────────────────────────────────────────────
        // v2.0.6.13 Codex 第五轮 §3.2：精确布局回滚断言 + 稳定内容哈希
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// v2.0.6.13 第五轮 §3.2：精确布局等同性比较。
        /// 比较 page 集合（Page/Width/Height）+ 每件物品的 (page,x,y,rot,id,amount,quality,sizeX,sizeY,state)。
        /// 不含 capturedAtUtc，纯状态比较。
        /// </summary>
        internal static bool SameExactLayout(FullInventorySnapshot before, FullInventorySnapshot after)
        {
            if (before == null || after == null) return false;
            if (ReferenceEquals(before, after)) return true;
            if (before.Pages.Count != after.Pages.Count || before.Items.Count != after.Items.Count)
                return false;

            var leftPages = before.Pages.OrderBy(p => p.Page).ToList();
            var rightPages = after.Pages.OrderBy(p => p.Page).ToList();
            for (int i = 0; i < leftPages.Count; i++)
            {
                if (leftPages[i].Page != rightPages[i].Page ||
                    leftPages[i].Width != rightPages[i].Width ||
                    leftPages[i].Height != rightPages[i].Height)
                    return false;
            }

            var left = before.Items.OrderBy(r => r.Page).ThenBy(r => r.X).ThenBy(r => r.Y).ThenBy(r => r.Rot).ToList();
            var right = after.Items.OrderBy(r => r.Page).ThenBy(r => r.X).ThenBy(r => r.Y).ThenBy(r => r.Rot).ToList();
            for (int i = 0; i < left.Count; i++)
            {
                ItemRecord a = left[i];
                ItemRecord b = right[i];
                if (a.Page != b.Page || a.X != b.X || a.Y != b.Y || a.Rot != b.Rot ||
                    a.Id != b.Id || a.Amount != b.Amount || a.Quality != b.Quality ||
                    a.SizeX != b.SizeX || a.SizeY != b.SizeY)
                    return false;

                byte[] aState = a.State ?? Array.Empty<byte>();
                byte[] bState = b.State ?? Array.Empty<byte>();
                if (!aState.SequenceEqual(bState)) return false;
            }
            return true;
        }

        /// <summary>
        /// v2.0.6.13 第五轮 §3.2：稳定内容哈希（不含 capturedAtUtc）。
        /// 用于故障注入 before/after 状态等同性证明，文件 SHA-256 只用于归档防篡改。
        /// </summary>
        internal static string ComputeContentSha256(FullInventorySnapshot snapshot)
        {
            if (snapshot == null) return string.Empty;
            var payload = new
            {
                pages = snapshot.Pages.OrderBy(p => p.Page)
                    .Select(p => new { p.Page, p.Width, p.Height }).ToArray(),
                items = snapshot.Items.OrderBy(i => i.Page).ThenBy(i => i.X).ThenBy(i => i.Y).ThenBy(i => i.Rot)
                    .Select(i => new
                    {
                        i.Page, i.X, i.Y, i.Rot, i.Id, i.Amount, i.Quality, i.SizeX, i.SizeY,
                        State = BitConverter.ToString(i.State ?? Array.Empty<byte>())
                    }).ToArray()
            };

            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(
                JsonConvert.SerializeObject(payload, Formatting.None));
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }
    }
}
#endif
