#if TIDY_TEST_HARNESS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SDG.Unturned;

namespace LaunchInventoryTidy
{
    /// <summary>
    /// v2.0.6.13 Codex 第五轮 §3.3：TestHarness 自给自足的受控夹具会话。
    ///
    /// 设计契约：
    ///   1. TryCreate：捕获原始库存 + 快捷键深拷贝 -> 清空 page 2..6 -> 注入受控物品 ->
    ///      绑定键 3/7 -> FixtureValidator 验证
    ///   2. Restore：恢复原始库存 + 快捷键 -> SameExactLayout 验证
    ///   3. 任何异常/FAIL/BLOCKED 都走 finally 中的 Restore
    ///
    /// 受控夹具内容：
    ///   - 两件同 ID 1×1 物品，quality=100 与 quality=99（同 ID 不同 quality 多实例）
    ///   - 至少一件非 1×1 物品（混合尺寸覆盖）
    ///   - 填满 page 2（满足 HasFullyOccupiedValidPage）
    ///   - 键 3 (HotkeyIndex 0) 绑定到 quality=100 实例
    ///   - 键 7 (HotkeyIndex 4) 绑定到 quality=99 实例
    ///
    /// 安全约束：
    ///   - 只在 #if TIDY_TEST_HARNESS 下编译
    ///   - 夹具创建/清页/放物品/绑定快捷键/恢复均通过主线程执行
    ///   - 受控物品 ID 通过运行时 Assets.find 搜索，找不到合法资产为 TestHarness 配置失败
    /// </summary>
    internal sealed class TestFixtureSession
    {
        private const byte PAGE_SLOTS = 2;
        private const byte PAGE_BACKPACK = 3;
        private const byte PAGE_PANTS = 6;
        private const byte HOTKEY_KEY3_INDEX = 0;
        private const byte HOTKEY_KEY7_INDEX = 4;
        private const int HOTKEY_COUNT = 8;

        private readonly Player _player;
        private readonly IndependentSnapshot.FullInventorySnapshot _originalSnapshot;
        private readonly OriginalHotkeyBinding[] _originalHotkeys;
        private readonly OriginalFunctionalClothing _originalClothing;
        private readonly ItemAsset _fixtureItem1x1;

        public IndependentSnapshot.FullInventorySnapshot OriginalSnapshot => _originalSnapshot;

        private struct OriginalHotkeyBinding
        {
            public bool WasEmpty;
            public ushort Id;
            public byte Page;
            public byte X;
            public byte Y;
        }

        /// <summary>
        /// 仅保存会影响 page 3..6 容量的四件衣物。帽子、面罩、眼镜不影响背包页，
        /// 不触碰它们可避免为测试引入无关的外观/模组状态风险。
        /// </summary>
        private struct OriginalFunctionalClothing
        {
            public ClothingSlot Shirt;
            public ClothingSlot Pants;
            public ClothingSlot Backpack;
            public ClothingSlot Vest;
        }

        private struct ClothingSlot
        {
            public ushort Id;
            public byte Quality;
            public byte[] State;
        }

        private struct TestClothingAssets
        {
            public ItemShirtAsset Shirt;
            public ItemPantsAsset Pants;
            public ItemBackpackAsset Backpack;
            public ItemVestAsset Vest;
        }

        private TestFixtureSession(Player player,
            IndependentSnapshot.FullInventorySnapshot originalSnapshot,
            OriginalHotkeyBinding[] originalHotkeys,
            OriginalFunctionalClothing originalClothing,
            ItemAsset fixtureItem1x1)
        {
            _player = player;
            _originalSnapshot = originalSnapshot;
            _originalHotkeys = originalHotkeys;
            _originalClothing = originalClothing;
            _fixtureItem1x1 = fixtureItem1x1;
        }

        // ===== 候选物品 ID（按优先级尝试；找不到时运行时搜索）=====

        // 1×1 候选（用于同 ID 对 + 填充）
        private static readonly ushort[] CandidateIds_1x1 = { 4, 5, 136, 138, 139, 140, 141, 119, 92, 93 };
        // 非 1×1 候选（用于混合尺寸覆盖）
        private static readonly ushort[] CandidateIds_Non1x1 = { 17, 20, 81, 102, 103, 145, 146 };

