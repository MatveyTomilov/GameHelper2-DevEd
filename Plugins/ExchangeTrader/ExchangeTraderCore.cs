using System.Collections.Concurrent;
using System.Numerics;
using GameHelper;
using GameHelper.Plugin;
using GameHelper.Settings;
using GameHelper.Utils;
using ImGuiNET;

namespace ExchangeTrader;

public sealed partial class ExchangeTraderCore : PCore<ExchangeTraderSettings>, IPluginSmokeProbe
{
    private readonly FrameContext frame = new();
    private readonly SaleControls controls = new();
    private readonly List<string> messages = [];
    private SessionStatus status = new(SessionPhase.IDLE, "Готов");
    private CancellationTokenSource? cancellation;
    private bool starting;
    private long? launchStartDeadline;
    private Vector2 stopMin, stopMax;
    private FileInfo SettingsFile => new(Path.Combine(DllDirectory, "config", "settings.json"));
    private bool IsRunning => controls.Active;

    public override string GetDescription() => "Однократная торговая сессия Currency Exchange.";

    public override void OnEnable(bool isGameOpened)
    {
        Settings = JsonHelper.CreateOrLoadJsonFile<ExchangeTraderSettings>(SettingsFile);
        Settings.Normalize();
        if (Environment.GetEnvironmentVariable("GH_EXCHANGE_TRADER_START") == "1")
        {
            Environment.SetEnvironmentVariable("GH_EXCHANGE_TRADER_START", null);
            launchStartDeadline = Environment.TickCount64 + 15000;
        }
    }

    public override void OnDisable()
    {
        cancellation?.Cancel();
        frame.Pump();
    }

    public override void SaveSettings()
    {
        var file = SettingsFile;
        file.Directory?.Create();
        JsonHelper.SafeToFile(Settings, file);
    }

    public override void DrawSettings()
    {
        var stash = VisibleStash();
        DrawConfiguration(stash);
        DrawStatus();
    }

