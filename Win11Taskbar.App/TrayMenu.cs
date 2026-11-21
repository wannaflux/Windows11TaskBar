using System.ComponentModel;
using System.Drawing;
using Win11Taskbar.Core;
using Forms = System.Windows.Forms;

namespace Win11Taskbar.App;

internal enum TrayGlyph
{
    None, Settings, Display, Layers, Startup, Tools, Restart, Update, Exit, Check, Chevron
}

internal sealed class TrayRow(string text, TrayGlyph glyph) : Forms.ToolStripMenuItem(text)
{
    public TrayGlyph Glyph { get; } = glyph;
    public string? Detail { get; set; }
    public bool Primary { get; init; }
    public bool Destructive { get; init; }
}

internal sealed class TrayHeading : Forms.ToolStripLabel
{
    public string Subtitle { get; private set; } = "";
    public bool Active { get; private set; }

    public void SetStatus(bool active, string subtitle)
    {
        Active = active;
        Subtitle = subtitle;
        Text = $"Win11Taskbar · {subtitle}";
    }
}

internal class RoundedTrayMenu : Forms.ContextMenuStrip
{
    private readonly int logicalWidth;
    private readonly TrayMenuRenderer painter = new();

    public RoundedTrayMenu(int logicalWidth)
    {
        this.logicalWidth = logicalWidth;
        ShowImageMargin = false;
        ShowCheckMargin = false;
        DropShadowEnabled = true;
        Renderer = painter;
    }

    protected override void OnOpening(CancelEventArgs e)
    {
        painter.SetTheme(TrayPalette.Read(), DeviceDpi);
        BackColor = painter.Palette.Background;
        ForeColor = painter.Palette.Text;
        int margin = painter.Scale(7);
        SuspendLayout();
        try
        {
            Padding = new Forms.Padding(margin);
            foreach (Forms.ToolStripItem item in Items)
            {
                item.AutoSize = false;
                item.Margin = new Forms.Padding(0, 1, 0, 1);
                item.Size = new Size(painter.Scale(logicalWidth) - margin * 2,
                    painter.Scale(item switch
                    {
                        TrayHeading => 62,
                        Forms.ToolStripSeparator => 9,
                        _ => 36
                    }));
            }
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }
        UpdateCorners();
        base.OnOpening(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateCorners();
    }

    private void UpdateCorners()
    {
        if (ClientSize.Width < 2 || ClientSize.Height < 2)
            return;

        using var path = TrayIcon.RoundedRectangle(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height),
            13f * DeviceDpi / 96);
        var prior = Region;
        Region = new Region(path);
        prior?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            painter.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class TrayMenu : RoundedTrayMenu
{
    private static readonly (string Value, string Label, string ShortLabel)[] Styles =
    [
        ("Windows 11", "Windows 11", "Windows 11"),
        ("Floating", "Парящая", "Парящая"),
        ("Transparent", "Прозрачная", "Прозрачная"),
        ("Acrylic", "Акрил", "Акрил"),
        ("Solid", "Сплошная", "Сплошная")
    ];

    private readonly Settings applied;
    private readonly Settings draft;
    private readonly Func<bool> hasPendingChanges;
    private readonly TrayRow open = new("Открыть настройки", TrayGlyph.Settings) { Primary = true };
    private readonly TrayHeading heading = new();
    private readonly TrayRow toggle = new("Приостановить оформление", TrayGlyph.Display);
    private readonly TrayRow style = new("Стиль панели", TrayGlyph.Layers);
    private readonly TrayRow autostart = new("Запускать с Windows", TrayGlyph.Startup);
    private readonly RoundedTrayMenu profiles = new(218);
    private readonly RoundedTrayMenu service = new(252);
    private readonly Dictionary<string, TrayRow> profileRows = new();

    public TrayMenu(Settings applied, Settings draft, Func<bool> hasPendingChanges,
        Action showSettings, Action restartExplorer, Action restartApp,
        Func<Task> checkUpdates, Action exit) : base(302)
    {
        this.applied = applied;
        this.draft = draft;
        this.hasPendingChanges = hasPendingChanges;

        open.Click += (_, _) => showSettings();
        toggle.Click += (_, _) =>
        {
            draft.Enabled = !draft.Enabled;
            showSettings();
        };
        autostart.Click += (_, _) =>
        {
            draft.StartWithWindows = !draft.StartWithWindows;
            showSettings();
        };

        foreach (var (value, label, _) in Styles)
        {
            var choice = new TrayRow(label, TrayGlyph.None);
            choice.Click += (_, _) =>
            {
                draft.TaskbarStyle = value;
                showSettings();
            };
            profileRows.Add(value, choice);
            profiles.Items.Add(choice);
        }
        style.DropDown = profiles;

        var tools = new TrayRow("Обслуживание", TrayGlyph.Tools) { DropDown = service };
        service.Items.Add(Command("Проверить обновления", TrayGlyph.Update, async () => await checkUpdates()));
        service.Items.Add(new Forms.ToolStripSeparator());
        service.Items.Add(Command("Перезапустить Explorer…", TrayGlyph.Restart, restartExplorer));
        service.Items.Add(Command("Перезапустить Win11Taskbar", TrayGlyph.Restart, restartApp));
        var quit = new TrayRow("Выход", TrayGlyph.Exit) { Destructive = true };
        quit.Click += (_, _) => exit();

        Items.AddRange([heading, open, toggle, style, new Forms.ToolStripSeparator(),
            autostart, tools, new Forms.ToolStripSeparator(), quit]);
    }

    protected override void OnOpening(CancelEventArgs e)
    {
        bool previewOnly = Environment.OSVersion.Version.Build >= 22000;
        heading.SetStatus(applied.Enabled && !previewOnly, previewOnly
            ? "Режим предпросмотра" : applied.Enabled ? "Оформление включено" : "Панель Windows");
        open.Detail = hasPendingChanges() ? "Черновик" : null;
        toggle.Text = draft.Enabled ? "Приостановить оформление" : "Включить оформление";
        toggle.Enabled = !previewOnly;
        toggle.Checked = draft.Enabled && !previewOnly;
        autostart.Checked = draft.StartWithWindows;
        style.Detail = Styles.FirstOrDefault(p => p.Value == draft.TaskbarStyle).ShortLabel;
        foreach (var (value, row) in profileRows)
            row.Checked = value == draft.TaskbarStyle;
        base.OnOpening(e);
    }

    private static TrayRow Command(string label, TrayGlyph glyph, Action action)
    {
        var item = new TrayRow(label, glyph);
        item.Click += (_, _) => action();
        return item;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            profiles.Dispose();
            service.Dispose();
        }
        base.Dispose(disposing);
    }
}
