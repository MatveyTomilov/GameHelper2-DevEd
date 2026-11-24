using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using static ExchangeTrader.ExchangeUi;

namespace ExchangeTrader;

internal sealed partial class GameUiAdapter
{
    private string[] pickerNames = [];
    private SideQuote? selectedQuote;
    private Draft? preparedDraft;
    private bool submitted;

    private static IntPtr Picker(IntPtr panel)
    {
        var fields = Children(panel);
        IntPtr inactive = IntPtr.Zero;
        IntPtr visible = IntPtr.Zero;
        // Orders and notifications can shift sibling indices. The heading and list identify the picker.
        foreach (var field in fields.Skip(19))
        {
            if (!IsText(Text(At(field, 3)), "I Have", "У меня есть", "I Want", "Я хочу") || At(field, 6) == IntPtr.Zero)
                continue;
            if (!Visible(field)) { inactive = field; continue; }
            if (visible != IntPtr.Zero) throw new TradeBlockedException("На бирже открыто два списка выбора товара.");
            visible = field;
        }
        return visible != IntPtr.Zero ? visible : inactive;
    }
    private static string HaveName(IntPtr panel) => ProductName(Text(At(panel, 10, 0)));
    private static string WantName(IntPtr panel) => ProductName(Text(At(panel, 7, 0)));
    private static string ProductName(string value) => Regex.Replace(Plain(value), @"\s+", " ");
    private static bool Matches(IntPtr panel, Pair pair) => HaveName(panel) == pair.Have.Name && WantName(panel) == pair.Want.Name;

    public async Task<IReadOnlyList<PickerItem>> ReadPicker(CancellationToken token)
    {
        IntPtr picker;
        try { picker = await OpenPicker(true, token); }
        catch (MarketUnavailableException ex) { throw new TradeBlockedException(ex.Message); }
        PickerEntry[]? previous = null;
        var entries = await Until(() =>
        {
            var current = PickerRows(picker).ToArray();
            if (current.Length < 20) return null;
            if (previous != null && current.SequenceEqual(previous)) return current;
            previous = current;
            return null;
        }, "Список доступных товаров не загрузился.", token);
        pickerNames = entries.Select(row => row.Name).Distinct().ToArray();
        await ClosePicker(picker, token);
        return entries.Select(row => new PickerItem(row.Name, row.Amount, ReadNumber(row.Amount))).ToArray();
    }

    private async Task<IntPtr> OpenPicker(bool have, CancellationToken token)
    {
        var panel = RequireExchange();
        int buttonIndex = have ? 10 : 7;
        string expected = have ? "I Have" : "I Want";
        string localized = have ? "У меня есть" : "Я хочу";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var picker = ReadOpenPicker();
            if (ReadCursorStack() != null)
                throw new TradeBlockedException($"Курсор занят перед открытием списка {expected}.");
            if (attempt > 0 && CloseOrderDialog() != null)
                throw new TradeBlockedException($"Подтверждение отмены перекрывает список {expected}.");
            if (picker != IntPtr.Zero)
            {
                if (IsExpected(picker)) return picker;
                await ClosePicker(picker, token);
                panel = RequireExchange();
                picker = ReadOpenPicker();
                if (picker != IntPtr.Zero)
                {
                    if (IsExpected(picker)) return picker;
                    continue;
                }
            }

            // A quote may be refreshed while the client updates the order list. Re-read the current button.
            var button = At(panel, buttonIndex);
            if (!Visible(button))
            {
                await Task.Delay(120, token);
                continue;
            }
            Click(button, token);
            long deadline = Environment.TickCount64 + (attempt == 0 ? 1050 : 2300);
            do
            {
                picker = ReadOpenPicker();
                if (picker != IntPtr.Zero && IsExpected(picker)) return picker;
                await Task.Delay(55, token);
            } while (Environment.TickCount64 < deadline);
        }

        var pending = ReadOpenPicker();
        if (ReadCursorStack() != null || CloseOrderDialog() != null)
            throw new TradeBlockedException($"Курсор или окно подтверждения блокирует список {expected}.");
        if (pending != IntPtr.Zero)
        {
            if (IsExpected(pending)) return pending;
            await ClosePicker(pending, token);
        }
        throw new MarketUnavailableException($"Список {expected} не открылся. Котировка пропущена.");

        bool IsExpected(IntPtr picker) => IsText(Text(At(picker, 3)), expected, localized);

