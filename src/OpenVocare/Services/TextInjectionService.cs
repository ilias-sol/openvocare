using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media.Imaging;

namespace OpenVocare.Services;

public readonly record struct WindowTarget(IntPtr Handle, uint ProcessId)
{
    public static WindowTarget Capture()
    {
        IntPtr handle = Native.GetForegroundWindow();
        return handle != IntPtr.Zero && Native.GetWindowThreadProcessId(handle, out uint processId) != 0
            ? new WindowTarget(handle, processId)
            : default;
    }

    public bool IsValid =>
        Handle != IntPtr.Zero
        && ProcessId != 0
        && Native.IsWindow(Handle)
        && Native.GetWindowThreadProcessId(Handle, out uint currentProcessId) != 0
        && currentProcessId == ProcessId;

    private static class Native
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr window);
    }
}

public enum PasteResult
{
    Pasted,
    PastedClipboardRestored,
    CopiedFocusRestoreFailed,
    CopiedElevatedTarget,
    CopiedPasswordField,
    CopiedShortcutStillHeld,
    ClipboardChangedBeforePaste,
    CopiedInputBlocked,
    ClipboardUnavailable
}

public sealed class TextInjectionService
{
    private static readonly TimeSpan ClipboardRetryDelay = TimeSpan.FromMilliseconds(35);
    private static readonly TimeSpan ClipboardRestoreDelay = TimeSpan.FromMilliseconds(250);
    internal static int NativeInputSize => Marshal.SizeOf<Native.Input>();
    internal static int NativeInputUnionOffset => Marshal.OffsetOf<Native.Input>(nameof(Native.Input.Union)).ToInt32();
    internal static int NativeKeyboardExtraInfoOffset => Marshal.OffsetOf<Native.KeyboardInput>(nameof(Native.KeyboardInput.ExtraInfo)).ToInt32();

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Injected workflow service.")]
    public async Task<PasteResult> CopyAndTryPasteAsync(
        string text,
        WindowTarget target,
        bool restorePreviousClipboard = false,
        CancellationToken cancellationToken = default)
    {
        ClipboardSnapshot? previousClipboard = restorePreviousClipboard
            ? await TryCaptureClipboardAsync(cancellationToken)
            : null;

        if (!await TrySetClipboardTextAsync(text, cancellationToken))
        {
            return PasteResult.ClipboardUnavailable;
        }
        uint transcriptClipboardSequence = Native.GetClipboardSequenceNumber();

        if (!target.IsValid || !await TryRestoreFocusAsync(target, cancellationToken))
        {
            return PasteResult.CopiedFocusRestoreFailed;
        }
        if (ShouldBlockElevationBoundary(
                TryIsProcessElevated((uint)Environment.ProcessId),
                TryIsProcessElevated(target.ProcessId)))
        {
            return PasteResult.CopiedElevatedTarget;
        }
        if (IsFocusedPasswordField())
        {
            return PasteResult.CopiedPasswordField;
        }
        if (!await WaitForShortcutModifiersReleasedAsync(cancellationToken))
        {
            return PasteResult.CopiedShortcutStillHeld;
        }
        if (transcriptClipboardSequence != 0
            && Native.GetClipboardSequenceNumber() != transcriptClipboardSequence)
        {
            return PasteResult.ClipboardChangedBeforePaste;
        }
        if (!Native.SendCtrlV())
        {
            return PasteResult.CopiedInputBlocked;
        }

        if (previousClipboard is not null)
        {
            await Task.Delay(ClipboardRestoreDelay, CancellationToken.None);
            if (CanRestoreClipboard(
                    transcriptClipboardSequence,
                    Native.GetClipboardSequenceNumber()))
            {
                if (await TryRestoreClipboardAsync(previousClipboard))
                {
                    return PasteResult.PastedClipboardRestored;
                }
                AppLog.Write("The previous clipboard could not be restored after paste.");
            }
        }
        return PasteResult.Pasted;
    }

    internal static bool CanRestoreClipboard(
        uint transcriptSequence,
        uint currentSequence) =>
        transcriptSequence != 0 && transcriptSequence == currentSequence;

