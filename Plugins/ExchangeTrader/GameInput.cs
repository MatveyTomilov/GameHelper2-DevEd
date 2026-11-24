using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using GameHelper;

namespace ExchangeTrader;

internal static class GameInput
{
    private const uint LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010, KeyUp = 0x0002, Unicode = 0x0004;
    internal const ushort Enter = 0x0d, Control = 0x11, Shift = 0x10, Escape = 0x1b, LeftArrow = 0x25, RightArrow = 0x27;

    internal static bool IsGameForeground => Core.Process.Pid != 0 && GetForegroundWindow() == Core.Process.Information.MainWindowHandle;

    internal static bool CursorOver(Vector2 min, Vector2 max)
    {
        if (!GetCursorPos(out var position)) return false;
        var bounds = Core.Process.WindowArea;
        var local = new Vector2(position.X - bounds.X, position.Y - bounds.Y);
        return local.X >= min.X && local.X < max.X && local.Y >= min.Y && local.Y < max.Y;
    }

    internal static void ReturnFocusFromOverlay()
    {
        if (Core.Process.Pid == 0) return;
        GetWindowThreadProcessId(GetForegroundWindow(), out uint owner);
        if (owner == Environment.ProcessId) SetForegroundWindow(Core.Process.Information.MainWindowHandle);
    }

    internal static void Move(Vector2 center)
    {
        var bounds = Core.Process.WindowArea;
        if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) || center.X < 0 || center.Y < 0 ||
            center.X >= bounds.Width || center.Y >= bounds.Height)
            throw new TradeBlockedException("Точка действия вне окна игры.");
        var target = new Point(bounds.X + (int)center.X, bounds.Y + (int)center.Y);
        var window = Core.Process.Information.MainWindowHandle;
        if (!IsGameForeground || WindowFromPoint(target) != window || !SetCursorPos(target.X, target.Y) ||
            !GetCursorPos(out var actual) || actual != target || WindowFromPoint(actual) != window)
            throw new TradeBlockedException("Игровое окно перекрыто или потеряло фокус.");
    }

    internal static void Click(Vector2 center, ushort modifier = 0, bool rightClick = false, Action? verifyTarget = null)
    {
        Move(center);
        verifyTarget?.Invoke();
        var down = Mouse(rightClick ? RightDown : LeftDown);
        var up = Mouse(rightClick ? RightUp : LeftUp);
        Send(modifier == 0 ? [down, up] : [Key(modifier), down, up, Key(modifier, KeyUp)]);
    }

    internal static void ClickAndHold(Vector2 center, ushort modifier = 0, bool rightClick = false)
    {
        Move(center);
        var down = Mouse(rightClick ? RightDown : LeftDown);
        var up = Mouse(rightClick ? RightUp : LeftUp);
        if (modifier != 0) Send([Key(modifier)]);
        try
        {
            Send([down]);
            Thread.Sleep(60);
        }
        finally
        {
            // Release both inputs even if the window lost focus during the press.
            Send(modifier == 0 ? [up] : [up, Key(modifier, KeyUp)], requireForeground: false);
        }
    }

    internal static void Press(ushort key, int count = 1)
    {
        var keys = new Input[count * 2];
        for (int index = 0; index < keys.Length; index += 2)
        {
            keys[index] = Key(key);
            keys[index + 1] = Key(key, KeyUp);
        }
        Send(keys);
    }

    internal static void PressAndHold(ushort key)
    {
        Send([Key(key)]);
        try { Thread.Sleep(60); }
        finally { Send([Key(key, KeyUp)], requireForeground: false); }
    }

    internal static void SelectAllText() => Send(SelectAllKeys());

    internal static void ReplaceText(string value, bool selectAll = true)
    {
        var inputs = new List<Input>();
        if (selectAll) inputs.AddRange(SelectAllKeys());
        foreach (char character in value)
        {
            if (char.IsAsciiDigit(character))
            {
                inputs.Add(Key(character));
                inputs.Add(Key(character, KeyUp));
                continue;
            }
            inputs.Add(Key(0, Unicode, character));
            inputs.Add(Key(0, Unicode | KeyUp, character));
        }
        if (value.Length == 0)
        {
            inputs.Add(Key(0x08));
            inputs.Add(Key(0x08, KeyUp));
        }
        Send(inputs.ToArray());
    }

    private static Input[] SelectAllKeys() => [Key(Control), Key(0x41), Key(0x41, KeyUp), Key(Control, KeyUp)];

    private static void Send(Input[] inputs, bool requireForeground = true)
    {
        if (requireForeground && !IsGameForeground) throw new TradeBlockedException("Игра потеряла фокус.");
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            foreach (var input in inputs.Skip((int)sent))
                if ((input.Type == 1 && (input.Data.Keyboard.Flags & KeyUp) != 0) ||
                    (input.Type == 0 && input.Data.Mouse.Flags is LeftUp or RightUp))
                    SendInput(1, [input], Marshal.SizeOf<Input>());
            throw new TradeBlockedException("Windows не подтвердил весь ввод. Действие не повторяется.");
        }
    }

    private static Input Mouse(uint flags) => new() { Type = 0, Data = new() { Mouse = new() { Flags = flags } } };
    private static Input Key(ushort key, uint flags = 0, ushort scan = 0)
        => new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Scan = scan, Flags = flags } } };

    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public MouseData Mouse;
        [FieldOffset(0)] public KeyboardData Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
}