        /// <summary>
        /// 创建受控夹具会话。失败时 out reason 描述原因，且不会修改任何库存状态。
        /// </summary>
        public static bool TryCreate(Player player, out TestFixtureSession fixture, out string reason)
        {
            fixture = null;
            reason = null;

            if (!IsOnPluginMainThread(out reason))
                return false;

            if (player?.inventory?.items == null)
            {
                reason = "player.inventory.items 为 null";
                return false;
            }

            if (player.equipment == null)
            {
                reason = "player.equipment 为 null";
                return false;
            }

            // 1. page 2 是基础固定页，应始终可用。page 3..6 由下方 TestHarness
            // 专用测试套装创建，不能再要求用户预先穿背包、背心、衬衫和裤子。
            Items slotsPage;
            try
            {
                slotsPage = player.inventory.items[PAGE_SLOTS];
            }
            catch (Exception e)
            {
                reason = $"读取 page 2 异常: {e.Message}";
                return false;
            }
            if (slotsPage == null || slotsPage.width < 4 || slotsPage.height < 2)
            {
                reason = $"page 2 (SLOTS) 不可用或尺寸不足（需要 >= 4×2，实际 {(slotsPage == null ? "null" : $"{slotsPage.width}×{slotsPage.height}")}）；请装备衬衫";
                return false;
            }

            // 2. 捕获原始状态（深拷贝）
            var originalSnapshot = IndependentSnapshot.CaptureAllPages(player.inventory);
            var originalHotkeys = CaptureAllHotkeys(player);
            var originalClothing = CaptureFunctionalClothing(player);

            // 3. 在修改任何玩家库存之前，选择与生产 ACK 路径同样可绑定的 1×1 物品。
            //    若没有合格资产，此处零副作用失败，不能在清包/换装后才发现夹具非法。
            if (!TryFindHotkeyEligibleOneByOneItemAsset(out var item1x1, out reason))
                return false;
            if (!TryFindNonTrivialItemAsset(
                    CandidateIds_Non1x1, slotsPage.width, slotsPage.height, out var itemNon1x1, out reason))
                return false;
            if (!TryFindTestClothingAssets(out var testClothes, out reason))
                return false;

            // 4. 先快照后清空；随后脱下原功能衣物并穿上测试套装。
            // 脱衣 API 会将旧衣服 forceAdd 回库存，因此每一步都会再次清页；旧衣服
            // 已在 originalClothing 保存，不会丢失恢复所需的值。
            if (!TryClearPages2to6(player.inventory, out reason))
            {
                RestoreAfterCreateFailure(player, originalSnapshot, originalHotkeys, originalClothing);
                return false;
            }
            if (!TryEquipTestClothing(player, testClothes, out reason))
            {
                RestoreAfterCreateFailure(player, originalSnapshot, originalHotkeys, originalClothing);
                return false;
            }

            // 衣物切换会调整 page 3..6 尺寸，必须重新取得 Items 引用。
            slotsPage = player.inventory.items[PAGE_SLOTS];
            if (slotsPage == null || slotsPage.width < 4 || slotsPage.height < 2 ||
                !IsPageUsable(player.inventory, PAGE_BACKPACK) ||
                !IsPageUsable(player.inventory, 4) ||
                !IsPageUsable(player.inventory, 5) ||
                !IsPageUsable(player.inventory, 6))
            {
                reason = "测试套装穿戴后 page 2..6 未全部可用";
                RestoreAfterCreateFailure(player, originalSnapshot, originalHotkeys, originalClothing);
                return false;
            }

            // 5. 注入受控夹具物品到 page 2
            if (!TryInjectFixtureItems(player.inventory, slotsPage, item1x1, itemNon1x1, out reason))
            {
                RestoreAfterCreateFailure(player, originalSnapshot, originalHotkeys, originalClothing);
                return false;
            }

            // 6. 绑定键 3 和 7 到同 ID 对
            if (!TryBindRequiredHotkeys(player, item1x1, out reason))
            {
                RestoreAfterCreateFailure(player, originalSnapshot, originalHotkeys, originalClothing);
                return false;
            }

            // 7. 验证夹具
            if (!FixtureValidator.TryValidateAllRequiredShapes(player.inventory, out reason))
            {
                RestoreAfterCreateFailure(player, originalSnapshot, originalHotkeys, originalClothing);
                return false;
            }
            if (!FixtureValidator.TryCaptureRequiredHotkeys(out _, out reason))
            {
                RestoreAfterCreateFailure(player, originalSnapshot, originalHotkeys, originalClothing);
                return false;
            }
            if (!HasAtLeastItems(player.inventory, PAGE_BACKPACK, 3, out reason))
            {
                RestoreAfterCreateFailure(player, originalSnapshot, originalHotkeys, originalClothing);
                return false;
            }

            fixture = new TestFixtureSession(player, originalSnapshot, originalHotkeys, originalClothing, item1x1);
            LaunchInventoryTidyPlugin.Log?.LogInfo(
                $"[TestFixtureSession] 夹具已建立：item1x1={item1x1.id}({item1x1.itemName}), " +
                $"itemNon1x1={itemNon1x1.id}({itemNon1x1.itemName}, {itemNon1x1.size_x}×{itemNon1x1.size_y}), " +
                $"clothes=[B{testClothes.Backpack.id},V{testClothes.Vest.id},S{testClothes.Shirt.id},P{testClothes.Pants.id}]");
            return true;
        }

        /// <summary>
        /// 恢复原始库存和快捷键。返回 true 表示恢复成功并通过 SameExactLayout 验证。
        /// </summary>
        public bool RestoreOriginalInventoryAndHotkeys()
        {
            if (!IsOnPluginMainThread(out _))
                return false;
            if (_player?.inventory?.items == null)
                return false;

            // 1. 清除夹具物品。
            bool clearedFixture = TryClearPages2to6(_player.inventory, out _);

            // 2. 恢复原功能衣物。每次换装会把被替换的测试衣物 forceAdd 回库存；
            // 再次清页可将其清除，避免混入原始快照。
            bool clothingRestored = TryRestoreFunctionalClothing(_player, _originalClothing);
            bool clearedReturnedTestClothes = TryClearPages2to6(_player.inventory, out _);

            // 3. 在已恢复的原页面尺寸中恢复原始物品。
            bool inventoryRestored = TryRestoreFromSnapshot(_player.inventory, _originalSnapshot);

            // 4. 恢复原始快捷键。
            bool hotkeysRestored = TryRestoreHotkeys(_player, _originalHotkeys);

            // 4. SameExactLayout 验证
            var afterRestore = IndependentSnapshot.CaptureAllPages(_player.inventory);
            bool layoutMatch = IndependentSnapshot.SameExactLayout(_originalSnapshot, afterRestore);

            LaunchInventoryTidyPlugin.Log?.LogInfo(
                $"[TestFixtureSession] Restore：clearFixture={clearedFixture}, clothes={clothingRestored}, " +
                $"clearTestClothes={clearedReturnedTestClothes}, inventory={inventoryRestored}, " +
                $"hotkeys={hotkeysRestored}, layoutMatch={layoutMatch}");

            return clearedFixture && clothingRestored && clearedReturnedTestClothes &&
                   inventoryRestored && hotkeysRestored && layoutMatch;
        }

