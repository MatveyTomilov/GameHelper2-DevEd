using System.Numerics;

namespace ExchangeTrader;

internal static class Settlements
{
    internal const string ExaltedMetadata = "Metadata/Items/Currency/CurrencyAddModToRare";

    internal static string Name(SettlementCurrency currency) => currency == SettlementCurrency.Divine ? "Divine Orb" : "Exalted Orb";

    internal static TradeProduct Product(SettlementCurrency currency, StockIndex stock)
    {
        string name = Name(currency);
        if (stock.ByName.TryGetValue(name, out var entry)) return entry.Product;
        string id = currency == SettlementCurrency.Divine ? "Metadata/Items/Currency/CurrencyModValues" : ExaltedMetadata;
        return new(id, name);
    }

    internal static SettlementCurrency CurrencyOf(TradeProduct product) =>
        product.Name == Name(SettlementCurrency.Divine) ? SettlementCurrency.Divine : SettlementCurrency.Exalted;
}

internal sealed record SettlementRates(Ratio DivineToExalted, Ratio ExaltedToDivine, DateTimeOffset Expires)
{
    internal static SettlementRates FromBook(MarketQuote book, Pair expected, DateTimeOffset now)
    {
        if (book.SalePair != expected || book.Buyer.Pair != expected || book.Seller.Pair != new Pair(expected.Want, expected.Have) ||
            book.Buyer.Generation <= book.Seller.Generation || book.Buyer.ReceivedAt < book.Seller.ReceivedAt ||
            book.Buyer.ReceivedAt > now || now - book.Seller.ReceivedAt > PricingEngine.MaximumQuoteAge ||
            !book.Buyer.Ratio.IsValid || !book.Seller.Ratio.IsValid ||
            (Int128)book.Buyer.Ratio.Want * book.Seller.Ratio.Want > (Int128)book.Buyer.Ratio.Have * book.Seller.Ratio.Have)
            throw new MarketUnavailableException("Не подтверждён свежий курс Divine ↔ Exalted.");
        return new(book.Buyer.Ratio, book.Seller.Ratio, book.Seller.ReceivedAt + PricingEngine.MaximumQuoteAge);
    }

    internal Ratio ConvertFrom(SettlementCurrency currency, DateTimeOffset now)
    {
        if (now > Expires) throw new MarketUnavailableException("Курс Divine ↔ Exalted устарел.");
        return currency == SettlementCurrency.Divine ? DivineToExalted : ExaltedToDivine;
    }
}

internal sealed record PricedLot(SaleOrder Sale, MarketQuote Market, DateTimeOffset? ValuationExpires = null);

internal static class StackPricing
{
    internal static bool NeedsConversion(TradeProduct quote, TradingPolicy policy) =>
        policy.MinimumStackValue > 0 && Settlements.CurrencyOf(quote) != policy.ThresholdCurrency;

    internal static PricedLot CheckMinimum(PricedLot lot, TradingPolicy policy, SettlementRates? rates, DateTimeOffset now)
    {
        if (policy.MinimumStackValue == 0) return lot;
        bool converted = NeedsConversion(lot.Sale.Pair.Want, policy);
        var rate = converted
            ? (rates ?? throw new MarketUnavailableException("Для проверки минимальной стоимости нет курса Divine ↔ Exalted."))
                .ConvertFrom(Settlements.CurrencyOf(lot.Sale.Pair.Want), now)
            : new Ratio(1, 1);
        var bits = decimal.GetBits(policy.MinimumStackValue);
        BigInteger minimum = (uint)bits[0] + ((BigInteger)(uint)bits[1] << 32) + ((BigInteger)(uint)bits[2] << 64);
        BigInteger scale = BigInteger.Pow(10, (bits[3] >> 16) & 0xff);
        if ((BigInteger)lot.Sale.Want * rate.Want * scale < minimum * rate.Have)
            throw new MarketUnavailableException(
                $"лот стоит {lot.Sale.Want} {lot.Sale.Pair.Want.Name}, ниже порога {policy.MinimumStackValue} {Settlements.Name(policy.ThresholdCurrency)}.");
        return converted ? lot with { ValuationExpires = rates!.Expires } : lot;
    }

    internal static PricedLot Choose(IReadOnlyList<PricedLot> lots, SettlementRates? rates, DateTimeOffset now)
    {
        if (lots.Count == 1) return lots[0];
        if (lots.Any(lot => now - lot.Market.Seller.ReceivedAt > PricingEngine.MaximumQuoteAge || lot.Market.Buyer.ReceivedAt > now))
            throw new MarketUnavailableException("Для сравнения Exalted и Divine нужна свежая цена в обеих валютах.");
        var exalted = lots.Single(lot => Settlements.CurrencyOf(lot.Sale.Pair.Want) == SettlementCurrency.Exalted);
        var divine = lots.Single(lot => Settlements.CurrencyOf(lot.Sale.Pair.Want) == SettlementCurrency.Divine);
        var verified = rates ?? throw new MarketUnavailableException("Для выбора валюты нет подтверждённого курса Divine ↔ Exalted.");
        var rate = verified.ConvertFrom(SettlementCurrency.Divine, now);
        var best = (Int128)divine.Sale.Want * rate.Want > (Int128)exalted.Sale.Want * rate.Have ? divine : exalted;
        return best with { ValuationExpires = verified.Expires };
    }
}

