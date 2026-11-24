using System.Runtime.InteropServices;
using GameHelper;
using GameHelper.Plugin;
using GameHelper.RemoteObjects.Components;
using GameHelper.RemoteObjects.States.InGameStateObjects;
using GameOffsets.Natives;
using GameOffsets.Objects.States.InGameState;
using static ExchangeTrader.ExchangeUi;

namespace ExchangeTrader;

internal sealed partial class GameUiAdapter
{
    private const int TabDescriptorsOffset = 0x340;
    private const int TabDescriptorSize = 0x90;
    private readonly HashSet<IntPtr> confirmedStashInventories = [];
    private readonly Dictionary<StashTab, IntPtr> protectedInventories = [];
    private readonly Dictionary<IntPtr, StockStack> stackCache = new();

    private static IntPtr StashController(IntPtr panel) => At(panel, 2, 0, 0, 0, 1);
    private static IntPtr StashList(IntPtr panel) => At(StashController(panel), 4, 2);
    private static IntPtr StashViews(IntPtr panel) => At(StashController(panel), 1);

    public async Task<IReadOnlyList<StashTab>> ReadStashTabs(CancellationToken token)
    {
        var panel = RequireStash();
        if (!Visible(StashList(panel)))
        {
            Click(At(StashController(panel), 2), token);
            await Until(() => Visible(StashList(RequireStash())) ? new object() : null, "Список вкладок сундука не открылся.", token);
        }
        return await Until(() => TryReadTabList(panel), "Не подтверждены дескрипторы вкладок сундука.", token);
    }

    internal static StashTab[]? TryReadTabList(IntPtr panel)
    {
        if (panel == IntPtr.Zero) return null;
        var controller = StashController(panel);
        var rows = Children(StashList(panel));
        var views = Children(StashViews(panel));
        var reader = Core.Process.Handle;
        if (rows.Length == 0 || rows.Length != views.Length ||
            !reader.TryReadMemory<StdVector>(controller + TabDescriptorsOffset, out var vector) ||
            vector.Last.ToInt64() - vector.First.ToInt64() != (long)rows.Length * TabDescriptorSize ||
            vector.End.ToInt64() < vector.Last.ToInt64())
            return null;
        var descriptors = reader.ReadStdVector<StashTabDescriptor>(vector);
        if (descriptors.Length != rows.Length) return null;
        var tabs = new List<StashTab>();
        for (int index = 0; index < descriptors.Length; index++)
        {
            var entry = descriptors[index];
            string name = reader.ReadStdWString(entry.Name);
            if (entry.View != views[index] || name != Text(At(rows[index], 0, 1)) || !TryNode(rows[index], out var row))
                return null;
            if (!GameOffsets.Objects.UiElement.UiElementBaseFuncs.IsVisibleChecker(row.Flags) || name.Length == 0 ||
                name.EndsWith("(Unavailable)", StringComparison.OrdinalIgnoreCase)) continue;
            tabs.Add(new(index + 1, name, StashTabIdentity.Create(name, entry.Color), entry.Color));
        }
        return tabs.ToArray();
    }

    internal static IReadOnlyList<TradeProduct> ReadVisibleStashProducts(IntPtr panel)
    {
        if (Children(StashViews(panel)).Where(Visible).ToArray() is not [var view]) return [];
        return ReadVisibleSlots(view).Items.Keys.Select(address => new Item(address))
            .Where(item => item.IsValid && !string.IsNullOrEmpty(item.Path))
            .Select(item => (Item: item, Name: item.TryGetComponent<Base>(out var itemBase) ? itemBase.BaseItemName : string.Empty))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(entry => new TradeProduct(entry.Item.Path, entry.Name)).Distinct().ToArray();
    }

