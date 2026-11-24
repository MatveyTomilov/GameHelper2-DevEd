namespace ExchangeTrader;

internal sealed class TradeSession(IExchangeGame game, Action<SessionStatus> publish, Action<string> log,
    Action<IReadOnlyList<StashTab>>? observeTabs = null, Action<IEnumerable<StockStack>>? observeItems = null)
{
    private static readonly TimeSpan InactivityLimit = TimeSpan.FromMinutes(10);
    private SessionStatus status = new(SessionPhase.IDLE, "Готов");

    internal async Task Run(TradingPolicy policy, CancellationToken token)
    {
        try
        {
            game.ValidateSession(token);
            Set(SessionPhase.SCANNING, "Открываю сундук");
            var scanner = new StockScanner(game);
            var (stock, catalog) = await scanner.Scan(policy.ExcludedTabs, (read, total, name) =>
            {
                status = status with { TabsRead = read, TabsTotal = total };
                Set(SessionPhase.SCANNING, name);
            }, message =>
            {
                status = status with { Skipped = status.Skipped + 1 };
                log(message);
            }, token, observeTabs, observeItems);
            status = status with { Products = stock.ByName.Count + status.Skipped };
            log($"Каталог {catalog.Select(item => item.Name).Distinct().Count()}, сверено с запасами {stock.ByName.Count}.");
            var exalted = Settlements.Product(SettlementCurrency.Exalted, stock);
            var divine = Settlements.Product(SettlementCurrency.Divine, stock);
            TradeProduct[] currencies = policy.Settlement switch
            {
                SettlementMode.Divine => [divine],
                // Exalted won the measured pass more often. Read that book last to avoid reselecting it before the draft.
                SettlementMode.Auto => [divine, exalted],
                _ => [exalted],
            };
            var availableQuotes = currencies.Where(quote => !policy.Excludes(quote) && catalog.Any(item => item.Name == quote.Name)).ToArray();
            var queue = new Queue<SaleItem>(SaleQueue.Build(stock, currencies, policy.Rules, Skip));
            status = status with { Queued = queue.Count, Remaining = queue.Count };
            var pricing = new SettlementPricing(game, exalted, divine, log);
            var orders = new OrderExecutor(game);
            var collection = new CollectionManager(game);
            var stash = new StashManager(game);
            bool hasItemExclusions = policy.Rules.Any(rule => rule.Excluded);
            var proceeds = new Dictionary<string, long>(StringComparer.Ordinal);
            var tracked = new Dictionary<long, OrderProgress>();
            var reservesFromReturns = new Dictionary<string, long>(StringComparer.Ordinal);

            while (true)
            {
                if (!queue.TryPeek(out var item))
                {
                    var board = await ReadAndCollect();
                    if (queue.Count > 0) continue;
                    if (await RecycleStalledOrder(board)) continue;
                    int active = board.Orders.Count(order => order.Active && CanClaim(order));
                    if (active == 0) break;

                    await StoreProceedsWhileTrading();
                    Set(SessionPhase.WAITING_FOR_BUYER, $"Жду покупателей. Активных ордеров: {active}.");
                    await game.WaitForOrdersPoll(token);
                    continue;
                }

                await AwaitSpace();
                Set(SessionPhase.SELLING, item.Product.Name);
                try
                {
                    var prepared = await WithOpenExchange(async () =>
                    {
                        var priced = await pricing.Price(item, availableQuotes, policy, token);
                        var (sale, market, _) = priced;
                        var ask = market.Seller.Ratio.Invert();
                        var bid = market.Buyer.Ratio;
                        log($"{item.Product.Name} → {sale.Pair.Want.Name}, ask {ask.Want}:{ask.Have}, bid {bid.Want}:{bid.Have}, лот {sale.Want}:{sale.Have}.");
                        return await orders.Prepare(priced, token);
                    });
                    var order = await orders.Place(prepared, token);
                    if (order.Active) tracked[order.Id] = new(order, game.Now);
                    status = status with { Placed = status.Placed + 1 };
                    var placed = prepared.Draft.Order;
                    log($"{item.Product.Name}: {placed.Have} → {placed.Want} {placed.Pair.Want.Name}, ордер {order.Id}.");
                }
                catch (MarketUnavailableException ex)
                {
                    Skip($"{item.Product.Name} — {ex.Message}");
                }
                queue.Dequeue();
                status = status with { Remaining = queue.Count };
            }
            await DepositProceeds();
            Set(SessionPhase.FINISHED, "Допустимые лоты обработаны, доступная выручка в сундуке.");

            bool CanClaim(OwnOrder order) => !hasItemExclusions || order.Pair is { } pair &&
                !policy.Excludes(pair.Want) && (order.RemainingGoods == 0 || !policy.Excludes(pair.Have));

            bool HasClaimableProceeds(ExchangeOrders board) =>
                board.Orders.Any(order => order.HasReceipt && CanClaim(order));

            async Task<T> WithOpenExchange<T>(Func<Task<T>> readOrPrepare)
            {
                try { return await readOrPrepare(); }
                catch (ExchangeClosedException)
                {
                    await game.OpenExchange(token);
                    log("Биржа открыта заново. Перечитываю текущее состояние.");
                    return await readOrPrepare();
                }
            }

            async Task<ExchangeOrders> ReadAndCollect()
            {
                while (true)
                {
                    var board = await WithOpenExchange(() => game.ReadOrders(token));
                    if (await CollectCancelled(board)) continue;
                    if (!HasClaimableProceeds(board)) return board;
                    await CollectAvailable();
                }
            }

            async Task AwaitSpace()
            {
                while (true)
                {
                    var board = await WithOpenExchange(() => game.ReadOrders(token));
                    if (await CollectCancelled(board)) continue;
                    if (!board.HasSpace && HasClaimableProceeds(board))
                    {
                        await CollectAvailable();
                        await StoreProceedsWhileTrading();
                        continue;
                    }
                    if (await RecycleStalledOrder(board)) continue;
                    await StoreProceedsWhileTrading();
                    if (board.HasSpace) return;
                    if (!board.Orders.Any(order => order.Active))
                        throw new TradeBlockedException("Все слоты заняты ордерами, которые требуют ручного получения выручки или возврата.");
                    Set(SessionPhase.WAITING_FOR_SLOT,
                        $"Жду свободный слот ({board.Used}/{board.Capacity}). В очереди: {status.Remaining}. Под таймером: {tracked.Count}.");
                    await game.WaitForOrdersPoll(token);
                }
            }

            async Task<bool> RecycleStalledOrder(ExchangeOrders board)
            {
                foreach (long id in tracked.Keys.Where(id => !board.Orders.Any(order => order.Id == id && order.Active)).ToArray())
                    tracked.Remove(id);

                OwnOrder? stale = null;
                var now = game.Now;
                foreach (var order in board.Orders)
                {
                    if (!order.Active || order.RemainingGoods <= 0 || !CanClaim(order)) continue;
                    if (!tracked.TryGetValue(order.Id, out var progress) || order != progress.Order)
                        tracked[order.Id] = new(order, now);
                    else if (now - progress.ChangedAt >= InactivityLimit)
                        stale ??= order;
                }
                if (stale == null) return false;

                string label = stale.Pair?.Have.Name ?? $"Ордер {stale.Id}";
                Set(SessionPhase.COLLECTING, $"{label}: нет продаж 10 минут, проверяю остаток.");
                if (!await game.CancelOnce(stale, token)) return true;
                var closed = (await game.ReadOrders(token)).Orders.SingleOrDefault(order => order.Id == stale.Id);
                if (closed == null || closed.Active)
                    throw new TradeBlockedException("Отмена ордера не подтверждена. Повторного нажатия не было.");
                log($"Ордер {closed.Id} снят после 10 минут без продаж. Остаток: {closed.RemainingGoods}, выручка: {closed.Unclaimed}.");
                await RecoverReturn(closed);
                return true;
            }

            async Task<bool> CollectCancelled(ExchangeOrders board)
            {
                var cancelled = board.Orders.FirstOrDefault(order => !order.Listed && !order.Complete && order.HasReceipt && CanClaim(order));
                if (cancelled == null) return false;
                await RecoverReturn(cancelled);
                return true;
            }

            async Task RecoverReturn(OwnOrder closed)
            {
                var (returned, product) = await CollectAvailable(closed.Id);
                tracked.Remove(closed.Id);
                if (returned != closed.RemainingGoods || (returned > 0 && product == null) ||
                    (await game.ReadOrders(token)).Orders.Any(order => order.Id == closed.Id))
                    throw new TradeBlockedException("Возврат остатка и освобождение слота не подтверждены.");
                await StoreProceedsWhileTrading();
                if (product != null) QueueReturn(product, returned);
            }

            void QueueReturn(TradeProduct product, long returned)
            {
                stock.ByName.TryGetValue(product.Name, out var initial);
                if (policy.Excludes(product) || currencies.Any(quote => quote.Id == product.Id || quote.Name == product.Name) ||
                    !catalog.Any(entry => entry.Name == product.Name) || initial is { Protection: not null } ||
                    initial != null && initial.Product.Id != product.Id)
                {
                    log($"{product.Name}: возвращено {returned} в сундук. Товар не подходит для продажи по текущим настройкам.");
                    return;
                }

                long reserve = policy.Rules.Where(rule => rule.Key == product.Id || rule.Key == product.Name)
                    .Select(rule => rule.Reserve).DefaultIfEmpty().Max();
                long shortfall = Math.Max(0, reserve - (initial?.Available ?? 0));
                long held = Math.Min(returned, shortfall - reservesFromReturns.GetValueOrDefault(product.Id));
                if (held > 0) reservesFromReturns[product.Id] = reservesFromReturns.GetValueOrDefault(product.Id) + held;
                long sellable = returned - held;
                if (sellable == 0)
                {
                    log($"{product.Name}: все {returned} единиц возврата оставлены в резерве.");
                    return;
                }
                queue.Enqueue(new(product, checked((int)sellable)));
                status = status with { Queued = status.Queued + 1, Remaining = queue.Count };
                log($"{product.Name}: возвращено {returned}, в конец очереди добавлено {sellable}.");
            }

            async Task<(long Quantity, TradeProduct? Product)> CollectAvailable(long? onlyOrder = null)
            {
                Set(SessionPhase.COLLECTING, "Проверяю завершённые ордера");
                long returned = 0;
                TradeProduct? returnedProduct = null;
                while (true)
                {
                    var receipt = await collection.Collect(log, token, CanClaim, onlyOrder);
                    returned = checked(returned + receipt.Returned);
                    if (receipt.ReturnedProduct is { } item)
                    {
                        if (returnedProduct != null && returnedProduct != item)
                            throw new TradeBlockedException("Получены разные товары по возврату одного ордера.");
                        returnedProduct = item;
                    }
                    foreach (var entry in receipt.Proceeds)
                    {
                        var product = stock.ByName.Values.FirstOrDefault(value => value.Product.Id == entry.Key)?.Product ??
                            new TradeProduct(entry.Key, entry.Key);
                        if (policy.Excludes(product)) throw new TradeBlockedException("Получена исключённая валюта. Перенос в сундук остановлен.");
                        proceeds[entry.Key] = checked(proceeds.GetValueOrDefault(entry.Key) + entry.Value);
                    }
                    status = status with { Collected = status.Collected + receipt.Orders };
                    if (!receipt.BackpackFull) return (returned, returnedProduct);
                    if (proceeds.Count == 0)
                    {
                        long carriedExalted = (await game.ReadBackpack(token))
                            .Where(stack => stack.Metadata == Settlements.ExaltedMetadata).Sum(stack => stack.Quantity);
                        if (carriedExalted == 0 || policy.Excludes(exalted))
                            throw new TradeBlockedException("Нет свободных ячеек в рюкзаке для получения выручки.");
                        proceeds.Add(Settlements.ExaltedMetadata, carriedExalted);
                    }
                    await StoreProceedsWhileTrading();
                    Set(SessionPhase.COLLECTING, "Продолжаю получать выручку");
                }
            }

            async Task StoreProceedsWhileTrading()
            {
                if (proceeds.Count == 0) return;
                await DepositProceeds();
                await game.OpenExchange(token);
            }

            async Task DepositProceeds()
            {
                Set(SessionPhase.DEPOSITING, "Проверяю перенос выручки");
                var deposit = await stash.Deposit(proceeds, token);
                status = status with { Deposited = status.Deposited + deposit.Transferred };
                foreach (string pending in deposit.Pending) log(pending);
                if (deposit.Pending.Count > 0) throw new TradeBlockedException("Часть выручки осталась в рюкзаке.");
                proceeds.Clear();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Set(SessionPhase.STOPPED, "Сессия остановлена."); }
        catch (TradeBlockedException ex) { Set(SessionPhase.BLOCKED, ex.Message); log(ex.Message); }
        catch (Exception ex) { Set(SessionPhase.ERROR, ex.Message); log(ex.ToString()); }
    }

    private sealed record OrderProgress(OwnOrder Order, DateTimeOffset ChangedAt);

    private void Set(SessionPhase phase, string detail)
    {
        status = status with { Phase = phase, Detail = detail };
        publish(status);
    }

    private void Skip(string message)
    {
        status = status with { Skipped = status.Skipped + 1 };
        log(message);
    }
}