internal sealed class SettlementPricing(IExchangeGame game, TradeProduct exalted, TradeProduct divine, Action<string> log)
{
    private readonly MarketReader reader = new(game);
    private SettlementRates? rates;
    private (DateTimeOffset RetryAt, string Reason)? unavailableRate;

    internal async Task<PricedLot> Price(SaleItem item, IReadOnlyList<TradeProduct> quotes, TradingPolicy policy, CancellationToken token)
    {
        if (quotes.Count == 0) throw new MarketUnavailableException("Все валюты получения исключены или отсутствуют в каталоге.");
        if (quotes.Count > 1 || quotes.Any(quote => StackPricing.NeedsConversion(quote, policy)))
        {
            if ((rates == null || game.Now > rates.Expires) &&
                (unavailableRate == null || game.Now >= unavailableRate.Value.RetryAt))
            {
                try
                {
                    var pair = new Pair(divine, exalted);
                    rates = SettlementRates.FromBook(await reader.Read(pair, token), pair, game.Now);
                    unavailableRate = null;
                    var toExalted = rates.DivineToExalted;
                    var toDivine = rates.ExaltedToDivine;
                    log($"Курс в бирже · Divine → Exalted {toExalted.Want}:{toExalted.Have} · Exalted → Divine {toDivine.Want}:{toDivine.Have}.");
                }
                catch (MarketUnavailableException ex)
                {
                    rates = null;
                    unavailableRate = (game.Now + PricingEngine.MaximumQuoteAge, ex.Message);
                }
            }
            if (rates == null && quotes.Count == 1 && unavailableRate is { } unavailable)
                throw new MarketUnavailableException($"Курс Divine ↔ Exalted не подтверждён. {unavailable.Reason}");
        }

        var lots = new List<PricedLot>();
        var skipped = new List<string>();
        PricedLot? current = null;
        foreach (var quote in quotes)
        {
            current = null;
            try
            {
                current = await ReadLot(item, quote, policy, token);
                lots.Add(StackPricing.CheckMinimum(current, policy, rates, game.Now));
            }
            catch (MarketUnavailableException ex)
            {
                skipped.Add($"{quote.Name}: {ex.Message}");
            }
        }
        if (lots.Count == 0) throw new MarketUnavailableException(string.Join(" / ", skipped));
        var best = StackPricing.Choose(lots, rates, game.Now);
        if (current?.Market != best.Market)
        {
            // Re-select the winning pair and price that exact draft from its new book.
            var live = StackPricing.CheckMinimum(await ReadLot(item, best.Sale.Pair.Want, policy, token), policy, rates, game.Now);
            lots[lots.FindIndex(lot => lot.Sale.Pair == live.Sale.Pair)] = live;
            best = StackPricing.Choose(lots, rates, game.Now);
            if (best.Market != live.Market)
                throw new MarketUnavailableException("Лучшая валюта получения изменилась при повторной проверке пары.");
        }
        if (policy.Settlement == SettlementMode.Auto)
        {
            if (lots.Count == 2)
            {
                var ex = lots.Single(lot => lot.Sale.Pair.Want == exalted);
                var div = lots.Single(lot => lot.Sale.Pair.Want == divine);
                var rate = rates!.DivineToExalted;
                log($"Auto · {item.Product.Name}, {item.Quantity}: {ex.Sale.Want} Exalted или {div.Sale.Want} Divine " +
                    $"при курсе {rate.Want}:{rate.Have}. Выбрано {best.Sale.Pair.Want.Name}.");
            }
            else
            {
                string reason = skipped.Count == 0 ? "вторая валюта недоступна в каталоге или исключена" : string.Join(" / ", skipped);
                log($"Auto · {item.Product.Name}: подтверждён только лот в {best.Sale.Pair.Want.Name}. {reason.TrimEnd('.')}.");
            }
        }
        return best;
    }


    private async Task<PricedLot> ReadLot(SaleItem item, TradeProduct quote, TradingPolicy policy, CancellationToken token)
    {
        var pair = new Pair(item.Product, quote);
        var book = await reader.Read(pair, token);
        return new(PricingEngine.Calculate(pair, book, item.Quantity, policy.DiscountBasisPoints, game.Now), book);
    }
}