    public async Task<TabStock> ReadTab(StashTab tab, CancellationToken token)
    {
        ValidateSession(token);
        var panel = RequireStash();
        int index = checked((int)tab.Key - 1);
        var row = At(StashList(panel), index);
        var view = At(StashViews(panel), index);
        var reader = Core.Process.Handle;
        if (!Visible(row) || view == IntPtr.Zero || Text(At(row, 0, 1)) != tab.Name ||
            !reader.TryReadMemory<StdVector>(StashController(panel) + TabDescriptorsOffset, out var descriptors) ||
            descriptors.First == IntPtr.Zero || descriptors.Last.ToInt64() - descriptors.First.ToInt64() < (index + 1L) * TabDescriptorSize ||
            !reader.TryReadMemory<StashTabDescriptor>(descriptors.First + index * TabDescriptorSize, out var descriptor) ||
            descriptor.View != view || descriptor.Color != tab.Color || reader.ReadStdWString(descriptor.Name) != tab.Name)
            throw new TradeBlockedException($"Вкладка {tab.Name} изменилась до открытия.");
        if (!Visible(view)) Click(At(row, 0, 1), token);
        await Until(() => RequireStash() == panel && Visible(view) && Children(view).Length > 0 &&
            Children(At(view, 0)).Length > 0 ? new object() : null, $"Вкладка {tab.Name} не загрузилась.", token);
        var grid = At(view, 0, 0);
        if (TryNode(grid, out var node) && (node.Flags & 0x00f00000) == 0x00400000 &&
            node.UnscaledSize.X > 600 && node.UnscaledSize.X == node.UnscaledSize.Y)
        {
            var loaded = await Until(() =>
            {
                if (RequireStash() != panel || !Visible(view)) throw new TradeBlockedException("Открытая вкладка изменилась.");
                return ReadStashGrid(grid);
            }, $"ServerData не подтвердил загрузку вкладки «{tab.Name}».", token);
            if (excludedTabs.Contains(tab.Id)) protectedInventories[tab] = loaded.Inventory;
            return new(tab, loaded.Stacks, true);
        }
        var result = new Dictionary<long, StockStack>();
        await CaptureView(view, tab.Name, result, token);
        return new(tab, result.Values.ToArray(), false);
    }

    private GridStock? ReadStashGrid(IntPtr grid)
    {
        if (!Core.Process.Handle.TryReadMemory<StashGridInventory>(grid, out var link) || link.Inventory == IntPtr.Zero ||
            link.Owner != link.Inventory - 0x10) return null;
        var rows = InventoryTable().Where(row => row.InventoryId >= 128 && row.InventoryPtr0 == link.Inventory &&
            row.InventoryPtr1 == link.Owner).ToArray();
        if (rows.Length != 1 || !Inventory.TryReadSlots(link.Inventory, out var header, out var slots) ||
            header.ServerRequestCounter <= 0 || header.TotalBoxes.X is not (12 or 24) || header.TotalBoxes.Y != header.TotalBoxes.X)
            return null;
        confirmedStashInventories.Add(link.Inventory);
        return new(link.Inventory, ReadStacks(slots.Select(slot => slot.Item)));
    }

    private void ValidateProtectedStock(TradeProduct product)
    {
        if (excludedTabs.Count == 0) return;
        if (!excludedTabs.SetEquals(protectedInventories.Keys.Select(tab => tab.Id)))
            throw new TradeBlockedException("Защищённые вкладки не подтверждены в этой сессии.");
        var live = InventoryTable().Select(row => row.InventoryPtr0).ToHashSet();
        foreach (var entry in protectedInventories)
        {
            if (!live.Contains(entry.Value) || !Inventory.TryReadSlots(entry.Value, out _, out var inventory))
                throw new TradeBlockedException($"Потеряна связь с исключённой вкладкой «{entry.Key.Name}».");
            foreach (var slot in inventory)
            {
                if (!PluginUiElementReflection.TryValidateItemAddress(slot.Item, out string metadata, out _))
                    throw new TradeBlockedException($"Нельзя проверить предмет в исключённой вкладке «{entry.Key.Name}».");
                if (metadata == product.Id)
                    throw new MarketUnavailableException($"Товар есть в исключённой вкладке «{entry.Key.Name}». Источник списания выбрать нельзя.");
            }
        }
    }

    private async Task CaptureView(IntPtr view, string name, Dictionary<long, StockStack> result, CancellationToken token)
    {
        SlotWitness? last = null;
        int repeats = 0;
        var stable = await Until(() =>
        {
            RequireStash();
            if (!Visible(view)) throw new TradeBlockedException("Выбранная вкладка сундука изменилась.");
            var witness = ReadVisibleSlots(view);
            if (witness.Tiles == 0) return null;
            if (last == null || witness.Tiles != last.Tiles || !witness.Items.Keys.ToHashSet().SetEquals(last.Items.Keys))
            {
                last = witness;
                repeats = 0;
                return null;
            }
            // Empty slots need a longer stable window than an occupied, identified inventory.
            return ++repeats >= (witness.Items.Count > 0 ? 2 : 12) ? witness : null;
        }, $"Не удалось подтвердить загрузку ячеек вкладки «{name}».", token, 4300);

        foreach (var stack in ReadBoundInventory(stable)) result.TryAdd(stack.Identity, stack);
    }

