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
    }

    /// <summary>
    /// 基于 FFD/FFI（First-Fit Decreasing / Increasing）启发式 + DFS 回溯的 2D 装箱求解器。
    ///
    /// 算法特性：
    ///  - 纯 C#，零外部依赖，可在任何 .NET 环境（含单元测试）运行。
    ///  - 不修改输入 items 的现有字段；所有结果写入 PackableItem.ResultX/Y/Rot。
    ///  - 回溯深度受物品数量限制；典型背包场景（&lt;=50 物品）可在毫秒级完成。
    ///  - 排序策略：先按面积，面积相同按长边，再按 size_x（FFD/FFI 经典启发式）。
    ///    sortDescending=true 时三者均降序（默认，大件优先）；
    ///    sortDescending=false 时均升序（小件优先，FFI）。
    ///  - 单格物品跳过旋转尝试以减少分支因子。
    /// </summary>
    public static class InventorySolver
    {
        /// <summary>
        /// 尝试将所有物品装入 width x height 的虚拟网格。
        /// 成功时把每个物品的 ResultX/ResultY/ResultRot 写好，并按排序后的顺序返回；
        /// 失败返回 false（result 为 null，调用方无需读取）。
        ///
        /// sortDescending=true（默认）按从大到小装填（大件优先，FFD 经典）；
        /// sortDescending=false 按从小到大装填（小件优先，FFI）。
        /// </summary>
        public static bool TryPack(byte width, byte height, List<PackableItem> items,
                                    out List<PackableItem> result, bool sortDescending = true)
        {
            result = null;
            if (items == null || items.Count == 0)
            {
                // 空集合视为已装好
                result = new List<PackableItem>(0);
                return true;
            }

            // 边界：网格尺寸为 0 直接失败
            if (width == 0 || height == 0) return false;

            // -- 步骤 1：快速粗筛 --
            // 面积总和超过网格容量，物理上不可能装下，立即失败。
            long totalArea = 0;
            for (int i = 0; i < items.Count; i++)
            {
                PackableItem it = items[i];
                if (it == null || it.size_x == 0 || it.size_y == 0) return false;
                // 两种旋转都放不下，直接失败（同时排除越界物品）
                if (!FitsGrid(it.size_x, it.size_y, width, height)
                    && !FitsGrid(it.size_y, it.size_x, width, height))
                {
                    return false;
                }
                totalArea += (long)it.size_x * it.size_y;
            }
            if (totalArea > (long)width * height) return false;

            // -- 步骤 2：FFD/FFI 排序 --
            // 复制一份，避免污染调用方传入的列表顺序。
            // 排序键：面积 -> 长边 -> size_x（稳定 tie-break）
            // sortDescending=true 时三者均降序（大件优先）；false 时均升序（小件优先）。
            var sorted = new List<PackableItem>(items);
            sorted.Sort((a, b) =>
            {
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

            // -- 步骤 3：建立虚拟网格 --
            // 索引约定：virtualGrid[x, y]，x = 列索引（0..width-1），y = 行索引（0..height-1）。
            bool[,] virtualGrid = new bool[width, height];

            // -- 步骤 4：DFS 回溯 --
            if (PlaceItem(sorted, 0, virtualGrid, width, height))
            {
                result = sorted;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 判断原始尺寸 (sx, sy) 在不旋转的情况下能否塞进 width x height 网格。
        /// </summary>
        private static bool FitsGrid(byte sx, byte sy, byte width, byte height)
        {
            return sx <= width && sy <= height;
        }

        /// <summary>
        /// DFS 回溯核心：尝试把 sorted[index] 放入网格，并递归处理后续物品。
        /// 成功时对应物品的 ResultX/Y/Rot 已写好；失败时所有写入痕迹会被清除。
        /// </summary>
        private static bool PlaceItem(List<PackableItem> sorted, int index, bool[,] virtualGrid, byte width, byte height)
        {
            // 出口条件：所有物品均已成功放入
            if (index == sorted.Count) return true;

            PackableItem current = sorted[index];

            // 寻找网格中第一个（按行主序）未被占用的格子作为锚点。
            // 这是 FFD/FFI "First-Fit" 的行为：填底层、靠左侧优先。
            // 行主序扫描：先固定 y（行），再遍历 x（列）。
            byte startX = 0, startY = 0;
            bool foundEmpty = false;
            for (byte y = 0; y < height && !foundEmpty; y++)
            {
                for (byte x = 0; x < width; x++)
                {
                    if (!virtualGrid[x, y])
                    {
                        startX = x;
                        startY = y;
                        foundEmpty = true;
                        break;
                    }
                }
            }

            // 没找到空格有两种可能：
            //  - 网格全部被占满 -> 如果还有物品要放，直接失败
            //  - 物品数为 0（不会走到这里，因为 index != count 才进入本方法）
            if (!foundEmpty) return false;

            // -- 尝试 A：不旋转 (Rot = 0) --
            if (TryPlace(current, startX, startY, rot: 0, virtualGrid, width, height))
            {
                if (PlaceItem(sorted, index + 1, virtualGrid, width, height))
                {
                    current.ResultX = startX;
                    current.ResultY = startY;
                    current.ResultRot = 0;
                    return true;
                }
                // 回溯：清除本次占用的格子
                Unmark(current.size_x, current.size_y, startX, startY, virtualGrid);
            }

            // -- 尝试 B：旋转 90度 (Rot = 1) --
            // 跳过正方形（size_x == size_y）：旋转后占用完全相同，避免重复分支。
            if (current.size_x != current.size_y)
            {
                if (TryPlace(current, startX, startY, rot: 1, virtualGrid, width, height))
                {
                    if (PlaceItem(sorted, index + 1, virtualGrid, width, height))
                    {
                        current.ResultX = startX;
                        current.ResultY = startY;
                        current.ResultRot = 1;
                        return true;
                    }
                    // 回溯
                    Unmark(current.size_y, current.size_x, startX, startY, virtualGrid);
                }
            }

            // 在第一个空格上两种旋转都放不下，触发上层回溯
            return false;
        }

        /// <summary>
        /// 尝试把一个物品（按指定 rot）放在 (startX, startY)，并在合法时标记占用矩阵。
        /// 返回 true 表示已成功标记，调用方负责在回溯时调 Unmark。
        /// </summary>
        private static bool TryPlace(PackableItem item, byte startX, byte startY, byte rot,
                                     bool[,] virtualGrid, byte width, byte height)
        {
            // 根据 rot 计算实际占用的宽高。约定：rot 奇数 = 旋转 90度，与 InvUnturned 一致。
            byte w = item.size_x;
            byte h = item.size_y;
            if ((rot & 1) == 1)
            {
                w = item.size_y;
                h = item.size_x;
            }

            // 边界检查：右下角不超过网格
            // 用 int 防止 startX + w 溢出 byte（实际不会，但稳妥）
            int endX = (int)startX + w;
            int endY = (int)startY + h;
            if (endX > width || endY > height) return false;

            // 占用检查：所有目标格子必须为空
            for (int x = startX; x < endX; x++)
            {
                for (int y = startY; y < endY; y++)
                {
                    if (virtualGrid[x, y]) return false;
                }
            }

            // 标记占用
            for (int x = startX; x < endX; x++)
            {
                for (int y = startY; y < endY; y++)
                {
                    virtualGrid[x, y] = true;
                }
            }
            return true;
        }

        /// <summary>
        /// 清除物品在 (startX, startY) 处按给定 (w, h) 占用的所有格子。
        /// 注意 w/h 是"实际占用"尺寸（即调用方应根据 rot 计算后传入）。
        /// </summary>
        private static void Unmark(byte w, byte h, byte startX, byte startY, bool[,] virtualGrid)
        {
            int endX = (int)startX + w;
            int endY = (int)startY + h;
            for (int x = startX; x < endX; x++)
            {
                for (int y = startY; y < endY; y++)
                {
                    virtualGrid[x, y] = false;
                }
            }
        }
    }
}
