namespace HistoryClipboard.Core;

public sealed record HistoryEntry(long Id, string Preview, DateTimeOffset CreatedAt)
{
    public string DisplayDate => CreatedAt.ToString("dd.MM.yyyy · HH:mm:ss");
}

public sealed record ClipboardSnapshot(long Version, string? Text);

public enum CaptureResult
{
    Ignored,
    Accepted,
    TooLarge
}

public sealed class CapturePolicy
{
    public const int MaxBytes = 1_048_576;
    public const int MaxEntries = 500;
    private long? _version;
    private string? _lastText;

    public bool IsCurrentVersion(long version) => _version == version;

    public void EstablishBaseline(long version)
    {
        _version = version;
        _lastText = null;
    }

    public void SuppressOwnCopy(ClipboardSnapshot snapshot)
    {
        _version = snapshot.Version;
        _lastText = snapshot.Text;
    }

    public CaptureResult Observe(ClipboardSnapshot snapshot)
    {
        if (_version == snapshot.Version)
            return CaptureResult.Ignored;

        _version = snapshot.Version;
        var text = snapshot.Text;
        if (text == _lastText)
            return CaptureResult.Ignored;

        _lastText = text;
        if (string.IsNullOrEmpty(text))
            return CaptureResult.Ignored;

        return System.Text.Encoding.UTF8.GetByteCount(text) > MaxBytes
            ? CaptureResult.TooLarge
            : CaptureResult.Accepted;
    }
}
