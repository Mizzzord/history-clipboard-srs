using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HistoryClipboard.Core;

public sealed class StorageException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class HistoryStore
{
    public string Path { get; }

    public HistoryStore(string path)
    {
        Path = path;
        try
        {
            if (File.Exists(path))
                ValidateExisting();
            else
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                CreateDatabase(path);
            }
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            throw new StorageException("Не удалось открыть историю. Исходные данные не заменены. Проверьте доступ к папке или начните новую историю явно.", ex);
        }
    }

    public IReadOnlyList<HistoryEntry> List(string query = "") => Execute(connection =>
    {
        connection.CreateFunction<string, string, bool>("contains_text", (text, needle) =>
            text.Contains(needle, StringComparison.OrdinalIgnoreCase), isDeterministic: true);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, substr(replace(replace(replace(text, char(13), ' '), char(10), ' '), char(9), ' '), 1, 160), created_at
            FROM entries WHERE $query = '' OR contains_text(text, $query) ORDER BY id DESC
            """;
        command.Parameters.AddWithValue("$query", query);
        using var reader = command.ExecuteReader();
        var entries = new List<HistoryEntry>();
        while (reader.Read())
            entries.Add(new HistoryEntry(reader.GetInt64(0), reader.GetString(1),
                DateTimeOffset.ParseExact(reader.GetString(2), "O", CultureInfo.InvariantCulture)));
        return entries;
    });

    public string? ReadText(long id) => Execute(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT text FROM entries WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    });

    public int Count() => Execute(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM entries";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    });

    public void Add(string text, DateTimeOffset time)
    {
        if (string.IsNullOrEmpty(text) || System.Text.Encoding.UTF8.GetByteCount(text) > CapturePolicy.MaxBytes)
            throw new ArgumentException("Текст не соответствует ограничениям записи.", nameof(text));

        Execute(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO entries (text, created_at) VALUES ($text, $time)";
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$time", time.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = "DELETE FROM entries WHERE id NOT IN (SELECT id FROM entries ORDER BY id DESC LIMIT 500)";
            command.ExecuteNonQuery();
            transaction.Commit();
            return true;
        }, writable: true);
    }

    public void Delete(long id) => Execute(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM entries WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
        return true;
    }, writable: true);

    public void Clear() => Execute(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM entries";
        command.ExecuteNonQuery();
        return true;
    }, writable: true);

    public static string RecoverExplicitly(string path)
    {
        var parent = System.IO.Path.GetDirectoryName(path)!;
        var backup = System.IO.Path.Combine(parent, $"history-backup-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        var fresh = System.IO.Path.Combine(parent, $"new-history-{Guid.NewGuid():N}.db");
        try
        {
            Directory.CreateDirectory(backup);
            var files = new[] { path, path + "-wal", path + "-shm", path + "-journal" }.Where(File.Exists).ToArray();
            foreach (var file in files)
                File.Copy(file, System.IO.Path.Combine(backup, System.IO.Path.GetFileName(file)));
            CreateDatabase(fresh);
            foreach (var file in files.Where(file => file != path))
                File.Delete(file);
            File.Move(fresh, path, overwrite: true);
            return backup;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            throw new StorageException("Не удалось создать новую историю. Сохранённые резервные файлы оставлены в папке приложения.", ex);
        }
        finally
        {
            if (File.Exists(fresh))
            {
                try { File.Delete(fresh); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private void ValidateExisting()
    {
        using var connection = Open(Path, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != "ok" || reader.Read())
                throw new StorageException("Хранилище повреждено.");
        }
        command.CommandText = "PRAGMA user_version";
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            throw new StorageException("Версия хранилища не поддерживается.");
        command.CommandText = "SELECT id, text, created_at FROM entries ORDER BY id DESC";
        using var entries = command.ExecuteReader();
        var count = 0;
        while (entries.Read())
        {
            var text = entries.GetString(1);
            if (++count > CapturePolicy.MaxEntries || entries.GetInt64(0) <= 0 || string.IsNullOrEmpty(text)
                || System.Text.Encoding.UTF8.GetByteCount(text) > CapturePolicy.MaxBytes
                || !DateTimeOffset.TryParseExact(entries.GetString(2), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new StorageException("Данные истории некорректны.");
        }
    }

    private static void CreateDatabase(string path)
    {
        using var connection = Open(path, SqliteOpenMode.ReadWriteCreate);
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "PRAGMA journal_mode = WAL";
            setup.ExecuteScalar();
        }
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE entries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                text TEXT NOT NULL CHECK(length(CAST(text AS BLOB)) BETWEEN 1 AND 1048576),
                created_at TEXT NOT NULL
            );
            PRAGMA user_version = 1;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private T Execute<T>(Func<SqliteConnection, T> operation, bool writable = false)
    {
        try
        {
            using var connection = Open(Path, writable ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly);
            return operation(connection);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            throw new StorageException("Не удалось прочитать или сохранить историю. Проверьте свободное место и права доступа; операция не подтверждена.", ex);
        }
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static bool IsStorageError(Exception ex) => ex is SqliteException or IOException or UnauthorizedAccessException
        or FormatException or InvalidCastException or StorageException;
}
