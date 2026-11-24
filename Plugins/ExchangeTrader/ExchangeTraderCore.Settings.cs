using System.Numerics;
using GameHelper;
using GameHelper.RemoteEnums;
using ImGuiNET;

namespace ExchangeTrader;

public sealed partial class ExchangeTraderCore
{
    private readonly Dictionary<string, TradeProduct> discovered = new(StringComparer.Ordinal);
    private StashTab[] listedTabs = [];
    private string itemSearch = string.Empty;
    private long nextSettingsRead;

    private static IntPtr VisibleStash() => Core.Process.Pid != 0 &&
        Core.States.GameCurrentState == GameStateTypes.InGameState
        ? ExchangeUi.StashPanel() : IntPtr.Zero;

    private void Remember(IEnumerable<TradeProduct> products)
    {
        foreach (var product in products)
            if (!string.IsNullOrWhiteSpace(product.Id))
                discovered[product.Id] = product;
    }

    private void DrawConfiguration(IntPtr stash, bool showButtons = true)
    {
        bool general = ImGui.CollapsingHeader("Общие##General", ImGuiTreeNodeFlags.DefaultOpen);
        bool changed = false;
        if (general && showButtons) DrawButtons(stash != IntPtr.Zero);
        ImGui.BeginDisabled(IsRunning);
        if (general)
        {
            int settlement = (int)Settings.Settlement;
            if (ImGui.Combo("Валюта получения", ref settlement, "Exalted Orb\0Divine Orb\0Auto (Exalted / Divine)\0"))
            {
                Settings.Settlement = (SettlementMode)settlement;
                changed = true;
            }
            ImGui.DragFloat("Демпинг, %", ref Settings.UndercutPercent, 0.05f, 0, 50, "%.2f");
            changed |= ImGui.IsItemDeactivatedAfterEdit();
            changed |= ImGui.Checkbox("Учитывать минимальную стоимость стака", ref Settings.UseMinimumStackValue);
            if (Settings.UseMinimumStackValue)
            {
                double amount = (double)Settings.MinimumStackSaleValue;
                if (ImGui.InputDouble("Минимум за лот", ref amount, 0, 0, "%.6f") && double.IsFinite(amount) && amount >= 0 && amount <= 1e20)
                    Settings.MinimumStackSaleValue = (decimal)amount;
                changed |= ImGui.IsItemDeactivatedAfterEdit();
                int threshold = (int)Settings.ThresholdCurrency;
                if (ImGui.Combo("Валюта порога", ref threshold, "Exalted Orb\0Divine Orb\0"))
                {
                    Settings.ThresholdCurrency = (SettlementCurrency)threshold;
                    changed = true;
                }
            }
            if (ImGui.TreeNode("Резервы"))
            {
                ImGui.InputTextMultiline("##reserves", ref Settings.Reserves, 24000, new Vector2(-1, 58));
                changed |= ImGui.IsItemDeactivatedAfterEdit();
                ImGui.TextWrapped("Имя или metadata = количество, одна запись на строку.");
                ImGui.TreePop();
            }
        }

        bool showItems = ImGui.CollapsingHeader("Исключённые предметы##ExcludedItems");
        if (showItems) changed |= DrawItemExclusions(stash);
        if (ImGui.CollapsingHeader($"Исключённые вкладки ({Settings.ExcludedStashTabs.Count})###ExcludedStashTabs"))
            changed |= DrawTabExclusions(stash);
        ImGui.EndDisabled();
        if (changed) SaveSettings();
    }

    private void RefreshStashSettings(IntPtr stash, bool readItems)
    {
        if (stash == IntPtr.Zero || IsRunning || Environment.TickCount64 < nextSettingsRead) return;
        nextSettingsRead = Environment.TickCount64 + 350;
        if (GameUiAdapter.TryReadTabList(stash) is { } tabs) listedTabs = tabs;
        if (readItems) Remember(GameUiAdapter.ReadVisibleStashProducts(stash));
    }

