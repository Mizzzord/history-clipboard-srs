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
            {
                if (ValidateExisting() == 1)
                    InitializeSchema(path, SqliteOpenMode.ReadWrite);
            }
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

    public IReadOnlyList<string> ReadTags(long id) => Execute(connection =>
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT t.name FROM tags t JOIN entry_tags et ON et.tag_id = t.id WHERE et.entry_id = $id ORDER BY t.name";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    });

    public void SetTags(long id, string input)
    {
        var names = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length > 12 || names.Any(name => name.Length > 40 || name.Any(char.IsControl)))
            throw new ArgumentException("Допускается до 12 меток по 40 символов без управляющих знаков.");
        Execute(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$id", id);
            command.CommandText = "SELECT id FROM entries WHERE id = $id";
            if (command.ExecuteScalar() is null)
                throw new ArgumentException("Эта запись уже удалена.");
            command.CommandText = "DELETE FROM entry_tags WHERE entry_id = $id";
            command.ExecuteNonQuery();
            var parameter = command.Parameters.Add("$name", SqliteType.Text);
            command.CommandText = """
                INSERT INTO tags(name) VALUES ($name) ON CONFLICT(name) DO NOTHING;
                INSERT INTO entry_tags(entry_id, tag_id) SELECT $id, id FROM tags WHERE name = $name;
                """;
            foreach (var name in names)
            {
                parameter.Value = name;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            return true;
        }, writable: true);
    }

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

    private int ValidateExisting()
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
        var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version is not (1 or 2))
            throw new StorageException("Версия хранилища не поддерживается.");
        if (version == 2)
        {
            command.CommandText = "SELECT t.name FROM entry_tags et JOIN tags t ON t.id = et.tag_id LIMIT 0";
            command.ExecuteScalar();
            command.CommandText = "PRAGMA foreign_key_check";
            if (command.ExecuteScalar() is not null)
                throw new StorageException("Нарушена связь записи с меткой.");
        }
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
        return version;
    }

    private static void CreateDatabase(string path)
    {
        InitializeSchema(path, SqliteOpenMode.ReadWriteCreate);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void InitializeSchema(string path, SqliteOpenMode mode)
    {
        using var connection = Open(path, mode);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL";
        command.ExecuteScalar();
        using var transaction = connection.BeginTransaction();
        command.Transaction = transaction;
        using var resource = typeof(HistoryStore).Assembly.GetManifestResourceStream("HistoryClipboard.Schema.sql")!;
        using var reader = new StreamReader(resource);
        command.CommandText = reader.ReadToEnd();
        command.ExecuteNonQuery();
        transaction.Commit();
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
            ForeignKeys = true,
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