    private IReadOnlyList<StockStack> ReadBoundInventory(SlotWitness witness)
    {
        if (witness.Items.Count == 0) return [];
        var table = InventoryTable();
        var candidates = table.Where(entry => entry.InventoryPtr0 != IntPtr.Zero).Select(entry => entry.InventoryPtr0).Distinct();
        InventoryItemStruct[]? match = null;
        IntPtr inventory = IntPtr.Zero;
        foreach (var address in candidates)
        {
            if (!Inventory.TryReadSlots(address, out _, out var slots) || slots.Length < witness.Items.Count) continue;
            if (!witness.Items.Keys.All(key => slots.Any(slot => slot.Item == key))) continue;
            if (match != null && !match.Select(slot => slot.Item).ToHashSet().SetEquals(slots.Select(slot => slot.Item)))
                throw new TradeBlockedException("Видимые ячейки указывают на несколько разных серверных контейнеров.");
            match = slots;
            inventory = address;
        }
        if (match == null) return ReadStacks(witness.Items.Keys);
        confirmedStashInventories.Add(inventory);
        return ReadStacks(match.Select(slot => slot.Item));
    }

    public Task<IReadOnlyList<StockStack>> ReadBackpack(CancellationToken token)
        => Task.FromResult<IReadOnlyList<StockStack>>(ReadStacks(ReadBackpackSlots(token, out _).Select(slot => slot.Item), false));

    public bool HasBackpackSpace(CancellationToken token)
    {
        ReadBackpackSlots(token, out int freeCells);
        return freeCells > 0;
    }

    private InventoryItemStruct[] ReadBackpackSlots(CancellationToken token, out int freeCells)
    {
        ValidateSession(token);
        var inventory = InventoryTable().Where(entry => entry.InventoryId == 1).ToArray();
        if (inventory.Length != 1 || !Inventory.TryReadSlots(inventory[0].InventoryPtr0, out var header, out var slots, out freeCells) ||
            header.TotalBoxes.X != 12 || header.TotalBoxes.Y != 5)
            throw new TradeBlockedException("Указатель или сетка рюкзака не подтверждены.");
        return slots;
    }

    private IReadOnlyList<StockStack> ReadStacks(IEnumerable<IntPtr> pointers, bool useCache = true)
    {
        var stacks = new List<StockStack>();
        foreach (var address in pointers.Distinct())
        {
            if (useCache && stackCache.TryGetValue(address, out var previous))
            {
                stacks.Add(previous);
                continue;
            }
            var item = new Item(address);
            if (!item.IsValid || string.IsNullOrEmpty(item.Path))
                throw new TradeBlockedException("Игровой предмет изменился во время чтения контейнера.");
            if (!item.TryGetComponent<Base>(out var itemBase) || string.IsNullOrWhiteSpace(itemBase.BaseItemName)) continue;
            long quantity = item.TryGetComponent<Stack>(out var stack) ? stack.Count : 1;
            if (quantity <= 0) throw new TradeBlockedException("Размер стека не подтверждён.");
            var info = new StockStack(address.ToInt64(), item.Path, itemBase.BaseItemName, quantity);
            stacks.Add(info);
            if (useCache) stackCache.TryAdd(address, info);
        }
        return stacks;
    }

    private static SlotWitness ReadVisibleSlots(IntPtr view)
    {
        var reader = Core.Process.Handle;
        int tiles = 0;
        var items = new Dictionary<IntPtr, IntPtr>();
        foreach (var address in Walk(view, 12, 8500))
        {
            if (!TryNode(address, out var node) || node.UnscaledSize.X is < 15 or > 460 || node.UnscaledSize.Y is < 15 or > 460)
                continue;
            bool isTile = (node.Flags & 0x00f00000) == 0x00400000;
            if (isTile) tiles++;
            if (!reader.TryReadMemory<IntPtr>(address + ItemOffset, out var item) || item == IntPtr.Zero || items.ContainsKey(item) ||
                !PluginUiElementReflection.TryValidateItemAddress(item, out _, out _)) continue;
            items.Add(item, address);
            if (!isTile) tiles++;
        }
        return new(tiles, items);
    }

