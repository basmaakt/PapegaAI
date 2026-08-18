using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Parrot.History;
using Parrot.Models;
using Parrot.Platform;

namespace Parrot.UI;

/// <summary>
/// Instellingen- en geschiedenisvenster, geopend vanuit het tray-menu.
/// Opslaan schrijft config.json; de daemon past sneltoets en overlay direct
/// toe en herstart zichzelf bij een model-, taal- of GPU-wijziging.
///
/// Bevat dezelfde keuzes als de Windows-versie, plus de dingen die alleen op
/// Linux een keuze zíjn: hoe tekst wordt ingevoegd en hoe de sneltoets wordt
/// gevolgd.
/// </summary>
sealed class SettingsWindow : Window
{
    /// <summary>Volgorde waarin de tabbladen worden toegevoegd.</summary>
    const int HistoryTabIndex = 1;

    static readonly (string Code, string Label)[] LanguageChoices =
    [
        ("auto", "Automatisch detecteren"),
        ("nl", "Nederlands"),
        ("en", "Engels"),
        ("de", "Duits"),
        ("fr", "Frans"),
        ("es", "Spaans"),
    ];

    static readonly string[] HotkeyChoices =
    [
        "right-ctrl", "left-ctrl", "right-alt", "right-shift", "left-shift",
        "right-super", "caps-lock", "scroll-lock", "f13", "f14", "f15",
    ];

    static readonly (string Value, string Label)[] InjectionChoices =
    [
        ("auto", "Automatisch (aanbevolen)"),
        ("xdotool", "xdotool — typt tekens (alleen X11)"),
        ("wtype", "wtype — typt tekens (Wayland: wlroots/KDE)"),
        ("ydotool", "ydotool — via de ydotoold-daemon"),
        ("uinput", "uinput — klembord + Ctrl+V (werkt overal)"),
        ("clipboard", "alleen klembord — je plakt zelf"),
    ];

    static readonly (string Value, string Label)[] PasteChoices =
    [
        ("ctrl+v", "Ctrl+V (normale vensters)"),
        ("ctrl+shift+v", "Ctrl+Shift+V (terminals)"),
    ];

    static readonly (string Value, string Label)[] BackendChoices =
    [
        ("auto", "Automatisch (aanbevolen)"),
        ("x11", "X11 RECORD — geen rechten nodig"),
        ("evdev", "evdev — werkt ook op Wayland"),
    ];

    readonly ComboBox modelBox = new();
    readonly ComboBox languageBox = new();
    readonly ComboBox hotkeyBox = new();
    readonly ComboBox injectionBox = new();
    readonly ComboBox pasteBox = new();
    readonly ComboBox backendBox = new();
    readonly CheckBox gpuBox = new() { Content = "Videokaartversnelling (GPU) gebruiken indien beschikbaar" };
    readonly CheckBox overlayBox = new() { Content = "Overlay (pilletje) tonen tijdens opname" };
    readonly CheckBox autostartBox = new() { Content = "PapegaAI starten bij inloggen" };
    readonly CheckBox clearHistoryBox = new() { Content = "Geschiedenis wissen na herstart van de computer" };
    readonly CheckBox leadingSpaceBox = new() { Content = "Spatie voor de tekst zetten (sluit aan bij dicteren op dicteren)" };
    readonly ListBox historyList = new();
    readonly TabControl tabs = new();

    readonly HistoryStore history;
    readonly Action<Config> onSave;
    readonly IAutostart autostart;
    // Config-only override zonder UI; ongewijzigd doorgeven bij opslaan.
    readonly string? cpuModelPassthrough;

    sealed record HistoryRow(string Time, string Duration, string Text);

