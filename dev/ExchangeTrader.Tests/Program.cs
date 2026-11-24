using ExchangeTrader;
using GameHelper.Utils;

int checks = 0;
var now = DateTimeOffset.UtcNow;
var sale = new Pair(new("product", "Exalted"), new("quote", "Chaos"));
var inverse = new Pair(sale.Want, sale.Have);
var book = new MarketQuote(sale, new(inverse, new(2, 5), 1, now), new(sale, new(22, 10), 2, now));
Assert(PricingEngine.Calculate(sale, book, 10, 100, now).Want == 24, "integer undercut from asks");
Reject(() => PricingEngine.Calculate(sale, book, 1, 100, now), "small order must not over-discount");
Reject(() => PricingEngine.Calculate(sale, book, 10, 100, now.AddMinutes(1)), "stale quote");
Reject(() => PricingEngine.Calculate(sale, book with { Seller = book.Seller with { Pair = sale } }, 10, 100, now), "unverified side");
Reject(() => PricingEngine.Calculate(sale, book with { Buyer = book.Buyer with { Ratio = new(12, 5) } }, 10, 100, now), "do not cross bid");
var committedDraft = new SaleOrder(sale, 25, 594);
Assert(PricingEngine.IsPassive(committedDraft, new(1, 21)), "a different exact bid below the committed sale stays passive");
Assert(!PricingEngine.IsPassive(committedDraft, new(24, 1)) && !PricingEngine.IsPassive(committedDraft, new(594, 25)) &&
    !PricingEngine.IsPassive(committedDraft, default), "a crossing, tied or inexact bid blocks a committed draft");

var confirmation = new MarketRatioConfirmation();
var exactRatio = new Ratio(5, 1);
Assert(confirmation.Observe(exactRatio, null, 0) == MarketRatioState.Pending &&
    confirmation.Observe(exactRatio, null, 250) == MarketRatioState.Pending,
    "repeating a displayed ratio without loading cannot establish freshness");
confirmation.ObserveLoading();
Assert(confirmation.Observe(exactRatio, null, 300) == MarketRatioState.Pending &&
    confirmation.Observe(exactRatio, null, 375) == MarketRatioState.Exact,
    "both exact observations must follow the loading marker");
confirmation.ObserveLoading();
Assert(confirmation.Observe(default, "1.20 : 1", 450) == MarketRatioState.Pending &&
    confirmation.Observe(default, "No Stock", 600) == MarketRatioState.Pending &&
    confirmation.Observe(default, "No Stock", 799) == MarketRatioState.Pending &&
    confirmation.Observe(default, "No Stock", 800) == MarketRatioState.Unavailable,
    "only the same final unavailable status can end the wait early");
confirmation.ObserveLoading();
Assert(confirmation.Observe(default, "1.20 : 1", 900) == MarketRatioState.Pending &&
    confirmation.Observe(exactRatio, null, 975) == MarketRatioState.Pending &&
    confirmation.Observe(exactRatio, null, 1050) == MarketRatioState.Exact,
    "a transient rounded label must not discard the exact quote that follows");
confirmation.ObserveLoading();
Assert(confirmation.Observe(default, "1.20 : 1", 1200) == MarketRatioState.Pending &&
    confirmation.Observe(default, null, 1300) == MarketRatioState.Pending &&
    confirmation.Observe(default, "1.20 : 1", 1400) == MarketRatioState.Pending &&
    confirmation.Observe(default, "1.20 : 1", 1600) == MarketRatioState.Unavailable,
    "a rounded ratio stays unavailable and an unknown sample resets its stability period");
Assert(MarketRatioConfirmation.UnavailableText("<<market_ratio>> Market Ratio \r\n<font:'fontin'>{<italic>{<grey>{No Stock}}}") == "No Stock" &&
    MarketRatioConfirmation.UnavailableText("Market Ratio \n<grey>{1} : <grey>{47.60}") == "1 : 47.60",
    "recognize the actual markup of the unavailable market labels");
Assert(MarketRatioConfirmation.UnavailableText("Market Ratio\r\nRetrieving Info...") == null &&
    MarketRatioConfirmation.UnavailableText("Market Ratio\r\n10 : 1") == null,
    "neither loading nor an exact ratio proves unavailable data");
Assert(ExchangeOrders.TryParseSlots("3/10", out int usedSlots, out int capacitySlots) && usedSlots == 3 && capacitySlots == 10,
    "normal and shifted slot counters use the same exact occupied/capacity format");
Assert(!ExchangeOrders.TryParseSlots("Current stock will not fill your order", out _, out _) &&
    !ExchangeOrders.TryParseSlots("11/10", out _, out _) && !ExchangeOrders.TryParseSlots("0/0", out _, out _),
    "an order notice or impossible count cannot be mistaken for a slot counter");

var stack = new StockStack(1, "product", "Exalted", 15479);
var logs = new List<string>();
var stock = StockIndex.Build([stack, stack], [new("Exalted", "15.4K", null), new("Exalted", "", null), new("Chaos", null, null)], logs.Add);
Assert(stock.ByName["Exalted"].Available == 15479, "preserve exact stock and deduplicate");
Assert(SaleQueue.Build(stock, [sale.Want], [new("product", 400, false)], _ => { })[0].Quantity == 15079, "preserve reserve");
Assert(SaleQueue.Build(stock, [sale.Want], [new("Exalted", 0, true)], _ => { }).Count == 0, "exclude legacy inventory by name");
Assert(SaleQueue.Build(stock, [sale.Want], [new("product", 0, true)], _ => { }).Count == 0, "exclude by metadata irrespective of display name");
stock = StockIndex.Build([stack], [new("Exalted", "150", 150)], logs.Add);
Assert(stock.ByName.Count == 0 && logs.Count == 1, "discrepancy must exclude the item");
stock = StockIndex.Build([stack, new(2, "another-tier", "Exalted", 1)], [new("Exalted", "15.4K", null)], logs.Add);
Assert(stock.ByName.Count == 0, "ambiguous variant");

var defaults = new ExchangeTraderSettings().ToPolicy();
Assert(defaults is { Settlement: SettlementMode.Auto, MinimumStackValue: 5m, ThresholdCurrency: SettlementCurrency.Exalted },
    "new profiles protect both settlement currencies and require five Exalted per lot");
var policy = new ExchangeTraderSettings { Settlement = SettlementMode.Exalted, UseMinimumStackValue = false }.ToPolicy();
var game = new TestGame();
SessionStatus? end = null;
await new TradeSession(game, result => end = result, _ => {}).Run(policy, default);
Assert(end is { Phase: SessionPhase.FINISHED, Placed: 1, Collected: 1, Deposited: 1, TabsRead: 2 }, "complete cycle");
Assert(game.ReadTabs.SequenceEqual(new long[] { 1, 2 }), "one pass over stash");
Assert(game.Submissions == 1 && game.Claims == 1 && game.PlacedOrder?.Want == 24, "one submission and claim with correct price");
await new TradeSession(game, result => end = result, _ => {}).Run(policy, default);
Assert(game.ReadTabs.SequenceEqual(new long[] { 1, 2, 1, 2 }) && game.Submissions == 2, "new session scans and sells again");

