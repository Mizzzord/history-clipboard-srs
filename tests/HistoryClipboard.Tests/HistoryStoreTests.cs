using HistoryClipboard.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HistoryClipboard.Tests;

public sealed class HistoryStoreTests : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "history-tests-" + Guid.NewGuid().ToString("N"));
    private string Database => System.IO.Path.Combine(_directory, "history.db");

    public HistoryStoreTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void FullUnicodeTextAndOriginalTimeSurviveRestart()
    {
        var store = new HistoryStore(Database);
        var text = "  Привет 👋\r\n\tстрока ' OR 1=1 --  ";
        var time = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.FromHours(3));
        store.Add(text, time);
        var restored = new HistoryStore(Database);
        var entry = Assert.Single(restored.List());
        Assert.Equal(text, restored.ReadText(entry.Id));
        Assert.Equal(time, entry.CreatedAt);
        Assert.Equal(TimeSpan.FromHours(3), entry.CreatedAt.Offset);
    }

    [Fact]
    public void OrderingUsesInsertionOrderEvenWhenClockMovesBackwards()
    {
        var store = new HistoryStore(Database);
        store.Add("первый", DateTimeOffset.Now);
        store.Add("второй", DateTimeOffset.Now.AddDays(-1));
        Assert.Equal(new[] { "второй", "первый" }, new HistoryStore(Database).List().Select(e => e.Preview));
    }

    [Fact]
    public void SearchIsUnicodeCaseInsensitiveSubstringAndDoesNotModifyHistory()
    {
        var store = new HistoryStore(Database);
        store.Add("Начало ПРИВЕТ конец", DateTimeOffset.Now);
        store.Add("other", DateTimeOffset.Now);
        Assert.Single(store.List("риве"));
        Assert.Empty(store.List("нет совпадения"));
        Assert.Empty(store.List("' OR 1=1 --"));
        Assert.Equal(2, store.List("").Count);
    }

    [Fact]
    public void RetentionKeepsExactlyFiveHundredNewestEntries()
    {
        var store = new HistoryStore(Database);
        for (var i = 0; i <= 500; i++)
            store.Add($"фрагмент {i}", DateTimeOffset.Now);
        var entries = new HistoryStore(Database).List();
        Assert.Equal(500, entries.Count);
        Assert.Equal("фрагмент 500", entries[0].Preview);
        Assert.Equal("фрагмент 1", entries[^1].Preview);
    }

    [Fact]
    public void DeletionAndClearPersistAcrossRestart()
    {
        var store = new HistoryStore(Database);
        store.Add("А", DateTimeOffset.Now);
        store.Add("Б", DateTimeOffset.Now);
        store.Delete(store.List()[0].Id);
        Assert.Equal("А", Assert.Single(new HistoryStore(Database).List()).Preview);
        store.Clear();
        Assert.Empty(new HistoryStore(Database).List());
    }

    [Theory]
    [InlineData("not a database")]
    [InlineData("")]
    public void CorruptFilesAreNotChangedAutomatically(string contents)
    {
        File.WriteAllText(Database, contents);
        var original = File.ReadAllBytes(Database);
        Assert.Throws<StorageException>(() => new HistoryStore(Database));
        Assert.Equal(original, File.ReadAllBytes(Database));
    }

    [Fact]
    public void ExplicitRecoveryPreservesOriginalFileAndCreatesEmptyDatabase()
    {
        File.WriteAllText(Database, "broken original");
        var backup = HistoryStore.RecoverExplicitly(Database);
        Assert.Equal("broken original", File.ReadAllText(System.IO.Path.Combine(backup, "history.db")));
        Assert.Empty(new HistoryStore(Database).List());
    }

    [Fact]
    public void OversizedInputCannotEnterDatabase()
    {
        var store = new HistoryStore(Database);
        Assert.Throws<ArgumentException>(() => store.Add(new string('я', CapturePolicy.MaxBytes), DateTimeOffset.Now));
        Assert.Empty(store.List());
        store.Add(new string('a', CapturePolicy.MaxBytes), DateTimeOffset.Now);
        Assert.Equal(CapturePolicy.MaxBytes, store.ReadText(Assert.Single(store.List()).Id)!.Length);
    }

    [Fact]
    public void ReadingHistoryDoesNotBlockNewClipboardEntry()
    {
        var store = new HistoryStore(Database);
        store.Add("старый текст", DateTimeOffset.Now);
        using var connection = new SqliteConnection($"Data Source={Database};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT text FROM entries";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        store.Add("новый текст во время поиска", DateTimeOffset.Now);
        Assert.Equal(2, store.Count());
    }

    [Fact]
    public void RetentionFailureRollsBackInsertion()
    {
        var store = new HistoryStore(Database);
        for (var i = 0; i < 500; i++)
            store.Add(i.ToString(), DateTimeOffset.Now);
        using var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_delete BEFORE DELETE ON entries BEGIN SELECT RAISE(ABORT, 'test failure'); END";
        command.ExecuteNonQuery();
        Assert.Throws<StorageException>(() => store.Add("не должна сохраниться", DateTimeOffset.Now));
        Assert.Equal(500, store.Count());
        Assert.Empty(store.List("не должна сохраниться"));
    }

    [Fact]
    public void UpgradePreservesEntriesAndSharedTagsSurviveDeletion()
    {
        using (var legacy = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            legacy.Open();
            using var setup = legacy.CreateCommand();
            setup.CommandText = """
                CREATE TABLE entries (id INTEGER PRIMARY KEY AUTOINCREMENT, text TEXT NOT NULL, created_at TEXT NOT NULL);
                INSERT INTO entries VALUES (7, 'Старый текст', '2026-09-30T12:00:00.0000000+03:00');
                PRAGMA user_version = 1;
                """;
            setup.ExecuteNonQuery();
        }
        var store = new HistoryStore(Database);
        Assert.Equal("Старый текст", store.ReadText(7));
        store.Add("Другой текст", DateTimeOffset.Now);
        store.SetTags(7, " Работа , Архив, Работа");
        store.SetTags(8, "Работа");
        Assert.Equal(new[] { "Архив", "Работа" }, new HistoryStore(Database).ReadTags(7));
        Assert.Throws<ArgumentException>(() => store.SetTags(7, new string('x', 41)));
        Assert.Equal(2, store.ReadTags(7).Count);
        store.Delete(7);
        Assert.Equal("Работа", Assert.Single(store.ReadTags(8)));
        store.Clear();
        using var check = new SqliteConnection($"Data Source={Database};Pooling=False");
        check.Open();
        using var query = check.CreateCommand();
        query.CommandText = "SELECT (SELECT count(*) FROM tags) + (SELECT count(*) FROM entry_tags)";
        Assert.Equal(0L, query.ExecuteScalar());
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
