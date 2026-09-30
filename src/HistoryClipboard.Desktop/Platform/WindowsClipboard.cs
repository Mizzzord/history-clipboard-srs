using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using HistoryClipboard.Core;

namespace HistoryClipboard.Desktop.Platform;

internal sealed class WindowsClipboard : ISystemClipboard
{
    private const uint UnicodeText = 13;
    private const uint FileDrop = 15;
    private const uint ClipboardUpdate = 0x031D;
    private readonly WindowProcedure _procedure;
    private readonly string _className = "HistoryClipboardListener" + Guid.NewGuid().ToString("N");
    private readonly nint _instance;
    private readonly nint _window;
    private long _cachedVersion;
    private bool _dirty = true;

    public WindowsClipboard()
    {
        _procedure = HandleMessage;
        _instance = GetModuleHandle(null);
        var windowClass = new WindowClass
        {
            Procedure = Marshal.GetFunctionPointerForDelegate(_procedure),
            Instance = _instance,
            ClassName = _className
        };
        if (RegisterClass(ref windowClass) == 0)
            throw Failure();
        _window = CreateWindowEx(0, _className, "", 0, 0, 0, 0, 0, new nint(-3), 0, _instance, 0);
        if (_window == 0 || !AddClipboardFormatListener(_window))
        {
            Dispose();
            throw Failure();
        }
        _cachedVersion = GetClipboardSequenceNumber();
    }

    public long Version => _dirty ? GetClipboardSequenceNumber() : _cachedVersion;

    public ClipboardSnapshot Read()
    {
        if (!OpenClipboard(_window))
            throw Failure();
        try
        {
            var version = GetClipboardSequenceNumber();
            string? text = null;
            if (!IsClipboardFormatAvailable(FileDrop) && IsClipboardFormatAvailable(UnicodeText))
            {
                var data = GetClipboardData(UnicodeText);
                if (data == 0)
                    throw Failure();
                var pointer = GlobalLock(data);
                if (pointer == 0)
                    throw Failure();
                try { text = Marshal.PtrToStringUni(pointer); }
                finally { GlobalUnlock(data); }
            }
            _cachedVersion = version;
            _dirty = false;
            return new ClipboardSnapshot(version, text);
        }
        finally
        {
            CloseClipboard();
        }
    }

    public ClipboardSnapshot Write(string text)
    {
        var bytes = Encoding.Unicode.GetBytes(text + '\0');
        var data = GlobalAlloc(0x0042, (nuint)bytes.Length);
        if (data == 0)
            throw Failure();
        var transferred = false;
        try
        {
            var pointer = GlobalLock(data);
            if (pointer == 0)
                throw Failure();
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { GlobalUnlock(data); }
            if (!OpenClipboard(_window))
                throw Failure();
            try
            {
                if (!EmptyClipboard() || SetClipboardData(UnicodeText, data) == 0)
                    throw Failure();
                transferred = true;
            }
            finally { CloseClipboard(); }
            _dirty = true;
            return Read();
        }
        finally
        {
            if (!transferred)
                GlobalFree(data);
        }
    }

    public void Dispose()
    {
        if (_window != 0)
        {
            RemoveClipboardFormatListener(_window);
            DestroyWindow(_window);
        }
        UnregisterClass(_className, _instance);
    }

    private nint HandleMessage(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == ClipboardUpdate)
            _dirty = true;
        return DefWindowProc(window, message, wParam, lParam);
    }

    private static ClipboardAccessException Failure() => new($"Системный буфер недоступен (код {Marshal.GetLastWin32Error()}). Повторяем попытку.");

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public nint Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassW", SetLastError = true)]
    private static extern ushort RegisterClass(ref WindowClass windowClass);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, nint instance);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? module);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(nint window);
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint memory);
}