using var cancelWaitAtStart = new CancellationTokenSource();
game = new TestGame { Existing = new(3, null, 0, 0, false, 0), OnPoll = _ => cancelWaitAtStart.Cancel() };
await new TradeSession(game, result => end = result, _ => {}).Run(policy, cancelWaitAtStart.Token);
Assert(end is { Phase: SessionPhase.STOPPED, Remaining: 1 } && game.OrderPolls == 1 && game.MarketReads == 0 &&
    game.Submissions == 0 && game.Claims == 0, "a queued lot waits at capacity without touching the market and Stop leaves it unsubmitted");

game = new TestGame { HideConfirm = true };
await new TradeSession(game, result => end = result, _ => {}).Run(policy, default);
Assert(end?.Phase == SessionPhase.BLOCKED && game.Submissions == 1, "missing acknowledgement never resubmits");

game = new TestGame { ClaimChunkSize = 10 };
await new TradeSession(game, result => end = result, _ => {}).Run(policy, default);
Assert(end is { Phase: SessionPhase.FINISHED, Collected: 1, Deposited: 1 } && game.Claims == 3,
    "multi-stack reward requires confirmation before each transfer");

game = new TestGame { ClaimChunkSize = 0 };
await new TradeSession(game, result => end = result, _ => {}).Run(policy, default);
Assert(end?.Phase == SessionPhase.BLOCKED && game.Claims == 1, "missing collection acknowledgement never repeats the click");

using var stopped = new CancellationTokenSource();
stopped.Cancel();
game = new TestGame();
await new TradeSession(game, result => end = result, _ => {}).Run(policy, stopped.Token);
Assert(end?.Phase == SessionPhase.STOPPED && game.ReadTabs.Count == 0, "Stop prevents input");

var controls = new SaleControls();
Assert(controls.State(false) == (false, false, false) && !controls.TryStart(false), "Stash closed hides Start and prevents initiation");
Assert(controls.State(true) == (true, false, false) && controls.TryStart(true), "opening Stash makes Start available");
Assert(controls.State(true) == (true, true, true) && !controls.TryStart(true), "active highlight and Stop, duplicate Start refused");
Assert(controls.State(false) == (false, false, true) && controls.State(true) == (true, true, true), "Stop survives closing and highlight survives reopening");
foreach (var (terminal, testGame, token) in new (SessionPhase, TestGame, CancellationToken)[]
{
    (SessionPhase.STOPPED, new(), stopped.Token),
    (SessionPhase.FINISHED, new(), default),
    (SessionPhase.ERROR, new() { ThrowOnValidate = true }, default)
})
{
    try { await new TradeSession(testGame, result => end = result, _ => { }).Run(policy, token); }
    finally { controls.Complete(); }
    Assert(end?.Phase == terminal && controls.State(true) == (true, false, false) && controls.State(false) == (false, false, false),
        $"highlight and Stop clear on {terminal}");
    Assert(controls.TryStart(true), $"can start fresh after {terminal}");
}
controls.Complete();

var exalted = new TradeProduct("exalted", "Exalted Orb");
var divine = new TradeProduct("divine", "Divine Orb");
var product = new TradeProduct("product", "Chaos Orb");
var realChaos = new StockStack(30, "Metadata/Items/Currency/CurrencyRerollRare", "Chaos Orb", 10);
var stockWithoutDivine = StockIndex.Build([realChaos], [new("Chaos Orb", "10", 10), new("Divine Orb", null, null)], _ => { });
var fallbackDivine = Settlements.Product(SettlementCurrency.Divine, stockWithoutDivine);
Assert(fallbackDivine.Id == "Metadata/Items/Currency/CurrencyModValues" &&
    SaleQueue.Build(stockWithoutDivine, [fallbackDivine], [], _ => { }).Single().Product.Id == realChaos.Metadata,
    "missing Divine balance uses the live-verified metadata without reserving Chaos as the payout currency");
var payoutPair = new Pair(product, exalted);
var payoutBook = book with { SalePair = payoutPair };
var minFive = policy with { MinimumStackValue = 5 };
var low = new PricedLot(new(payoutPair, 100, 2), payoutBook);
var high = new PricedLot(new(payoutPair, 40, 12), payoutBook);
Reject(() => StackPricing.CheckMinimum(low, minFive, null, now), "100 items paying 2 Exalted fails minimum 5");
Assert(StackPricing.CheckMinimum(high, minFive, null, now) == high, "40 items paying 12 Exalted passes despite per-unit price");
Assert(StackPricing.CheckMinimum(low, policy, null, now) == low, "disabling the threshold admits a lower total");
var secondLow = low with { Sale = low.Sale with { Have = 20, Want = 3 } };
Reject(() => StackPricing.CheckMinimum(secondLow, minFive, null, now), "split lot is evaluated individually");
var atMinimum = low with { Sale = low.Sale with { Want = 5 } };
Assert(StackPricing.CheckMinimum(atMinimum, minFive, null, now) == atMinimum, "final amount exactly at minimum is eligible");
Reject(() => StackPricing.CheckMinimum(atMinimum, minFive with { MinimumStackValue = 5.000001m }, null, now), "threshold compares decimal exactly");

var cross = new Pair(divine, exalted);
var crossBook = new MarketQuote(cross, new(new(exalted, divine), new(1, 120), 1, now), new(cross, new(100, 1), 2, now));
var rates = SettlementRates.FromBook(crossBook, cross, now);
Assert(rates.DivineToExalted == new Ratio(100, 1) && rates.ExaltedToDivine == new Ratio(1, 120), "conservative executable conversion both ways");
Reject(() => SettlementRates.FromBook(crossBook with { Buyer = crossBook.Buyer with { Ratio = new(121, 1) } }, cross, now), "reject crossed conversion");
Reject(() => SettlementRates.FromBook(crossBook, cross, now.AddMinutes(1)), "reject stale conversion");
var divineLot = new PricedLot(new(new(product, divine), 40, 1), payoutBook);
Assert(StackPricing.CheckMinimum(divineLot, minFive with { MinimumStackValue = 100 }, rates, now).ValuationExpires == rates.Expires,
    "one Divine meets exactly 100 Exalted");
