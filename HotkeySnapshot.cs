using System;
using System.Collections.Generic;
using SDG.Unturned;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// 快捷键快照：整理前由客户端捕获的本地 3-0 数字键绑定。
    /// 通过网络传给服务器，服务器验证旧坐标存在 ItemJar 且 ID 匹配后，
    /// 在整理完成且客户端 ACK 库存已应用时，按新坐标重新绑定。
    ///
    /// HotkeyInfo（PlayerEquipment.cs:24-42）只含 id/page/x/y，无实例 ID，
    /// 因此"同一 ID 多实例"必须通过旧 (page,x,y) -> 新 (page,x,y) 坐标映射来恢复。
    /// </summary>
    public struct HotkeySnapshot
    {
        /// <summary>快捷键索引（0..7 对应数字键 3-0）。</summary>
        public byte HotkeyIndex;

        /// <summary>整理前该快捷键指向的物品 ID（用于服务器验证）。</summary>
        public ushort ExpectedItemId;

        /// <summary>整理前该快捷键指向的页码。</summary>
        public byte OldPage;

        /// <summary>整理前该快捷键指向的 X 坐标。</summary>
        public byte OldX;

        /// <summary>整理前该快捷键指向的 Y 坐标。</summary>
        public byte OldY;

        public HotkeySnapshot(byte hotkeyIndex, ushort expectedItemId, byte oldPage, byte oldX, byte oldY)
        {
            HotkeyIndex = hotkeyIndex;
            ExpectedItemId = expectedItemId;
            OldPage = oldPage;
            OldX = oldX;
            OldY = oldY;
        }
    }

    /// <summary>
    /// 快捷键快照工具：客户端捕获本地 _hotkeys，服务器验证旧坐标。
    /// </summary>
    public static class HotkeySnapshotUtil
    {
        /// <summary>Unturned 数字键快捷键数量固定为 8（3-0 + 4-9 中可绑定的槽位）。</summary>
        public const int HOTKEY_COUNT = 8;

        /// <summary>可整理的页范围（SLOTS=2 至 PANTS=6，不含 STORAGE=7 容器页）。
        /// PlayerInventory.SLOTS/PANTS 是 static readonly 不是 const，这里用硬编码值。</summary>
        public const byte TIDYABLE_PAGE_MIN = 2; // = PlayerInventory.SLOTS
        public const byte TIDYABLE_PAGE_MAX = 6; // = PlayerInventory.PANTS

        /// <summary>
        /// 客户端：捕获本地玩家的 _hotkeys 数组。
        /// 仅 LocalPlayer 的 _hotkeys 已初始化（PlayerEquipment.cs:3290 在 channel.IsLocalPlayer 内），
        /// 服务器端 _hotkeys 为 null。
        /// </summary>
        public static List<HotkeySnapshot> CaptureLocalHotkeys()
        {
            var list = new List<HotkeySnapshot>(HOTKEY_COUNT);
            Player player = Player.LocalPlayer;
            if (player?.equipment == null) return list;

            // PlayerEquipment.hotkeys 是 _hotkeys 的公开属性（PlayerEquipment.cs:238）
            HotkeyInfo[] hotkeys = player.equipment.hotkeys;
            if (hotkeys == null) return list;

            PlayerInventory inv = player.inventory;
            if (inv?.items == null) return list;

            for (byte i = 0; i < hotkeys.Length && i < HOTKEY_COUNT; i++)
            {
                HotkeyInfo info = hotkeys[i];
                if (info == null) continue;
                if (info.id == 0) continue; // 空槽
                if (info.page < TIDYABLE_PAGE_MIN || info.page > TIDYABLE_PAGE_MAX) continue;

                // 验证旧坐标确实存在 ItemJar 且 id 匹配
                Items pageItems = inv.items[info.page];
                if (pageItems == null) continue;
                byte jarIdx = pageItems.getIndex(info.x, info.y);
                if (jarIdx == byte.MaxValue) continue;
                ItemJar jar = pageItems.getItem(jarIdx);
                if (jar?.item == null) continue;
                if (jar.item.id != info.id) continue;

                list.Add(new HotkeySnapshot(i, info.id, info.page, info.x, info.y));
            }
            return list;
        }

        /// <summary>
        /// 服务器端：验证快捷键快照的旧坐标在 sender 的 inventory 中存在 ItemJar 且 id 匹配。
        /// 通过验证的快照条目 + 对应的旧 ItemJar 实例将作为事务映射保存。
        /// </summary>
        public static Dictionary<ItemJar, HotkeySnapshot> ValidateAndResolve(
            PlayerInventory inv, List<HotkeySnapshot> snapshots)
        {
            var resolved = new Dictionary<ItemJar, HotkeySnapshot>();
            if (inv?.items == null || snapshots == null) return resolved;

            var seenIndexes = new HashSet<byte>();
            for (int i = 0; i < snapshots.Count; i++)
            {
                HotkeySnapshot snap = snapshots[i];
                if (snap.HotkeyIndex >= HOTKEY_COUNT) continue;
                if (!seenIndexes.Add(snap.HotkeyIndex)) continue; // 同一索引不得重复
                if (snap.OldPage < TIDYABLE_PAGE_MIN || snap.OldPage > TIDYABLE_PAGE_MAX) continue;

                Items pageItems = inv.items[snap.OldPage];
                if (pageItems == null) continue;
                if (snap.OldX >= pageItems.width || snap.OldY >= pageItems.height) continue;

                byte jarIdx = pageItems.getIndex(snap.OldX, snap.OldY);
                if (jarIdx == byte.MaxValue) continue;
                ItemJar jar = pageItems.getItem(jarIdx);
                if (jar?.item == null) continue;
                if (jar.item.id != snap.ExpectedItemId) continue;

                resolved[jar] = snap;
            }
            return resolved;
        }
    }
}