    public override void DrawUI()
    {
        bool overStop = false;
        try
        {
            var stash = VisibleStash();
            if (launchStartDeadline is long deadline)
            {
                if (Environment.TickCount64 >= deadline)
                {
                    launchStartDeadline = null;
                    status = new(SessionPhase.BLOCKED, "Для запуска из командной строки откройте сундук в активном окне игры.");
                }
                else if (stash != IntPtr.Zero && GameInput.IsGameForeground && !Core.IsSettingsMenuOpen)
                {
                    launchStartDeadline = null;
                    Start(stashOpen: true);
                }
            }
            if (Core.IsSettingsMenuOpen) return;
            var client = Core.Process.WindowArea;
            if (client.Width <= 0 || client.Height <= 0) return;
            if (stash == IntPtr.Zero && !IsRunning && status.Phase == SessionPhase.IDLE) return;
            ImGui.SetNextWindowSize(new Vector2(300, 200), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(new Vector2(client.Width * 0.535f, client.Height * 0.69f), ImGuiCond.FirstUseEver);
            ImGuiWindowFlags flags = ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoSavedSettings;
            // While the trader drives the game, only the Stop button needs to capture mouse input.
            overStop = !starting && IsRunning && GameInput.CursorOver(stopMin, stopMax);
            if (starting || IsRunning && !overStop) flags |= ImGuiWindowFlags.NoInputs;
            ImGui.SetNextWindowBgAlpha(0.9f);
            if (ImGui.Begin("ExchangeTrader##trade", flags))
            {
                DrawButtons(stash != IntPtr.Zero, overlay: true);
                DrawStatus();
                if (ImGui.CollapsingHeader("Настройки"))
                    DrawConfiguration(stash, showButtons: false);
            }
            ImGui.End();
        }
        finally
        {
            // Let the user click Stop before the next continuation can take the cursor away.
            if (!overStop || cancellation?.IsCancellationRequested == true) frame.Pump();
        }
    }

    private void DrawButtons(bool stashOpen, bool overlay = false)
    {
        var (showSale, highlight, stop) = controls.State(stashOpen);
        if (showSale)
        {
            if (highlight)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.53f, 0.34f, 1f));
                ImGui.PushStyleVar(ImGuiStyleVar.DisabledAlpha, 1f);
            }
            ImGui.BeginDisabled(highlight);
            if (ImGui.Button("Продать всё")) Start(stashOpen);
            ImGui.EndDisabled();
            if (highlight)
            {
                ImGui.PopStyleVar();
                ImGui.PopStyleColor();
            }
            if (stop) ImGui.SameLine();
        }
        if (stop)
        {
            if (ImGui.Button("Stop")) cancellation?.Cancel();
            if (overlay)
            {
                stopMin = ImGui.GetItemRectMin();
                stopMax = ImGui.GetItemRectMax();
            }
        }
        else if (overlay) stopMin = stopMax = Vector2.Zero;
    }

    private void DrawStatus()
    {
        ImGui.TextUnformatted($"Состояние · {status.Phase}");
        ImGui.TextWrapped(status.Detail);
        if (status.TabsTotal > 0)
            ImGui.ProgressBar((float)status.TabsRead / status.TabsTotal, new Vector2(-1, 0), $"Вкладки {status.TabsRead}/{status.TabsTotal}");
        ImGui.TextWrapped($"Найдено {status.Products} · Ордера {status.Placed} · Пропущено {status.Skipped}");
        if (status.Queued > 0) ImGui.TextUnformatted($"Осталось в очереди {status.Remaining}/{status.Queued}");
        ImGui.TextWrapped($"Получено ордеров {status.Collected} · Перенесено стеков {status.Deposited}");
        if (messages.Count > 0 && ImGui.CollapsingHeader("Причины пропуска и ход сессии"))
        {
            if (ImGui.BeginChild("trade-report", new Vector2(-1, 120)))
                foreach (string message in messages) ImGui.TextWrapped(message);
            ImGui.EndChild();
        }
    }

    private void Start(bool stashOpen)
    {
        if (!controls.TryStart(stashOpen)) return;
        launchStartDeadline = null;
        messages.Clear();
        discovered.Clear();
        status = new(SessionPhase.IDLE, "Проверяю игровую сессию");
        TradingPolicy policy;
        try
        {
            policy = Settings.ToPolicy();
            SaveSettings();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            controls.Complete();
            status = new(ex is ArgumentException ? SessionPhase.BLOCKED : SessionPhase.ERROR, ex.Message);
            AddMessage(ex.Message);
            return;
        }
        cancellation?.Dispose();
        cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        if (Core.IsSettingsMenuOpen) SettingsWindow.HideSettingsWindow();
        starting = true;
        frame.Invoke(() => _ = Run(policy, token));
    }

    private async Task Run(TradingPolicy policy, CancellationToken token)
    {
        try
        {
            // Give ImGui a frame to release the mouse captured by Start.
            await Task.Delay(100, token);
            GameInput.ReturnFocusFromOverlay();
            starting = false;
            long deadline = Environment.TickCount64 + 3000;
            while (!GameInput.IsGameForeground && Environment.TickCount64 < deadline)
                await Task.Delay(60, token);
            var game = new GameUiAdapter(policy.ExcludedTabs);
            await game.CheckBuild(token);
            await new TradeSession(game, update => status = update, AddMessage, tabs => listedTabs = tabs.ToArray(),
                stacks => Remember(stacks.Select(stack => new TradeProduct(stack.Metadata, stack.Name)))).Run(policy, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { status = status with { Phase = SessionPhase.STOPPED, Detail = "Сессия остановлена." }; }
        catch (TradeBlockedException ex) { status = status with { Phase = SessionPhase.BLOCKED, Detail = ex.Message }; AddMessage(ex.Message); }
        catch (Exception ex) { status = status with { Phase = SessionPhase.ERROR, Detail = ex.Message }; AddMessage(ex.ToString()); }
        finally
        {
            starting = false;
            controls.Complete();
        }
    }

    private void AddMessage(string message) => messages.Add(message);

    object IPluginSmokeProbe.CollectSmokeData() => new
    {
        phase = status.Phase.ToString(), status.Detail, status.TabsRead, status.TabsTotal,
        status.Products, status.Placed, status.Skipped, status.Collected, status.Deposited, status.Queued, status.Remaining,
        saleVisible = controls.State(VisibleStash() != IntPtr.Zero).ShowSale,
        saleHighlighted = controls.State(VisibleStash() != IntPtr.Zero).Highlight,
        stopVisible = controls.Active,
        stashTabsAvailable = listedTabs.Length, itemsAvailable = discovered.Count,
        notes = messages.TakeLast(16).ToArray(),
    };

    private sealed class FrameContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Action, object? State)> pending = new();

        public override void Post(SendOrPostCallback action, object? state) => pending.Enqueue((action, state));

        internal void Pump()
        {
            // A snapshot and its guarded input run on the overlay thread, alongside the existing UI readers.
            for (int index = 0; index < 6 && pending.TryDequeue(out var task); index++)
                Invoke(() => task.Action(task.State));
        }

        internal void Invoke(Action action)
        {
            var before = Current;
            SetSynchronizationContext(this);
            try { action(); }
            finally { SetSynchronizationContext(before); }
        }
    }
}
