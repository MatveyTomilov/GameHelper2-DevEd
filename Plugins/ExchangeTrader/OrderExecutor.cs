namespace ExchangeTrader;

internal sealed class OrderExecutor(IExchangeGame game)
{
    internal async Task<PreparedOrder> Prepare(PricedLot priced, CancellationToken token)
    {
        var (sale, market, valuationExpires) = priced;
        var before = await game.ReadOrders(token);
        if (!before.HasSpace) throw new TradeBlockedException("Слоты ордеров заняты.");
        var draft = await game.Prepare(sale, token);
        if (draft.Order != sale || draft.GoldCost <= 0 || game.Now - market.Seller.ReceivedAt > PricingEngine.MaximumQuoteAge ||
            valuationExpires is { } deadline && game.Now > deadline)
            throw new MarketUnavailableException("Черновик не подтверждён или котировка устарела при заполнении.");

        return new(draft, before.Orders.Select(order => order.Id).ToHashSet());
    }

    internal async Task<OwnOrder> Place(PreparedOrder prepared, CancellationToken token)
    {
        var (draft, known) = prepared;
        var sale = draft.Order;
        game.ValidateSession(token);
        try
        {
            // A lost response after this call cannot lead to a second submission.
            await game.SubmitOnce(draft, token);
            for (int attempt = 0; attempt < 36; attempt++)
            {
                var after = await game.ReadOrders(token);
                var added = after.Orders.Where(order => !known.Contains(order.Id)).ToArray();
                if (added.Length == 1 && added[0].Pair == sale.Pair && added[0].Offered == sale.Have && added[0].Requested == sale.Want)
                    return added[0];
                if (added.Length > 1) break;
                await Task.Delay(100, token);
            }
        }
        catch (ExchangeClosedException)
        {
            throw new TradeBlockedException("Биржа закрылась при проверке размещения. Результат ордера не подтверждён.");
        }

        throw new TradeBlockedException("Ответ на размещение не подтверждён. Проверьте текущие ордера перед новым запуском.");
    }

    internal sealed record PreparedOrder(Draft Draft, IReadOnlySet<long> KnownOrders);
}

internal sealed class CollectionManager(IExchangeGame game)
{
    internal async Task<CollectionResult> Collect(Action<string> log, CancellationToken token, Func<OwnOrder, bool>? canClaim = null,
        long? onlyOrder = null)
    {
        var snapshot = await game.ReadOrders(token);
        var eligible = snapshot.Orders.Where(order => order.HasReceipt && (onlyOrder == null || order.Id == onlyOrder)).ToArray();
        var proceeds = new Dictionary<string, long>(StringComparer.Ordinal);
        int collected = 0;
        long returned = 0;
        TradeProduct? returnedProduct = null;
        foreach (var initial in eligible)
        {
            if (canClaim?.Invoke(initial) == false)
            {
                log("Завершённый ордер оставлен на бирже: выручка исключена или её тип не подтверждён.");
                continue;
            }
            while (true)
            {
                if (!game.HasBackpackSpace(token)) return new(collected, proceeds, returned, BackpackFull: true, ReturnedProduct: returnedProduct);
                var current = await game.ReadOrders(token);
                var order = current.Orders.SingleOrDefault(entry => entry.Id == initial.Id);
                if (order is not { HasReceipt: true } || canClaim?.Invoke(order) == false)
                    throw new TradeBlockedException("Карточка получения изменилась до действия.");
                bool returnGoods = order.Unclaimed == 0;
                var receipt = await ClaimBatch(current, order, returnGoods, token);
                proceeds[receipt.Product.Id] = checked(proceeds.GetValueOrDefault(receipt.Product.Id) + receipt.Quantity);
                if (returnGoods)
                {
                    returned = checked(returned + receipt.Quantity);
                    if (onlyOrder != null)
                    {
                        if (returnedProduct != null && returnedProduct != receipt.Product)
                            throw new TradeBlockedException("Разные порции возврата одного ордера содержат разные товары.");
                        returnedProduct = receipt.Product;
                    }
                }
                log($"{(returnGoods ? "Возвращено" : "Получено")} {receipt.Quantity} по ордеру {order.Id}.");
                if (!receipt.FreedSlot) continue;
                collected++;
                break;
            }
        }
        return new(collected, proceeds, returned, ReturnedProduct: returnedProduct);
    }

    private async Task<(TradeProduct Product, long Quantity, bool FreedSlot)> ClaimBatch(ExchangeOrders current, OwnOrder order,
        bool returnGoods, CancellationToken token)
    {
        var before = Totals(await game.ReadBackpack(token));
        await game.ClaimBatch(order, returnGoods, token);
        long quantity = returnGoods ? order.RemainingGoods : order.Unclaimed;
        long other = returnGoods ? order.Unclaimed : order.RemainingGoods;
        for (int attempt = 0; attempt < 30; attempt++)
        {
            var after = await game.ReadOrders(token);
            var updated = after.Orders.SingleOrDefault(entry => entry.Id == order.Id);
            bool freed = updated == null && other == 0 && after.Used == current.Used - 1;
            long removed = freed ? quantity : updated is { HasReceipt: true } && after.Used == current.Used &&
                (returnGoods ? updated.Unclaimed : updated.RemainingGoods) == other
                ? quantity - (returnGoods ? updated.RemainingGoods : updated.Unclaimed) : 0;
            if (removed > 0)
            {
                var stacks = await game.ReadBackpack(token);
                var backpack = Totals(stacks);
                var positive = backpack.Where(entry => entry.Value > before.GetValueOrDefault(entry.Key)).ToArray();
                if (positive.Length == 1 && positive[0].Value - before.GetValueOrDefault(positive[0].Key) == removed)
                {
                    if (order.Pair is { } pair && positive[0].Key != (returnGoods ? pair.Have : pair.Want).Id)
                        throw new TradeBlockedException("Тип полученной выручки или возврата не совпал с подтверждённым ордером.");
                    var names = stacks.Where(stack => stack.Metadata == positive[0].Key).Select(stack => stack.Name).Distinct().ToArray();
                    if (names.Length != 1 || string.IsNullOrWhiteSpace(names[0]))
                        throw new TradeBlockedException("Не подтверждено название предмета, полученного с биржи.");
                    return (new(positive[0].Key, names[0]), removed, freed);
                }
            }
            await Task.Delay(100, token);
        }
        throw new TradeBlockedException("Получение выручки не подтверждено карточкой и рюкзаком. Повторное нажатие пропущено.");
    }

    private static Dictionary<string, long> Totals(IEnumerable<StockStack> stacks)
        => stacks.GroupBy(stack => stack.Metadata).ToDictionary(group => group.Key, group => group.Sum(stack => stack.Quantity));
}

internal sealed class StashManager(IExchangeGame game)
{
    internal async Task<DepositResult> Deposit(IReadOnlyDictionary<string, long> proceeds, CancellationToken token)
    {
        await game.CloseExchange(token);
        if (proceeds.Count == 0) return new(0, []);
        await game.OpenStash(token);
        var result = await game.Deposit(proceeds, token);
        await game.CloseStash(token);
        return result;
    }
}