Reject(() => StackPricing.CheckMinimum(divineLot, minFive with { MinimumStackValue = 100.01m }, rates, now), "use verified Divine-to-Exalted bid");
var minDivine = minFive with { ThresholdCurrency = SettlementCurrency.Divine, MinimumStackValue = 0.1m };
Assert(StackPricing.CheckMinimum(high, minDivine, rates, now).ValuationExpires == rates.Expires, "reverse conversion meets exact fractional Divine threshold");
Reject(() => StackPricing.CheckMinimum(high, minDivine with { MinimumStackValue = 0.100001m }, rates, now), "use verified Exalted-to-Divine bid");
Reject(() => StackPricing.CheckMinimum(high, minDivine, null, now), "missing conversion skips the sale");
Assert(StackPricing.Choose([high, divineLot], rates, now).Sale.Pair.Want == divine, "Auto picks higher effective Divine payout");
Assert(StackPricing.Choose([high with { Sale = high.Sale with { Want = 101 } }, divineLot], rates, now).Sale.Pair.Want == exalted,
    "Auto picks higher effective Exalted payout");
Reject(() => StackPricing.Choose([high, divineLot], null, now), "Auto does not estimate the conversion");
var oldMarket = high.Market with { Seller = high.Market.Seller with { ReceivedAt = now - PricingEngine.MaximumQuoteAge - TimeSpan.FromSeconds(1) } };
Reject(() => StackPricing.Choose([high with { Market = oldMarket }, divineLot], rates, now),
    "Auto rejects a stale Exalted candidate even if the fresh Divine candidate would win");
var futureMarket = divineLot.Market with { Buyer = divineLot.Market.Buyer with { ReceivedAt = now.AddSeconds(1) } };
Reject(() => StackPricing.Choose([high, divineLot with { Market = futureMarket }], rates, now),
    "Auto rejects future-dated quote evidence");

logs.Clear();
game = new TestGame { ProductQuantity = 10 };
await new TradeSession(game, result => end = result, logs.Add).Run(policy with { MinimumStackValue = 25 }, default);
Assert(end is { Phase: SessionPhase.FINISHED, Placed: 0, Skipped: 1 } && game.Submissions == 0 &&
    logs.Any(line => line.Contains("лот стоит 24 Exalted Orb")), "minimum uses final price after discount and rounding");

logs.Clear();
game = new TestGame { ProtectedQuantity = 3 };
var excludeB = policy with { ExcludedTabs = new HashSet<string> { "b" } };
await new TradeSession(game, result => end = result, logs.Add).Run(excludeB, default);
Assert(end is { Phase: SessionPhase.FINISHED, Placed: 0, Skipped: 1 } && game.Submissions == 0 &&
    logs.Any(line => line.Contains("во вкладках «B»")), "mixed stock skips even when allowed stock exists");
var protectedIndex = StockIndex.Build([new(1, "product", "Chaos Orb", 10), new(2, "product", "Chaos Orb", 3)],
    [new("Chaos Orb", "13", 13)], _ => { }, new Dictionary<long, string> { [2] = "B" }, true);
Assert(protectedIndex.ByName["Chaos Orb"] is { Available: 10, Protection: not null }, "StockIndex tallies only allowed quantity");
var pooled = StockIndex.Build([new(1, product.Id, product.Name, 10), new(2, product.Id, product.Name, 3)],
    [new(product.Name, "13", 13)], _ => { });
Assert(SaleQueue.Build(pooled, [exalted], [], _ => { }).Single().Quantity == 13,
    "two allowed tabs with the same metadata produce one lot with the total quantity");

game = new TestGame { ProtectedQuantity = 3, ProductQuantity = 0 };
await new TradeSession(game, result => end = result, _ => { }).Run(excludeB, default);
Assert(end is { Phase: SessionPhase.FINISHED, Placed: 0, Skipped: 1 } && game.Submissions == 0, "stock entirely in excluded tab never submitted");
game = new TestGame();
await new TradeSession(game, result => end = result, _ => { }).Run(excludeB, default);
Assert(end is { Phase: SessionPhase.FINISHED, Placed: 1 } && game.Submissions == 1, "fully verified empty protected tab leaves allowed stock saleable");
game = new TestGame { IncompleteProtected = true };
await new TradeSession(game, result => end = result, _ => { }).Run(excludeB, default);
Assert(end?.Phase == SessionPhase.BLOCKED && game.Submissions == 0, "incomplete protected tab blocks the session");
game = new TestGame();
await new TradeSession(game, result => end = result, _ => { }).Run(policy with { ExcludedTabs = new HashSet<string> { "renamed" } }, default);
Assert(end?.Phase == SessionPhase.BLOCKED && game.ReadTabs.Count == 0, "unrecognized protected tab blocks the session");
string oldColorId = StashTabIdentity.Create("B", 0xff222233);
game = new TestGame { TabLayout = [new(1, "B", StashTabIdentity.Create("B", 0xff223344)), new(2, "A", "a")] };
await new TradeSession(game, result => end = result, _ => { }).Run(policy with { ExcludedTabs = new HashSet<string> { oldColorId } }, default);
Assert(end?.Phase == SessionPhase.BLOCKED && game.ReadTabs.Count == 0, "changed protected tab color stops before scanning any stock");
game = new TestGame { TabLayout = [new(2, "B", "b"), new(1, "A", "a")], ProtectedQuantity = 3 };
await new TradeSession(game, result => end = result, _ => { }).Run(excludeB, default);
Assert(end is { Phase: SessionPhase.FINISHED, Placed: 0 } && game.ReadTabs.SequenceEqual(new long[] { 2, 1 }) &&
    game.Submissions == 0, "reordering the same identified tab preserves its exclusion");
game = new TestGame { AmbiguousProtected = true };
await new TradeSession(game, result => end = result, _ => { }).Run(excludeB, default);
Assert(end?.Phase == SessionPhase.BLOCKED && game.ReadTabs.Count == 0, "ambiguous excluded tab blocks the session");
game = new TestGame { AbbreviatedPicker = true };
await new TradeSession(game, result => end = result, _ => { }).Run(excludeB, default);
Assert(end is { Placed: 0, Skipped: 1 } && game.Submissions == 0, "protected scan requires exact exchange balance");

game = new TestGame { Existing = new(3, null, 0, 0, true, 10) };
await new TradeSession(game, result => end = result, _ => { }).Run(policy with { Rules = [new("product", 0, true)] }, default);
Assert(end is { Placed: 0, Skipped: 1, Collected: 0, Deposited: 0 } && game.Submissions == 0 && game.Claims == 0,
    "excluded item never submitted and unknown historical proceeds never moved");
game = new TestGame();
await new TradeSession(game, result => end = result, _ => { }).Run(policy with { Settlement = SettlementMode.Auto }, default);
Assert(end is { Placed: 1, Collected: 1 } && game.PlacedOrder?.Pair.Want.Name == "Divine Orb",
    "Auto runs the sale and proceeds in the selected currency");
game = new TestGame { ExaltedBetter = true };
await new TradeSession(game, result => end = result, _ => { }).Run(policy with { Settlement = SettlementMode.Auto }, default);
Assert(end is { Placed: 1, Collected: 1 } && game.PlacedOrder?.Pair.Want.Name == "Exalted Orb" && game.LastQuotedPair == game.PlacedOrder.Pair,
    "Auto refreshes the winning pair if another pair was read last");
