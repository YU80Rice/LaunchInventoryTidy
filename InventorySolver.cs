using System;
using System.Collections.Generic;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// 背包自动整理算法的输入/输出物品表示。
    /// 该类刻意不引用任何 Unity / Unturned 类型，保持算法层纯净，
    /// 便于独立单元测试与跨项目复用。
    /// </summary>
    public class PackableItem
    {
        /// <summary>
        /// 算法不解释的附加数据；调用方通常在此存放原始 ItemJar 引用，
        /// 算法输出后由调用方读取 Tag 并把坐标写回游戏对象。
        /// </summary>
        public object Tag;

        /// <summary>物品原始（未旋转）宽度（占用的列数）。</summary>
        public byte size_x;

        /// <summary>物品原始（未旋转）高度（占用的行数）。</summary>
        public byte size_y;

        /// <summary>算法输出：在网格中的左上角列坐标。</summary>
        public byte ResultX;

        /// <summary>算法输出：在网格中的左上角行坐标。</summary>
        public byte ResultY;

        /// <summary>算法输出：0 = 不旋转，1 = 旋转 90度（与 Unturned 的 rot 字段语义一致）。</summary>
        public byte ResultRot;

        /// <summary>
        /// 算法输出：true = 已成功放置（ResultX/Y/Rot 有效）；
        /// false = 未放置（尺寸异常或网格装不下），调用方应保留原位。
        /// </summary>
        public bool Placed;
    }

    /// <summary>
    /// 整理算法模式选择。
    /// </summary>
    public enum TidyMode : byte
    {
        /// <summary>C：剩余大矩形优先（MaxRects + BSSF + 矩形分裂），剩余空间成大块。</summary>
        MaxRects = 0,

        /// <summary>D：大件优先贪心（FFD First-Fit 行主序扫描），当前默认行为。</summary>
        FFD = 1,
    }

    /// <summary>
    /// 2D 装箱求解器，支持两种模式：
    ///  - MaxRects（C 优先级）：维护剩余矩形列表，BSSF 选择最佳短边拟合，
    ///    放置后分裂矩形，保证剩余空间为若干大矩形而非碎片。
    ///  - FFD（D 优先级）：贪心 First-Fit 行主序扫描，物品放置后剩余空间易碎片化。
    ///
    /// 算法特性：
    ///  - 纯 C#，零外部依赖，可在任何 .NET 环境（含单元测试）运行。
    ///  - 不修改输入 items 的现有字段；所有结果写入 PackableItem.ResultX/Y/Rot/Placed。
    ///  - 排序策略：先按面积，面积相同按长边，再按 size_x。
    ///    sortDescending=true 时三者均降序（默认，大件优先）；
    ///    sortDescending=false 时均升序（小件优先，FFI）。
    ///  - 单格物品跳过旋转尝试以减少分支因子。
    ///  - 异常物品（size=0 或尺寸超容器）Placed=false，调用方保留原位。
    /// </summary>
    public static class InventorySolver
    {
        /// <summary>
        /// 尝试将所有物品装入 width x height 的虚拟网格。
        ///
        /// 返回值：
        ///   - true = 所有合法物品均已成功放置（异常物品 Placed=false 但不影响整体）
        ///   - false = 部分合法物品未放置（调用方可根据 Placed 标志决定部分重排或放弃）
        ///
        /// result 总是包含所有输入物品（含异常），调用方根据 Placed 区分处理。
        /// </summary>
        public static bool TryPack(byte width, byte height, List<PackableItem> items,
                                    out List<PackableItem> result, bool sortDescending = true,
                                    TidyMode mode = TidyMode.MaxRects)
        {
            result = null;
            if (items == null || items.Count == 0)
            {
                result = new List<PackableItem>(0);
                return true;
            }

            // 边界：网格尺寸为 0 直接失败
            if (width == 0 || height == 0) return false;

            // -- 步骤 1：复制 + 标记异常物品 --
            var sorted = new List<PackableItem>(items);
            int validCount = 0;
            for (int i = 0; i < sorted.Count; i++)
            {
                PackableItem it = sorted[i];
                if (it == null) continue;

                it.Placed = false;
                it.ResultX = 0;
                it.ResultY = 0;
                it.ResultRot = 0;

                bool isValid = it.size_x > 0 && it.size_y > 0
                    && (FitsGrid(it.size_x, it.size_y, width, height)
                        || FitsGrid(it.size_y, it.size_x, width, height));
                if (isValid) validCount++;
            }

            // -- 步骤 2：排序（两种模式共用）--
            sorted.Sort((a, b) =>
            {
                bool aInvalid = a == null || a.size_x == 0 || a.size_y == 0;
                bool bInvalid = b == null || b.size_x == 0 || b.size_y == 0;
                if (aInvalid && bInvalid) return 0;
                if (aInvalid) return 1;
                if (bInvalid) return -1;

                long areaA = (long)a.size_x * a.size_y;
                long areaB = (long)b.size_x * b.size_y;
                if (areaA != areaB)
                    return sortDescending ? areaB.CompareTo(areaA) : areaA.CompareTo(areaB);
                int longA = Math.Max(a.size_x, a.size_y);
                int longB = Math.Max(b.size_x, b.size_y);
                if (longA != longB)
                    return sortDescending ? longB.CompareTo(longA) : longA.CompareTo(longB);
                return sortDescending ? b.size_x.CompareTo(a.size_x) : a.size_x.CompareTo(b.size_x);
            });

            // -- 步骤 3：按模式分发 --
            int placedCount;
            if (mode == TidyMode.MaxRects)
            {
                placedCount = TryPackMaxRects(sorted, width, height);
            }
            else
            {
                placedCount = TryPackFFD(sorted, width, height);
            }

            result = sorted;
            return placedCount == validCount;
        }

        // ─────────────────────────────────────────────────────────────
        // D 模式：FFD First-Fit 行主序扫描（原算法）
        // ─────────────────────────────────────────────────────────────

        private static int TryPackFFD(List<PackableItem> sorted, byte width, byte height)
        {
            bool[,] virtualGrid = new bool[width, height];
            int placedCount = 0;
            for (int idx = 0; idx < sorted.Count; idx++)
            {
                PackableItem current = sorted[idx];
                if (current == null || current.size_x == 0 || current.size_y == 0) continue;

                if (TryPlaceFirstFit(current, virtualGrid, width, height))
                {
                    placedCount++;
                }
            }
            return placedCount;
        }

        /// <summary>
        /// 判断原始尺寸 (sx, sy) 在不旋转的情况下能否塞进 width x height 网格。
        /// </summary>
        private static bool FitsGrid(byte sx, byte sy, byte width, byte height)
        {
            return sx <= width && sy <= height;
        }

        /// <summary>
        /// 贪心 First-Fit：遍历所有空格（行主序），找第一个能放下 item 的位置。
        /// 找到则标记占用、写回 ResultX/Y/Rot/Placed=true；找不到则 Placed=false。
        /// </summary>
        private static bool TryPlaceFirstFit(PackableItem item, bool[,] virtualGrid, byte width, byte height)
        {
            for (byte y = 0; y < height; y++)
            {
                for (byte x = 0; x < width; x++)
                {
                    if (virtualGrid[x, y]) continue;

                    if (TryPlace(item, x, y, rot: 0, virtualGrid, width, height))
                    {
                        item.ResultX = x;
                        item.ResultY = y;
                        item.ResultRot = 0;
                        item.Placed = true;
                        return true;
                    }

                    if (item.size_x != item.size_y &&
                        TryPlace(item, x, y, rot: 1, virtualGrid, width, height))
                    {
                        item.ResultX = x;
                        item.ResultY = y;
                        item.ResultRot = 1;
                        item.Placed = true;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 尝试把一个物品（按指定 rot）放在 (startX, startY)，并在合法时标记占用矩阵。
        /// </summary>
        private static bool TryPlace(PackableItem item, byte startX, byte startY, byte rot,
                                     bool[,] virtualGrid, byte width, byte height)
        {
            byte w = item.size_x;
            byte h = item.size_y;
            if ((rot & 1) == 1)
            {
                w = item.size_y;
                h = item.size_x;
            }

            int endX = (int)startX + w;
            int endY = (int)startY + h;
            if (endX > width || endY > height) return false;

            for (int x = startX; x < endX; x++)
            {
                for (int y = startY; y < endY; y++)
                {
                    if (virtualGrid[x, y]) return false;
                }
            }

            for (int x = startX; x < endX; x++)
            {
                for (int y = startY; y < endY; y++)
                {
                    virtualGrid[x, y] = true;
                }
            }
            return true;
        }

        // ─────────────────────────────────────────────────────────────
        // C 模式：MaxRects（BSSF + 矩形分裂）
        // ─────────────────────────────────────────────────────────────

        /// <summary>剩余矩形（坐标使用 int 以便分裂运算，网格实际尺寸 &lt;= 255）。</summary>
        private struct Rect
        {
            public int x, y, w, h;
            public Rect(int x, int y, int w, int h) { this.x = x; this.y = y; this.w = w; this.h = h; }
        }

        /// <summary>
        /// MaxRects 装箱：维护剩余矩形列表，每个物品用 BSSF 选最佳矩形，
        /// 放置后分裂被占用的矩形，清理被包含的矩形。
        /// 保证剩余空间为若干大矩形而非碎片。
        /// </summary>
        private static int TryPackMaxRects(List<PackableItem> sorted, byte width, byte height)
        {
            var freeRects = new List<Rect> { new Rect(0, 0, width, height) };
            int placedCount = 0;

            for (int idx = 0; idx < sorted.Count; idx++)
            {
                PackableItem current = sorted[idx];
                if (current == null || current.size_x == 0 || current.size_y == 0) continue;

                if (TryPlaceMaxRectsBSSF(current, freeRects))
                {
                    placedCount++;
                }
            }
            return placedCount;
        }

        /// <summary>
        /// BSSF（Best Short Side Fit）：在 freeRects 中找最佳剩余矩形放置物品。
        /// 短边剩余最小者优先，tie-break 取长边剩余最小者。
        /// 尝试 rot=0 和 rot=1（正方形跳过旋转）。
        /// </summary>
        private static bool TryPlaceMaxRectsBSSF(PackableItem item, List<Rect> freeRects)
        {
            int bestShortSide = int.MaxValue;
            int bestLongSide = int.MaxValue;
            int bestRectIndex = -1;
            byte bestRot = 0;

            for (int i = 0; i < freeRects.Count; i++)
            {
                Rect r = freeRects[i];
                // rot=0
                if (item.size_x <= r.w && item.size_y <= r.h)
                {
                    int leftoverW = r.w - item.size_x;
                    int leftoverH = r.h - item.size_y;
                    int shortSide = Math.Min(leftoverW, leftoverH);
                    int longSide = Math.Max(leftoverW, leftoverH);
                    if (shortSide < bestShortSide ||
                        (shortSide == bestShortSide && longSide < bestLongSide))
                    {
                        bestShortSide = shortSide;
                        bestLongSide = longSide;
                        bestRectIndex = i;
                        bestRot = 0;
                    }
                }
                // rot=1（正方形跳过）
                if (item.size_x != item.size_y &&
                    item.size_y <= r.w && item.size_x <= r.h)
                {
                    int leftoverW = r.w - item.size_y;
                    int leftoverH = r.h - item.size_x;
                    int shortSide = Math.Min(leftoverW, leftoverH);
                    int longSide = Math.Max(leftoverW, leftoverH);
                    if (shortSide < bestShortSide ||
                        (shortSide == bestShortSide && longSide < bestLongSide))
                    {
                        bestShortSide = shortSide;
                        bestLongSide = longSide;
                        bestRectIndex = i;
                        bestRot = 1;
                    }
                }
            }

            if (bestRectIndex < 0) return false;

            // 放置到最佳矩形左上角
            Rect best = freeRects[bestRectIndex];
            item.ResultX = (byte)best.x;
            item.ResultY = (byte)best.y;
            item.ResultRot = bestRot;
            item.Placed = true;

            byte pw = bestRot == 0 ? item.size_x : item.size_y;
            byte ph = bestRot == 0 ? item.size_y : item.size_x;

            // 分裂最佳矩形为最多 4 个新剩余矩形（上/下/左/右）
            Rect placed = new Rect(best.x, best.y, pw, ph);
            freeRects.RemoveAt(bestRectIndex);
            SplitRect(best, placed, freeRects);
            PruneContainedRects(freeRects);
            return true;
        }

        /// <summary>
        /// 将 outer 矩形减去 inner 矩形，产生最多 4 个剩余矩形（上/下/左/右）。
        /// </summary>
        private static void SplitRect(Rect outer, Rect inner, List<Rect> output)
        {
            // 上边（inner 上方还有空间）
            if (inner.y > outer.y)
            {
                output.Add(new Rect(outer.x, outer.y, outer.w, inner.y - outer.y));
            }
            // 下边（inner 下方还有空间）
            int innerBottom = inner.y + inner.h;
            int outerBottom = outer.y + outer.h;
            if (innerBottom < outerBottom)
            {
                output.Add(new Rect(outer.x, innerBottom, outer.w, outerBottom - innerBottom));
            }
            // 左边（inner 左方还有空间）
            if (inner.x > outer.x)
            {
                output.Add(new Rect(outer.x, inner.y, inner.x - outer.x, inner.h));
            }
            // 右边（inner 右方还有空间）
            int innerRight = inner.x + inner.w;
            int outerRight = outer.x + outer.w;
            if (innerRight < outerRight)
            {
                output.Add(new Rect(innerRight, inner.y, outerRight - innerRight, inner.h));
            }
        }

        /// <summary>
        /// 清理被其他矩形包含的矩形（含完全相同的矩形，保留其一）。
        /// </summary>
        private static void PruneContainedRects(List<Rect> rects)
        {
            for (int i = 0; i < rects.Count; i++)
            {
                for (int j = rects.Count - 1; j > i; j--)
                {
                    if (Contains(rects[i], rects[j]))
                    {
                        rects.RemoveAt(j);
                    }
                    else if (Contains(rects[j], rects[i]))
                    {
                        rects.RemoveAt(i);
                        i--;
                        break;
                    }
                }
            }
        }

        /// <summary>outer 是否包含 inner（含等于）。</summary>
        private static bool Contains(Rect outer, Rect inner)
        {
            return inner.x >= outer.x && inner.y >= outer.y
                && inner.x + inner.w <= outer.x + outer.w
                && inner.y + inner.h <= outer.y + outer.h;
        }
    }
}
