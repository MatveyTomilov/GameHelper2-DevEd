using System.Numerics;
using System.Text.RegularExpressions;

namespace ExchangeTrader;

internal sealed class MarketReader(IExchangeGame game)
{
    internal async Task<MarketQuote> Read(Pair sale, CancellationToken token)
    {
        var inverse = new Pair(sale.Want, sale.Have);
        var ask = await game.ReadSide(inverse, token);
        var bid = await game.ReadSide(sale, token);
        if (ask.Pair != inverse || bid.Pair != sale || bid.Generation <= ask.Generation ||
            !ask.Ratio.IsValid || !bid.Ratio.IsValid)
            throw new MarketUnavailableException("Биржа не подтвердила две стороны выбранной пары.");
        return new(sale, ask, bid);
    }
}

internal enum MarketRatioState { Pending, Exact, Unavailable }

internal struct MarketRatioConfirmation
{
    private bool loadingSeen;
    private Ratio lastExact;
    private string? lastUnavailable;
    private long unavailableSince;

    internal void ObserveLoading()
    {
        loadingSeen = true;
        lastExact = default;
        lastUnavailable = null;
    }

    internal MarketRatioState Observe(Ratio exact, string? unavailable, long milliseconds)
    {
        if (!loadingSeen) return MarketRatioState.Pending;
        bool confirmed = exact.IsValid && exact == lastExact;
        lastExact = exact;

        if (unavailable != lastUnavailable)
        {
            lastUnavailable = unavailable;
            unavailableSince = milliseconds;
        }
        if (confirmed) return MarketRatioState.Exact;
        return unavailable != null && milliseconds - unavailableSince >= 200
            ? MarketRatioState.Unavailable : MarketRatioState.Pending;
    }

    internal static string? UnavailableText(string raw)
    {
        string value = Regex.Replace(raw[(raw.LastIndexOf('\n') + 1)..], @"<[^>]*>|[{}]", "").Trim();
        if (value.Equals("No Stock", StringComparison.OrdinalIgnoreCase) || value is "Нет предложений" or "Нет в наличии")
            return "No Stock";
        const string number = @"[0-9]+(?:[.,][0-9]{1,2})?";
        return (value.Contains('.') || value.Contains(',')) && Regex.IsMatch(value, $@"^{number}\s*:\s*{number}$")
            ? value : null;
    }
}

internal static class PricingEngine
{
    internal static readonly TimeSpan MaximumQuoteAge = TimeSpan.FromSeconds(15);

    internal static bool IsPassive(SaleOrder order, Ratio bid) => bid.IsValid &&
        (Int128)order.Want * bid.Have > (Int128)order.Have * bid.Want;

    internal static SaleOrder Calculate(Pair sale, MarketQuote book, int quantity, int discountBasisPoints, DateTimeOffset now)
    {
        if (quantity <= 0 || discountBasisPoints is < 0 or > 5000)
            throw new MarketUnavailableException("Некорректное количество или процент демпинга.");
        if (book.SalePair != sale || book.Seller.Pair != new Pair(sale.Want, sale.Have) || book.Buyer.Pair != sale ||
            book.Buyer.Generation <= book.Seller.Generation || book.Buyer.ReceivedAt < book.Seller.ReceivedAt)
            throw new MarketUnavailableException("Обратная и прямая котировки относятся к разным парам или поколениям.");
        if (book.Buyer.ReceivedAt > now || now - book.Seller.ReceivedAt > MaximumQuoteAge)
            throw new MarketUnavailableException("Котировка устарела.");

        var ask = book.Seller.Ratio.Invert();
        var bid = book.Buyer.Ratio;
        if (!ask.IsValid || !bid.IsValid) throw new MarketUnavailableException("Некорректное соотношение биржи.");

        BigInteger numerator = (BigInteger)quantity * ask.Want;
        BigInteger denominator = (BigInteger)ask.Have * 10000;
        BigInteger strictlyLower = (numerator - 1) / ask.Have;
        BigInteger target = numerator * (10000 - discountBasisPoints) / denominator;
        BigInteger desired = BigInteger.Min(strictlyLower, target);
        int maximumEffectiveDiscount = Math.Min(9500, Math.Max(500, discountBasisPoints + 100));
        BigInteger discountFloor = Ceiling(numerator * (10000 - maximumEffectiveDiscount), denominator);
        BigInteger passiveFloor = (BigInteger)quantity * bid.Want / bid.Have + 1;
        BigInteger lower = BigInteger.Max(1, BigInteger.Max(discountFloor, passiveFloor));
        if (desired < lower || desired > int.MaxValue)
            throw new MarketUnavailableException("Для этого лота нет целой цены, которая ниже ask и выше bid без лишнего дисконта.");
        return new(sale, quantity, (int)desired);
    }

    private static BigInteger Ceiling(BigInteger numerator, BigInteger denominator)
        => (numerator + denominator - 1) / denominator;
}
