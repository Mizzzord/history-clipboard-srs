using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using HistoryClipboard.Core;

namespace HistoryClipboard.Desktop;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _query = "";
    private HistoryEntry? _selected;
    private string _selectedText = "";
    private string _status = "Открываем историю…";
    private string _notice = "";
    private string _storageError = "";
    private bool _paused = true;
    private bool _ready;
    private bool _loading;
    private int _total;
    private const int PageSize = 8192;
    private readonly List<(int Start, int Length)> _pages = [];
    private int _page;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? QueryChanged;
    public event Action? SelectionChanged;
    public ObservableCollection<HistoryEntry> Entries { get; } = [];

    public string Query
    {
        get => _query;
        set { if (Set(ref _query, value ?? "")) { Update(); QueryChanged?.Invoke(); } }
    }
    public HistoryEntry? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { SelectedText = ""; Loading = value is not null; Update(); SelectionChanged?.Invoke(); } }
    }
    public string SelectedText
    {
        get => _selectedText;
        set
        {
            if (!Set(ref _selectedText, value ?? ""))
                return;
            _pages.Clear();
            _page = 0;
            for (var start = 0; start < _selectedText.Length;)
            {
                var end = Math.Min(start + PageSize, _selectedText.Length);
                if (end < _selectedText.Length && (char.IsHighSurrogate(_selectedText[end - 1]) && char.IsLowSurrogate(_selectedText[end])
                    || _selectedText[end - 1] == '\r' && _selectedText[end] == '\n'))
                    end--;
                _pages.Add((start, end - start));
                start = end;
            }
            UpdatePages();
        }
    }
    public string VisibleText => _pages.Count == 0 ? "" : SelectedText.Substring(_pages[_page].Start, _pages[_page].Length);
    public bool ShowPages => _pages.Count > 1;
    public bool CanPreviousPage => _page > 0;
    public bool CanNextPage => _page + 1 < _pages.Count;
    public string PageLabel => $"Часть {_page + 1} из {_pages.Count} · копируется весь текст";
    public void MovePage(int delta)
    {
        _page = Math.Clamp(_page + delta, 0, Math.Max(0, _pages.Count - 1));
        UpdatePages();
    }
    private void UpdatePages()
    {
        foreach (var name in new[] { nameof(VisibleText), nameof(ShowPages), nameof(CanPreviousPage), nameof(CanNextPage), nameof(PageLabel) })
            Notify(name);
    }
    public string SelectedDate => Selected?.DisplayDate ?? "";
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Notice { get => _notice; set { Set(ref _notice, value); Notify(nameof(HasNotice)); } }
    public string StorageError { get => _storageError; set { Set(ref _storageError, value); Update(); } }
    public bool Paused { get => _paused; set { Set(ref _paused, value); Update(); } }
    public bool Ready { get => _ready; set { Set(ref _ready, value); Update(); } }
    public bool Loading { get => _loading; set { Set(ref _loading, value); Update(); } }
    public int Total { get => _total; set { Set(ref _total, value); Update(); } }
    public bool HasNotice => Notice.Length > 0;
    public bool HasStorageError => StorageError.Length > 0;
    public bool HasSelection => Selected is not null;
    public bool NoSelection => !HasSelection;
    public bool CanAct => Ready && !HasStorageError && HasSelection && !Loading;
    public bool CanClear => Ready && !HasStorageError && Total > 0;
    public bool CanToggle => Ready && !HasStorageError;
    public string PauseLabel => Paused ? "Возобновить сбор" : "Пауза";
    public string CountLabel => Query.Length == 0 ? $"{Total} / 500" : $"{Entries.Count} из {Total}";
    public bool ShowEmpty => Entries.Count == 0;
    public string EmptyTitle => !Ready ? "Открываем историю…" : HasStorageError ? "История недоступна" : Total == 0 ? "История пуста" : "Ничего не найдено";
    public string EmptyHint => !Ready ? "Это займёт немного времени." : HasStorageError ? "Проверьте сообщение об ошибке ниже." : Total == 0 ? "Скопируйте текст в другом приложении. Он появится здесь, когда сбор включён." : "Попробуйте другой запрос или очистите поиск.";

    public void Update()
    {
        foreach (var property in new[] { nameof(SelectedDate), nameof(HasSelection), nameof(NoSelection), nameof(CanAct), nameof(CanClear), nameof(CanToggle), nameof(PauseLabel), nameof(CountLabel), nameof(ShowEmpty), nameof(EmptyTitle), nameof(EmptyHint), nameof(HasStorageError) })
            Notify(property);
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Notify(property);
        return true;
    }
    private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