    private bool DrawItemExclusions(IntPtr stash)
    {
        RefreshStashSettings(stash, readItems: true);
        bool changed = false;
        ImGui.InputTextWithHint("##item-filter", "Название или metadata", ref itemSearch, 160);
        if (discovered.Count == 0)
            ImGui.TextWrapped("Откройте нужную вкладку сундука, чтобы увидеть предметы.");
        else if (ImGui.BeginCombo("Добавить предмет", "Выбрать из сундука"))
        {
            foreach (var product in discovered.Values.OrderBy(item => item.Name))
            {
                if (Settings.ExcludedItems.ContainsKey(product.Id) ||
                    !product.Name.Contains(itemSearch, StringComparison.OrdinalIgnoreCase) &&
                    !product.Id.Contains(itemSearch, StringComparison.OrdinalIgnoreCase)) continue;
                if (ImGui.Selectable($"{product.Name}##{product.Id}"))
                {
                    Settings.ExcludedItems[product.Id] = product.Name;
                    changed = true;
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(product.Id);
            }
            ImGui.EndCombo();
        }
        foreach (var excluded in Settings.ExcludedItems.OrderBy(entry => entry.Value).ToArray())
        {
            if (RemoveRow(excluded.Value, excluded.Key))
            {
                Settings.ExcludedItems.Remove(excluded.Key);
                changed = true;
            }
        }
        foreach (string legacy in Settings.LegacyExclusions.Distinct())
        {
            if (RemoveRow($"{legacy} · прежняя запись", $"legacy:{legacy}"))
            {
                Settings.RemoveLegacyExclusion(legacy);
                changed = true;
            }
        }
        if (Settings.ExcludedItems.Count == 0 && Settings.LegacyExclusions.Length == 0)
            ImGui.TextDisabled("Список пуст");
        return changed;
    }

    private bool DrawTabExclusions(IntPtr stash)
    {
        RefreshStashSettings(stash, readItems: false);
        if (listedTabs.Length == 0)
            ImGui.TextWrapped(stash == IntPtr.Zero ? "Откройте сундук для чтения вкладок." : "Откройте список вкладок в интерфейсе сундука.");
        bool changed = false;
        foreach (var group in listedTabs.GroupBy(tab => tab.Id))
        {
            var tab = group.First();
            bool excluded = Settings.ExcludedStashTabs.ContainsKey(tab.Id);
            if (group.Count() > 1 && !excluded)
            {
                ImGui.TextDisabled($"{tab.Name} · совпадают имя и цвет");
                continue;
            }
            ImGui.PushID(tab.Id);
            uint rgb = tab.Color;
            var color = new Vector4((rgb >> 16 & 0xff) / 255f, (rgb >> 8 & 0xff) / 255f, (rgb & 0xff) / 255f, 1f);
            ImGui.ColorButton("##tab-color", color, ImGuiColorEditFlags.NoTooltip, new Vector2(12, 12));
            ImGui.SameLine();
            bool toggled = ImGui.Checkbox(tab.Name, ref excluded);
            ImGui.PopID();
            if (!toggled) continue;
            if (excluded) Settings.ExcludedStashTabs[tab.Id] = tab.Name;
            else Settings.ExcludedStashTabs.Remove(tab.Id);
            changed = true;
        }
        if (listedTabs.GroupBy(tab => tab.Id).Any(group => group.Count() > 1))
            ImGui.TextWrapped("Для выбора вкладки с совпадающими именем и цветом измените её имя или цвет.");
        if (stash != IntPtr.Zero || listedTabs.Length > 0)
            foreach (var entry in Settings.ExcludedStashTabs.Where(e => listedTabs.All(tab => tab.Id != e.Key)).ToArray())
            {
                if (RemoveRow($"{entry.Value} · не опознана", $"missing:{entry.Key}"))
                {
                    Settings.ExcludedStashTabs.Remove(entry.Key);
                    changed = true;
                }
            }
        else
            foreach (var entry in Settings.ExcludedStashTabs)
                ImGui.TextUnformatted(entry.Value);
        return changed;
    }

    private static bool RemoveRow(string label, string id)
    {
        ImGui.PushID(id);
        bool removed = ImGui.SmallButton("Удалить");
        ImGui.SameLine();
        ImGui.TextWrapped(label);
        ImGui.PopID();
        return removed;
    }
}
