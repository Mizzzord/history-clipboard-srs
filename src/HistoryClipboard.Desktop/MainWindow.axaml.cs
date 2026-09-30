using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HistoryClipboard.Core;
using HistoryClipboard.Desktop.Platform;

namespace HistoryClipboard.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _model = new();
    private readonly CapturePolicy _policy = new();
    private readonly SemaphoreSlim _databaseGate = new(1, 1);
    private readonly HashSet<Task> _pendingWork = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private HistoryStore? _store;
    private ISystemClipboard? _clipboard;
    private CancellationTokenSource? _searchCancellation;
    private bool _tickBusy;
    private bool _baselineNeeded = true;
    private bool _closing;
    private bool _canClose;
    private bool _dialogOpen;
    private int _listRevision;
    private int _selectionRevision;
    private string DatabasePath => Path.Combine(Program.DataDirectory, "history.db");
    private string SettingsPath => Path.Combine(Program.DataDirectory, "settings.json");

    public MainWindow() : this(initialize: true) { }

    internal MainWindow(bool initialize)
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = _model;
        _model.QueryChanged += SearchChanged;
        _model.SelectionChanged += async () => await LoadSelection();
        _timer.Tick += async (_, _) => await PollClipboard();
        if (initialize)
            Opened += async (_, _) => await Initialize();
        Closing += OnClosing;
        AddHandler(KeyDownEvent, HandleKeys, RoutingStrategies.Tunnel);
    }

    private async Task Initialize()
    {
        if (Program.StartupError is not null)
        {
            await Dialogs.Show(this, "Не удалось запустить приложение", Program.StartupError, "Закрыть", null);
            Close();
            return;
        }
        await OpenStorage();
        if (_closing)
            return;
        if (!WarningAcknowledged())
        {
            var accepted = await Dialogs.Show(this, "Ваш буфер — личные данные", "History Clipboard сохраняет скопированный текст только на этом устройстве. В историю могут попасть пароли и другие чувствительные данные.\n\nПеред копированием личных данных нажмите «Пауза». Ненужные записи можно удалить, а всю историю — очистить с подтверждением. История не зашифрована.\n\nСбор начнётся после нажатия «Понятно, начать».", "Понятно, начать", "Закрыть приложение");
            if (!accepted)
            {
                Close();
                return;
            }
            SaveWarningAcknowledged();
        }
        if (_closing)
            return;
        _model.Paused = _model.HasStorageError;
        _model.Ready = true;
        UpdateStatus();
        if (!_model.Paused)
            EstablishCaptureBaseline();
        _timer.Start();
        this.FindControl<TextBox>("SearchBox")?.Focus();
    }

    private async Task OpenStorage()
    {
        if (_closing)
            return;
        try
        {
            var store = await DatabaseWork(() => new HistoryStore(DatabasePath), writable: true);
            _store = store;
            _model.StorageError = "";
            await RefreshList();
        }
        catch (StorageException ex)
        {
            StorageFailed(ex);
        }
    }

    private Task<T> DatabaseWork<T>(Func<T> work, bool writable = false)
    {
        var task = writable ? SerializedDatabaseWork(work) : Task.Run(work);
        _pendingWork.Add(task);
        _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() => _pendingWork.Remove(task)), TaskScheduler.Default);
        return task;
    }

    private async Task<T> SerializedDatabaseWork<T>(Func<T> work)
    {
        await _databaseGate.WaitAsync();
        try { return await Task.Run(work); }
        finally { _databaseGate.Release(); }
    }

    private async Task RefreshList()
    {
        var store = _store;
        if (_closing || store is null)
            return;
        var revision = ++_listRevision;
        var query = _model.Query;
        try
        {
            var result = await DatabaseWork(() => (Entries: store.List(query), Total: store.Count()));
            if (_closing || revision != _listRevision || query != _model.Query)
                return;
            var selectedId = _model.Selected?.Id;
            var oldIndex = _model.Selected is null ? 0 : _model.Entries.IndexOf(_model.Selected);
            for (var index = 0; index < result.Entries.Count; index++)
            {
                var entry = result.Entries[index];
                var existing = _model.Entries.IndexOf(entry);
                if (existing < 0)
                    _model.Entries.Insert(index, entry);
                else if (existing != index)
                    _model.Entries.Move(existing, index);
            }
            while (_model.Entries.Count > result.Entries.Count)
                _model.Entries.RemoveAt(_model.Entries.Count - 1);
            _model.Total = result.Total;
            _model.Selected = result.Entries.FirstOrDefault(entry => entry.Id == selectedId)
                ?? result.Entries.ElementAtOrDefault(Math.Clamp(oldIndex, 0, Math.Max(0, result.Entries.Count - 1)));
            _model.Update();
        }
        catch (StorageException ex) { StorageFailed(ex); }
    }

    private async Task LoadSelection()
    {
        var store = _store;
        var selected = _model.Selected;
        var revision = ++_selectionRevision;
        if (_closing || store is null || selected is null)
        {
            _model.Loading = false;
            return;
        }
        try
        {
            var text = await DatabaseWork(() => store.ReadText(selected.Id));
            if (_closing || revision != _selectionRevision)
                return;
            _model.SelectedText = text ?? "";
            _model.Loading = false;
        }
        catch (StorageException ex)
        {
            _model.Loading = false;
            StorageFailed(ex);
        }
    }

    private async void SearchChanged()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        try
        {
            await Task.Delay(180, cancellation.Token);
            if (!_model.HasStorageError)
                await RefreshList();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private async Task PollClipboard()
    {
        var store = _store;
        if (_closing || _tickBusy || _model.Paused || _model.HasStorageError || store is null)
            return;
        _tickBusy = true;
        try
        {
            _clipboard ??= SystemClipboard.Create();
            var version = _clipboard.Version;
            if (_baselineNeeded)
            {
                _policy.EstablishBaseline(version);
                _baselineNeeded = false;
                UpdateStatus();
                return;
            }
            if (_policy.IsCurrentVersion(version))
            {
                UpdateStatus();
                return;
            }
            var snapshot = _clipboard.Read();
            var result = _policy.Observe(snapshot);
            UpdateStatus();
            if (result == CaptureResult.TooLarge)
                _model.Notice = "Текст больше 1 МиБ в UTF-8 и не сохранён. История осталась без изменений.";
            else if (result == CaptureResult.Accepted)
            {
                var time = DateTimeOffset.Now;
                await DatabaseWork(() => { store.Add(snapshot.Text!, time); return true; }, writable: true);
                if (!_closing)
                    await RefreshList();
            }
        }
        catch (ClipboardAccessException ex)
        {
            _model.Status = "Сбор недоступен: " + ex.Message + " Сохранённая история доступна.";
        }
        catch (StorageException ex) { StorageFailed(ex); }
        finally { _tickBusy = false; }
    }

    private void TogglePause(object? sender, RoutedEventArgs e)
    {
        if (!_model.CanToggle)
            return;
        _model.Paused = !_model.Paused;
        if (_model.Paused)
            _clipboard?.SetMonitoring(false);
        _baselineNeeded = true;
        UpdateStatus();
        if (!_model.Paused)
            EstablishCaptureBaseline();
    }

    private void EstablishCaptureBaseline()
    {
        try
        {
            _clipboard ??= SystemClipboard.Create();
            _clipboard.SetMonitoring(true);
            _policy.EstablishBaseline(_clipboard.Version);
            _baselineNeeded = false;
        }
        catch (ClipboardAccessException ex) { _model.Status = "Сбор недоступен: " + ex.Message; }
    }

    private void CopyEntry(object? sender, RoutedEventArgs e)
    {
        if (!_model.CanAct || _closing)
            return;
        try
        {
            _clipboard ??= SystemClipboard.Create();
            var text = _model.SelectedText;
            var snapshot = _clipboard.Write(text);
            if (snapshot.Text == text)
            {
                _policy.SuppressOwnCopy(snapshot);
                _model.Notice = "Полный текст скопирован. Вставьте его в нужное приложение.";
            }
            else
                _model.Notice = "Буфер изменён другим приложением сразу после копирования. Повторите действие.";
        }
        catch (ClipboardAccessException ex) { _model.Notice = "Не удалось скопировать: " + ex.Message; }
    }

    private void PreviousPage(object? sender, RoutedEventArgs e) => _model.MovePage(-1);
    private void NextPage(object? sender, RoutedEventArgs e) => _model.MovePage(1);

    private async void DeleteEntry(object? sender, RoutedEventArgs e)
    {
        var store = _store;
        var selected = _model.Selected;
        if (!_model.CanAct || _closing || store is null || selected is null)
            return;
        try
        {
            await DatabaseWork(() => { store.Delete(selected.Id); return true; }, writable: true);
            _model.Notice = "Запись удалена.";
            await RefreshList();
        }
        catch (StorageException ex) { StorageFailed(ex); }
    }

    private async void ClearHistory(object? sender, RoutedEventArgs e)
    {
        var store = _store;
        if (!_model.CanClear || _closing || _dialogOpen || store is null)
            return;
        _dialogOpen = true;
        try
        {
            if (!await Dialogs.Show(this, "Очистить всю историю?", "Все сохранённые записи будут удалены. Отменить это действие нельзя. Текст в системном буфере останется без изменений.", "Очистить историю"))
                return;
            await DatabaseWork(() => { store.Clear(); return true; }, writable: true);
            _model.Notice = "История очищена.";
            await RefreshList();
        }
        catch (StorageException ex) { StorageFailed(ex); }
        finally { _dialogOpen = false; }
    }

    private async void RetryStorage(object? sender, RoutedEventArgs e)
    {
        if (_dialogOpen || _closing)
            return;
        _dialogOpen = true;
        try
        {
            await OpenStorage();
            if (!_model.HasStorageError)
            {
                _model.Notice = "История открыта. Нажмите «Возобновить сбор», когда будете готовы.";
                UpdateStatus();
            }
        }
        finally { _dialogOpen = false; }
    }

    private async void ResetStorage(object? sender, RoutedEventArgs e)
    {
        if (_dialogOpen || _closing)
            return;
        _dialogOpen = true;
        try
        {
            if (!await Dialogs.Show(this, "Начать новую историю?", "Исходные файлы будут сохранены в резервной папке рядом с базой. Будет создана новая пустая история. Резервная копия тоже может содержать личные данные.", "Создать новую историю"))
                return;
            var backup = await DatabaseWork(() => HistoryStore.RecoverExplicitly(DatabasePath), writable: true);
            await OpenStorage();
            _model.Notice = "Новая история создана. Резервная папка: " + backup;
            _model.Paused = true;
            _baselineNeeded = true;
            UpdateStatus();
        }
        catch (StorageException ex) { StorageFailed(ex); }
        finally { _dialogOpen = false; }
    }

    private void StorageFailed(StorageException ex)
    {
        _model.StorageError = ex.Message;
        _model.Paused = true;
        _clipboard?.SetMonitoring(false);
        _model.Status = "Сбор остановлен из-за ошибки хранилища.";
        _model.Update();
    }

    private void UpdateStatus()
    {
        _model.Status = _model.HasStorageError ? "Сбор остановлен из-за ошибки хранилища."
            : _model.Paused ? "Сбор приостановлен · история доступна"
            : "Сбор включён · сохраняем новый текст из буфера";
    }

    private bool WarningAcknowledged()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return false;
            using var json = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("privacyAcknowledged", out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    private void SaveWarningAcknowledged()
    {
        var temporary = SettingsPath + ".tmp";
        try
        {
            File.WriteAllText(temporary, "{\"privacyAcknowledged\":true}");
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _model.Notice = "Не удалось сохранить настройку первого запуска. Предупреждение будет показано снова при следующем запуске.";
        }
    }

    private void HandleKeys(object? sender, KeyEventArgs e)
    {
        var primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.KeyModifiers.HasFlag(primary) && e.Key == Key.F)
        {
            this.FindControl<TextBox>("SearchBox")?.Focus();
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(primary) && e.Key == Key.Enter)
        {
            CopyEntry(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _model.Query.Length > 0)
        {
            _model.Query = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && this.FindControl<ListBox>("HistoryList") is { IsKeyboardFocusWithin: true })
        {
            DeleteEntry(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_canClose)
            return;
        e.Cancel = true;
        if (_closing)
            return;
        _closing = true;
        _timer.Stop();
        _searchCancellation?.Cancel();
        try { await Task.WhenAll(_pendingWork.ToArray()); }
        catch (StorageException ex)
        {
            await Dialogs.Show(this, "Последняя операция не сохранена", ex.Message, "Закрыть", null);
        }
        _clipboard?.Dispose();
        _canClose = true;
        Close();
    }
}
