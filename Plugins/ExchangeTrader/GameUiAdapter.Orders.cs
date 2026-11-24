using System.Numerics;
using GameHelper;
using static ExchangeTrader.ExchangeUi;

namespace ExchangeTrader;

internal sealed partial class GameUiAdapter
{
    private PendingSubmission? pendingSubmission;
    private readonly Dictionary<long, Pair> attributedOrders = [];

    private static long? ReadWallet() => ReadNumber(Text(At(Core.States.InGameStateObject.GameUi.RightPanel.Address, 5, 6, 1)));

    public Task<ExchangeOrders> ReadOrders(CancellationToken token)
        => Until(() => ReadOrderSnapshot(RequireExchange()), "Счётчик и карточки ордеров не согласованы.", token);

    public async Task WaitForOrdersPoll(CancellationToken token)
    {
        do
        {
            await Task.Delay(TimeSpan.FromSeconds(3), token);
            ValidateCharacter(token);
        } while (!GameInput.IsGameForeground || Core.IsSettingsMenuOpen);
    }

    private ExchangeOrders? ReadOrderSnapshot(IntPtr panel)
    {
        var fields = Children(panel);
        if (fields.Length < 20) return null;
        int counter = 18;
        if (!ExchangeOrders.TryParseSlots(Plain(Text(fields[counter])), out int used, out int capacity))
        {
            counter = 17;
            if (!ExchangeOrders.TryParseSlots(Plain(Text(fields[counter])), out used, out capacity)) return null;
        }

        var cards = Children(At(fields[counter + 1], 2)).Where(Visible).ToArray();
        if (cards.Length != used || cards.Distinct().Count() != used) return null;
        var orders = new List<OwnOrder>(used);
        foreach (var card in cards)
        {
            var requested = ReadNumber(Text(At(card, 6)));
            var offered = ReadNumber(Text(At(card, 7)));
            if (!IsText(Text(At(card, 1)), "Buying", "Покупка", "Покупаю") ||
                !IsText(Text(At(card, 2)), "Selling", "Продажа", "Продаю") ||
                requested is not (> 0 and <= int.MaxValue) || offered is not (> 0 and <= int.MaxValue))
                return null;
            var status = At(card, 3);
            if (!Visible(status)) return null;
            string state = Text(status);
            bool complete = IsText(state, "Order Completed", "Ордер выполнен", "Заказ выполнен");
            bool listed = IsText(state, "Order Listed", "Order Partially Filled", "Ордер размещён", "Ордер размещен",
                "Заказ размещён", "Заказ размещен", "Ордер частично выполнен", "Заказ частично выполнен");
            if (!complete && !listed && !IsText(state, "Order Cancelled", "Order Canceled", "Ордер отменён",
                "Ордер отменен", "Заказ отменён", "Заказ отменен")) return null;
            var unclaimed = ReadNumber(Text(At(card, 4, 1)));
            var remaining = ReadNumber(Text(At(card, 5, 1)));
            if (!unclaimed.HasValue || remaining == null || remaining > offered) return null;
            long id = card.ToInt64();
            orders.Add(new(id, attributedOrders.GetValueOrDefault(id), (int)offered.Value, (int)requested.Value,
                complete, unclaimed.Value, listed, remaining.Value));
        }

        var ids = orders.Select(order => order.Id).ToHashSet();
        foreach (long disappeared in attributedOrders.Keys.Where(id => !ids.Contains(id)).ToArray())
            attributedOrders.Remove(disappeared);
        if (pendingSubmission is { } pending && pending.Panel == panel && pending.Used + 1 == used &&
            pending.KnownCards.IsSubsetOf(ids) && Matches(panel, pending.Draft.Order.Pair) &&
            ReadWallet() == pending.Wallet - pending.Draft.GoldCost)
        {
            int index = orders.FindIndex(order => !pending.KnownCards.Contains(order.Id));
            var added = orders[index];
            var expected = pending.Draft.Order;
            if (added.Offered == expected.Have && added.Requested == expected.Want)
            {
                attributedOrders.Add(added.Id, expected.Pair);
                orders[index] = added with { Pair = expected.Pair };
                pendingSubmission = null;
            }
        }
        return new(used, capacity, ++orderGeneration, orders);
    }

    public async Task<bool> CancelOnce(OwnOrder order, CancellationToken token)
    {
        ValidateSession(token);
        if (order is not { Active: true, RemainingGoods: > 0 } ||
            CloseOrderDialog() != null || ReadCursorStack() != null)
            throw new TradeBlockedException("Биржа не готова к снятию неактивного лота.");

        var confirmation = await OpenOrderConfirmation(order, token);
        if (confirmation == null) return false;
        var (panel, dialog) = confirmation.Value;
        if (!Rect(dialog.Confirm, out var position, out var size))
            throw new TradeBlockedException("Не подтверждена кнопка снятия ордера.");
        GameInput.Move(position + size / 2);
        await Task.Delay(180, token);
        ValidateSession(token);
        if (CloseOrderDialog() != dialog || !Rect(dialog.Confirm, out var currentPosition, out var currentSize) ||
            !GameInput.CursorOver(currentPosition, currentPosition + currentSize))
            throw new TradeBlockedException("Диалог снятия изменился до подтверждения.");
        if (ReadOrderSnapshot(panel)?.Orders.SingleOrDefault(entry => entry.Id == order.Id) != order)
        {
            if (!Rect(dialog.Dismiss, out var dismissPosition, out var dismissSize))
                throw new TradeBlockedException("Не подтверждена кнопка закрытия диалога снятия.");
            GameInput.ClickAndHold(dismissPosition + dismissSize / 2);
            await Until(() => !Visible(dialog.Root) ? new object() : null, "Окно отмены не закрылось.", token);
            return false;
        }

        CheckWorldTarget();
        GameInput.ClickAndHold(currentPosition + currentSize / 2);
        await Until(() => !Visible(dialog.Root) && ReadOrderSnapshot(RequireExchange())?.Orders.SingleOrDefault(entry =>
            entry.Id == order.Id && !entry.Active && entry.Pair == order.Pair && entry.Offered == order.Offered &&
            entry.Requested == order.Requested && entry.RemainingGoods <= order.RemainingGoods) is { HasReceipt: true } watched ? watched : null,
            "Снятие ордера не подтверждено. Повторного нажатия не было.", token, 4500);
        return true;
    }