game = new TestGame { MissingCrossRate = true };
await new TradeSession(game, result => end = result, _ => { }).Run(policy with { Settlement = SettlementMode.Auto }, default);
Assert(end is { Placed: 0, Skipped: 1 } && game.Submissions == 0, "Auto skips when the required conversion is unverified");
game = new TestGame { MissingCrossRate = true };
game.CustomSides[("Divine Orb", "Chaos Orb")] = default;
await new TradeSession(game, result => end = result, _ => { }).Run(policy with { Settlement = SettlementMode.Auto }, default);
Assert(end is { Placed: 1 } && game.PlacedOrder?.Pair.Want.Name == "Exalted Orb",
    "Auto can place the single verified lot when the rejected currency makes conversion unnecessary");
game = new TestGame { MissingCrossRate = true };
game.CustomSides[("Divine Orb", "Chaos Orb")] = default;
await new TradeSession(game, result => end = result, _ => { }).Run(policy with
{
    Settlement = SettlementMode.Auto, MinimumStackValue = 1, ThresholdCurrency = SettlementCurrency.Divine
}, default);
Assert(end is { Placed: 0, Skipped: 1 } && game.Submissions == 0,
    "a single verified lot still requires a conversion when the threshold uses a different currency");
Assert(StashTabIdentity.Create("duplicate", 0xff00ff00) != StashTabIdentity.Create("duplicate", 0xff00ff01),
    "same names on different colors get distinct IDs independently of positions");

var greaterChaos = new TradeProduct("greater-chaos", "Greater Chaos Orb");
var vaal = new TradeProduct("vaal", "Vaal Orb");
game = new TestGame
{
    Capacity = 2,
    ExtraStock = [new(3, vaal.Id, vaal.Name, 4), new(4, greaterChaos.Id, greaterChaos.Name, 10)],
    UnquotedProducts = [vaal.Name]
};
var phases = new List<SessionStatus>();
logs.Clear();
await new TradeSession(game, result => { end = result; phases.Add(result); }, logs.Add).Run(policy, default);
Assert(end is { Phase: SessionPhase.FINISHED, Placed: 2, Skipped: 1, Collected: 2, Deposited: 1 } &&
    game.Placed.Select(order => order.Pair.Have).SequenceEqual([product, greaterChaos]),
    "one bad market quote does not stop other lots, and neither order is submitted twice");
Assert(game.ReadTabs.SequenceEqual(new long[] { 1, 2 }) && phases.Where(status => status.Phase != SessionPhase.SCANNING)
    .All(status => status.TabsRead == 2), "multi-product session scans each tab once");
Assert(game.LastDeposit.Values.Single() == 48, "deposit only the sum of confirmed claims from different orders");

game = new TestGame { RejectedDraft = product, ExtraStock = [new(4, greaterChaos.Id, greaterChaos.Name, 10)] };
logs.Clear();
await new TradeSession(game, result => end = result, logs.Add).Run(policy, default);
Assert(end is { Phase: SessionPhase.FINISHED, Placed: 1, Skipped: 1 } && game.Submissions == 1 &&
    game.PlacedOrder?.Pair.Have == greaterChaos && logs.Any(line => line.Contains("I Have не приняло количество")),
    "a rejected quantity prevents submission for that product and the next lot can still sell");

foreach (var interrupted in new[] { new TestGame { CloseBeforeBoard = true }, new TestGame { CloseWhileQuoting = true } })
{
    logs.Clear();
    await new TradeSession(interrupted, result => end = result, logs.Add).Run(policy, default);
    Assert(end is { Phase: SessionPhase.FINISHED, Placed: 1, Remaining: 0 } && interrupted.Submissions == 1 &&
        interrupted.ExchangeOpens == 2 && interrupted.ReadTabs.SequenceEqual(new long[] { 1, 2 }) &&
        logs.Any(line => line.Contains("Биржа открыта заново")),
        "a closed exchange before placement is reopened once and the lot is processed without scanning the stash again");
}

game = new TestGame { CloseAfterSubmission = true };
await new TradeSession(game, result => end = result, _ => { }).Run(policy, default);
Assert(end is { Phase: SessionPhase.BLOCKED, Placed: 0, Remaining: 1 } && game.Submissions == 1 && game.ExchangeOpens == 1 &&
    end.Detail.Contains("Результат ордера не подтверждён"),
    "a closed exchange after submission does not cause another placement or discard the unresolved product");

game = new TestGame
{
    Capacity = 1,
    Existing = new(50, new(product, new("existing-divine", "Divine Orb")), 1, 1, true, 1),
    ExtraStock = [new(4, greaterChaos.Id, greaterChaos.Name, 10)]
};
await new TradeSession(game, result => end = result, _ => { }).Run(policy, default);
Assert(end is { Phase: SessionPhase.FINISHED, Placed: 2, Collected: 3 } && game.AllDeposits.Count == 2 &&
    game.AllDeposits["existing-divine"] == 1 && game.DepositedQuantity == 49,
    "full slots release completed orders and both currencies reach deposit");

using var cancelWaitForSecond = new CancellationTokenSource();
game = new TestGame { ExtraStock = [new(4, greaterChaos.Id, greaterChaos.Name, 10)], KeepPending = true,
    OnPoll = _ => cancelWaitForSecond.Cancel() };
await new TradeSession(game, result => end = result, _ => { }).Run(policy, cancelWaitForSecond.Token);
Assert(end is { Phase: SessionPhase.STOPPED, Placed: 1, Remaining: 1 } && game.Submissions == 1 && game.Claims == 0,
    "an unfilled test order at capacity leaves the next item in the queue until stopped");

game = new TestGame();
await new TradeSession(game, result => end = result, _ => { }).Run(policy with
{
    Settlement = SettlementMode.Divine, MinimumStackValue = 1, ThresholdCurrency = SettlementCurrency.Divine
}, default);
Assert(end is { Phase: SessionPhase.FINISHED, Placed: 1 } && game.PlacedOrder is { Have: 10, Want: 24 } &&
    game.PlacedOrder.Pair.Want.Name == "Divine Orb", "fixed Divine uses its own bid and ask, without conversion");

