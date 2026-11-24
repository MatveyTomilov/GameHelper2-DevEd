using System.Globalization;
using GameHelper.Plugin;

namespace ExchangeTrader;

public sealed class ExchangeTraderSettings : IPSettings
{
    public SettlementMode Settlement = SettlementMode.Auto;
    public float UndercutPercent = 1f;
    public bool UseMinimumStackValue = true;
    public decimal MinimumStackSaleValue = 5m;
    public SettlementCurrency ThresholdCurrency = SettlementCurrency.Exalted;
    public Dictionary<string, string> ExcludedItems = new(StringComparer.Ordinal);
    public Dictionary<string, string> ExcludedStashTabs = new(StringComparer.Ordinal);
    public string Reserves = string.Empty;

    // Read old profiles and keep existing exclusions until a unique metadata can be verified.
    public string? QuoteName;
    public string Exclusions = string.Empty;

    internal void Normalize()
    {
        Settlement = QuoteName?.Trim() switch
        {
            "Divine Orb" => SettlementMode.Divine,
            "Exalted Orb" or "Chaos Orb" => SettlementMode.Exalted,
            _ => Enum.IsDefined(Settlement) ? Settlement : SettlementMode.Exalted
        };
        QuoteName = null;
        if (!Enum.IsDefined(ThresholdCurrency)) ThresholdCurrency = SettlementCurrency.Exalted;
        ExcludedItems ??= new(StringComparer.Ordinal);
        ExcludedStashTabs ??= new(StringComparer.Ordinal);
    }

    internal TradingPolicy ToPolicy()
    {
        if (!float.IsFinite(UndercutPercent) || UndercutPercent is < 0 or > 50)
            throw new ArgumentException("Демпинг должен быть от 0 до 50%.");
        if (UseMinimumStackValue && MinimumStackSaleValue <= 0)
            throw new ArgumentException("Укажите положительную минимальную стоимость стака или отключите порог.");

        var rules = ExcludedItems.Keys.Concat(LegacyExclusions).Select(key => new CurrencyRule(key, 0, true)).ToList();
        foreach (string text in Lines(Reserves))
        {
            var parts = text.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 ||
                !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long reserve))
                throw new ArgumentException($"Некорректная строка резерва: {text}");
            rules.Add(new(parts[0], reserve, false));
        }
        return new(Settlement, (int)Math.Round(UndercutPercent * 100), rules,
            ExcludedStashTabs.Keys.ToHashSet(StringComparer.Ordinal), UseMinimumStackValue ? MinimumStackSaleValue : 0, ThresholdCurrency);
    }

    internal string[] LegacyExclusions => Lines(Exclusions);

    internal void RemoveLegacyExclusion(string name) =>
        Exclusions = string.Join(Environment.NewLine, LegacyExclusions.Where(entry => entry != name));

    private static string[] Lines(string? value) => (value ?? string.Empty)
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

internal static class StashTabIdentity
{
    internal static string Create(string name, uint color) => $"{color:X8}:{name}";
}

internal sealed class SaleControls
{
    internal bool Active { get; private set; }
    internal (bool ShowSale, bool Highlight, bool ShowStop) State(bool stashOpen) => (stashOpen, stashOpen && Active, Active);

    internal bool TryStart(bool stashOpen)
    {
        if (!stashOpen || Active) return false;
        Active = true;
        return true;
    }

    internal void Complete() => Active = false;
}
