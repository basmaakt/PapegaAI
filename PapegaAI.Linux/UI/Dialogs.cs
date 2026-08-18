using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Parrot.UI;

/// <summary>
/// Message boxes. Avalonia ships none — it targets platforms that disagree on
/// what one should look like — so these are two small windows that behave the
/// way the WinForms version's dialogs do.
/// </summary>
static class Dialogs
{
    public static Task Info(Window owner, string message) =>
        Show(owner, message, ["OK"]).ContinueWith(_ => { });

    public static async Task<bool> Confirm(Window owner, string message) =>
        await Show(owner, message, ["Ja", "Nee"]) == 0;

    static Task<int> Show(Window owner, string message, string[] buttons)
    {
        var result = new TaskCompletionSource<int>();

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 16, 0, 0),
        };

        var window = new Window
        {
            Title = "PapegaAI",
            Icon = new WindowIcon(Icons.Small),
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            MaxWidth = 480,
        };

        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var button = new Button { Content = buttons[i], MinWidth = 80 };
            button.Click += (_, _) =>
            {
                result.TrySetResult(index);
                window.Close();
            };
            if (i == 0) button.IsDefault = true;
            panel.Children.Add(button);
        }

        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                panel,
            },
        };
        window.Closed += (_, _) => result.TrySetResult(buttons.Length - 1);

        _ = window.ShowDialog(owner);
        return result.Task;
    }
}