var largeProducts = Enumerable.Range(0, 24).Select(index => new TradeProduct($"audit-{index}", $"Test Stock {index}")).ToArray();
var largeStock = largeProducts.Select((entry, index) => new StockStack(500 + index, entry.Id, entry.Name, 10)).ToArray();
var protectedProduct = largeProducts[4];
var actualExalted = Settlements.Product(SettlementCurrency.Exalted, stockWithoutDivine);
game = new TestGame
{
    ProductQuantity = 0, Capacity = 10, ExtraStock = largeStock,
    ProtectedStock = [new(800, protectedProduct.Id, protectedProduct.Name, 5)],
    UnquotedProducts = [largeProducts[0].Name],
    ExtraOrders = Enumerable.Range(200, 8).Select(id => new OwnOrder(id, null, 1, 1, false, 0))
        .Concat([new(301, new(product, actualExalted), 1, 3, true, 3), new(302, new(product, fallbackDivine), 1, 4, true, 4)]).ToList()
};
foreach (var (item, index) in largeProducts.Select((item, index) => (item, index)))
{
    game.CustomSides[(item.Name, "Divine Orb")] = index == 2 ? new(12, 5) : new(22, 10);
    game.CustomSides[("Divine Orb", item.Name)] = new(2, 5);
    game.CustomSides[(item.Name, "Exalted Orb")] = index switch
    {
        2 => new(12, 5), 3 => new(22, 10), _ when index % 2 == 0 => new(290, 1), _ => new(35, 10)
    };
    game.CustomSides[("Exalted Orb", item.Name)] = index switch
    {
        2 or 3 => new(2, 5), _ when index % 2 == 0 => new(1, 300), _ => new(1, 4)
    };
}
logs.Clear();
await new TradeSession(game, result => end = result, logs.Add).Run(policy with
{
    Settlement = SettlementMode.Auto, ExcludedTabs = new HashSet<string> { "b" }, MinimumStackValue = 30,
    Rules = [new(largeProducts[1].Id, 0, true)]
}, default);
Assert(end is { Phase: SessionPhase.FINISHED, Queued: 22, Remaining: 0, Placed: 20, Collected: 22 },
    "24 distinct products: 22 queued, two quote/minimum failures skipped, 20 placed, both full slots freed repeatedly");
Assert(game.ReadTabs.SequenceEqual(new long[] { 1, 2 }) && game.Placed.Select(order => order.Pair.Have.Id).Distinct().Count() == 20,
    "24-product pass scans once and never duplicates a submitted product");
Assert(game.Placed.Any(order => order.Pair.Want.Name == "Divine Orb") && game.Placed.Any(order => order.Pair.Want.Name == "Exalted Orb") &&
    logs.Any(line => line.Contains("Auto") && line.Contains("Выбрано Divine")) &&
    logs.Any(line => line.Contains("Auto") && line.Contains("Выбрано Exalted")), "one queue uses both payouts and reports why");
Assert(game.AllDeposits.Count == 2 && game.AllDeposits[actualExalted.Id] > 3 && game.AllDeposits[fallbackDivine.Id] > 4,
    "two payout currencies survive repeated slot recovery and deposit");
Assert(game.ExtraOrders.Count == 8 && game.ExtraOrders.All(order => !order.Complete),
    "slot recovery never claims active orders");

game = new TestGame
{
    ProductQuantity = 0, Capacity = 10, ExtraStock = largeStock[..12], KeepPending = true,
    OnPoll = adapter => adapter.CompleteSubmitted(adapter.OrderPolls < 3 ? 1 : 10)
};
foreach (var item in largeProducts.Take(12))
{
    game.CustomSides[(item.Name, "Exalted Orb")] = new(22, 10);
    game.CustomSides[("Exalted Orb", item.Name)] = new(2, 5);
}
phases.Clear();
await new TradeSession(game, result => { end = result; phases.Add(result); }, _ => { }).Run(policy, default);
var board = await game.ReadOrders(default);
Assert(game.SubmitOccupancy.SequenceEqual(Enumerable.Range(0, 10).Concat([9, 9])) &&
    game.Placed.Select(order => order.Pair.Have.Id).Distinct().Count() == 12,
    "capacity is never exceeded and each freed slot is reused for exactly one different lot");
Assert(phases.Where(status => status.Phase == SessionPhase.WAITING_FOR_SLOT).Select(status => status.Remaining).SequenceEqual([2, 1]) &&
    phases.Any(status => status is { Phase: SessionPhase.WAITING_FOR_BUYER, Remaining: 0, Placed: 12 }) &&
    game.ReadTabs.SequenceEqual(new long[] { 1, 2 }), "full board preserves the queue without a second stash scan and waits for the final buyers");
Assert(board.Used == 0 && end is { Phase: SessionPhase.FINISHED, Queued: 12, Placed: 12, Remaining: 0, Collected: 12 } &&
    game.DepositedQuantity == 288 && game.OrderPolls == 3, "the run finishes after all twelve sales and deposits confirmed proceeds");

using var stopAtCapacity = new CancellationTokenSource();
game = new TestGame
{
    Capacity = 10, OnPoll = _ => stopAtCapacity.Cancel(),
    ExtraOrders = Enumerable.Range(100, 10).Select(id => new OwnOrder(id, null, 10, 24, false, id == 100 ? 1 : 0)).ToList()
};
await new TradeSession(game, result => end = result, _ => { }).Run(policy, stopAtCapacity.Token);
Assert(end is { Phase: SessionPhase.STOPPED, Remaining: 1 } && game.OrderPolls == 1 && game.Claims == 0 && game.Submissions == 0 &&
    (await game.ReadOrders(default)).Orders[0] is { Complete: false, Unclaimed: 1 },
    "an active partial fill neither frees a slot nor causes a claim, and Stop cancels the wait");

using var stopWithNoQueue = new CancellationTokenSource();
game = new TestGame { KeepPending = true, OnPoll = _ => stopWithNoQueue.Cancel() };
await new TradeSession(game, result => end = result, _ => { }).Run(policy, stopWithNoQueue.Token);
Assert(end is { Phase: SessionPhase.STOPPED, Placed: 1, Remaining: 0 } && game.OrderPolls == 1 && game.Claims == 0 &&
    (await game.ReadOrders(default)).Used == 1, "Stop during the final buyer wait keeps the listed order intact");

game = new TestGame { Existing = new(50, null, 1, 10, true, 10) };
await new TradeSession(game, result => end = result, _ => { }).Run(policy with { Rules = [new("protected", 0, true)] }, default);
Assert(end is { Phase: SessionPhase.BLOCKED, Placed: 0, Remaining: 1 } && game.OrderPolls == 0 && game.Claims == 0,
    "a full board with unidentifiable payouts and no buyers left reports the manual action required");

using var stopAfterRequeue = new CancellationTokenSource();
game = new TestGame
{
    Capacity = 2, KeepPending = true, ExtraStock = largeStock[..1], ClaimChunkSize = 4, BackpackStackCapacity = 2,
    Existing = new(50, new Pair(new("protected-order", "Protected order"), actualExalted), 10, 24, false, 0, RemainingGoods: 10),
    OnPoll = adapter =>
    {
        switch (adapter.OrderPolls)
        {
            case 1:
                adapter.Now = adapter.Now.AddMinutes(9);
                adapter.PartiallyFillSubmitted(9, 2);
                break;
            case 2: adapter.Now = adapter.Now.AddMinutes(2); break;
            case 3: adapter.Now = adapter.Now.AddMinutes(8); break;
            case 4: adapter.CompleteSubmitted(1); break;
            default: stopAfterRequeue.Cancel(); break;
        }
    }
};
game.CustomSides[(largeProducts[0].Name, "Exalted Orb")] = new(22, 10);
game.CustomSides[("Exalted Orb", largeProducts[0].Name)] = new(2, 5);
logs.Clear();
await new TradeSession(game, result => end = result, logs.Add).Run(policy with
{
    MinimumStackValue = 5m, Rules = [new("protected-order", 0, true)]
}, stopAfterRequeue.Token);
Assert(end is { Phase: SessionPhase.STOPPED, Placed: 3, Remaining: 0 } && game.Cancels == 1 && game.OrderPolls == 5 &&
    game.Placed.Select(order => (order.Pair.Have.Id, order.Have)).SequenceEqual([(product.Id, 10), (largeProducts[0].Id, 10), (product.Id, 9)]) &&
    game.Existing is { Active: true, RemainingGoods: 10 } && game.ReadTabs.SequenceEqual(new long[] { 1, 2 }),
    "partial fill restarts the ten minute timer, then its remainder follows the pending item and the excluded order is preserved");