    private async Task<(IntPtr Panel, OrderDialog Dialog)?> OpenOrderConfirmation(OwnOrder order, CancellationToken token)
    {
        var card = new IntPtr(order.Id);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var panel = RequireExchange();
            var picker = Picker(panel);
            if (Visible(picker))
            {
                await ClosePicker(picker, token);
                panel = RequireExchange();
            }
            if (!SameOrder()) return null;

            var close = At(card, 10);
            if (!TryGetCancelRect(out var position, out var size))
                throw new TradeBlockedException("Не подтверждена кнопка снятия на карточке ордера.");
            GameInput.Move(position + size / 2);
            await Task.Delay(120, token);
            ValidateSession(token);
            if (ExchangePanel() != panel || CloseOrderDialog() != null || Visible(Picker(panel)) || ReadCursorStack() != null)
                throw new TradeBlockedException("Панель биржи изменилась перед открытием подтверждения снятия.");
            if (!SameOrder()) return null;
            if (!TryGetCancelRect(out position, out size) || !GameInput.CursorOver(position, position + size))
                throw new TradeBlockedException("Карточка переместилась перед открытием подтверждения снятия.");
            CheckWorldTarget();
            GameInput.ClickAndHold(position + size / 2);

            long deadline = Environment.TickCount64 + 3000;
            do
            {
                ValidateSession(token);
                if (CloseOrderDialog() is { } dialog && ExchangePanel() == panel) return (panel, dialog);
                if (ExchangePanel() == IntPtr.Zero) break;
                await Task.Delay(75, token);
            } while (Environment.TickCount64 < deadline);

            if (ExchangePanel() != IntPtr.Zero || CloseOrderDialog() != null)
                throw new TradeBlockedException("Подтверждение снятия ордера не распознано на открытой бирже.");
            if (attempt != 0 || Core.States.InGameStateObject.GameUi.LeftPanel.IsVisible || ReadCursorStack() != null)
                throw new TradeBlockedException("Биржа закрылась до подтверждения снятия. Ордер не подтверждался.");
            await OpenExchange(token);

            bool SameOrder() => ReadOrderSnapshot(panel)?.Orders.SingleOrDefault(entry => entry.Id == order.Id) == order;

            bool TryGetCancelRect(out Vector2 position, out Vector2 size) =>
                Rect(close, out position, out size) && At(card, 10) == close && Visible(close) &&
                Rect(At(panel, 16), out var submit, out var submitSize) && position.Y >= submit.Y + submitSize.Y;
        }
        return null;
    }

    private static OrderDialog? CloseOrderDialog()
    {
        OrderDialog? result = null;
        foreach (var window in Children(Root))
        {
            var content = At(window, 0, 0);
            if (!IsText(Plain(Text(At(content, 0))), "Close Order", "Закрыть ордер", "Закрыть заказ") || !Visible(content)) continue;
            var confirm = At(content, 3, 0);
            var dismiss = At(content, 3, 1);
            if (result != null || !Visible(confirm) || !Visible(dismiss) ||
                !IsText(Plain(Text(At(confirm, 0))), "OK", "ОК") ||
                !IsText(Plain(Text(At(dismiss, 0))), "Cancel", "Отмена")) return null;
            result = new(window, confirm, dismiss);
        }
        return result;
    }

    public async Task ClaimBatch(OwnOrder order, bool returnGoods, CancellationToken token)
    {
        ValidateSession(token);
        var panel = RequireExchange();
        var item = At(new IntPtr(order.Id), returnGoods ? 5 : 4);
        if (!order.HasReceipt || (returnGoods ? order.RemainingGoods : order.Unclaimed) <= 0 ||
            !StillClaimable() || !Visible(item) || !Rect(item, out var position, out var size))
            throw new TradeBlockedException("Содержимое карточки изменилось перед получением выручки.");
        GameInput.Move(position + size / 2);
        await Task.Delay(120, token);
        ValidateSession(token);
        if (!StillClaimable() || !Visible(item) || !Rect(item, out position, out size) ||
            !GameInput.CursorOver(position, position + size))
            throw new TradeBlockedException("Карточка или курсор изменились перед получением выручки.");
        CheckWorldTarget();
        GameInput.ClickAndHold(position + size / 2, GameInput.Control, rightClick: true);
        // Let the exchange apply the transfer before another claim, even if the card already updated.
        await Task.Delay(500, token);
        ValidateSession(token);
        if (ReadCursorStack() != null) throw new TradeBlockedException("После получения выручки или возврата курсор остался занят.");

        bool StillClaimable() => ExchangePanel() == panel && ReadCursorStack() == null &&
            ReadOrderSnapshot(panel)?.Orders.SingleOrDefault(entry => entry.Id == order.Id) == order;
    }

    private long orderGeneration;
    private sealed record OrderDialog(IntPtr Root, IntPtr Confirm, IntPtr Dismiss);
    private sealed record PendingSubmission(Draft Draft, IntPtr Panel, int Used, long Wallet, HashSet<long> KnownCards);
}