        IntPtr ReadOpenPicker()
        {
            ValidateSession(token);
            if (RequireExchange() != panel) throw new TradeBlockedException($"Панель биржи сменилась при открытии списка {expected}.");
            var picker = Picker(panel);
            return Visible(picker) ? picker : IntPtr.Zero;
        }
    }

    private async Task ClosePicker(IntPtr picker, CancellationToken token)
    {
        ValidateSession(token);
        var panel = RequireExchange();
        if (!Visible(picker)) return;
        if (Picker(panel) != picker || CloseOrderDialog() != null || ReadCursorStack() != null)
            throw new TradeBlockedException("Закрытию списка товаров мешает другое окно или занятый курсор.");
        GameInput.PressAndHold(GameInput.Escape);
        // In this client Escape closes the exchange as well as the picker.
        await Until(() => ExchangePanel() == IntPtr.Zero && !Visible(picker) ? new object() : null,
            "Список товаров и биржа не закрылись по Esc.", token);
        await OpenExchange(token);
    }

    private static IEnumerable<IntPtr> PickerRowAddresses(IntPtr picker)
    {
        var list = At(picker, 6, 1);
        foreach (var category in Children(list))
            foreach (var group in Children(category))
                foreach (var row in Children(group).Skip(1))
                    yield return row;
    }

    private static IEnumerable<PickerEntry> PickerRows(IntPtr picker)
    {
        foreach (var row in PickerRowAddresses(picker))
        {
            var cells = Children(row);
            if (cells.Length == 0) continue;
            string name = ProductName(Text(cells[0]));
            if (name.Length > 1)
                yield return new(row, name, cells.Length > 1 ? Plain(Text(At(cells[1], 0))) : "");
        }
    }

    private static PickerEntry? SelectableRow(IntPtr picker, string name)
    {
        if (!Rect(At(picker, 6), out var viewport, out var size)) return null;
        var rows = new List<(IntPtr Address, string Name, Vector2 Position, Vector2 Size)>();
        foreach (var address in PickerRowAddresses(picker))
        {
            if (!Visible(address) || !Rect(address, out var pos, out var rowSize) ||
                !Contains(viewport, size, pos + rowSize / 2)) continue;
            string rowName = ProductName(Text(At(address, 0)));
            if (rowName.Length > 1) rows.Add((address, rowName, pos, rowSize));
        }

        var candidates = rows.Where(row => row.Name == name)
            .Select(row => (Row: row, Amount: Plain(Text(At(row.Address, 1, 0)))))
            .OrderByDescending(candidate => candidate.Amount.Length);
        foreach (var (row, amount) in candidates)
        {
            var center = row.Position + row.Size / 2;
            if (!rows.Any(other => other.Name != name && Contains(other.Position, other.Size, center)))
                return new(row.Address, row.Name, amount);
        }
        return null;
    }

    private static bool Contains(Vector2 pos, Vector2 size, Vector2 item)
        => item.X >= pos.X && item.X < pos.X + size.X && item.Y >= pos.Y && item.Y < pos.Y + size.Y;

    private async Task SelectProduct(string name, bool have, Action observeMarket, CancellationToken token)
    {
        var panel = RequireExchange();
        if ((have ? HaveName(panel) : WantName(panel)) == name) return;
        var picker = await OpenPicker(have, token);
        panel = RequireExchange();
        var choice = SelectableRow(picker, name);
        if (choice != null && await Choose(choice)) return;
        if (!Visible(picker) || RequireExchange() != panel)
        {
            throw new MarketUnavailableException($"Биржа не подтвердила выбор {name}.");
        }

        // Hidden category rows can retain stale screen coordinates until the search results settle.
        var all = Walk(At(picker, 5), 4, 140).SingleOrDefault(address => IsText(Text(address), "All", "Все"));
        if (all != IntPtr.Zero)
        {
            Click(all, token);
            // The game can restore the pointer to the category while it processes the click.
            await Task.Delay(55, token);
            ValidateSession(token);
            if (RequireExchange() != panel || Picker(panel) != picker || !Visible(picker))
                throw new TradeBlockedException("Окно выбора валюты изменилось после открытия категории.");
        }
        var input = At(picker, 4, 0);
        Click(input, token);
        await Task.Delay(70, token);
        ValidateSession(token);
        if (Picker(RequireExchange()) != picker || !Visible(input)) throw new TradeBlockedException("Поле поиска исчезло.");
        GameInput.ReplaceText(name);

        for (int attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(70, token);
            ValidateSession(token);
            if (Picker(RequireExchange()) != picker || !Visible(picker))
                throw new TradeBlockedException("Окно выбора валюты закрылось во время поиска.");
            if (ProductName(Text(input)) != name) continue;
            choice = SelectableRow(picker, name);
            if (choice == null) continue;
            if (await Choose(choice)) return;
            break;
        }
        if (Visible(picker)) await ClosePicker(picker, token);
        throw new MarketUnavailableException($"Биржа не подтвердила выбор {name}.");

        async Task<bool> Choose(PickerEntry candidate)
        {
            if (!Rect(candidate.Address, out var position, out var size)) return false;
            GameInput.Move(position + size / 2);
            await Task.Delay(65, token);
            if (RequireExchange() != panel || Picker(panel) != picker || !Visible(picker))
                throw new TradeBlockedException("Picker изменился перед выбором.");
            var target = SelectableRow(picker, name);
            if (target == null || target.Address != candidate.Address ||
                !Rect(target.Address, out position, out size) || !GameInput.CursorOver(position, position + size)) return false;
            Click(target.Address, token);
            for (int attempt = 0; attempt < 12; attempt++)
            {
                ValidateSession(token);
                if (RequireExchange() != panel) throw new TradeBlockedException("Панель биржи изменилась.");
                observeMarket();
                if (!Visible(picker) && (have ? HaveName(panel) : WantName(panel)) == name) return true;
                await Task.Delay(55, token);
            }
            return false;
        }
    }

    public async Task<SideQuote> ReadSide(Pair pair, CancellationToken token)
    {
        ValidateSession(token);
        selectedQuote = null;
        var before = RequireExchange();
        var confirmation = new MarketRatioConfirmation();
        void Observe()
        {
            var panel = RequireExchange();
            if (panel != before || !Matches(panel, pair)) return;
            if (IsMarketLoading(ReadMarketLabel(panel))) confirmation.ObserveLoading();
        }

        string have = HaveName(before);
        string want = WantName(before);
        if (Matches(before, pair) || have == pair.Want.Name && want == pair.Have.Name)
        {
            string alternate = pickerNames.FirstOrDefault(name => name != pair.Have.Name && name != pair.Want.Name)
                ?? throw new MarketUnavailableException("Нельзя обновить прежнюю пару: нет третьей позиции для смены направления.");
            await SelectProduct(alternate, false, Observe, token);
            want = alternate;
        }
        if (want == pair.Have.Name)
        {
            await SelectProduct(pair.Want.Name, false, Observe, token);
            await SelectProduct(pair.Have.Name, true, Observe, token);
        }
        else
        {
            await SelectProduct(pair.Have.Name, true, Observe, token);
            await SelectProduct(pair.Want.Name, false, Observe, token);
        }

        for (int attempt = 0; attempt < 30; attempt++)
        {
            ValidateSession(token);
            var panel = RequireExchange();
            if (panel != before) throw new MarketUnavailableException("Панель биржи переоткрылась во время чтения котировки.");
            if (!Matches(panel, pair)) throw new TradeBlockedException("Пара изменилась после выбора.");
            var raw = ReadMarketLabel(panel);
            if (IsMarketLoading(raw)) confirmation.ObserveLoading();
            else
            {
                var ratio = ReadRatio(raw);
                string? unavailable = ratio.IsValid ? null : MarketRatioConfirmation.UnavailableText(raw);
                switch (confirmation.Observe(ratio, unavailable, Environment.TickCount64))
                {
                    case MarketRatioState.Exact:
                        return selectedQuote = new(pair, ratio, ++generation, Now);
                    case MarketRatioState.Unavailable:
                        throw new MarketUnavailableException(unavailable == "No Stock"
                            ? "Свежий Market Ratio подтвердил отсутствие предложений."
                            : "Свежий Market Ratio отображается с округлением. Точная цена не подтверждена.");
                }
            }
            await Task.Delay(75, token);
        }
        throw new MarketUnavailableException("Не подтверждён свежий Market Ratio для выбранной стороны.");
    }

    private static bool IsMarketLoading(string text) => text.Contains("Retrieving Info", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("Получение информации", StringComparison.OrdinalIgnoreCase);

    private static string ReadMarketLabel(IntPtr panel) => Text(At(panel, 14));

    public async Task<Draft> Prepare(SaleOrder order, CancellationToken token)
    {
        preparedDraft = null;
        var panel = RequireExchange();
        RequireQuote(panel, order);
        await FillAmount(panel, order, 8, order.Have, token);
        await FillAmount(panel, order, 5, order.Want, token);
        // Moving focus to the label commits both inputs without placing the order.
        Click(At(panel, 6), token);
        await Task.Delay(100, token);
        ValidateSession(token);
        var draft = InspectDraft(panel, order);
        if (draft.ConfirmedBid != selectedQuote!.Ratio)
        {
            // The confirmed quantities can change which available depth the client shows for the same pair.
            await Task.Delay(75, token);
            ValidateSession(token);
            if (InspectDraft(panel, order) != draft)
                throw new MarketUnavailableException("Встречное предложение меняется после заполнения черновика.");
        }
        return preparedDraft = draft;
    }

    private async Task FillAmount(IntPtr panel, SaleOrder order, int index, int amount, CancellationToken token)
    {
        var input = At(panel, index);
        var value = amount.ToString(CultureInfo.InvariantCulture);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ValidateSession(token);
            RequireQuote(panel, order);
            if (At(panel, index) != input || !Visible(input) || !Rect(input, out var position, out var size))
                throw new TradeBlockedException("Поле количества изменилось.");
            GameInput.ClickAndHold(position + size / 2);
            await Task.Delay(75, token);
            CheckInput();
            GameInput.SelectAllText();
            await Task.Delay(50, token);
            CheckInput();
            GameInput.ReplaceText(value, selectAll: false);

            long deadline = Environment.TickCount64 + 1000;
            do
            {
                CheckInput();
                if (ReadNumber(Text(input)) == amount) return;
                await Task.Delay(65, token);
            } while (Environment.TickCount64 < deadline);
        }

        string field = index == 8 ? "I Have" : "I Want";
        string observed = ReadNumber(Text(input))?.ToString(CultureInfo.InvariantCulture) ?? "нет числа";
        throw new MarketUnavailableException($"{field} не приняло количество {value} (прочитано: {observed}).");

        void CheckInput()
        {
            ValidateSession(token);
            if (RequireExchange() != panel || !Matches(panel, order.Pair) || Visible(Picker(panel)) ||
                At(panel, index) != input || !Visible(input) || !Rect(input, out var position, out var size) ||
                !GameInput.CursorOver(position, position + size) || ReadCursorStack() != null)
                throw new TradeBlockedException("Поле количества или курсор изменились во время ввода.");
        }
    }

    private Ratio RequireQuote(IntPtr panel, SaleOrder order, bool checkCommittedBid = false)
    {
        var picker = Picker(panel);
        if (panel != RequireExchange() || !Matches(panel, order.Pair) || picker == IntPtr.Zero || Visible(picker))
            throw new TradeBlockedException("Биржа изменила пару или окно после расчёта.");
        var live = ReadRatio(ReadMarketLabel(panel));
        if (selectedQuote is not { } quote || quote.Pair != order.Pair || Now - quote.ReceivedAt > PricingEngine.MaximumQuoteAge ||
            (checkCommittedBid ? !PricingEngine.IsPassive(order, live) : quote.Ratio != live))
            throw new MarketUnavailableException("Не подтверждена пассивная цена перед размещением.");
        return live;
    }

    private Draft InspectDraft(IntPtr panel, SaleOrder order)
    {
        var bid = RequireQuote(panel, order, checkCommittedBid: true);
        var gold = ReadNumber(Text(At(panel, 15)));
        var wallet = ReadWallet();
        if (ReadNumber(Text(At(panel, 5))) != order.Want || ReadNumber(Text(At(panel, 8))) != order.Have)
            throw new MarketUnavailableException("Клиент изменил введённые количества черновика.");
        if (gold is not (> 0 and <= int.MaxValue) || wallet == null || wallet < gold)
            throw new MarketUnavailableException("Клиент не подтвердил стоимость или в рюкзаке недостаточно золота.");
        if (!HasEnabledPlaceButton(panel))
            throw new MarketUnavailableException("Кнопка размещения неактивна после фиксации обоих полей.");
        return new(order, generation, (int)gold.Value, bid);
    }

    public Task SubmitOnce(Draft draft, CancellationToken token)
    {
        ValidateSession(token);
        var panel = RequireExchange();
        if (preparedDraft != draft || submitted && lastSubmissionGeneration == draft.Context)
            throw new TradeBlockedException("Это намерение уже отправлено или не подготовлено.");
        var current = ReadOrderSnapshot(panel);
        var wallet = ReadWallet();
        if (pendingSubmission != null || InspectDraft(panel, draft.Order) != draft || current is not { HasSpace: true } ||
            wallet == null || ReadCursorStack() != null)
            throw new MarketUnavailableException("Черновик или свободные слоты изменились до размещения.");
        ValidateProtectedStock(draft.Order.Pair.Have);
        pendingSubmission = new(draft, panel, current.Used, wallet.Value, current.Orders.Select(order => order.Id).ToHashSet());
        submitted = true;
        lastSubmissionGeneration = draft.Context;
        Click(At(panel, 16), token);
        return Task.CompletedTask;
    }

    private long lastSubmissionGeneration;
    private static bool HasEnabledPlaceButton(IntPtr panel) =>
        TryNode(At(panel, 16), out var button) && (button.Flags & 0x2000) != 0 &&
        IsText(Text(At(panel, 16, 0)), "Place order", "Разместить ордер", "Разместить заказ");
    private sealed record PickerEntry(IntPtr Address, string Name, string Amount);
}