Assert(game.DepositedQuantity == 35 && logs.Any(line => line.Contains("возвращено 9")),
    "a cancelled partial order returns all nine items and its two earned orbs in verified batches before repricing");

var scrolls = new TradeProduct("scrolls", "Scroll of Wisdom");
game = new TestGame
{
    ProductQuantity = 0, Existing = new(71, null, 214, 51, false, 10, RemainingGoods: 174),
    ExistingReturn = scrolls, ExistingPayout = actualExalted,
    OnPoll = adapter =>
    {
        Assert(adapter.Cancels == 0 && adapter.MarketReads == 0, "an older order remains listed before ten observed minutes");
        adapter.Now = adapter.Now.AddMinutes(adapter.OrderPolls == 1 ? 9 : 1);
    }
};
game.CustomSides[("Exalted Orb", scrolls.Name)] = new(4, 1);
game.CustomSides[(scrolls.Name, "Exalted Orb")] = new(1, 5);
await new TradeSession(game, result => end = result, _ => { }).Run(policy with
{
    MinimumStackValue = 5m, Rules = [new(scrolls.Id, 4, false)]
}, default);
Assert(end is { Phase: SessionPhase.FINISHED, Placed: 1, Collected: 2 } && game.Cancels == 1 && game.OrderPolls == 2 &&
    game.PlacedOrder is { Have: 170, Want: 42 } && game.PlacedOrder.Pair.Have == scrolls &&
    game.AllDeposits[scrolls.Id] == 174 && game.AllDeposits[actualExalted.Id] == 52,
    "an order already listed before Start is cancelled at ten minutes and only its verified, unreserved return is relisted");

game = new TestGame
{
    ProductQuantity = 0, Existing = new(71, null, 174, 43, false, 0, Listed: false, RemainingGoods: 174),
    ExistingReturn = scrolls,
};
game.CustomSides[("Exalted Orb", scrolls.Name)] = new(4, 1);
game.CustomSides[(scrolls.Name, "Exalted Orb")] = new(1, 5);
await new TradeSession(game, result => end = result, _ => { }).Run(policy with { MinimumStackValue = 5m }, default);
Assert(end is { Phase: SessionPhase.FINISHED, Placed: 1, Collected: 2 } && game.Cancels == 0 &&
    game.PlacedOrder?.Have == 174 && game.AllDeposits[scrolls.Id] == 174,
    "a cancellation already acknowledged by the game is recovered once from the live board on a new Start");

game = new TestGame
{
    Existing = new(50, new Pair(product, actualExalted), 1, 10, false, 0),
    OnPoll = adapter =>
    {
        Assert(adapter.MarketReads == 0, "a full board has not opened or priced the next trading pair");
        adapter.Existing = adapter.Existing! with { Complete = true, Unclaimed = 10 };
        adapter.CustomSides[("Exalted Orb", "Chaos Orb")] = new(100, 41);
        adapter.CustomSides[("Chaos Orb", "Exalted Orb")] = new(1, 10);
    }
};
logs.Clear();
await new TradeSession(game, result => end = result, logs.Add).Run(policy with { MinimumStackValue = 5m }, default);
Assert(end is { Phase: SessionPhase.FINISHED, Placed: 0, Skipped: 1, Collected: 1 } && game.OrderPolls == 1 &&
    logs.Any(line => line.Contains("лот стоит 4 Exalted Orb, ниже порога 5")),
    "the first quote is fetched after a slot opens and rejects a lot now worth four Exalted");

game = new TestGame { ProductQuantity = 0, BackpackStackSize = 20, BackpackStackCapacity = 4,
    Existing = new(50, new Pair(product, actualExalted), 1, 1260, true, 1260) };
await new TradeSession(game, result => end = result, _ => { }).Run(policy, default);
Assert(end is { Phase: SessionPhase.FINISHED, Collected: 1 } && game.Claims == 16 &&
    game.DepositedQuantity == 1260 && game.DepositCalls == 16,
    "1260 orbs are claimed and deposited in sixteen batches of up to four stacks");

game = new TestGame { ProductQuantity = 0, BackpackStackSize = 20, BackpackStackCapacity = 40,
    Existing = new(50, new Pair(product, actualExalted), 1, 540, true, 540) };
await new TradeSession(game, result => end = result, _ => { }).Run(policy, default);
Assert(end is { Phase: SessionPhase.FINISHED, Collected: 1 } && game.Claims == 1 && game.DepositedQuantity == 540,
    "twenty-seven stacks of Exalted are collected by one batch claim when the backpack has room");

var carriedExalted = new TradeProduct(Settlements.ExaltedMetadata, "Exalted Orb");
game = new TestGame { ProductQuantity = 0, BackpackStackSize = 20, BackpackStackCapacity = 4,
    Existing = new(50, new Pair(product, carriedExalted), 1, 24, true, 24) };
game.SeedBackpack(carriedExalted, 80, 4);
await new TradeSession(game, result => end = result, _ => { }).Run(policy, default);
Assert(end is { Phase: SessionPhase.FINISHED, Collected: 1 } && game.DepositCalls == 2 && game.Claims == 1 &&
    game.AllDeposits[carriedExalted.Id] == 104,
    "existing Exalted can be deposited as one batch to make room for an outstanding payout");

game = new TestGame { OverrideReceived = divine };
await new TradeSession(game, result => end = result, _ => { }).Run(policy, default);
Assert(end is { Phase: SessionPhase.BLOCKED, Placed: 1, Deposited: 0 } &&
    game.Claims == 1 && end.Detail.Contains("Тип полученной выручки"),
    "a conflicting currency receipt is never counted or sent to the stash");

using var stopInQueue = new CancellationTokenSource();
game = new TestGame { ExtraStock = largeStock };
await new TradeSession(game, result =>
{
    end = result;
    if (result.Phase == SessionPhase.SELLING) stopInQueue.Cancel();
}, _ => { }).Run(policy, stopInQueue.Token);
Assert(end is { Phase: SessionPhase.STOPPED, Remaining: 25 } && game.Submissions == 0 && game.Claims == 0,
    "stop in the pricing stage leaves the queue untouched and sends no further input");

