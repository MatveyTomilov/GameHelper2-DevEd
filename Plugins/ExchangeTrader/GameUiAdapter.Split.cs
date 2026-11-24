using System.Numerics;
using GameHelper;
using GameHelper.RemoteEnums;
using GameHelper.RemoteObjects.States.InGameStateObjects;
using GameOffsets.Objects.States.InGameState;
using static ExchangeTrader.ExchangeUi;

namespace ExchangeTrader;

internal sealed partial class GameUiAdapter
{
    private async Task<StockStack?> SplitBackpackStack(StockStack item, long quantity, CancellationToken token)
    {
        var stash = RequireStash();
        var slot = BackpackSlot(item);
        if (slot == IntPtr.Zero || quantity <= 0 || quantity >= item.Quantity || FindEmptyBackpackCell() == null)
            return null;
        if (ReadCursorStack() != null || FindSplitView() != null || !(await ReadBackpack(token)).Contains(item))
            throw new TradeBlockedException("Курсор или исходный стек изменились перед разделением.");

        Click(slot, token, GameInput.Shift);
        var opened = await Until(FindSplitView, "Не открылось окно разделения стека.", token);
        SplitView? ReadSplit()
        {
            if (RequireStash() != stash) throw new TradeBlockedException("Сундук закрылся при разделении стека.");
            var view = FindSplitView();
            if (view != null && view.Panel != opened.Panel) throw new TradeBlockedException("Окно разделения стека сменилось.");
            return view != null && view.Quantity + view.Retained == item.Quantity ? view : null;
        }

        var current = await Until(ReadSplit, "Исходное количество стека не подтверждено.", token, 650);
        for (int attempt = 0; attempt < 40; attempt++)
        {
            long difference = quantity - current.Quantity;
            if (difference == 0) break;
            ValidateSession(token);
            int steps = Math.Abs(difference) > 32 ? 32 : 1;
            GameInput.Press(difference > 0 ? GameInput.RightArrow : GameInput.LeftArrow, steps);
            await Task.Delay(65, token);
            current = await Until(() =>
            {
                var updated = ReadSplit();
                return updated?.Quantity != current.Quantity ? updated : null;
            }, "Клиент не подтвердил изменение количества при разделении.", token, 650);
        }
        if (ReadSplit()?.Quantity != quantity || FindEmptyBackpackCell() == null || ReadCursorStack() != null)
            throw new TradeBlockedException("Не подтверждены точное количество или свободная ячейка.");
        ValidateSession(token);
        GameInput.Press(GameInput.Enter);

        var held = await Until(() =>
        {
            if (RequireStash() != stash) throw new TradeBlockedException("Сундук закрылся после разделения.");
            var cursor = ReadCursorStack();
            return FindSplitView() == null && cursor?.Metadata == item.Metadata && cursor.Quantity == quantity ? cursor : null;
        }, "Не подтверждён отделённый стек на курсоре.", token);

        var cell = FindEmptyBackpackCell();
        ValidateSession(token);
        if (cell == null || ReadCursorStack() != held)
            throw new TradeBlockedException("Свободная ячейка или отделённый стек изменились.");
        GameInput.Move(cell.Value);
        await Task.Delay(85, token);
        ValidateSession(token);
        if (RequireStash() != stash || FindEmptyBackpackCell() != cell || ReadCursorStack() != held)
            throw new TradeBlockedException("Состояние рюкзака изменилось перед укладкой стека.");
        GameInput.Click(cell.Value);
        return await Until(() =>
        {
            if (RequireStash() != stash) throw new TradeBlockedException("Сундук закрылся до фиксации отделённого стека.");
            return ReadCursorStack() == null ? ReadLandedStack(cell.Value, held) : null;
        }, "Отделённый стек в рюкзаке не подтверждён.", token);
    }