        /// <summary>
        /// v2.0.6.13 Round 9（Codex Round 8 §3.3 HK-CROSS-01）：
        /// TryRebindHotkeys 已永久删除。原方法在 SP-HK 启动前重新绑定键 3/7，
        /// 会掩盖 SP-CONS 整理后真实发生的快捷键丢失（cleared=2 failed=2），
        /// 使后续套件显示 PASS 但不能证明"用户点击整理后快捷键得到保留"。
        ///
        /// 替代方案：
        ///   - 生产代码（ManualTidyNetwork）的 ACK 恢复路径改用完整指纹校验
        ///   - SP-CONS 每个 committed case 纳入 FixtureValidator.VerifyHotkeyCase 断言
        ///   - 真实发生快捷键丢失时，SP-CONS case 直接 FAIL，不通过重绑掩盖
        /// </summary>

        // ===== 私有实现 =====

        private static OriginalHotkeyBinding[] CaptureAllHotkeys(Player player)
        {
            var bindings = new OriginalHotkeyBinding[HOTKEY_COUNT];
            try
            {
                HotkeyInfo[] hotkeys = player.equipment.hotkeys;
                if (hotkeys == null) return bindings;

                for (byte i = 0; i < HOTKEY_COUNT && i < hotkeys.Length; i++)
                {
                    HotkeyInfo info = hotkeys[i];
                    if (info == null || info.id == 0)
                    {
                        bindings[i] = new OriginalHotkeyBinding { WasEmpty = true };
                    }
                    else
                    {
                        bindings[i] = new OriginalHotkeyBinding
                        {
                            WasEmpty = false,
                            Id = info.id,
                            Page = info.page,
                            X = info.x,
                            Y = info.y,
                        };
                    }
                }
            }
            catch (Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogWarning(
                    $"[TestFixtureSession] CaptureAllHotkeys 异常: {e.Message}");
            }
            return bindings;
        }

        private static OriginalFunctionalClothing CaptureFunctionalClothing(Player player)
        {
            PlayerClothing clothes = player?.clothing;
            if (clothes == null) return default(OriginalFunctionalClothing);

            return new OriginalFunctionalClothing
            {
                Shirt = CaptureClothingSlot(clothes.shirt, clothes.shirtQuality, clothes.shirtState),
                Pants = CaptureClothingSlot(clothes.pants, clothes.pantsQuality, clothes.pantsState),
                Backpack = CaptureClothingSlot(clothes.backpack, clothes.backpackQuality, clothes.backpackState),
                Vest = CaptureClothingSlot(clothes.vest, clothes.vestQuality, clothes.vestState),
            };
        }

        private static ClothingSlot CaptureClothingSlot(ushort id, byte quality, byte[] state)
        {
            return new ClothingSlot
            {
                Id = id,
                Quality = quality,
                State = CloneState(state),
            };
        }

        private static bool TryFindTestClothingAssets(out TestClothingAssets result, out string reason)
        {
            result = default(TestClothingAssets);
            reason = null;

#pragma warning disable CS0618
            try
            {
                Asset[] all = Assets.find(EAssetType.ITEM);
                if (all == null)
                {
                    reason = "Assets.find(EItemType.ITEM) 返回 null";
                    return false;
                }

                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] is ItemBackpackAsset backpack && result.Backpack == null &&
                        backpack.width > 0 && backpack.height > 0 && backpack.width * backpack.height >= 3)
                        result.Backpack = backpack;
                    else if (all[i] is ItemVestAsset vest && result.Vest == null && vest.width > 0 && vest.height > 0)
                        result.Vest = vest;
                    else if (all[i] is ItemShirtAsset shirt && result.Shirt == null && shirt.width > 0 && shirt.height > 0)
                        result.Shirt = shirt;
                    else if (all[i] is ItemPantsAsset pants && result.Pants == null && pants.width > 0 && pants.height > 0)
                        result.Pants = pants;

                    if (result.Backpack != null && result.Vest != null &&
                        result.Shirt != null && result.Pants != null)
                        return true;
                }
            }
            catch (Exception e)
            {
                reason = "枚举测试衣物资产异常: " + e.Message;
                return false;
            }