    public SettingsWindow(
        Config current,
        string runtime,
        string hotkeyMechanism,
        string injectionMechanism,
        HistoryStore history,
        IAutostart autostart,
        Action<Config> onSave)
    {
        this.history = history;
        this.onSave = onSave;
        this.autostart = autostart;
        cpuModelPassthrough = current.CpuModel;

        Title = "PapegaAI — instellingen";
        Icon = new WindowIcon(Icons.Small);
        Width = 620;
        Height = 560;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        tabs.Items.Add(new TabItem
        {
            Header = "Instellingen",
            Content = BuildSettingsTab(current, runtime, hotkeyMechanism, injectionMechanism),
        });
        tabs.Items.Add(new TabItem { Header = "Geschiedenis", Content = BuildHistoryTab() });

        var save = new Button { Content = "Opslaan", MinWidth = 90, IsDefault = true };
        var close = new Button { Content = "Sluiten", MinWidth = 90, IsCancel = true };
        save.Click += async (_, _) => await SaveClicked();
        close.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(12, 8, 12, 12),
            Children = { close, save },
        };

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(tabs);
        Content = root;
    }

    Control BuildSettingsTab(Config current, string runtime, string hotkeyMechanism, string injectionMechanism)
    {
        Fill(modelBox, ModelRegistry.All.Select(Describe).ToArray(),
             Array.FindIndex(ModelRegistry.All,
                 m => m.Id == (current.Model ?? ModelRegistry.Recommended().Id)));

        FillPairs(languageBox, LanguageChoices, current.Language ?? "auto");
        Fill(hotkeyBox, HotkeyChoices, Array.IndexOf(HotkeyChoices, current.Hotkey ?? HotkeyNames.Default));
        FillPairs(injectionBox, InjectionChoices, current.Injection ?? "auto");
        FillPairs(pasteBox, PasteChoices, current.PasteShortcut ?? "ctrl+v");
        FillPairs(backendBox, BackendChoices, current.HotkeyBackend ?? "auto");

        gpuBox.IsChecked = current.Gpu ?? true;
        overlayBox.IsChecked = current.Overlay ?? true;
        autostartBox.IsChecked = autostart.IsEnabled;
        clearHistoryBox.IsChecked = current.ClearHistoryOnReboot ?? false;
        leadingSpaceBox.IsChecked = current.LeadingSpace ?? Parrot.Transcription.OutputFormatting.LeadingSpaceByDefault;

        var grid = new Grid
        {
            Margin = new Thickness(16),
            ColumnDefinitions = ColumnDefinitions.Parse("170,*"),
        };

        int row = 0;
        void AddRow(string label, Control control)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            if (label.Length > 0)
            {
                var text = new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 6, 8, 6),
                };
                Grid.SetRow(text, row);
                Grid.SetColumn(text, 0);
                grid.Children.Add(text);
            }
            control.Margin = new Thickness(0, 6, 0, 6);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, label.Length > 0 ? 1 : 0);
            Grid.SetColumnSpan(control, label.Length > 0 ? 1 : 2);
            grid.Children.Add(control);
            row++;
        }

        AddRow("Model", modelBox);
        AddRow("Taal", languageBox);
        AddRow("Push-to-talk-toets", hotkeyBox);
        AddRow("Sneltoets uitlezen", backendBox);
        AddRow("Tekst invoegen", injectionBox);
        AddRow("Plaksneltoets", pasteBox);
        AddRow("", gpuBox);
        AddRow("", overlayBox);
        AddRow("", autostartBox);
        AddRow("", clearHistoryBox);
        AddRow("", leadingSpaceBox);

        var status = new TextBlock
        {
            Text = $"Sessie: {LinuxSession.Describe()}\n" +
                   $"Runtime: {runtime}\n" +
                   $"Sneltoets via: {hotkeyMechanism}\n" +
                   $"Tekst invoegen via: {injectionMechanism}",
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        };
        AddRow("", status);

        var explanation = new TextBlock
        {
            Text = "Zonder geschikte videokaart valt PapegaAI bij grote modellen automatisch terug op " +
                   "whisper-small; zet je het GPU-vinkje zélf uit, dan blijft je modelkeuze staan. Een " +
                   "wijziging van model, taal of GPU herstart PapegaAI even (een nieuw model wordt eerst " +
                   "gedownload). Engelstalige modellen (.en) negeren de taalkeuze. De plaksneltoets geldt " +
                   "alleen voor de klembord-methodes.",
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };
        AddRow("", explanation);

        var bird = new Image
        {
            Source = Icons.Idle,
            Width = 72,
            Height = 72,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 12, 0, 0),
        };
        AddRow("", bird);

        return new ScrollViewer { Content = grid };
    }

    Control BuildHistoryTab()
    {
        historyList.ItemTemplate = new FuncDataTemplate<HistoryRow>((row, _) =>
        {
            var line = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("110,60,*") };
            var time = new TextBlock { Text = row.Time, Opacity = 0.7 };
            var duration = new TextBlock { Text = row.Duration, Opacity = 0.7 };
            var text = new TextBlock { Text = row.Text, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(time, 0);
            Grid.SetColumn(duration, 1);
            Grid.SetColumn(text, 2);
            line.Children.Add(time);
            line.Children.Add(duration);
            line.Children.Add(text);
            return line;
        });
        historyList.DoubleTapped += (_, _) => CopySelected();

        var copy = new Button { Content = "Kopiëren" };
        var clear = new Button { Content = "Geschiedenis wissen" };
        copy.Click += (_, _) => CopySelected();
        clear.Click += async (_, _) =>
        {
            if (await Dialogs.Confirm(this, "Alle opgeslagen transcripties wissen?"))
            {
                history.Clear();
                RefreshHistory();
            }
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(12, 8, 12, 12),
            Children = { copy, clear },
        };

        var panel = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(historyList);
        RefreshHistory();
        return panel;
    }

    /// <summary>Open meteen op het geschiedenis-tabblad — het tray-menu heeft
    /// daar een eigen ingang voor.</summary>
    public void ShowHistory()
    {
        tabs.SelectedIndex = HistoryTabIndex;
        RefreshHistory();
    }

    public void RefreshHistory()
    {
        historyList.ItemsSource = history.Newest()
            .Select(e => new HistoryRow(e.Time.ToString("dd-MM HH:mm"), $"{e.Seconds:0.0}s", e.Text))
            .ToList();
    }

    async void CopySelected()
    {
        if (historyList.SelectedItem is not HistoryRow row || row.Text.Length == 0) return;
        if (Avalonia.Controls.TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(row.Text);
    }

    static string Describe(TranscriptionModel model)
    {
        string cached = ModelDownloader.IsCached(model) ? " · gedownload" : "";
        string lang = model.Languages.Contains("multi") ? "meertalig" : "alleen Engels";
        return $"{model.Id} — {model.SizeMB} MB · {lang}{cached}";
    }

    static void Fill(ComboBox box, string[] items, int selected)
    {
        box.ItemsSource = items;
        box.SelectedIndex = Math.Max(0, selected);
    }

    /// <summary>Fill a combo with "value — label" pairs, adding the current
    /// value as an extra entry when it is something we do not offer (someone
    /// hand-edited config.json).</summary>
    static void FillPairs(ComboBox box, (string Value, string Label)[] choices, string current)
    {
        var items = choices.Select(c => $"{c.Value} — {c.Label}").ToList();
        int index = Array.FindIndex(choices, c => c.Value == current);
        if (index < 0)
        {
            items.Add(current);
            index = items.Count - 1;
        }
        box.ItemsSource = items;
        box.SelectedIndex = index;
    }

    static string ValueOf(ComboBox box) =>
        ((string)box.SelectedItem!).Split(' ')[0];

    async Task SaveClicked()
    {
        var model = ModelRegistry.All[modelBox.SelectedIndex];

        if (autostartBox.IsChecked == true) autostart.Enable();
        else autostart.Disable();

        if (!ModelDownloader.IsCached(model))
        {
            await Dialogs.Info(this,
                $"Het model {model.Id} ({model.SizeMB} MB) wordt eerst gedownload — " +
                "PapegaAI is daarna pas weer beschikbaar (voortgang niet zichtbaar op de achtergrond).");
        }

        onSave(new Config
        {
            Model = model.Id,
            CpuModel = cpuModelPassthrough,
            Gpu = gpuBox.IsChecked ?? true,
            Language = ValueOf(languageBox),
            Hotkey = (string)hotkeyBox.SelectedItem!,
            Overlay = overlayBox.IsChecked ?? true,
            ClearHistoryOnReboot = clearHistoryBox.IsChecked ?? false,
            LeadingSpace = leadingSpaceBox.IsChecked ?? true,
            Injection = ValueOf(injectionBox),
            PasteShortcut = ValueOf(pasteBox),
            HotkeyBackend = ValueOf(backendBox),
        });
        Close();
    }
}