    internal static bool IsTargetIdentityValid(IntPtr expectedHandle, uint expectedProcessId, IntPtr actualHandle, uint actualProcessId) =>
        expectedHandle != IntPtr.Zero
        && expectedProcessId != 0
        && expectedHandle == actualHandle
        && expectedProcessId == actualProcessId;

    internal static bool IsTargetAlreadyForeground(
        WindowTarget target,
        IntPtr foregroundHandle,
        uint foregroundProcessId) =>
        IsTargetIdentityValid(
            target.Handle,
            target.ProcessId,
            foregroundHandle,
            foregroundProcessId);

    private static async Task<bool> TryRestoreFocusAsync(WindowTarget target, CancellationToken cancellationToken)
    {
        IntPtr currentForeground = Native.GetForegroundWindow();
        if (Native.GetWindowThreadProcessId(currentForeground, out uint currentProcessId) != 0
            && IsTargetAlreadyForeground(target, currentForeground, currentProcessId))
        {
            return true;
        }

        if (Native.IsIconic(target.Handle))
        {
            Native.ShowWindow(target.Handle, Native.SwRestore);
        }

        for (int attempt = 0; attempt < 4; attempt++)
        {
            Native.SetForegroundWindow(target.Handle);
            await Task.Delay(60, cancellationToken);
            IntPtr foreground = Native.GetForegroundWindow();
            uint threadId = Native.GetWindowThreadProcessId(foreground, out uint processId);
            if (threadId == 0) continue;
            if (IsTargetIdentityValid(target.Handle, target.ProcessId, foreground, processId))
            {
                return true;
            }
        }
        return false;
    }

