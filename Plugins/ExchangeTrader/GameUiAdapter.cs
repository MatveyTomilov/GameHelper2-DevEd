using System.Security.Cryptography;
using GameHelper;
using GameHelper.Plugin;
using GameHelper.RemoteEnums;
using GameHelper.RemoteObjects.States.InGameStateObjects;
using GameOffsets.Objects.States.InGameState;
using static ExchangeTrader.ExchangeUi;

namespace ExchangeTrader;

internal sealed partial class GameUiAdapter(IReadOnlySet<string>? excludedTabs = null) : IExchangeGame
{
    private const string VerifiedClient = "DFC10EF7E4E4411DF7418779C5467D804DEF225E80814BE6EE88F96CBD10DCCA";
    private readonly uint pid = Core.Process.Pid;
    private readonly IntPtr player = Area.Player.Address;
    private readonly string areaHash = Area.AreaHash;
    private long generation;

    private static AreaInstance Area => Core.States.InGameStateObject.CurrentAreaInstance;
    private readonly IReadOnlySet<string> excludedTabs = excludedTabs ?? new HashSet<string>(StringComparer.Ordinal);
    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    internal async Task CheckBuild(CancellationToken token)
    {
        ValidateSession(token);
        var path = Core.Process.Information.MainModule?.FileName ?? throw new TradeBlockedException("Не удалось определить версию клиента.");
        var hash = await Task.Run(() =>
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }, token);
        ValidateSession(token);
        if (hash != VerifiedClient) throw new TradeBlockedException("Версия PoE 2 изменилась. Требуется проверка разметки биржи и сундука.");
        if (ReadCursorStack() != null) throw new TradeBlockedException("Освободите курсор перед торговой сессией.");
        if (FindSplitView() != null) throw new TradeBlockedException("Закройте окно разделения стека перед торговой сессией.");
    }

    public void ValidateSession(CancellationToken token)
    {
        ValidateCharacter(token);
        if (!GameInput.IsGameForeground || Core.IsSettingsMenuOpen)
            throw new TradeBlockedException("Игра потеряла фокус или открылись настройки оверлея.");
    }

    private void ValidateCharacter(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (pid == 0 || Core.Process.Pid != pid || Area.Player.Address != player || player == IntPtr.Zero ||
            Area.AreaHash != areaHash || Core.States.GameCurrentState != GameStateTypes.InGameState)
            throw new TradeBlockedException("Игровой процесс, персонаж или область изменились.");
        if (Core.GHSettings.EnableControllerMode || !Core.States.InGameStateObject.CurrentWorldInstance.AreaDetails.IsHideout)
            throw new TradeBlockedException("Нужно находиться в убежище в режиме мыши и клавиатуры.");
        if (Core.GHSettings.AutomateNpcQuests || PManager.Plugins.Any(plugin => plugin.Metadata.Enable &&
            plugin.Name is "AutoHotKeyTrigger" or "PickupHelper"))
            throw new TradeBlockedException("Другая игровая автоматизация активна в этом профиле.");
    }

    private async Task<T> Until<T>(Func<T?> observe, string message, CancellationToken token, int milliseconds = 3000)
        where T : class
    {
        long deadline = Environment.TickCount64 + milliseconds;
        do
        {
            ValidateSession(token);
            var value = observe();
            if (value != null) return value;
            await Task.Delay(55, token);
        } while (Environment.TickCount64 < deadline);
        throw new TradeBlockedException(message);
    }

    private void Click(IntPtr element, CancellationToken token, ushort modifier = 0, bool rightClick = false)
    {
        ValidateSession(token);
        if (!Visible(element) || !Rect(element, out var position, out var size))
            throw new TradeBlockedException("Элемент управления исчез перед действием.");
        GameInput.Click(position + size / 2, modifier, rightClick, () =>
        {
            ValidateSession(token);
            if (!Visible(element) || !Rect(element, out var currentPosition, out var currentSize) ||
                !GameInput.CursorOver(currentPosition, currentPosition + currentSize))
                throw new TradeBlockedException("Элемент управления переместился после наведения.");
            CheckWorldTarget();
        });
    }

    private static void CheckWorldTarget()
    {
        var hovered = Core.States.InGameStateObject.MouseOverEntity;
        if (hovered.IsValid && (hovered.Path.Contains("Portal", StringComparison.OrdinalIgnoreCase) ||
            hovered.Path.Contains("AreaTransition", StringComparison.OrdinalIgnoreCase)))
            throw new TradeBlockedException("Курсор навёлся на игровой портал или переход. Нажатие пропущено.");
    }

    private async Task OpenGroundLabel(string entityPath, CancellationToken token, params string[] names)
    {
        ValidateSession(token);
        var targets = Area.AwakeEntities.Values.Where(entity => entity.IsValid && entity.Path == entityPath)
            .Select(entity => entity.Address).Take(2).ToArray();
        if (targets.Length != 1)
            throw new TradeBlockedException($"В видимой части убежища нет однозначной цели {names[0]}.");
        long deadline = Environment.TickCount64 + 2800;
        bool seen = false;
        do
        {
            var label = FindLabel();
            if (!Rect(label, out var position, out var size))
            {
                await Task.Delay(110, token);
                ValidateSession(token);
                continue;
            }

            seen = true;
            GameInput.Move(position + size / 2);
            await Task.Delay(110, token);
            ValidateSession(token);
            if (ExchangePanel() != IntPtr.Zero || Core.States.InGameStateObject.GameUi.LeftPanel.IsVisible || ReadCursorStack() != null)
                throw new TradeBlockedException($"Метка {names[0]} перекрыта другим окном или курсор занят.");

            var hovered = Core.States.InGameStateObject.MouseOverEntity;
            if (hovered.IsValid && hovered.Address == targets[0] && hovered.Path == entityPath &&
                GameInput.CursorOver(position, position + size))
            {
                // Ground label text can disappear from the base layer when the intended entity is highlighted.
                GameInput.ClickAndHold(position + size / 2);
                return;
            }
            if (hovered.IsValid || !Rect(FindLabel(), out position, out size) ||
                !GameInput.CursorOver(position + size / 4, position + size * .75f)) continue;

            CheckWorldTarget();
            GameInput.ClickAndHold(position + size / 2);
            return;
        } while (Environment.TickCount64 < deadline);

        throw new TradeBlockedException(seen ? $"Метка {names[0]} не удержалась под курсором." :
            $"Игровая метка {names[0]} не найдена однозначно.");

        IntPtr FindLabel()
        {
            var labels = Children(Root).Skip(6).Take(7).SelectMany(root => Children(At(root, 0, 0)))
                .Distinct().Where(address => IsText(Text(address), names) && Visible(address)).Take(2).ToArray();
            return labels.Length == 1 ? labels[0] : IntPtr.Zero;
        }
    }

    public async Task OpenStash(CancellationToken token)
    {
        ValidateSession(token);
        if (StashPanel() != IntPtr.Zero) return;
        if (ExchangePanel() != IntPtr.Zero || Core.States.InGameStateObject.GameUi.LeftPanel.IsVisible)
            throw new TradeBlockedException("Другая панель перекрывает сундук.");
        await OpenGroundLabel("Metadata/MiscellaneousObjects/Stash", token, "Stash", "Тайник");
        await Until(() => StashPanel() != IntPtr.Zero ? new object() : null, "Сундук не открылся.", token, 7500);
    }

    public async Task CloseStash(CancellationToken token)
    {
        var panel = RequireStash();
        Click(At(panel, 0), token);
        await Until(() => StashPanel() == IntPtr.Zero ? new object() : null, "Сундук не закрылся.", token);
    }

    public async Task OpenExchange(CancellationToken token)
    {
        ValidateSession(token);
        if (ExchangePanel() != IntPtr.Zero) return;
        if (Core.States.InGameStateObject.GameUi.LeftPanel.IsVisible || CloseOrderDialog() != null ||
            FindSplitView() != null || ReadCursorStack() != null || pendingSubmission != null)
            throw new TradeBlockedException("Биржа не может открыться: другая панель, занятый курсор или неподтверждённый ордер.");
        selectedQuote = null;
        preparedDraft = null;
        var action = ReadAngeExchangeAction();
        for (int attempt = 0; action == IntPtr.Zero && attempt < 2; attempt++)
        {
            if (FindAngeDialog() == IntPtr.Zero)
                await OpenGroundLabel("Metadata/NPC/Hideout/Ange", token, "Ange", "Анж");
            long deadline = Environment.TickCount64 + (attempt == 0 ? 2800 : 4500);
            do
            {
                ValidateSession(token);
                if (ExchangePanel() != IntPtr.Zero) return;
                action = ReadAngeExchangeAction();
                if (action != IntPtr.Zero) break;
                await Task.Delay(75, token);
            } while (Environment.TickCount64 < deadline);
        }

        if (action == IntPtr.Zero)
            throw new TradeBlockedException("Открытие меню Ange и пункта Currency Exchange не подтвердилось за две попытки.");
        if (!Rect(action, out var position, out var size))
            throw new TradeBlockedException("Пункт Currency Exchange исчез из меню Ange.");
        GameInput.Move(position + size / 2);
        await Task.Delay(85, token);
        ValidateSession(token);
        if (!Rect(ReadAngeExchangeAction(), out position, out size) ||
            !GameInput.CursorOver(position, position + size) || ReadCursorStack() != null)
            throw new TradeBlockedException("Меню Ange переместилось перед выбором Currency Exchange.");
        CheckWorldTarget();
        GameInput.ClickAndHold(position + size / 2);
        await Until(() => ExchangePanel() != IntPtr.Zero ? new object() : null, "Биржа не открылась.", token);
    }

    private static IntPtr FindAngeDialog()
    {
        var dialogs = Children(Root).Skip(19).Take(10).Where(dialog => Visible(dialog) &&
            IsText(Plain(Text(At(dialog, 0, 0))), "Ange", "Анж")).Take(2).ToArray();
        return dialogs.Length == 1 ? dialogs[0] : IntPtr.Zero;
    }

    private static IntPtr ReadAngeExchangeAction()
    {
        var dialog = FindAngeDialog();
        var actions = Walk(At(dialog, 1), 6, 120)
            .Where(address => IsText(Text(address), "Currency Exchange", "Обмен валюты", "Обмен валют") &&
                Rect(address, out _, out _)).Take(2).ToArray();
        return actions.Length == 1 ? actions[0] : IntPtr.Zero;
    }

    public async Task CloseExchange(CancellationToken token)
    {
        ValidateSession(token);
        var panel = RequireExchange();
        if (Visible(Picker(panel)) || CloseOrderDialog() != null || ReadCursorStack() != null)
            throw new TradeBlockedException("Закрытию биржи мешает другое окно или занятый курсор.");
        GameInput.PressAndHold(GameInput.Escape);
        await Until(() => ExchangePanel() == IntPtr.Zero ? new object() : null, "Биржа не закрылась.", token);
    }

    private static IntPtr RequireStash() => StashPanel() is var panel && panel != IntPtr.Zero
        ? panel : throw new TradeBlockedException("Сундук закрылся во время операции.");

    private static IntPtr RequireExchange() => ExchangePanel() is var panel && panel != IntPtr.Zero
        ? panel : throw new ExchangeClosedException();

    private static InventoryArrayStruct[] InventoryTable()
        => Area.ServerDataObject.TryReadInventoryTable(out var entries) ? entries
            : throw new TradeBlockedException("ServerData не подтвердил таблицу инвентарей.");

}