    public async Task<DepositResult> Deposit(IReadOnlyDictionary<string, long> proceeds, CancellationToken token)
    {
        RequireStash();
        var after = await ReadBackpack(token);
        int transferred = 0;
        var pending = new List<string>();
        foreach (var entry in proceeds)
        {
            long remaining = entry.Value;
            var stacks = after.Where(item => item.Metadata == entry.Key).OrderByDescending(item => item.Quantity).ToArray();
            long inBackpack = stacks.Sum(item => item.Quantity);
            if (entry.Key == Settlements.ExaltedMetadata)
            {
                if (stacks.Length > 0 && inBackpack >= remaining && await MoveToStash(stacks[0], inBackpack, token, bulk: true))
                    transferred += stacks.Length;
                else
                    pending.Add("Не подтверждён групповой перенос Exalted Orb из рюкзака в сундук.");
                continue;
            }
            if (stacks.Length > 1 && entry.Key.StartsWith("Metadata/Items/Currency/", StringComparison.Ordinal) &&
                inBackpack == remaining &&
                await MoveToStash(stacks[0], remaining, token, bulk: true))
            {
                transferred += stacks.Length;
                continue;
            }
            foreach (var item in stacks)
            {
                if (remaining <= 0) break;
                if (item.Quantity > remaining) continue;
                if (!await MoveToStash(item, item.Quantity, token)) continue;
                transferred++;
                remaining -= item.Quantity;
            }
            if (remaining > 0)
            {
                var current = await ReadBackpack(token);
                var source = current.Where(item => item.Metadata == entry.Key && item.Quantity > remaining)
                    .MinBy(item => item.Quantity);
                if (source != null && await SplitBackpackStack(source, remaining, token) is { } split &&
                    await MoveToStash(split, split.Quantity, token))
                {
                    transferred++;
                    remaining = 0;
                }
            }
            if (remaining > 0) pending.Add($"{entry.Key} — {remaining} единиц выручки остались в рюкзаке.");
        }
        return new(transferred, pending);
    }

    private async Task<bool> MoveToStash(StockStack item, long quantityToMove, CancellationToken token, bool bulk = false)
    {
        var stash = RequireStash();
        var current = await ReadBackpack(token);
        if (!current.Contains(item)) return false;
        long beforeQuantity = current.Where(stack => stack.Metadata == item.Metadata).Sum(stack => stack.Quantity);
        if (bulk && (beforeQuantity != quantityToMove || current.Any(stack => stack.Name == item.Name && stack.Metadata != item.Metadata)))
            return false;
        var slot = BackpackSlot(item);
        long? stashBefore = ConfirmedStashCount(item.Metadata);
        if (slot == IntPtr.Zero || stashBefore == null) return false;
        if (RequireStash() != stash || ReadCursorStack() != null) throw new TradeBlockedException("Окно сундука или курсор изменились.");
        Click(slot, token, GameInput.Control, rightClick: bulk);
        for (int attempt = 0; attempt < 24; attempt++)
        {
            await Task.Delay(75, token);
            if (RequireStash() != stash) throw new TradeBlockedException("Сундук закрылся при проверке переноса.");
            var updated = await ReadBackpack(token);
            long quantity = updated.Where(stack => stack.Metadata == item.Metadata).Sum(stack => stack.Quantity);
            if (beforeQuantity - quantity == quantityToMove && ConfirmedStashCount(item.Metadata) == stashBefore + quantityToMove)
                return true;
        }
        throw new TradeBlockedException($"Перенос {item.Name} не подтвердился одновременно в рюкзаке и сундуке.");
    }

    private static IntPtr BackpackSlot(StockStack item)
    {
        var right = Core.States.InGameStateObject.GameUi.RightPanel;
        return right.IsVisible ? ReadVisibleSlots(At(right.Address, 5, 36)).Items.GetValueOrDefault(new IntPtr(item.Identity)) : IntPtr.Zero;
    }

    private long? ConfirmedStashCount(string metadata)
    {
        if (confirmedStashInventories.Count == 0) return null;
        var addresses = InventoryTable().Select(entry => entry.InventoryPtr0).ToHashSet();
        var items = new HashSet<IntPtr>();
        foreach (var inventory in confirmedStashInventories)
        {
            if (!addresses.Contains(inventory) || !Inventory.TryReadSlots(inventory, out _, out var slots)) return null;
            foreach (var slot in slots) items.Add(slot.Item);
        }
        return ReadStacks(items, false).Where(item => item.Metadata == metadata).Sum(item => item.Quantity);
    }

    private sealed record SlotWitness(int Tiles, Dictionary<IntPtr, IntPtr> Items);
    private sealed record GridStock(IntPtr Inventory, IReadOnlyList<StockStack> Stacks);

    [StructLayout(LayoutKind.Explicit, Size = TabDescriptorSize)]
    private struct StashTabDescriptor
    {
        [FieldOffset(0x00)] internal StdWString Name;
        [FieldOffset(0x20)] internal uint Color;
        [FieldOffset(0x80)] internal IntPtr View;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct StashGridInventory
    {
        [FieldOffset(0x6f8)] internal IntPtr Inventory;
        [FieldOffset(0x700)] internal IntPtr Owner;
    }
}
