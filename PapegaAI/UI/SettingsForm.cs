using System.Windows.Forms;
using Parrot.History;
using Parrot.Models;

namespace Parrot.UI;

/// <summary>
/// Klein instellingen- en geschiedenisvenster, geopend vanuit het
/// tray-menu. Opslaan schrijft config.json; de daemon past hotkey en overlay
/// direct toe en herstart zichzelf bij een model- of taalwijziging.
/// </summary>
sealed class SettingsForm : Form
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
        "right-ctrl", "left-ctrl", "right-alt", "right-shift",
        "caps-lock", "scroll-lock", "f13", "f14", "f15",
    ];

    readonly ComboBox modelBox = MakeCombo();
    readonly ComboBox languageBox = MakeCombo();
    readonly ComboBox hotkeyBox = MakeCombo();
    readonly CheckBox gpuBox = new() { Text = "Videokaartversnelling (GPU) gebruiken indien beschikbaar", AutoSize = true };
    readonly CheckBox overlayBox = new() { Text = "Overlay (pilletje) tonen tijdens opname", AutoSize = true };
    readonly CheckBox autostartBox = new() { Text = "PapegaAI starten bij inloggen", AutoSize = true };
    readonly CheckBox clearHistoryBox = new() { Text = "Geschiedenis wissen na herstart van de computer", AutoSize = true };
    readonly ListView historyList = new()
    {
        View = View.Details,
        FullRowSelect = true,
        Dock = DockStyle.Fill,
        MultiSelect = false,
    };

    readonly TabControl tabs = new() { Dock = DockStyle.Fill };

    readonly HistoryStore history;
    readonly Action<Config> onSave;
    readonly string runtime;
    // Config-only override zonder UI; ongewijzigd doorgeven bij opslaan.
    readonly string? cpuModelPassthrough;

    sealed record ModelItem(TranscriptionModel Model)
    {
        public override string ToString()
        {
            string cached = ModelDownloader.IsCached(Model) ? " · gedownload" : "";
            string lang = Model.Languages.Contains("multi") ? "meertalig" : "alleen Engels";
            return $"{Model.Id} — {Model.SizeMB} MB · {lang}{cached}";
        }
    }

    public SettingsForm(Config current, string runtime, HistoryStore history, Action<Config> onSave)
    {
        this.history = history;
        this.onSave = onSave;
        this.runtime = runtime;
        cpuModelPassthrough = current.CpuModel;

        Text = "PapegaAI — instellingen";
        // FixedSingle in plaats van FixedDialog: die laatste verbergt het
        // titelbalk-icoontje, en de papegaai verdient een plek daar.
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = true;
        Icon = TrayController.DrawIcon(recording: false);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 505);

        tabs.TabPages.Add(BuildSettingsTab(current));
        tabs.TabPages.Add(BuildHistoryTab());
        Controls.Add(tabs);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            Padding = new Padding(8),
        };
        var save = new Button { Text = "Opslaan", AutoSize = true };
        var close = new Button { Text = "Sluiten", AutoSize = true };
        save.Click += (_, _) => SaveClicked();
        close.Click += (_, _) => Close();
        buttons.Controls.Add(save);
        buttons.Controls.Add(close);
        Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = close;
    }

    static ComboBox MakeCombo() => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Dock = DockStyle.Fill,
    };

    TabPage BuildSettingsTab(Config current)
    {
        foreach (var m in ModelRegistry.All)
            modelBox.Items.Add(new ModelItem(m));
        string currentModel = current.Model ?? ModelRegistry.Recommended().Id;
        modelBox.SelectedIndex = Math.Max(0, Array.FindIndex(
            ModelRegistry.All, m => m.Id == currentModel));

        foreach (var (code, label) in LanguageChoices)
            languageBox.Items.Add($"{code} — {label}");
        string currentLanguage = current.Language ?? "auto";
        int langIndex = Array.FindIndex(LanguageChoices, c => c.Code == currentLanguage);
        if (langIndex < 0)
        {
            languageBox.Items.Add(currentLanguage);
            langIndex = languageBox.Items.Count - 1;
        }
        languageBox.SelectedIndex = langIndex;

        foreach (string k in HotkeyChoices)
            hotkeyBox.Items.Add(k);
        string currentHotkey = current.Hotkey ?? "right-ctrl";
        int hotkeyIndex = Array.IndexOf(HotkeyChoices, currentHotkey);
        if (hotkeyIndex < 0)
        {
            hotkeyBox.Items.Add(currentHotkey);
            hotkeyIndex = hotkeyBox.Items.Count - 1;
        }
        hotkeyBox.SelectedIndex = hotkeyIndex;

        gpuBox.Checked = current.Gpu ?? true;
        overlayBox.Checked = current.Overlay ?? true;
        autostartBox.Checked = Install.IsEnabled();
        clearHistoryBox.Checked = current.ClearHistoryOnReboot ?? false;

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(12),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(string label, Control control)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            grid.Controls.Add(new Label
            {
                Text = label,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
            });
            grid.Controls.Add(control);
        }

        AddRow("Model", modelBox);
        AddRow("Taal", languageBox);
        AddRow("Push-to-talk-toets", hotkeyBox);
        AddRow("", gpuBox);
        AddRow("", overlayBox);
        AddRow("", autostartBox);
        AddRow("", clearHistoryBox);

        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(new PictureBox
        {
            Image = TrayController.DrawBitmap(96, recording: false),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(96, 96),
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Margin = new Padding(8, 12, 0, 0),
        });
        grid.Controls.Add(new Label
        {
            // Geen handmatige regeleindes: het label breekt zelf af op de
            // beschikbare breedte.
            Text = $"Actieve runtime: {runtime}.\n\n" +
                   "Zonder geschikte videokaart valt PapegaAI bij grote modellen automatisch terug " +
                   "op whisper-small; zet je het GPU-vinkje zélf uit, dan blijft je modelkeuze staan. " +
                   "Een wijziging van model, taal of GPU herstart PapegaAI even (een nieuw model wordt " +
                   "eerst gedownload). Engelstalige modellen (.en) negeren de taalkeuze.",
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
        });

        return new TabPage("Instellingen") { Controls = { grid } };
    }

    TabPage BuildHistoryTab()
    {
        historyList.Columns.Add("Tijd", 110);
        historyList.Columns.Add("Duur", 55);
        historyList.Columns.Add("Tekst", 340);
        historyList.DoubleClick += (_, _) => CopySelected();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            Height = 40,
            Padding = new Padding(4),
        };
        var copy = new Button { Text = "Kopiëren", AutoSize = true };
        var clear = new Button { Text = "Geschiedenis wissen", AutoSize = true };
        copy.Click += (_, _) => CopySelected();
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "Alle opgeslagen transcripties wissen?", "PapegaAI",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                history.Clear();
                RefreshHistory();
            }
        };
        buttons.Controls.Add(copy);
        buttons.Controls.Add(clear);

        var page = new TabPage("Geschiedenis");
        page.Controls.Add(historyList);
        page.Controls.Add(buttons);
        RefreshHistory();
        return page;
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
        historyList.BeginUpdate();
        historyList.Items.Clear();
        foreach (var e in history.Newest())
        {
            var item = new ListViewItem(e.Time.ToString("dd-MM HH:mm"));
            item.SubItems.Add($"{e.Seconds:0.0}s");
            item.SubItems.Add(e.Text);
            item.Tag = e.Text;
            historyList.Items.Add(item);
        }
        historyList.EndUpdate();
    }

    void CopySelected()
    {
        if (historyList.SelectedItems.Count == 0) return;
        if (historyList.SelectedItems[0].Tag is string text && text.Length > 0)
            Clipboard.SetText(text);
    }

    void SaveClicked()
    {
        var model = ((ModelItem)modelBox.SelectedItem!).Model;
        string language = languageBox.SelectedItem!.ToString()!.Split(' ')[0];
        string hotkey = hotkeyBox.SelectedItem!.ToString()!;

        if (autostartBox.Checked) Install.Enable();
        else Install.Disable();

        if (!ModelDownloader.IsCached(model))
        {
            MessageBox.Show(this,
                $"Het model {model.Id} ({model.SizeMB} MB) wordt eerst gedownload — " +
                "PapegaAI is daarna pas weer beschikbaar (voortgang niet zichtbaar op de achtergrond).",
                "PapegaAI", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        onSave(new Config
        {
            Model = model.Id,
            CpuModel = cpuModelPassthrough,
            Gpu = gpuBox.Checked,
            Language = language,
            Hotkey = hotkey,
            Overlay = overlayBox.Checked,
            ClearHistoryOnReboot = clearHistoryBox.Checked,
        });
        Close();
    }
}
