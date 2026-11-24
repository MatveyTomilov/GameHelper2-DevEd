namespace ExchangeTrader;

internal sealed class StockIndex(IReadOnlyDictionary<string, StockIndex.Stock> byName)
{
    internal sealed record Stock(TradeProduct Product, long Available, string? Protection = null);
    internal IReadOnlyDictionary<string, Stock> ByName { get; } = byName;

    internal static StockIndex Build(IEnumerable<StockStack> stacks, IEnumerable<PickerItem> picker, Action<string> log,
        IReadOnlyDictionary<long, string>? protectedStacks = null, bool verifySources = false)
    {
        var unique = new Dictionary<long, StockStack>();
        foreach (var stack in stacks)
        {
            if (stack.Identity == 0 || stack.Quantity <= 0 || string.IsNullOrWhiteSpace(stack.Metadata))
                throw new TradeBlockedException("Снимок запасов содержит непроверенный стек.");
            if (!unique.TryAdd(stack.Identity, stack) && unique[stack.Identity] != stack)
                throw new TradeBlockedException("Один стек изменился во время обхода вкладок.");
        }

        var named = unique.Values.ToLookup(stack => stack.Name, StringComparer.Ordinal);
        var result = new Dictionary<string, Stock>(StringComparer.Ordinal);
        foreach (var rows in picker.GroupBy(row => row.Name, StringComparer.Ordinal))
        {
            var counts = rows.Where(row => !string.IsNullOrWhiteSpace(row.DisplayQuantity)).Distinct().ToArray();
            if (counts.Length == 0) continue;
            var match = named[rows.Key].ToArray();
            if (counts.Length != 1 || match.Length == 0 || match.Select(stack => stack.Metadata).Distinct().Count() != 1)
            {
                log($"{rows.Key} — не удалось однозначно связать количество и metadata.");
                continue;
            }

            long exact = match.Sum(stack => stack.Quantity);
            var count = counts[0];
            if (verifySources && count.ExactQuantity is null)
            {
                log($"{rows.Key} — при исключённых вкладках требуется точное число в I Have, получено {count.DisplayQuantity}.");
                continue;
            }
            if (count.ExactQuantity != exact && (count.ExactQuantity.HasValue || !MatchesAbbreviation(count.DisplayQuantity!, exact)))
            {
                log($"{rows.Key} — биржа показывает {count.DisplayQuantity}, в контейнерах {exact}.");
                continue;
            }
            var blocked = match.Where(stack => protectedStacks?.ContainsKey(stack.Identity) == true).ToArray();
            var protection = blocked.Length > 0
                ? $"есть запас во вкладках «{string.Join("», «", blocked.Select(stack => protectedStacks![stack.Identity]).Distinct())}». Биржа не позволяет выбрать источник списания."
                : null;
            result.Add(rows.Key, new(new(match[0].Metadata, rows.Key), exact - blocked.Sum(stack => stack.Quantity), protection));
        }
        return new StockIndex(result);
    }

    private static bool MatchesAbbreviation(string displayed, long exact)
    {
        var parts = System.Text.RegularExpressions.Regex.Match(displayed, @"^([0-9]+)(?:[.,]([0-9]{1,3}))?([KkМMmКк])$");
        if (!parts.Success || !long.TryParse(parts.Groups[1].Value, out long whole)) return false;
        long multiplier = "MmМм".Contains(parts.Groups[3].Value) ? 1000000 : 1000;
        var decimals = parts.Groups[2].Value;
        long subdivisions = decimals.Length switch { 1 => 10, 2 => 100, 3 => 1000, _ => 1 };
        long fraction = decimals.Length == 0 ? 0 : int.Parse(decimals, System.Globalization.CultureInfo.InvariantCulture);
        long step = multiplier / subdivisions;
        Int128 lower = (Int128)whole * multiplier + fraction * step;
        return exact >= lower && exact < lower + step;
    }
}

internal sealed class StockScanner(IExchangeGame game)
{
    internal async Task<(StockIndex Index, IReadOnlyList<PickerItem> Catalog)> Scan(
        IReadOnlySet<string> excludedTabs, Action<int, int, string> progress, Action<string> log, CancellationToken token,
        Action<IReadOnlyList<StashTab>>? observeTabs = null, Action<IEnumerable<StockStack>>? observeItems = null)
    {
        await game.OpenStash(token);
        var tabs = await game.ReadStashTabs(token);
        if (tabs.Count == 0 || tabs.Select(tab => tab.Key).Distinct().Count() != tabs.Count)
            throw new TradeBlockedException("Список вкладок неполный или содержит повторяющиеся идентификаторы.");
        observeTabs?.Invoke(tabs);
        foreach (string id in excludedTabs)
            if (tabs.Count(tab => tab.Id == id) != 1)
                throw new TradeBlockedException($"Исключённая вкладка «{id[(id.IndexOf(':') + 1)..]}» не опознана однозначно. Проверьте список исключений.");

        var stacks = new List<StockStack>();
        var protectedStacks = new Dictionary<long, string>();
        progress(0, tabs.Count, "Читаю сундук");
        for (int index = 0; index < tabs.Count; index++)
        {
            var tab = tabs[index];
            var snapshot = await game.ReadTab(tab, token);
            if (snapshot.Tab != tab) throw new TradeBlockedException("Сундук вернул данные другой вкладки.");
            observeItems?.Invoke(snapshot.Stacks);
            if (excludedTabs.Contains(tab.Id))
            {
                if (!snapshot.Complete)
                    throw new TradeBlockedException($"Не подтверждено полное содержимое исключённой вкладки «{tab.Name}». Источник списания защитить нельзя.");
                foreach (var stack in snapshot.Stacks)
                    protectedStacks[stack.Identity] = tab.Name;
            }
            stacks.AddRange(snapshot.Stacks);
            progress(index + 1, tabs.Count, tab.Name);
        }
        stacks.AddRange(await game.ReadBackpack(token));
        await game.CloseStash(token);
        await game.OpenExchange(token);
        var picker = await game.ReadPicker(token);
        if (picker.Count == 0) throw new TradeBlockedException("Каталог биржи пуст или не загрузился.");
        return (StockIndex.Build(stacks, picker, log, protectedStacks, excludedTabs.Count > 0), picker);
    }
}

internal static class SaleQueue
{
    internal static IReadOnlyList<SaleItem> Build(StockIndex stock, IReadOnlyList<TradeProduct> quotes,
        IReadOnlyList<CurrencyRule> rules, Action<string> log)
    {
        var queue = new List<SaleItem>();
        foreach (var item in stock.ByName.Values)
        {
            string? reason = item.Protection;
            var applied = rules.Where(rule => rule.Key == item.Product.Id || rule.Key == item.Product.Name).ToArray();
            if (applied.Any(rule => rule.Excluded)) reason = "предмет в списке исключений.";
            else if (reason == null && quotes.Any(quote => item.Product.Name == quote.Name || item.Product.Id == quote.Id))
                reason = "валюта расчёта.";
            long reserve = applied.Length == 0 ? 0 : applied.Max(rule => rule.Reserve);
            long available = item.Available - reserve;
            if (reason == null && available <= 0) reason = "доступный запас покрывает только резерв.";
            if (reason != null)
                log($"{item.Product.Name} — {reason}");
            else
                queue.Add(new(item.Product, (int)Math.Min(available, int.MaxValue)));
        }
        return queue;
    }
}
