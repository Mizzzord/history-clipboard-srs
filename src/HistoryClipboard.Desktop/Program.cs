using Avalonia;
using HistoryClipboard.Core;
using System.Text.Json;

namespace HistoryClipboard.Desktop;

internal static class Program
{
    public static string DataDirectory { get; private set; } = "";
    public static string? StartupError { get; private set; }
    private static FileStream? _instanceLock;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--self-test"))
            return SelfTest(args);

        try
        {
            var configured = Environment.GetEnvironmentVariable("HISTORY_CLIPBOARD_DATA_DIR");
            var root = OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            DataDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? Path.Combine(root, "HistoryClipboard") : configured);
            var existed = Directory.Exists(DataDirectory);
            Directory.CreateDirectory(DataDirectory);
            if (!existed && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(DataDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            _instanceLock = new FileStream(Path.Combine(DataDirectory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StartupError = "Не удалось открыть папку данных. Возможно, History Clipboard уже запущен. Закройте другой экземпляр и проверьте права доступа к папке профиля.";
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return StartupError is null ? 0 : 1;
        }
        finally
        {
            _instanceLock?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();

    private static int SelfTest(string[] args)
    {
        var directory = Path.Combine(Path.GetTempPath(), "history-clipboard-self-test-" + Guid.NewGuid().ToString("N"));
        var checks = new List<string>();
        var success = false;
        try
        {
            var path = Path.Combine(directory, "history.db");
            var text = "Проверка 👋\r\n\tТекст без изменений";
            var store = new HistoryStore(path);
            store.Add(text, DateTimeOffset.Now);
            var entry = new HistoryStore(path).List("пРОВЕРКА").Single();
            if (store.ReadText(entry.Id) != text)
                throw new InvalidOperationException();
            checks.Add("SQLite: сохранение, восстановление, Unicode и поиск");
            store.Delete(entry.Id);
            if (new HistoryStore(path).Count() != 0)
                throw new InvalidOperationException();
            checks.Add("Удаление сохраняется после открытия базы");
            var policy = new CapturePolicy();
            policy.EstablishBaseline(1);
            if (policy.Observe(new(1, text)) != CaptureResult.Ignored || policy.Observe(new(2, text)) != CaptureResult.Accepted)
                throw new InvalidOperationException();
            checks.Add("Состояние наблюдения буфера");
            success = true;
        }
        catch (Exception ex) when (ex is StorageException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            checks.Add("Ошибка проверки: " + ex.GetType().Name);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        var result = JsonSerializer.Serialize(new { success, platform = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, checks }, new JsonSerializerOptions { WriteIndented = true });
        var reportIndex = Array.IndexOf(args, "--report");
        if (reportIndex >= 0 && reportIndex + 1 < args.Length)
            File.WriteAllText(args[reportIndex + 1], result);
        else
            Console.WriteLine(result);
        return success ? 0 : 1;
    }
}