    private static SplitView? FindSplitView()
    {
        foreach (var root in Children(Root))
        {
            if (!TryNode(root, out var node) || node.UnscaledSize.X != 2560 || node.UnscaledSize.Y != 1600 ||
                !Visible(root) || Children(root) is not [var panel] || !TryNode(panel, out var dialog) ||
                dialog.UnscaledSize.X != 358 || dialog.UnscaledSize.Y != 176 ||
                Children(panel) is not [var slider, var confirm] || !TryNode(confirm, out var check) ||
                check.UnscaledSize.X != 60 || check.UnscaledSize.Y != 60 || !Visible(confirm) ||
                Children(slider) is not [var retained, var split]) continue;
            if (ReadNumber(Text(retained)) is not (long left and >= 0) ||
                ReadNumber(Text(split)) is not (long right and > 0)) continue;
            return new(panel, left, right);
        }
        return null;
    }

    private StockStack? ReadCursorStack()
    {
        var slots = InventorySlots(InventoryName.Cursor1, 4, 4);
        if (slots.Length == 0) return null;
        if (slots.Length != 1 || ReadStacks(slots.Select(slot => slot.Item), false) is not [var item])
            throw new TradeBlockedException("Предмет на курсоре не подтверждён.");
        return item;
    }

    private static InventoryItemStruct[] InventorySlots(InventoryName name, int width, int height)
    {
        var matches = InventoryTable().Where(row => row.InventoryId == (int)name).ToArray();
        if (matches.Length != 1 || !Inventory.TryReadSlots(matches[0].InventoryPtr0, out var header, out var slots) ||
            header.TotalBoxes.X != width || header.TotalBoxes.Y != height ||
            slots.Any(s => s.SlotStart.X < 0 || s.SlotStart.Y < 0 || s.SlotEnd.X > width || s.SlotEnd.Y > height ||
                s.SlotEnd.X <= s.SlotStart.X || s.SlotEnd.Y <= s.SlotStart.Y))
            throw new TradeBlockedException($"Геометрия контейнера {name} не подтверждена.");
        return slots;
    }

    private static Vector2? FindEmptyBackpackCell()
    {
        if (!BackpackGrid(out var position, out var cell)) return null;
        var slots = InventorySlots(InventoryName.MainInventory1, 12, 5);
        for (int y = 0; y < 5; y++)
            for (int x = 0; x < 12; x++)
                if (!slots.Any(s => x >= s.SlotStart.X && x < s.SlotEnd.X && y >= s.SlotStart.Y && y < s.SlotEnd.Y))
                    return position + new Vector2(x + 0.5f, y + 0.5f) * cell;
        return null;
    }

    private StockStack? ReadLandedStack(Vector2 target, StockStack held)
    {
        if (!BackpackGrid(out var position, out var cell)) return null;
        var local = (target - position) / cell;
        if (local.X < 0 || local.X >= 12 || local.Y < 0 || local.Y >= 5) return null;
        int x = (int)local.X, y = (int)local.Y;
        var slots = InventorySlots(InventoryName.MainInventory1, 12, 5);
        var match = slots.Where(s => s.SlotStart.X == x && s.SlotStart.Y == y &&
            s.SlotEnd.X == x + 1 && s.SlotEnd.Y == y + 1).ToArray();
        if (match.Length != 1 || ReadStacks(match.Select(slot => slot.Item), false) is not [var landed] ||
            landed.Metadata != held.Metadata || landed.Quantity != held.Quantity) return null;
        return landed;
    }

    private static bool BackpackGrid(out Vector2 position, out Vector2 cell)
    {
        cell = Vector2.Zero;
        var grid = At(Core.States.InGameStateObject.GameUi.RightPanel.Address, 5, 36);
        if (!Rect(grid, out position, out var size) ||
            !Core.Process.Handle.TryReadMemory<StashGridInventory>(grid, out var link) ||
            !InventoryTable().Any(row => row.InventoryId == (int)InventoryName.MainInventory1 &&
                row.InventoryPtr0 == link.Inventory && row.InventoryPtr1 == link.Owner))
            return false;
        cell = size / new Vector2(12, 5);
        return cell.X > 15 && cell.Y > 15 && Math.Abs(cell.X - cell.Y) < 1f;
    }

    private sealed record SplitView(IntPtr Panel, long Retained, long Quantity);
}