#pragma warning restore CS0618

            reason = "找不到可用测试套装（需要 backpack>=3格、vest、shirt、pants 四类 ItemBagAsset）";
            return false;
        }

        private static bool TryEquipTestClothing(Player player, TestClothingAssets assets, out string reason)
        {
            reason = null;
            PlayerClothing clothes = player?.clothing;
            if (clothes == null)
            {
                reason = "PlayerClothing 为 null";
                return false;
            }

            try
            {
                // 逐件卸下并清理被原生 forceAdd 回来的旧衣物。原值已在快照中保存。
                clothes.askWearBackpack((ItemBackpackAsset)null, 0, Array.Empty<byte>(), playEffect: false);
                if (!TryClearPages2to6(player.inventory, out reason)) return false;
                clothes.askWearVest((ItemVestAsset)null, 0, Array.Empty<byte>(), playEffect: false);
                if (!TryClearPages2to6(player.inventory, out reason)) return false;
                clothes.askWearShirt((ItemShirtAsset)null, 0, Array.Empty<byte>(), playEffect: false);
                if (!TryClearPages2to6(player.inventory, out reason)) return false;
                clothes.askWearPants((ItemPantsAsset)null, 0, Array.Empty<byte>(), playEffect: false);
                if (!TryClearPages2to6(player.inventory, out reason)) return false;

                // 穿上功能性测试套装；只使用原生 askWear* 路径，保证 page 3..6
                // 由 PlayerInventory 的 clothing 回调创建与调整尺寸。
                clothes.askWearBackpack(assets.Backpack, 100, GetAdminState(assets.Backpack), playEffect: false);
                clothes.askWearVest(assets.Vest, 100, GetAdminState(assets.Vest), playEffect: false);
                clothes.askWearShirt(assets.Shirt, 100, GetAdminState(assets.Shirt), playEffect: false);
                clothes.askWearPants(assets.Pants, 100, GetAdminState(assets.Pants), playEffect: false);

                if (clothes.backpack != assets.Backpack.id || clothes.vest != assets.Vest.id ||
                    clothes.shirt != assets.Shirt.id || clothes.pants != assets.Pants.id)
                {
                    reason = "测试套装穿戴后 clothing ID 不匹配";
                    return false;
                }
                return true;
            }
            catch (Exception e)
            {
                reason = "穿戴测试套装异常: " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        private static bool TryRestoreFunctionalClothing(Player player, OriginalFunctionalClothing original)
        {
            PlayerClothing clothes = player?.clothing;
            if (clothes == null) return false;

            try
            {
                if (!RestoreBackpack(clothes, original.Backpack)) return false;
                if (!TryClearPages2to6(player.inventory, out _)) return false;
                if (!RestoreVest(clothes, original.Vest)) return false;
                if (!TryClearPages2to6(player.inventory, out _)) return false;
                if (!RestoreShirt(clothes, original.Shirt)) return false;
                if (!TryClearPages2to6(player.inventory, out _)) return false;
                if (!RestorePants(clothes, original.Pants)) return false;
                return true;
            }
            catch (Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogWarning(
                    $"[TestFixtureSession] RestoreFunctionalClothing 异常: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        private static bool RestoreBackpack(PlayerClothing clothes, ClothingSlot expected)
        {
            if (IsSameClothing(clothes.backpack, clothes.backpackQuality, clothes.backpackState, expected)) return true;
            ItemBackpackAsset asset = expected.Id == 0 ? null : Assets.find(EAssetType.ITEM, expected.Id) as ItemBackpackAsset;
            if (expected.Id != 0 && asset == null) return false;
            clothes.askWearBackpack(asset, expected.Quality, CloneState(expected.State) ?? Array.Empty<byte>(), playEffect: false);
            return IsSameClothing(clothes.backpack, clothes.backpackQuality, clothes.backpackState, expected);
        }

        private static bool RestoreVest(PlayerClothing clothes, ClothingSlot expected)
        {
            if (IsSameClothing(clothes.vest, clothes.vestQuality, clothes.vestState, expected)) return true;
            ItemVestAsset asset = expected.Id == 0 ? null : Assets.find(EAssetType.ITEM, expected.Id) as ItemVestAsset;
            if (expected.Id != 0 && asset == null) return false;
            clothes.askWearVest(asset, expected.Quality, CloneState(expected.State) ?? Array.Empty<byte>(), playEffect: false);
            return IsSameClothing(clothes.vest, clothes.vestQuality, clothes.vestState, expected);
        }

        private static bool RestoreShirt(PlayerClothing clothes, ClothingSlot expected)
        {
            if (IsSameClothing(clothes.shirt, clothes.shirtQuality, clothes.shirtState, expected)) return true;
            ItemShirtAsset asset = expected.Id == 0 ? null : Assets.find(EAssetType.ITEM, expected.Id) as ItemShirtAsset;
            if (expected.Id != 0 && asset == null) return false;
            clothes.askWearShirt(asset, expected.Quality, CloneState(expected.State) ?? Array.Empty<byte>(), playEffect: false);
            return IsSameClothing(clothes.shirt, clothes.shirtQuality, clothes.shirtState, expected);
        }

        private static bool RestorePants(PlayerClothing clothes, ClothingSlot expected)
        {
            if (IsSameClothing(clothes.pants, clothes.pantsQuality, clothes.pantsState, expected)) return true;
            ItemPantsAsset asset = expected.Id == 0 ? null : Assets.find(EAssetType.ITEM, expected.Id) as ItemPantsAsset;
            if (expected.Id != 0 && asset == null) return false;
            clothes.askWearPants(asset, expected.Quality, CloneState(expected.State) ?? Array.Empty<byte>(), playEffect: false);
            return IsSameClothing(clothes.pants, clothes.pantsQuality, clothes.pantsState, expected);
        }

        private static bool IsSameClothing(ushort actualId, byte actualQuality, byte[] actualState, ClothingSlot expected)
        {
            return actualId == expected.Id && actualQuality == expected.Quality &&
                   (actualState ?? Array.Empty<byte>()).SequenceEqual(expected.State ?? Array.Empty<byte>());
        }

        private static byte[] GetAdminState(ItemAsset asset)
        {
            byte[] state = asset?.getState(EItemOrigin.ADMIN);
            return CloneState(state) ?? Array.Empty<byte>();
        }

        private static byte[] CloneState(byte[] state)
        {
            return state == null ? null : (byte[])state.Clone();
        }

        private static bool TryRestoreHotkeys(Player player, OriginalHotkeyBinding[] originalHotkeys)
        {
            if (player?.equipment == null || originalHotkeys == null) return false;

            try
            {
                for (byte i = 0; i < HOTKEY_COUNT && i < originalHotkeys.Length; i++)
                {
                    var orig = originalHotkeys[i];
                    if (orig.WasEmpty)
                    {
                        player.equipment.ServerClearItemHotkey(i);
                    }
                    else
                    {
                        var asset = Assets.find(EAssetType.ITEM, orig.Id) as ItemAsset;
                        if (asset != null)
                        {
                            player.equipment.ServerBindItemHotkey(i, asset, orig.Page, orig.X, orig.Y);
                        }
                        else
                        {
                            player.equipment.ServerClearItemHotkey(i);
                        }
                    }
                }
                return VerifyHotkeys(player, originalHotkeys);
            }
            catch (Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogWarning(
                    $"[TestFixtureSession] TryRestoreHotkeys 异常: {e.Message}");
                return false;
            }
        }

        private static bool VerifyHotkeys(Player player, OriginalHotkeyBinding[] expected)
        {
            try
            {
                HotkeyInfo[] actual = player?.equipment?.hotkeys;
                if (actual == null || expected == null) return false;

                for (byte i = 0; i < HOTKEY_COUNT && i < expected.Length; i++)
                {
                    HotkeyInfo current = i < actual.Length ? actual[i] : null;
                    OriginalHotkeyBinding wanted = expected[i];
                    bool isEmpty = current == null || current.id == 0;
                    if (wanted.WasEmpty)
                    {
                        if (!isEmpty) return false;
                        continue;
                    }

                    if (isEmpty || current.id != wanted.Id || current.page != wanted.Page ||
                        current.x != wanted.X || current.y != wanted.Y)
                        return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 判断候选物品能否作为键 3/7 的受控夹具。
        /// 必须与生产 ACK 恢复路径使用相同的原版资格判定，防止夹具构造出
        /// "可写 HotkeyInfo、但生产恢复必然拒绝" 的伪场景。
        /// </summary>
        private static bool IsHotkeyEligibleOneByOne(ItemAsset asset)
        {
            if (asset == null || asset.size_x != 1 || asset.size_y != 1)
                return false;

            try
            {
                return ItemTool.checkUseable(PAGE_SLOTS, asset.id);
            }
            catch (Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogWarning(
                    $"[TestFixtureSession] checkUseable 失败 id={asset.id}: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// 选择原版允许绑定到 PAGE_SLOTS 的 1×1 资产。
        /// 本方法必须在 TryClearPages2to6 / TryEquipTestClothing 之前调用，失败时不触碰玩家状态。
        /// </summary>
        private static bool TryFindHotkeyEligibleOneByOneItemAsset(out ItemAsset asset, out string reason)
        {
            asset = null;
            reason = null;

            // 优先按稳定候选序列选择，但每一个候选均须通过生产同款资格检查。
            for (int i = 0; i < CandidateIds_1x1.Length; i++)
            {
                ushort id = CandidateIds_1x1[i];
                try
                {
                    ItemAsset candidate = Assets.find(EAssetType.ITEM, id) as ItemAsset;
                    if (!IsHotkeyEligibleOneByOne(candidate))
                        continue;

                    asset = candidate;
                    LaunchInventoryTidyPlugin.Log?.LogInfo(
                        $"[TestFixtureSession] 已选择可绑定 1×1 夹具 id={candidate.id} ({candidate.itemName})");
                    return true;
                }
                catch (Exception e)
                {
                    LaunchInventoryTidyPlugin.Log?.LogWarning(
                        $"[TestFixtureSession] 候选夹具读取失败 id={id}: {e.Message}");
                }
            }

            // TestHarness 回退：遍历本机资源，但仍使用完全相同的资格校验。
#pragma warning disable CS0618
            try
            {
                var all = Assets.find(EAssetType.ITEM);
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i] is ItemAsset candidate && IsHotkeyEligibleOneByOne(candidate))
                        {
                            asset = candidate;
                            LaunchInventoryTidyPlugin.Log?.LogInfo(
                                $"[TestFixtureSession] 已选择回退可绑定 1×1 夹具 id={candidate.id} ({candidate.itemName})");
                            return true;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                reason = "枚举 ItemAsset 失败: " + e.Message;
                return false;
            }
#pragma warning restore CS0618
            reason = "找不到可绑定到 page 2 的 1×1 ItemAsset；夹具未修改玩家库存";
            return false;
        }

        private static bool TryFindNonTrivialItemAsset(
            ushort[] candidateIds, byte pageWidth, byte pageHeight, out ItemAsset asset, out string reason)
        {
            asset = null;
            reason = null;
            for (int i = 0; i < candidateIds.Length; i++)
            {
                try
                {
                    var a = Assets.find(EAssetType.ITEM, candidateIds[i]) as ItemAsset;
                    if (CanPlaceNonTrivialAsset(a, pageWidth, pageHeight))
                    {
                        asset = a;
                        return true;
                    }
                }
                catch { }
            }
            // 运行时全量搜索兜底（obsolete 警告已禁用：TestHarness 仅用于 fallback 全量枚举）
#pragma warning disable CS0618
            try
            {
                var all = Assets.find(EAssetType.ITEM);
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i] is ItemAsset ia && CanPlaceNonTrivialAsset(ia, pageWidth, pageHeight))
                        {
                            asset = ia;
                            return true;
                        }
                    }
                }
            }
            catch { }
#pragma warning restore CS0618
            reason = $"找不到可放入 page 2({pageWidth}×{pageHeight}) 的非 1×1 ItemAsset（TestHarness 配置失败）";
            return false;
        }

        private static bool CanPlaceNonTrivialAsset(ItemAsset asset, byte pageWidth, byte pageHeight)
        {
            if (asset == null || (asset.size_x <= 1 && asset.size_y <= 1)) return false;

            // (0,0)/(1,0) 已被同 ID 快捷键对占用；非 1×1 必须能放在其右侧，
            // 或放在下一行。与 TryInjectFixtureItems 的实际摆放策略保持同一事实源。
            return (2 + asset.size_x <= pageWidth && asset.size_y <= pageHeight) ||
                   (2 + asset.size_y <= pageWidth && asset.size_x <= pageHeight) ||
                   (asset.size_x <= pageWidth && 1 + asset.size_y <= pageHeight);
        }

        private static bool TryClearPages2to6(PlayerInventory inv, out string reason)
        {
            reason = null;
            try
            {
                for (byte page = PAGE_SLOTS; page <= PAGE_PANTS; page++)
                {
                    Items items;
                    try { items = inv.items[page]; }
                    catch { continue; }
                    if (items == null) continue;

                    // 从末尾开始删除，避免索引移位
                    while (items.getItemCount() > 0)
                    {
                        byte lastIndex = (byte)(items.getItemCount() - 1);
                        items.removeItem(lastIndex);
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                reason = $"清空 page 2..6 异常: {e.Message}";
                return false;
            }
        }

        private sealed class FixturePlacement
        {
            public ItemAsset Asset;
            public byte Quality;
            public byte X;
            public byte Y;
            public byte Rot;
        }

        private static bool TryInjectFixtureItems(
            PlayerInventory inventory,
            Items slotsPage,
            ItemAsset item1x1,
            ItemAsset itemNon1x1,
            out string reason)
        {
            reason = null;

            // STEP 1：Unity / Unturned 库存只能在主线程访问。
            if (!IsOnPluginMainThread(out reason))
                return false;

            if (inventory == null || slotsPage == null)
            {
                reason = "fixture inventory 或 page 2 为 null";
                return false;
            }

            if (item1x1 == null || item1x1.size_x != 1 || item1x1.size_y != 1)
            {
                reason = "item1x1 不是有效 1x1 ItemAsset";
                return false;
            }

            if (itemNon1x1 == null ||
                (itemNon1x1.size_x <= 1 && itemNon1x1.size_y <= 1))
            {
                reason = "itemNon1x1 不是有效非 1x1 ItemAsset";
                return false;
            }

            if (slotsPage.width < 4 || slotsPage.height < 2)
            {
                reason = "page 2 尺寸小于 fixture 所需的 4x2";
                return false;
            }

            // STEP 2：构建真实矩形占用图；不得依赖 getIndex。
            bool[,] slotsOccupied;
            if (!TryBuildOccupiedGrid(slotsPage, out slotsOccupied, out reason))
                return false;

            var slotsPlan = new List<FixturePlacement>();

            // 键 3 / 键 7：同 ID、不同 quality。
            if (!TryPlanExact(slotsOccupied, slotsPage.width, slotsPage.height,
                    item1x1, 100, 0, 0, 0, slotsPlan, out reason) ||
                !TryPlanExact(slotsOccupied, slotsPage.width, slotsPage.height,
                    item1x1, 99, 1, 0, 0, slotsPlan, out reason))
                return false;

            // STEP 3：预规划大件，尚未写入 Items。
            if (!TryPlanFirstFit(slotsOccupied, slotsPage.width, slotsPage.height,
                    itemNon1x1, 100, slotsPlan, out reason))
                return false;

            // STEP 4：用占用矩阵填满剩余格。
            for (byte y = 0; y < slotsPage.height; y++)
            for (byte x = 0; x < slotsPage.width; x++)
            {
                if (slotsOccupied[x, y])
                    continue;

                if (!TryPlanExact(slotsOccupied, slotsPage.width, slotsPage.height,
                        item1x1, 100, x, y, 0, slotsPlan, out reason))
                    return false;
            }

            if (!IsFullyOccupied(slotsOccupied))
            {
                reason = "page 2 预规划未填满";
                return false;
            }

            // STEP 5：写入后必须检查 count + 几何。
            if (!TryApplyPlan(slotsPage, slotsPlan, out reason) ||
                !TryValidatePageGeometry(slotsPage, out reason))
                return false;

            Items backpack;
            try
            {
                backpack = inventory.items[PAGE_BACKPACK];
            }
            catch (Exception e)
            {
                reason = "读取 page 3 异常: " + e.Message;
                return false;
            }

            if (backpack == null || backpack.width == 0 || backpack.height == 0)
            {
                reason = "page 3 (BACKPACK) 不可用";
                return false;
            }

            bool[,] backpackOccupied;
            if (!TryBuildOccupiedGrid(backpack, out backpackOccupied, out reason))
                return false;

            var backpackPlan = new List<FixturePlacement>();
            for (int i = 0; i < 3; i++)
            {
                if (!TryPlanFirstFit(backpackOccupied, backpack.width, backpack.height,
                        item1x1, (byte)(98 - i), backpackPlan, out reason))
                    return false;
            }

            return TryApplyPlan(backpack, backpackPlan, out reason) &&
                   TryValidatePageGeometry(backpack, out reason);
        }

        private static bool TryPlanExact(
            bool[,] occupied, byte pageWidth, byte pageHeight,
            ItemAsset asset, byte quality, byte x, byte y, byte rot,
            List<FixturePlacement> plan, out string reason)
        {
            reason = null;
            if (occupied == null || plan == null || asset == null)
            {
                reason = "预规划参数为 null";
                return false;
            }

            int width;
            int height;
            if (!TryGetFootprint(asset, rot, out width, out height))
            {
                reason = "物品尺寸或旋转非法: " + asset.id;
                return false;
            }

            if (x + width > pageWidth || y + height > pageHeight)
            {
                reason = "预规划越界: id=" + asset.id;
                return false;
            }

            for (int dx = 0; dx < width; dx++)
            for (int dy = 0; dy < height; dy++)
            {
                if (occupied[x + dx, y + dy])
                {
                    reason = "预规划重叠: id=" + asset.id;
                    return false;
                }
            }

            for (int dx = 0; dx < width; dx++)
            for (int dy = 0; dy < height; dy++)
                occupied[x + dx, y + dy] = true;

            plan.Add(new FixturePlacement
            {
                Asset = asset,
                Quality = quality,
                X = x,
                Y = y,
                Rot = rot
            });
            return true;
        }

        private static bool TryPlanFirstFit(
            bool[,] occupied, byte pageWidth, byte pageHeight,
            ItemAsset asset, byte quality,
            List<FixturePlacement> plan, out string reason)
        {
            reason = null;

            for (byte rot = 0; rot <= 1; rot++)
            for (byte y = 0; y < pageHeight; y++)
            for (byte x = 0; x < pageWidth; x++)
            {
                int width;
                int height;
                if (!TryGetFootprint(asset, rot, out width, out height) ||
                    x + width > pageWidth || y + height > pageHeight)
                    continue;

                bool fits = true;
                for (int dx = 0; dx < width && fits; dx++)
                for (int dy = 0; dy < height; dy++)
                    if (occupied[x + dx, y + dy])
                    {
                        fits = false;
                        break;
                    }

                if (fits)
                    return TryPlanExact(
                        occupied, pageWidth, pageHeight,
                        asset, quality, x, y, rot, plan, out reason);
            }

            reason = "没有可用位置: id=" + (asset == null ? "null" : asset.id.ToString());
            return false;
        }

        private static bool TryGetFootprint(
            ItemAsset asset, byte rot, out int width, out int height)
        {
            width = 0;
            height = 0;

            if (asset == null || asset.size_x == 0 || asset.size_y == 0 ||
                (rot != 0 && rot != 1))
                return false;

            width = rot == 0 ? asset.size_x : asset.size_y;
            height = rot == 0 ? asset.size_y : asset.size_x;
            return width > 0 && height > 0;
        }

        private static bool TryApplyPlan(
            Items page, List<FixturePlacement> plan, out string reason)
        {
            reason = null;

            if (page == null || plan == null)
            {
                reason = "写入计划参数为 null";
                return false;
            }

            foreach (FixturePlacement entry in plan)
            {
                int before = page.getItemCount();
                byte[] state = entry.Asset.getState(EItemOrigin.ADMIN);
                byte[] stateCopy = state == null ? new byte[0] : (byte[])state.Clone();

                page.addItem(entry.X, entry.Y, entry.Rot,
                    new Item(entry.Asset.id, 1, entry.Quality, stateCopy));

                if (page.getItemCount() != before + 1)
                {
                    reason = "Items.addItem 未增加条目数: id=" + entry.Asset.id;
                    return false;
                }
            }

            return true;
        }

        private static bool TryBuildOccupiedGrid(
            Items page, out bool[,] occupied, out string reason)
        {
            occupied = null;
            reason = null;

            if (page == null || page.width == 0 || page.height == 0)
            {
                reason = "页面不存在或尺寸为零";
                return false;
            }

            occupied = new bool[page.width, page.height];

            for (byte i = 0; i < page.getItemCount(); i++)
            {
                ItemJar jar = page.getItem(i);
                if (jar == null || jar.item == null)
                {
                    reason = "页面含 null jar/item";
                    return false;
                }

                if (jar.rot != 0 && jar.rot != 1)
                {
                    reason = "页面含非法旋转";
                    return false;
                }

                int width = jar.rot == 0 ? jar.size_x : jar.size_y;
                int height = jar.rot == 0 ? jar.size_y : jar.size_x;

                if (width <= 0 || height <= 0 ||
                    jar.x + width > page.width || jar.y + height > page.height)
                {
                    reason = "页面含非法 jar 几何";
                    return false;
                }

                for (int dx = 0; dx < width; dx++)
                for (int dy = 0; dy < height; dy++)
                {
                    int x = jar.x + dx;
                    int y = jar.y + dy;

                    if (occupied[x, y])
                    {
                        reason = "页面含重叠 jar";
                        return false;
                    }

                    occupied[x, y] = true;
                }
            }

            return true;
        }

        private static bool TryValidatePageGeometry(Items page, out string reason)
        {
            bool[,] ignored;
            return TryBuildOccupiedGrid(page, out ignored, out reason);
        }

        private static bool IsFullyOccupied(bool[,] occupied)
        {
            if (occupied == null)
                return false;

            for (int x = 0; x < occupied.GetLength(0); x++)
            for (int y = 0; y < occupied.GetLength(1); y++)
                if (!occupied[x, y])
                    return false;

            return true;
        }

        private static void RestoreAfterCreateFailure(Player player,
            IndependentSnapshot.FullInventorySnapshot originalSnapshot,
            OriginalHotkeyBinding[] originalHotkeys,
            OriginalFunctionalClothing originalClothing)
        {
            try
            {
                if (player?.inventory != null)
                {
                    // 失败可能发生在部分注入或换装后。必须先清除夹具、恢复页面尺寸，
                    // 再恢复原快照；绝不能把快照叠加到测试衣物的页面上。
                    TryClearPages2to6(player.inventory, out _);
                    TryRestoreFunctionalClothing(player, originalClothing);
                    TryClearPages2to6(player.inventory, out _);
                    TryRestoreFromSnapshot(player.inventory, originalSnapshot);
                }
                TryRestoreHotkeys(player, originalHotkeys);
            }
            catch (Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogError(
                    $"[TestFixtureSession] 夹具建立失败后的恢复异常: {e.GetType().Name}: {e.Message}");
            }
        }

        private static bool IsPageUsable(PlayerInventory inventory, byte page)
        {
            try
            {
                Items items = inventory?.items?[page];
                return items != null && items.width > 0 && items.height > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsOnPluginMainThread(out string reason)
        {
            int expected = LaunchInventoryTidyPlugin.MainThreadId;
            int current = System.Threading.Thread.CurrentThread.ManagedThreadId;
            if (expected != 0 && current == expected)
            {
                reason = null;
                return true;
            }

            reason = $"TestFixtureSession 必须在插件主线程执行（current={current}, expected={expected}）";
            return false;
        }

        private static bool HasAtLeastItems(PlayerInventory inventory, byte page, int minimum, out string reason)
        {
            try
            {
                Items items = inventory?.items?[page];
                int count = items == null ? 0 : items.getItemCount();
                if (count >= minimum)
                {
                    reason = null;
                    return true;
                }
                reason = $"page {page} 夹具物品数 {count} < {minimum}";
                return false;
            }
            catch (Exception e)
            {
                reason = $"读取 page {page} 夹具异常: {e.Message}";
                return false;
            }
        }

        private static bool TryBindRequiredHotkeys(Player player, ItemAsset item1x1, out string reason)
        {
            reason = null;
            if (player == null || player.inventory == null || player.equipment == null || item1x1 == null)
            {
                reason = "player/inventory/equipment/item1x1 is null";
                return false;
            }
            // v2.0.6.13 Round 9（Codex Round 8 §3.3 P0-FIXTURE-02）：
            // 夹具必须选择原版允许绑定快捷键的物品。生产恢复路径在
            // HandleInventoryAppliedAck 中要求 ItemTool.checkUseable(page, asset.id) 为真；
            // 夹具的初始绑定路径同样必须做此资格校验，避免夹具非法却通过测试。
            if (!ItemTool.checkUseable(PAGE_SLOTS, item1x1.id))
            {
                reason = $"fixture id={item1x1.id} is not hotkey-eligible on page {PAGE_SLOTS}";
                return false;
            }

            Items page = player.inventory.items[PAGE_SLOTS];
            if (page == null || page.getIndex(0, 0) == byte.MaxValue || page.getIndex(1, 0) == byte.MaxValue)
            {
                reason = "fixture targets (0,0)/(1,0) do not exist";
                return false;
            }

            player.equipment.ServerBindItemHotkey(HOTKEY_KEY3_INDEX, item1x1, PAGE_SLOTS, 0, 0);
            player.equipment.ServerBindItemHotkey(HOTKEY_KEY7_INDEX, item1x1, PAGE_SLOTS, 1, 0);

            // v2.0.6.13 Round 9：写入后必须用 FixtureValidator 验证绑定结果，
            // 不能用"调用没有抛异常"作为成功。
            if (!FixtureValidator.TryCaptureRequiredHotkeys(out _, out reason))
            {
                reason = "fixture hotkey write verification failed: " + reason;
                return false;
            }
            return true;
        }

        private static bool TryRestoreFromSnapshot(PlayerInventory inv,
            IndependentSnapshot.FullInventorySnapshot snapshot)
        {
            if (inv?.items == null || snapshot == null) return false;

            try
            {
                foreach (var r in snapshot.Items)
                {
                    if (r.Page < PAGE_SLOTS || r.Page > PAGE_PANTS) continue;
                    Items page;
                    try { page = inv.items[r.Page]; }
                    catch { continue; }
                    if (page == null) continue;

                    var stateCopy = r.State == null ? new byte[0] : (byte[])r.State.Clone();
                    var item = new Item(r.Id, r.Amount, r.Quality, stateCopy);
                    page.addItem(r.X, r.Y, r.Rot, item);
                }
                return true;
            }
            catch (Exception e)
            {
                LaunchInventoryTidyPlugin.Log?.LogWarning(
                    $"[TestFixtureSession] TryRestoreFromSnapshot 异常: {e.Message}");
                return false;
            }
        }
    }
}
#endif
