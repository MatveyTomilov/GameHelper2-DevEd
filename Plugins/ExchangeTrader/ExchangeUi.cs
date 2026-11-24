using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using GameHelper;
using GameHelper.Plugin;
using GameOffsets.Natives;
using GameOffsets.Objects.UiElement;

namespace ExchangeTrader;

internal static class ExchangeUi
{
    private const int TextOffset = 0x360;
    internal const int ItemOffset = 0x4e0;

    internal static IntPtr Root => Core.States.InGameStateObject.GameUi.Address;
    internal static bool IsText(string value, params string[] choices) => choices.Contains(value, StringComparer.OrdinalIgnoreCase);

    internal static IntPtr[] Children(IntPtr address)
    {
        if (!TryNode(address, out var node)) return [];
        var vector = node.ChildrensPtr;
        long bytes = vector.Last.ToInt64() - vector.First.ToInt64();
        if (bytes <= 0 || bytes % IntPtr.Size != 0 || bytes / IntPtr.Size > 4096 || vector.End.ToInt64() < vector.Last.ToInt64())
            return [];
        var children = Core.Process.Handle.ReadStdVector<IntPtr>(vector);
        return children.Length == bytes / IntPtr.Size ? children : [];
    }

    internal static IntPtr At(IntPtr address, params int[] indices)
    {
        foreach (int index in indices)
        {
            var children = Children(address);
            if ((uint)index >= (uint)children.Length) return IntPtr.Zero;
            address = children[index];
        }
        return address;
    }

    internal static bool TryNode(IntPtr address, out UiElementBaseOffset node)
        => Core.Process.Handle.TryReadMemory(address, out node) && address != IntPtr.Zero && node.Self == address;

    internal static bool Visible(IntPtr address)
    {
        for (int i = 0; i < 40; i++)
        {
            if (!TryNode(address, out var node) || !UiElementBaseFuncs.IsVisibleChecker(node.Flags)) return false;
            if (node.ParentPtr == IntPtr.Zero) return true;
            address = node.ParentPtr;
        }
        return false;
    }

    internal static string Text(IntPtr address)
    {
        if (address == IntPtr.Zero || !Core.Process.Handle.TryReadMemory<StdWString>(address + TextOffset, out var text) ||
            text.Length is <= 0 or > 512 || text.Capacity < text.Length || text.Capacity > 65536) return string.Empty;
        string value = Core.Process.Handle.ReadStdWString(text);
        return value.Contains('\0') ? string.Empty : value.Trim();
    }

    internal static string Plain(string value) => Regex.Replace(value, @"<<[\w_]+>>|</?[\w_]+>|[{}]", "").Trim();

    internal static long? ReadNumber(string value)
    {
        var text = Plain(value);
        if (!Regex.IsMatch(text, @"^(?:[0-9]+|[0-9]{1,3}(?:[\s,][0-9]{3})+)$")) return null;
        return long.TryParse(string.Concat(text.Where(char.IsAsciiDigit)), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number : null;
    }

    internal static Ratio ReadRatio(string raw)
    {
        string text = Plain(raw).Split('\n').Last().Trim();
        var values = text.Split(':');
        return values.Length == 2 && ReadNumber(values[0]) is > 0 and long want && ReadNumber(values[1]) is > 0 and long have
            ? new(want, have) : default;
    }

    internal static IEnumerable<IntPtr> Walk(IntPtr root, int depth, int limit, bool onlyVisible = true)
    {
        if (root == IntPtr.Zero) yield break;
        var queue = new Queue<(IntPtr Address, int Depth)>();
        var seen = new HashSet<IntPtr>();
        queue.Enqueue((root, 0));
        while (queue.TryDequeue(out var entry))
        {
            if (!seen.Add(entry.Address) || !TryNode(entry.Address, out var node) ||
                (onlyVisible && !UiElementBaseFuncs.IsVisibleChecker(node.Flags))) continue;
            if (seen.Count > limit) throw new TradeBlockedException("Дерево игровой панели изменилось или превысило лимит чтения.");
            yield return entry.Address;
            if (entry.Depth == depth) continue;
            foreach (var child in Children(entry.Address))
                if (child != IntPtr.Zero) queue.Enqueue((child, entry.Depth + 1));
        }
    }

    internal static bool Rect(IntPtr address, out Vector2 position, out Vector2 size)
        => PluginUiElementReflection.TryGetAbsoluteRect(address, out position, out size);

    internal static IntPtr ExchangePanel()
    {
        foreach (var child in Children(Root))
        {
            if (!TryNode(child, out var node) || !UiElementBaseFuncs.IsVisibleChecker(node.Flags) ||
                node.UnscaledSize.X < 800 || node.UnscaledSize.Y < 500) continue;
            if (IsText(Text(At(child, 1, 1)), "Currency Exchange", "Обмен валюты", "Обмен валют") && Visible(child))
                return child;
        }
        return IntPtr.Zero;
    }

    internal static IntPtr StashPanel()
    {
        var address = Core.States.InGameStateObject.GameUi.LeftPanel.Address;
        return IsText(Text(At(address, 1)), "Stash", "Тайник") && Visible(address) ? address : IntPtr.Zero;
    }
}
