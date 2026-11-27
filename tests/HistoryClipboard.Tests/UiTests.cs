using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using HistoryClipboard.Core;
using HistoryClipboard.Desktop;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(HistoryClipboard.Tests.TestAppBuilder))]

namespace HistoryClipboard.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class UiTests
{
    [Fact]
    public void LongTextPagesPreserveSurrogatePairsCrLfAndFullCopySource()
    {
        var model = new MainViewModel();
        var text = new string('a', 8191) + "👋" + new string('я', 8190) + "\r\n" + new string('b', 1_000_000);
        model.SelectedText = text;
        var parts = new List<string>();
        do
        {
            parts.Add(model.VisibleText);
            Assert.InRange(model.VisibleText.Length, 1, 8192);
            Assert.False(char.IsHighSurrogate(model.VisibleText[^1]));
            Assert.False(char.IsLowSurrogate(model.VisibleText[0]));
            Assert.NotEqual('\r', model.VisibleText[^1]);
            if (!model.CanNextPage)
                break;
            model.MovePage(1);
        } while (true);
        Assert.Equal(text, string.Concat(parts));
        Assert.Equal(text, model.SelectedText);
        Assert.True(model.ShowPages);
    }

    [AvaloniaTheory]
    [InlineData(760, 520)]
    [InlineData(1060, 720)]
    public async Task EditTagsForSelectedEntry(int width, int height)
    {
        var temporary = Directory.CreateTempSubdirectory();
        try
        {
            var store = new HistoryStore(Path.Combine(temporary.FullName, "history.db"));
            store.Add("Тест", DateTimeOffset.Now);
            var window = new MainWindow(initialize: false, store: store) { Width = width, Height = height };
            window.Show();
            try
            {
                var model = (MainViewModel)window.DataContext!;
                var save = window.FindControl<Button>("SaveTagsButton")!;
                Assert.False(save.IsEffectivelyEnabled);
                var entry = Assert.Single(store.List());
                model.Entries.Add(entry);
                model.Ready = true;
                model.Selected = entry;
                await WaitUntil(() => !model.Loading);
                var input = window.FindControl<TextBox>("TagInput")!;
                Assert.True(input.Bounds.Width > 0 && save.Bounds.Width > 0);
                input.Focus();
                window.KeyTextInput("Работа, Архив");
                save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                await WaitUntil(() => model.Notice == "Метки сохранены.");
                Assert.Equal(new[] { "Архив", "Работа" }, store.ReadTags(entry.Id));
            }
            finally { window.Close(); }
        }
        finally { temporary.Delete(true); }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    [AvaloniaFact]
    public void CopyShortcutDoesNotToggleSelectedListItem()
    {
        var window = new MainWindow(initialize: false);
        window.Show();
        var model = (MainViewModel)window.DataContext!;
        var entry = new HistoryEntry(1, "проверка", DateTimeOffset.Now);
        model.Entries.Add(entry);
        model.Selected = entry;
        Dispatcher.UIThread.RunJobs();
        var list = window.FindControl<ListBox>("HistoryList")!;
        list.ContainerFromIndex(0)!.Focus();
        var primary = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        window.KeyPressQwerty(PhysicalKey.Enter, primary);
        window.KeyReleaseQwerty(PhysicalKey.Enter, primary);
        Assert.Equal(entry, model.Selected);
        Assert.Equal(entry, list.SelectedItem);
        window.Close();
    }

    [AvaloniaFact]
    public void SearchShortcutAndEscapeWorkWithKeyboard()
    {
        var window = new MainWindow(initialize: false);
        window.Show();
        var model = (MainViewModel)window.DataContext!;
        var primary = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        window.KeyPressQwerty(PhysicalKey.F, primary);
        window.KeyReleaseQwerty(PhysicalKey.F, primary);
        Assert.True(window.FindControl<TextBox>("SearchBox")!.IsFocused);
        window.KeyTextInput("ПРИВЕТ");
        Assert.Equal("ПРИВЕТ", model.Query);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Equal("", model.Query);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ClearConfirmationStartsOnCancelAndEscapeCancels()
    {
        var owner = new MainWindow(initialize: false);
        owner.Show();
        var task = Dialogs.Show(owner, "Очистить всю историю?", "Тестовые записи", "Очистить историю");
        var dialog = Assert.Single(owner.OwnedWindows);
        var focused = dialog.FocusManager!.GetFocusedElement();
        Assert.Equal("Отмена", Assert.IsType<Button>(focused).Content);
        dialog.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(await task);
        owner.Close();
    }
}