    private static async Task<bool> TrySetClipboardTextAsync(string text, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException) when (attempt < 4)
            {
                await Task.Delay(ClipboardRetryDelay, cancellationToken);
            }
            catch (ExternalException)
            {
                return false;
            }
        }
        return false;
    }

    private static async Task<ClipboardSnapshot?> TryCaptureClipboardAsync(
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                System.Windows.IDataObject? source = System.Windows.Clipboard.GetDataObject();
                if (source is null)
                {
                    return new ClipboardSnapshot([]);
                }

                string[] formats = source.GetFormats(autoConvert: false);
                List<ClipboardEntry> entries = [];
                foreach (string format in formats.Distinct(StringComparer.Ordinal))
                {
                    object? value = source.GetData(format, autoConvert: false);
                    object? copy = CloneClipboardValue(value);
                    if (copy is null)
                    {
                        // Partial restoration is more surprising than leaving the
                        // transcript on the clipboard.
                        return null;
                    }
                    entries.Add(new ClipboardEntry(format, copy));
                }
                return new ClipboardSnapshot(entries);
            }
            catch (ExternalException) when (attempt < 2)
            {
                await Task.Delay(ClipboardRetryDelay, cancellationToken);
            }
            catch (Exception exception) when (
                exception is ExternalException
                    or InvalidOperationException
                    or NotSupportedException)
            {
                return null;
            }
        }
        return null;
    }

    private static object? CloneClipboardValue(object? value) => value switch
    {
        null => null,
        string text => text,
        string[] paths => paths.ToArray(),
        byte[] bytes => bytes.ToArray(),
        MemoryStream stream => new MemoryStream(stream.ToArray(), writable: false),
        Stream stream => CloneStream(stream),
        BitmapSource image => CloneBitmap(image),
        ICloneable cloneable => cloneable.Clone(),
        _ => null
    };

    private static MemoryStream CloneStream(Stream stream)
    {
        long originalPosition = stream.CanSeek ? stream.Position : 0;
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }
        MemoryStream copy = new();
        stream.CopyTo(copy);
        copy.Position = 0;
        if (stream.CanSeek)
        {
            stream.Position = originalPosition;
        }
        return copy;
    }

    private static BitmapSource CloneBitmap(BitmapSource source)
    {
        BitmapSource copy = source.Clone();
        copy.Freeze();
        return copy;
    }

    private static async Task<bool> TryRestoreClipboardAsync(ClipboardSnapshot snapshot)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (snapshot.Entries.Count == 0)
                {
                    System.Windows.Clipboard.Clear();
                }
                else
                {
                    System.Windows.DataObject restored = new();
                    foreach (ClipboardEntry entry in snapshot.Entries)
                    {
                        restored.SetData(entry.Format, entry.Value);
                    }
                    System.Windows.Clipboard.SetDataObject(restored, copy: true);
                }
                return true;
            }
            catch (ExternalException) when (attempt < 2)
            {
                await Task.Delay(ClipboardRetryDelay, CancellationToken.None);
            }
            catch (Exception exception) when (
                exception is ExternalException
                    or InvalidOperationException
                    or NotSupportedException)
            {
                return false;
            }
        }
        return false;
    }

    private static async Task<bool> WaitForShortcutModifiersReleasedAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (!Native.IsKeyDown(Native.VkControl)
                && !Native.IsKeyDown(Native.VkMenu)
                && !Native.IsKeyDown(Native.VkShift)
                && !Native.IsKeyDown(Native.VkLWin)
                && !Native.IsKeyDown(Native.VkRWin))
            {
                return true;
            }
            await Task.Delay(25, cancellationToken);
        }
        return false;
    }

    internal static bool ShouldBlockPasswordPaste(bool? isPassword) =>
        isPassword ?? true;

    internal static bool ShouldBlockElevationBoundary(
        bool? currentProcessElevated,
        bool? targetProcessElevated) =>
        targetProcessElevated is null
        || (targetProcessElevated == true && currentProcessElevated != true);

    private static bool IsFocusedPasswordField()
    {
        try
        {
            AutomationElement? focused = AutomationElement.FocusedElement;
            return ShouldBlockPasswordPaste(focused?.Current.IsPassword);
        }
        catch (Exception exception) when (
            exception is ElementNotAvailableException
                or InvalidOperationException
                or COMException)
        {
            // If Windows cannot inspect the focused field, copying is safe but
            // automatic paste is not. Fail closed rather than risk injecting a
            // transcript into a protected field.
            return true;
        }
    }

    private static bool? TryIsProcessElevated(uint processId)
    {
        IntPtr process = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!Native.OpenProcessToken(process, Native.TokenQuery, out IntPtr token)) return null;
            try
            {
                int size = Marshal.SizeOf<Native.TokenElevation>();
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    return Native.GetTokenInformation(
                            token,
                            Native.TokenElevationClass,
                            buffer,
                            size,
                            out _)
                        ? Marshal.PtrToStructure<Native.TokenElevation>(buffer).TokenIsElevated != 0
                        : null;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { Native.CloseHandle(token); }
        }
        finally { Native.CloseHandle(process); }
    }

    private static class Native
    {
        public const uint ProcessQueryLimitedInformation = 0x1000;
        public const uint TokenQuery = 0x0008;
        public const int TokenElevationClass = 20;
        public const int SwRestore = 9;
        private const uint InputKeyboard = 1;
        private const uint KeyEventKeyUp = 0x0002;
        public const ushort VkControl = 0x11;
        public const ushort VkMenu = 0x12;
        public const ushort VkShift = 0x10;
        public const ushort VkLWin = 0x5B;
        public const ushort VkRWin = 0x5C;
        private const ushort VkV = 0x56;

        [StructLayout(LayoutKind.Sequential)]
        public struct TokenElevation { public int TokenIsElevated; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Input { public uint Type; public InputUnion Union; }

        [StructLayout(LayoutKind.Explicit, Size = 32)]
        internal struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KeyboardInput
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint inputCount, [In] Input[] inputs, int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int virtualKey);
        [DllImport("user32.dll")]
        public static extern uint GetClipboardSequenceNumber();

        public static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

        public static bool SendCtrlV()
        {
            Input[] inputs = [Key(VkControl, 0), Key(VkV, 0), Key(VkV, KeyEventKeyUp), Key(VkControl, KeyEventKeyUp)];
            return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
        }

        private static Input Key(ushort virtualKey, uint flags) => new()
        {
            Type = InputKeyboard,
            Union = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = flags } }
        };
    }

    private sealed record ClipboardEntry(string Format, object Value);
    private sealed record ClipboardSnapshot(IReadOnlyList<ClipboardEntry> Entries);
}
