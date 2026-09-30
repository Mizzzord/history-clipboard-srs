using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace HistoryClipboard.Desktop;

internal static class Dialogs
{
    public static Task<bool> Show(Window owner, string title, string message, string accept, string? cancel = "Отмена")
    {
        var dialog = new Window
        {
            Title = title,
            Width = 500,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        Button? cancelButton = null;
        if (cancel is not null)
        {
            cancelButton = new Button { Content = cancel, IsCancel = true };
            cancelButton.Click += (_, _) => dialog.Close(false);
            buttons.Children.Add(cancelButton);
        }
        var acceptButton = new Button { Content = accept };
        acceptButton.Classes.Add("primary");
        acceptButton.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(acceptButton);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(26), Spacing = 22,
            Children =
            {
                new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 23 },
                buttons
            }
        };
        dialog.Opened += (_, _) => (cancelButton ?? acceptButton).Focus();
        return dialog.ShowDialog<bool>(owner);
    }
}