var settingsPath = new FileInfo(Path.Combine(Path.GetTempPath(), $"exchange-trader-test-{Guid.NewGuid():N}.json"));
try
{
    var settings = new ExchangeTraderSettings
    {
        Settlement = SettlementMode.Auto, UndercutPercent = 1.5f, UseMinimumStackValue = true,
        MinimumStackSaleValue = 0.25m, ThresholdCurrency = SettlementCurrency.Divine,
        ExcludedItems = new() { ["product"] = "Chaos Orb" }, ExcludedStashTabs = new() { ["b"] = "B" }
    };
    JsonHelper.SafeToFile(settings, settingsPath);
    var reloaded = JsonHelper.CreateOrLoadJsonFile<ExchangeTraderSettings>(settingsPath);
    reloaded.Normalize();
    var persisted = reloaded.ToPolicy();
    Assert(persisted.Settlement == SettlementMode.Auto && persisted.MinimumStackValue == 0.25m &&
        persisted.ThresholdCurrency == SettlementCurrency.Divine && persisted.DiscountBasisPoints == 150 &&
        persisted.ExcludedTabs.Contains("b") && persisted.Excludes(product), "settings round trip via GameHelper profile");
    reloaded.UseMinimumStackValue = false;
    JsonHelper.SafeToFile(reloaded, settingsPath);
    reloaded = JsonHelper.CreateOrLoadJsonFile<ExchangeTraderSettings>(settingsPath);
    Assert(reloaded.MinimumStackSaleValue == 0.25m && reloaded.ToPolicy().MinimumStackValue == 0,
        "disabled threshold persists independently of last configured value");
    reloaded.ExcludedItems.Remove(product.Id);
    JsonHelper.SafeToFile(reloaded, settingsPath);
    reloaded = JsonHelper.CreateOrLoadJsonFile<ExchangeTraderSettings>(settingsPath);
    Assert(SaleQueue.Build(StockIndex.Build([new(1, product.Id, "Renamed Chaos", 2)], [new("Renamed Chaos", "2", 2)], _ => { }),
        [exalted], reloaded.ToPolicy().Rules, _ => { }).Single().Product.Id == product.Id,
        "removing a metadata exclusion restores the renamed product after restart");
    var legacy = new ExchangeTraderSettings { QuoteName = "Chaos Orb", Exclusions = "Old Name" };
    legacy.Normalize();
    Assert(legacy.ToPolicy().Settlement == SettlementMode.Exalted && legacy.ToPolicy().Excludes(new("old", "Old Name")),
        "legacy Chaos defaults migrate to Exalted while preserving exclusions");
}
finally { settingsPath.Delete(); }

Console.WriteLine($"ExchangeTrader: all {checks} checks passed.");

void Assert(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    checks++;
}

void Reject(Action action, string description)
{
    try { action(); }
    catch (MarketUnavailableException) { checks++; return; }
    throw new Exception(description);
}

sealed class TestGame : IExchangeGame
{
    private readonly Dictionary<TradeProduct, long> received = [];
    private readonly List<OwnOrder> submitted = [];
    private long generation;
    private long nextOrder;
    internal bool HideConfirm;
    internal bool ThrowOnValidate, ExaltedBetter, IncompleteProtected, AbbreviatedPicker, AmbiguousProtected, MissingCrossRate, KeepPending;
    internal int ProductQuantity = 10, ProtectedQuantity;
    internal OwnOrder? Existing;
    internal int Submissions, Claims, Cancels;
    internal int OrderPolls, DepositCalls, MarketReads;
    internal int ExchangeOpens;
    internal bool CloseBeforeBoard, CloseWhileQuoting, CloseAfterSubmission;
    private bool exchangeLost;
    internal long DepositedQuantity;
    internal Action<TestGame>? OnPoll;
    internal List<int> SubmitOccupancy = [];
    internal int ClaimChunkSize = int.MaxValue, Capacity = 1;
    internal int BackpackStackSize = int.MaxValue;
    internal int BackpackStackCapacity = int.MaxValue;
    private int receivedStacks;
    internal SaleOrder? PlacedOrder;
    internal Pair? LastQuotedPair;
    internal List<long> ReadTabs = [];
    internal StashTab[]? TabLayout;
    internal StockStack[] ExtraStock = [];
    internal StockStack[] ProtectedStock = [];
    internal HashSet<string> UnquotedProducts = [];
    internal TradeProduct? RejectedDraft;
    internal List<SaleOrder> Placed = [];
    internal List<OwnOrder> ExtraOrders = [];
    internal Dictionary<(string Have, string Want), Ratio> CustomSides = [];
    internal TradeProduct? OverrideReceived, ExistingReturn, ExistingPayout;
    internal IReadOnlyDictionary<string, long> LastDeposit = new Dictionary<string, long>();
    internal Dictionary<string, long> AllDeposits = [];

