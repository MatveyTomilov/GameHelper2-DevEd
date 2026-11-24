namespace GameHelper.Ui
{
    using System;
    using System.Collections.Generic;
    using System.ComponentModel;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Security.Cryptography;
    using System.Threading.Tasks;
    using Coroutine;
    using CoroutineEvents;
    using GameOffsets.Natives;
    using GameOffsets.Objects.UiElement;
    using Settings;
    using Utils;

    internal sealed class NpcQuestAutomation
    {
        private const string ClientSha256 = "DFC10EF7E4E4411DF7418779C5467D804DEF225E80814BE6EE88F96CBD10DCCA";
        private const int NameOffset = 0x98;
        private const int TextOffset = 0x490;
        private const int PanelType = 0x2F96340;
        private const int TextType = 0x35B0D50;
        private const int ActionLinkType = 0x2FC4D10;
        private const int InventoryPanelType = 0x30B8A48;
        private const uint Enabled = 0x2000;
        private const uint Interactive = 0x08000000;
        private const ushort Escape = 0x1B;
        private const int PollMilliseconds = 150;
        private const int BetweenKeysMilliseconds = 350;

        private uint clientPid;
        private Task<bool>? clientVerified;
        private IntPtr moduleBase;
        private long pollAt;
        private long nextKeyAt;
        private DialogueAction? pending;
        private DialogueAction? lastSent;

        internal static void InitializeCoroutines() =>
            CoroutineHandler.Start(new NpcQuestAutomation().Run());

        private IEnumerator<Wait> Run()
        {
            while (true)
            {
                yield return new Wait(GameHelperEvents.OnPostRender);
                var now = Environment.TickCount64;

                var game = Core.Process;
                var ui = Core.States.InGameStateObject.GameUi;
                if (!Core.GHSettings.AutomateNpcQuests || !game.Foreground ||
                    SettingsWindow.IsExpanded || Core.GHSettings.EnableControllerMode ||
                    ui.Address == IntPtr.Zero || ui.ChatParent.IsChatActive || ui.IsAnyLargePanelOtherThanRightOpen)
                {
                    this.pending = null;
                    continue;
                }

                if (now < this.pollAt)
                {
                    continue;
                }

                this.pollAt = now + PollMilliseconds;
                if (this.IsVerifiedClient(game))
                {
                    this.ProcessDialogue(game, ui.Address, ui.RightPanel.IsVisible, now);
                }
            }
        }

        private bool IsVerifiedClient(GameProcess game)
        {
            var pid = game.Pid;
            if (pid == 0)
            {
                return false;
            }

            if (pid != this.clientPid)
            {
                this.clientPid = pid;
                this.clientVerified = null;
                this.pending = null;
                this.lastSent = null;
            }

            if (this.clientVerified == null)
            {
                try
                {
                    var module = game.Information.MainModule;
                    if (module == null)
                    {
                        return false;
                    }

                    this.moduleBase = module.BaseAddress;
                    var path = module.FileName;
                    this.clientVerified = Task.Run(() => VerifyClientFile(path));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    return false;
                }
            }

            return this.clientVerified.IsCompletedSuccessfully && this.clientVerified.Result;
        }

        private static bool VerifyClientFile(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var supported = Convert.ToHexString(SHA256.HashData(stream)) == ClientSha256;
                if (!supported)
                {
                    Console.WriteLine("[NpcQuestAutomation] Game executable differs from the verified Steam client.");
                }

                return supported;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"[NpcQuestAutomation] Could not verify game executable: {ex.Message}");
                return false;
            }
        }

        private void ProcessDialogue(GameProcess game, IntPtr root, bool rightPanelOpen, long now)
        {
            var action = this.FindAction(game.Handle, root, out var dialog);
            if (dialog != this.lastSent?.Dialog)
            {
                this.lastSent = null;
            }

            var previous = this.pending;
            this.pending = rightPanelOpen && action is { InventoryOpen: false } ? null : action;
            if (this.pending is not { } recognized || recognized != previous || recognized == this.lastSent || now < this.nextKeyAt)
            {
                return;
            }

            try
            {
                var window = game.Information.MainWindowHandle;
                if (this.TrySendEscape(game, root, recognized, window))
                {
                    this.lastSent = recognized;
                    this.nextKeyAt = now + BetweenKeysMilliseconds;
                    Console.WriteLine($"[NpcQuestAutomation] Sent Escape on {recognized.Label} (inventory open: {recognized.InventoryOpen}).");
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                this.pending = null;
            }
        }

        private DialogueAction? FindAction(SafeMemoryHandle reader, IntPtr root, out IntPtr dialog)
        {
            dialog = IntPtr.Zero;
            if (!ReadNode(reader, root, out var ui) ||
                !TryReadChildren(reader, ui, 256, out var top) || top.Length <= 99 ||
                !ReadVisibleNode(reader, top[22], root, 0x3055528, out var host))
            {
                return null;
            }

            dialog = top[22];
            if (!ReadVisibleNode(reader, top[99], root, 0x2F9F610, out var popups) ||
                ReadString(reader, top[99] + NameOffset) != "PopupManager" ||
                !HasVisibleChildren(reader, top[99], popups, 0, out _) ||
                !TryGetNarrativeFrame(reader, dialog, host, out var frameAddress, out var frame))
            {
                return null;
            }

            var inventoryOpen = this.IsInventoryOpen(reader, root, top[33]);
            if (HasVisibleChildren(reader, frameAddress, frame, 2, out var offer) &&
                ReadVisibleNode(reader, offer[1], frameAddress, ActionLinkType, out var actions) &&
                this.TryGetQuestOfferButton(reader, offer[1], actions, out var offerButton))
            {
                return ReadAction(reader, dialog, frameAddress, offer[0], offer[1], offerButton,
                    0x35ACEF8, Enabled | Interactive, "Accept Quest", inventoryOpen);
            }

            if (HasVisibleChildren(reader, frameAddress, frame, 3, out var dialogue) &&
                ReadVisibleNode(reader, dialogue[1], frameAddress, PanelType, out var separator) &&
                HasVisibleChildren(reader, dialogue[1], separator, 0, out _))
            {
                return ReadAction(reader, dialog, frameAddress, dialogue[0], frameAddress, dialogue[2],
                    ActionLinkType, Enabled, "Continue", inventoryOpen);
            }

            return null;
        }

        private DialogueAction? ReadAction(
            SafeMemoryHandle reader, IntPtr dialog, IntPtr frame, IntPtr narrative, IntPtr buttonParent,
            IntPtr buttonAddress, int buttonType, uint requiredFlags, string label, bool inventoryOpen)
        {
            if (!ReadVisibleNode(reader, narrative, frame, TextType, out _) ||
                !ReadVisibleNode(reader, buttonAddress, buttonParent, buttonType, out var button) ||
                (button.Flags & requiredFlags) != requiredFlags ||
                !HasVisibleChildren(reader, buttonAddress, button, 1, out var labels) ||
                !ReadVisibleNode(reader, labels[0], buttonAddress, TextType, out _) ||
                !ReadString(reader, labels[0] + TextOffset).Equals(label, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var line = ReadString(reader, narrative + TextOffset);
            return string.IsNullOrWhiteSpace(line) ? null : new DialogueAction(dialog, buttonAddress, label, line, inventoryOpen);
        }

        private bool IsInventoryOpen(SafeMemoryHandle reader, IntPtr root, IntPtr address) =>
            ReadVisibleNode(reader, address, root, InventoryPanelType, out var inventory) &&
            TryReadChildren(reader, inventory, 16, out var children) && children.Length > 1 &&
            ReadVisibleNode(reader, children[1], address, TextType, out _) &&
            ReadString(reader, children[1] + NameOffset) == "label_title" &&
            ReadString(reader, children[1] + TextOffset) == "Inventory";

        private bool TryGetQuestOfferButton(
            SafeMemoryHandle reader, IntPtr strip, UiElementBaseOffset actions, out IntPtr button)
        {
            button = IntPtr.Zero;
            if (!TryReadVisibleChildren(reader, strip, actions, 8, out var children) || children.Count == 0)
            {
                return false;
            }

            if (children.Count > 1)
            {
                // Some offers display the future rewards alongside the Accept Quest button.
                if (children.Count < 3 || !ReadVisibleNode(reader, children[0], strip, TextType, out _) ||
                    ReadString(reader, children[0] + TextOffset) != "Reward:")
                {
                    return false;
                }

                for (var i = 1; i < children.Count - 1; i++)
                {
                    if (!ReadVisibleNode(reader, children[i], strip, PanelType, out var preview) ||
                        (preview.Flags & Interactive) != 0)
                    {
                        return false;
                    }
                }
            }

            button = children[^1];
            return true;
        }

        private bool TryGetNarrativeFrame(
            SafeMemoryHandle reader, IntPtr dialog, UiElementBaseOffset host,
            out IntPtr frameAddress, out UiElementBaseOffset frame)
        {
            frameAddress = IntPtr.Zero;
            frame = default;
            if (!HasVisibleChildren(reader, dialog, host, 2, out var hostChildren) ||
                !ReadVisibleNode(reader, hostChildren[0], dialog, PanelType, out _) ||
                ReadString(reader, hostChildren[0] + NameOffset) != "name_label_panel" ||
                !ReadVisibleNode(reader, hostChildren[1], dialog, PanelType, out var content) ||
                !HasVisibleChildren(reader, hostChildren[1], content, 1, out var contentChildren))
            {
                return false;
            }

            var scrollContainerAddress = contentChildren[0];
            if (!ReadVisibleNode(reader, scrollContainerAddress, hostChildren[1], PanelType, out var scrollContainer) ||
                !HasVisibleChildren(reader, scrollContainerAddress, scrollContainer, 2, out var scrollChildren) ||
                !ReadVisibleNode(reader, scrollChildren[0], scrollContainerAddress, 0x35B9460, out var scroll) ||
                !ReadVisibleNode(reader, scrollChildren[1], scrollContainerAddress, 0x35BC028, out _) ||
                !HasVisibleChildren(reader, scrollChildren[0], scroll, 1, out var scrollBody))
            {
                return false;
            }

            frameAddress = scrollBody[0];
            return ReadVisibleNode(reader, frameAddress, scrollChildren[0], PanelType, out frame) &&
                ReadString(reader, frameAddress + NameOffset) == "main_frame";
        }

        private bool ReadVisibleNode(SafeMemoryHandle reader, IntPtr address, IntPtr parent, int type, out UiElementBaseOffset node) =>
            ReadNode(reader, address, out node) && node.ParentPtr == parent &&
            node.Vtable == this.moduleBase + type && UiElementBaseFuncs.IsVisibleChecker(node.Flags);

        private static bool ReadNode(SafeMemoryHandle reader, IntPtr address, out UiElementBaseOffset node) =>
            reader.TryReadMemory(address, out node) && (node.Self == IntPtr.Zero || node.Self == address);

        private static string ReadString(SafeMemoryHandle reader, IntPtr address) =>
            reader.TryReadMemory<StdWString>(address, out var data) && data.Length <= data.Capacity
                ? reader.ReadStdWString(data)
                : string.Empty;

        private static bool TryReadChildren(SafeMemoryHandle reader, UiElementBaseOffset node, int limit, out IntPtr[] children)
        {
            children = Array.Empty<IntPtr>();
            var vector = node.ChildrensPtr;
            var bytes = (long)vector.Last - (long)vector.First;
            if (bytes < 0 || bytes > limit * IntPtr.Size || bytes % IntPtr.Size != 0)
            {
                return false;
            }

            children = reader.ReadStdVector<IntPtr>(vector);
            return children.Length == bytes / IntPtr.Size;
        }

        private static bool HasVisibleChildren(
            SafeMemoryHandle reader, IntPtr parent, UiElementBaseOffset node, int count, out List<IntPtr> visible) =>
            TryReadVisibleChildren(reader, parent, node, count, out visible) && visible.Count == count;

        private static bool TryReadVisibleChildren(
            SafeMemoryHandle reader, IntPtr parent, UiElementBaseOffset node, int limit, out List<IntPtr> visible)
        {
            visible = new List<IntPtr>(limit);
            if (!TryReadChildren(reader, node, 32, out var children))
            {
                return false;
            }

            foreach (var child in children)
            {
                if (!ReadNode(reader, child, out var candidate) || candidate.ParentPtr != parent)
                {
                    return false;
                }

                if (UiElementBaseFuncs.IsVisibleChecker(candidate.Flags))
                {
                    if (visible.Count == limit)
                    {
                        return false;
                    }

                    visible.Add(child);
                }
            }

            return true;
        }

        private bool TrySendEscape(GameProcess game, IntPtr root, DialogueAction action, IntPtr window)
        {
            return window != IntPtr.Zero && GetForegroundWindow() == window &&
                GetAsyncKeyState(Escape) >= 0 && GetAsyncKeyState(0x10) >= 0 &&
                GetAsyncKeyState(0x11) >= 0 && GetAsyncKeyState(0x12) >= 0 &&
                this.FindAction(game.Handle, root, out _) == action && GetForegroundWindow() == window &&
                SendInput(2, EscapeInput, Marshal.SizeOf<KeyboardInput>()) == 2;
        }

        private readonly record struct DialogueAction(IntPtr Dialog, IntPtr Button, string Label, string Line, bool InventoryOpen);

        // Win32 INPUT containing a KEYBDINPUT in this x64 build.
        [StructLayout(LayoutKind.Explicit, Size = 40)]
        private struct KeyboardInput
        {
            [FieldOffset(0)] public uint Type;
            [FieldOffset(8)] public ushort VirtualKey;
            [FieldOffset(12)] public uint Flags;
        }

        private static readonly KeyboardInput[] EscapeInput =
        [
            new() { Type = 1, VirtualKey = Escape },
            new() { Type = 1, VirtualKey = Escape, Flags = 0x02 },
        ];

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern uint SendInput(uint count, KeyboardInput[] inputs, int size);
    }
}
