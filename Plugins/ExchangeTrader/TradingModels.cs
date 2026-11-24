using System.Globalization;

namespace ExchangeTrader;

internal enum SessionPhase { IDLE, SCANNING, SELLING, WAITING_FOR_SLOT, WAITING_FOR_BUYER, COLLECTING, DEPOSITING, FINISHED, BLOCKED, ERROR, STOPPED }

internal sealed record SessionStatus(SessionPhase Phase, string Detail, int TabsRead = 0, int TabsTotal = 0,
    int Products = 0, int Placed = 0, int Skipped = 0, int Collected = 0, int Deposited = 0,
    int Queued = 0, int Remaining = 0);

internal sealed record TradeProduct(string Id, string Name);
internal sealed record StashTab(long Key, string Name, string Id, uint Color = 0);
internal sealed record TabStock(StashTab Tab, IReadOnlyList<StockStack> Stacks, bool Complete);
internal sealed record StockStack(long Identity, string Metadata, string Name, long Quantity);
internal sealed record PickerItem(string Name, string? DisplayQuantity, long? ExactQuantity);
internal sealed record CurrencyRule(string Key, long Reserve, bool Excluded);
public enum SettlementMode { Exalted, Divine, Auto }
public enum SettlementCurrency { Exalted, Divine }
internal sealed record TradingPolicy(SettlementMode Settlement, int DiscountBasisPoints, IReadOnlyList<CurrencyRule> Rules,
    IReadOnlySet<string> ExcludedTabs, decimal MinimumStackValue = 0, SettlementCurrency ThresholdCurrency = SettlementCurrency.Exalted)
{
    internal bool Excludes(TradeProduct product) => Rules.Any(rule => rule.Excluded && (rule.Key == product.Id || rule.Key == product.Name));
}
internal sealed record SaleItem(TradeProduct Product, int Quantity);
internal sealed record Pair(TradeProduct Have, TradeProduct Want);

internal readonly record struct Ratio(long Want, long Have)
{
    internal bool IsValid => Want > 0 && Have > 0;
    internal Ratio Invert() => new(Have, Want);
}

internal sealed record SideQuote(Pair Pair, Ratio Ratio, long Generation, DateTimeOffset ReceivedAt);
internal sealed record MarketQuote(Pair SalePair, SideQuote Seller, SideQuote Buyer);
internal sealed record SaleOrder(Pair Pair, int Have, int Want);
internal sealed record Draft(SaleOrder Order, long Context, int GoldCost, Ratio ConfirmedBid = default);

internal sealed record OwnOrder(long Id, Pair? Pair, int Offered, int Requested, bool Complete, long Unclaimed,
    bool Listed = true, long RemainingGoods = 0)
{
    internal bool Active => Listed && !Complete;
    internal bool HasReceipt => !Active && (Unclaimed > 0 || RemainingGoods > 0);
}
internal sealed record ExchangeOrders(int Used, int Capacity, long Generation, IReadOnlyList<OwnOrder> Orders)
{
    internal bool HasSpace => Used < Capacity;

    internal static bool TryParseSlots(string text, out int used, out int capacity)
    {
        used = capacity = 0;
        var counts = text.Split('/');
        return counts.Length == 2 &&
            int.TryParse(counts[0], NumberStyles.None, CultureInfo.InvariantCulture, out used) &&
            int.TryParse(counts[1], NumberStyles.None, CultureInfo.InvariantCulture, out capacity) &&
            capacity is > 0 and <= 100 && used <= capacity;
    }
}

internal sealed record DepositResult(int Transferred, IReadOnlyList<string> Pending);
internal sealed record CollectionResult(int Orders, IReadOnlyDictionary<string, long> Proceeds, long Returned = 0,
    bool BackpackFull = false, TradeProduct? ReturnedProduct = null);

internal class TradeBlockedException(string message) : Exception(message);
internal sealed class ExchangeClosedException() : TradeBlockedException("Биржа закрылась во время операции.");
internal sealed class MarketUnavailableException(string message) : Exception(message);

internal interface IExchangeGame
{
    DateTimeOffset Now { get; }
    void ValidateSession(CancellationToken token);
    Task OpenStash(CancellationToken token);
    Task CloseStash(CancellationToken token);
    Task<IReadOnlyList<StashTab>> ReadStashTabs(CancellationToken token);
    Task<TabStock> ReadTab(StashTab tab, CancellationToken token);
    Task<IReadOnlyList<StockStack>> ReadBackpack(CancellationToken token);
    Task OpenExchange(CancellationToken token);
    Task CloseExchange(CancellationToken token);
    Task<IReadOnlyList<PickerItem>> ReadPicker(CancellationToken token);
    Task<SideQuote> ReadSide(Pair pair, CancellationToken token);
    Task<ExchangeOrders> ReadOrders(CancellationToken token);
    Task WaitForOrdersPoll(CancellationToken token);
    bool HasBackpackSpace(CancellationToken token);
    Task<Draft> Prepare(SaleOrder order, CancellationToken token);
    Task SubmitOnce(Draft draft, CancellationToken token);
    Task<bool> CancelOnce(OwnOrder order, CancellationToken token);
    Task ClaimBatch(OwnOrder order, bool returnGoods, CancellationToken token);
    Task<DepositResult> Deposit(IReadOnlyDictionary<string, long> proceeds, CancellationToken token);
}