    internal void SeedBackpack(TradeProduct product, long quantity, int stacks)
    {
        received.Add(product, quantity);
        receivedStacks = stacks;
    }

    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public void ValidateSession(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ThrowOnValidate) throw new InvalidOperationException("simulated error");
    }
    public Task OpenStash(CancellationToken token) => Task.CompletedTask;
    public Task CloseStash(CancellationToken token) => Task.CompletedTask;
    public Task OpenExchange(CancellationToken token)
    {
        ExchangeOpens++;
        exchangeLost = false;
        return Task.CompletedTask;
    }
    public Task CloseExchange(CancellationToken token) => Task.CompletedTask;
    public Task<IReadOnlyList<StashTab>> ReadStashTabs(CancellationToken token) =>
        Result(TabLayout ?? (AmbiguousProtected ? [new(1, "B", "b"), new(2, "B", "b")] : [new(1, "A", "a"), new(2, "B", "b")]));
    public Task<TabStock> ReadTab(StashTab tab, CancellationToken token)
    {
        ReadTabs.Add(tab.Key);
        int quantity = tab.Key == 1 ? ProductQuantity : ProtectedQuantity;
        var stacks = new List<StockStack>();
        if (quantity > 0) stacks.Add(new(tab.Key, "product", "Chaos Orb", quantity));
        if (tab.Key == 1) stacks.AddRange(ExtraStock);
        else stacks.AddRange(ProtectedStock);
        return Task.FromResult(new TabStock(tab, stacks, tab.Key != 2 || !IncompleteProtected));
    }

    public Task<IReadOnlyList<StockStack>> ReadBackpack(CancellationToken token)
        => Result(received.Select((entry, index) => new StockStack(index + 10, entry.Key.Id, entry.Key.Name, entry.Value)).ToArray());
    public Task<IReadOnlyList<PickerItem>> ReadPicker(CancellationToken token)
    {
        var count = ProductQuantity + ProtectedQuantity;
        var rows = new List<PickerItem>
        {
            AbbreviatedPicker ? new("Chaos Orb", "0.01K", null) : new("Chaos Orb", count.ToString(), count),
            new("Exalted Orb", null, null), new("Divine Orb", null, null)
        };
        rows.AddRange(ExtraStock.Concat(ProtectedStock).GroupBy(stack => stack.Name).Select(group =>
            new PickerItem(group.Key, group.Sum(stack => stack.Quantity).ToString(), group.Sum(stack => stack.Quantity))));
        if (ExistingReturn is { } returned) rows.Add(new(returned.Name, null, null));
        return Result(rows.ToArray());
    }
    public Task<SideQuote> ReadSide(Pair pair, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        MarketReads++;
        if (CloseWhileQuoting)
        {
            CloseWhileQuoting = false;
            exchangeLost = true;
            throw new ExchangeClosedException();
        }
        LastQuotedPair = pair;
        if (UnquotedProducts.Contains(pair.Have.Name) || UnquotedProducts.Contains(pair.Want.Name))
            throw new MarketUnavailableException("simulated stale book");
        var names = (pair.Have.Name, pair.Want.Name);
        if (CustomSides.TryGetValue(names, out var configured))
            return Task.FromResult(new SideQuote(pair, configured, ++generation, Now));
        var ratio = names switch
        {
            ("Exalted Orb", "Divine Orb") when MissingCrossRate => default,
            ("Exalted Orb", "Divine Orb") => new Ratio(1, 120),
            ("Divine Orb", "Exalted Orb") => new Ratio(100, 1),
            ("Divine Orb", "Chaos Orb") => new Ratio(2, 5),
            ("Chaos Orb", "Divine Orb") => new Ratio(22, 10),
            ("Exalted Orb", "Chaos Orb") when ExaltedBetter => new Ratio(1, 300),
            ("Chaos Orb", "Exalted Orb") when ExaltedBetter => new Ratio(290, 1),
            ("Exalted Orb", "Chaos Orb") => new Ratio(2, 5),
            ("Exalted Orb", "Greater Chaos Orb") => new Ratio(2, 5),
            _ => new Ratio(22, 10)
        };
        return Task.FromResult(new SideQuote(pair, ratio, ++generation, Now));
    }
    public Task<ExchangeOrders> ReadOrders(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (CloseBeforeBoard)
        {
            exchangeLost = true;
            CloseBeforeBoard = false;
        }
        if (exchangeLost) throw new ExchangeClosedException();
        IReadOnlyList<OwnOrder> orders = Existing == null ? [.. ExtraOrders, .. submitted] : [Existing, .. ExtraOrders, .. submitted];
        return Task.FromResult(new ExchangeOrders(orders.Count, Capacity, generation, orders));
    }

    public Task WaitForOrdersPoll(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        OrderPolls++;
        (OnPoll ?? throw new InvalidOperationException("Unexpected wait in fixture."))(this);
        token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public bool HasBackpackSpace(CancellationToken token) => receivedStacks < BackpackStackCapacity;
    public Task<Draft> Prepare(SaleOrder order, CancellationToken token)
    {
        if (order.Pair.Have == RejectedDraft) throw new MarketUnavailableException("I Have не приняло количество.");
        return Task.FromResult(new Draft(order, generation, 100));
    }
    public Task SubmitOnce(Draft draft, CancellationToken token)
    {
        SubmitOccupancy.Add(ExtraOrders.Count + submitted.Count + (Existing == null ? 0 : 1));
        Submissions++;
        var order = PlacedOrder = draft.Order;
        Placed.Add(order);
        if (!HideConfirm)
            submitted.Add(new(++nextOrder, order.Pair, order.Have, order.Want, !KeepPending, KeepPending ? 0 : order.Want,
                RemainingGoods: KeepPending ? order.Have : 0));
        if (CloseAfterSubmission) exchangeLost = true;
        return Task.CompletedTask;
    }

    internal void CompleteSubmitted(int count)
    {
        for (int index = 0; index < submitted.Count && count > 0; index++)
        {
            var order = submitted[index];
            if (order.Complete) continue;
            submitted[index] = order with { Complete = true, Unclaimed = order.Requested, RemainingGoods = 0 };
            count--;
        }
    }

    internal void PartiallyFillSubmitted(int remaining, int unclaimed)
    {
        int index = submitted.FindIndex(order => order.Active);
        submitted[index] = submitted[index] with { RemainingGoods = remaining, Unclaimed = unclaimed };
    }

    public Task<bool> CancelOnce(OwnOrder order, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!order.Active) throw new InvalidOperationException("Cancelled an inactive order.");
        var closed = order with { Listed = false };
        if (Existing == order) Existing = closed;
        else submitted[submitted.IndexOf(order)] = closed;
        Cancels++;
        return Task.FromResult(true);
    }

    public Task ClaimBatch(OwnOrder order, bool returnGoods, CancellationToken token)
    {
        Claims++;
        long freeCapacity = (long)(BackpackStackCapacity - receivedStacks) * BackpackStackSize;
        long chunk = Math.Min(returnGoods ? order.RemainingGoods : order.Unclaimed, Math.Min(ClaimChunkSize, freeCapacity));
        if (chunk == 0) return Task.CompletedTask;
        receivedStacks += (int)((chunk - 1) / BackpackStackSize + 1);
        var remaining = returnGoods ? order with { RemainingGoods = order.RemainingGoods - chunk }
            : order with { Unclaimed = order.Unclaimed - chunk };
        if (Existing?.Id == order.Id)
            Existing = !remaining.HasReceipt ? null : remaining;
        else
        {
            var source = ExtraOrders.Any(entry => entry.Id == order.Id) ? ExtraOrders : submitted;
            int index = source.FindIndex(entry => entry.Id == order.Id);
            if (!remaining.HasReceipt) source.RemoveAt(index);
            else source[index] = remaining;
        }
        var currency = OverrideReceived ?? (returnGoods ? order.Pair?.Have ?? ExistingReturn : order.Pair?.Want ?? ExistingPayout) ??
            throw new InvalidOperationException("test receipt is missing an item");
        received[currency] = received.GetValueOrDefault(currency) + chunk;
        return Task.CompletedTask;
    }

    public Task<DepositResult> Deposit(IReadOnlyDictionary<string, long> proceeds, CancellationToken token)
    {
        if (proceeds.Count != received.Count || received.Any(entry => proceeds.GetValueOrDefault(entry.Key.Id) != entry.Value))
            throw new Exception("Depositing a different quantity from the receipts.");
        LastDeposit = proceeds.ToDictionary();
        foreach (var entry in proceeds) AllDeposits[entry.Key] = AllDeposits.GetValueOrDefault(entry.Key) + entry.Value;
        DepositedQuantity += proceeds.Values.Sum();
        DepositCalls++;
        received.Clear();
        receivedStacks = 0;
        return Task.FromResult(new DepositResult(proceeds.Count, []));
    }

    private static Task<IReadOnlyList<T>> Result<T>(params T[] items) => Task.FromResult<IReadOnlyList<T>>(items);
}
