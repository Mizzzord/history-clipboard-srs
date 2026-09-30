using HistoryClipboard.Core;

namespace HistoryClipboard.Desktop.Platform;

public sealed class ClipboardAccessException(string message) : Exception(message);

public interface ISystemClipboard : IDisposable
{
    long Version { get; }
    void SetMonitoring(bool active) { }
    ClipboardSnapshot Read();
    ClipboardSnapshot Write(string text);
}

public static class SystemClipboard
{
    public static ISystemClipboard Create()
    {
        if (OperatingSystem.IsMacOS())
            return new MacClipboard();
        if (OperatingSystem.IsWindows())
            return new WindowsClipboard();
        throw new ClipboardAccessException("Эта версия поддерживает Windows и macOS.");
    }
}
