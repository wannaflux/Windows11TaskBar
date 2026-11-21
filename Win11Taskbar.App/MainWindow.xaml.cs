using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Win11Taskbar.Core;
using Win11Taskbar.Native;

namespace Win11Taskbar.App;

public partial class MainWindow : Window
{
    readonly Settings settings;
    readonly Settings appliedSettings;
    readonly TaskbarService engine;
    readonly SettingsService store;
    readonly Dictionary<string, RadioButton> navigation = new();
    readonly Dictionary<string, RadioButton> presetOptions = new();
    readonly string presetGroup = Guid.NewGuid().ToString("N");
    readonly string navGroup = Guid.NewGuid().ToString("N");
    readonly uint taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    string page = "Appearance";
    bool updatingDraft;
    bool displayedPendingChanges;
    DispatcherOperation? workAreaUpdate;
    bool? lastLightTheme;
    string? lastAccent;
    (bool Light, bool Start, bool Search, bool View, bool Tray, bool Clock, double Size, string Accent) previewContent;

    static readonly (string Key, string Title, string Subtitle)[] Pages =
    [
        ("General", "Основные", "Автозапуск, работа в трее и поведение приложения"),
        ("Appearance", "Внешний вид", "Выберите стиль, цвет и прозрачность панели"),
        ("Taskbar", "Панель задач", "Расположение и видимость элементов"),
        ("Animation", "Анимация", "Плавность и скорость перемещения значков"),
        ("Advanced", "Дополнительно", "Настройки Explorer, журнал и восстановление"),
        ("About", "О программе", "Версия, совместимость и обновления")
    ];

    public MainWindow(Settings settings, TaskbarService engine, SettingsService store)
    {
        InitializeComponent();
        appliedSettings = settings;
        this.settings = SettingsDraft.Clone(appliedSettings);
        this.engine = engine;
        this.store = store;
        DataContext = this.settings;
        this.settings.PropertyChanged += OnSettingChanged;
        engine.StatusChanged += OnStatusChanged;
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle).AddHook(WndProc);
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && appliedSettings.MinimizeToTray) Hide();
            MaximizeGlyph.Data = Geometry.Parse(WindowState == WindowState.Maximized
                ? "M2,2 V0 H10 V8 H8 M0,2 H8 V10 H0Z" : "M0,0 H9 V9 H0Z");
            ScheduleWorkAreaUpdate();
        };
        Closing += OnClosing;
        UpdateTheme();
        CreateNavigation();
        ShowPage("Appearance");
        UpdateDraftStatus();
    }

    internal Settings DraftSettings => settings;
    internal bool HasPendingChanges => !SettingsDraft.Equal(settings, appliedSettings);
    internal event EventHandler? PendingChangesChanged;

    void CreateNavigation()
    {
        foreach (var section in Pages)
        {
            var option = new RadioButton { Style = FindStyle("NavigationOption"), GroupName = navGroup };
            AutomationProperties.SetName(option, section.Title);
            var parts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var icon = StudioVisuals.Glyph(section.Key, 16, Brushes.Transparent);
            icon.SetBinding(System.Windows.Shapes.Path.StrokeProperty, new Binding(nameof(Foreground)) { Source = option });
            parts.Children.Add(icon);
            var label = new TextBlock { Text = section.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new(11, 0, 0, 0) };
            label.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(Foreground)) { Source = option });
            parts.Children.Add(label);
            option.Content = parts;
            string key = section.Key;
            option.Checked += (_, _) => { if (page != key) ShowPage(key); };
            navigation.Add(key, option);
            Navigation.Children.Add(option);
        }
    }

    nint WndProc(nint hwnd, int msg, nint w, nint l, ref bool handled)
    {
        const int WindowPositionChanged = 0x47;
        if (WindowState == WindowState.Maximized &&
            (msg == taskbarCreated || msg is WindowPositionChanged or 0x7E or 0x1A or 0x2E0))
            ScheduleWorkAreaUpdate();

        if (msg == taskbarCreated || msg is 0x7E or 0x1A or 0x2E0)
        {
            engine.Queue();
            if (msg == 0x1A)
                Dispatcher.BeginInvoke(() => { UpdateTheme(); UpdatePreview(); });
        }
        return 0;
    }

    void ScheduleWorkAreaUpdate()
    {
        if (workAreaUpdate?.Status == DispatcherOperationStatus.Pending) return;
        workAreaUpdate = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (WindowState != WindowState.Maximized)
            {
                WindowFrame.Padding = default;
                return;
            }

            var hwnd = new WindowInteropHelper(this).Handle;
            var monitor = NativeMethods.MonitorFromWindow(hwnd, 2);
            var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info) ||
                !NativeMethods.GetClientRect(hwnd, out var client)) return;

            // WindowChrome clips the maximized frame to the working area, but the
            // WPF layout still sees the full client rectangle, including the resize frame.
            NativeMethods.MapWindowPoints(hwnd, 0, ref client, 2);
            var dpi = VisualTreeHelper.GetDpi(this);
            WindowFrame.Padding = new Thickness(
                Math.Max(0, info.Work.Left - client.Left) / dpi.DpiScaleX,
                Math.Max(0, info.Work.Top - client.Top) / dpi.DpiScaleY,
                Math.Max(0, client.Right - info.Work.Right) / dpi.DpiScaleX,
                Math.Max(0, client.Bottom - info.Work.Bottom) / dpi.DpiScaleY);
        });
    }

    void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (updatingDraft) return;
        if (e.PropertyName is nameof(Settings.Theme) or nameof(Settings.AccentColor)) UpdateTheme();
        if (e.PropertyName == nameof(Settings.TaskbarStyle))
        {
            UpdatePresetSelection();
            UpdateOpacityAvailability();
        }
        UpdatePreview();
        UpdateDraftStatus();
    }

    internal void EditDraft(Action<Settings> edit)
    {
        updatingDraft = true;
        try { edit(settings); }
        finally { updatingDraft = false; }
        UpdateTheme();
        UpdatePresetSelection();
        UpdateOpacityAvailability();
        UpdatePreview();
        UpdateDraftStatus();
    }

    internal bool SaveDraft()
    {
        if (!HasPendingChanges) return true;
        var candidate = SettingsDraft.Clone(settings);
        candidate.Validate();
        try
        {
            // Keep the live settings intact if the atomic file write fails.
            store.Save(candidate);
        }
        catch (Exception ex)
        {
            Log.Write("ERROR", ex.ToString());
            UpdateDraftStatus("Не удалось сохранить");
            Notify("Не удалось сохранить настройки: " + ex.Message);
            return false;
        }
        SettingsDraft.Copy(candidate, appliedSettings);
        EditDraft(draft => SettingsDraft.Copy(candidate, draft));
        UpdateDraftStatus("Настройки сохранены");
        engine.Queue();
        return true;
    }

    internal void RevertDraft()
    {
        if (!HasPendingChanges) return;
        double scrollOffset = PageScroll.VerticalOffset;
        EditDraft(draft => SettingsDraft.Copy(appliedSettings, draft));
        ShowPage(page);
        PageScroll.ScrollToVerticalOffset(scrollOffset);
        UpdateDraftStatus("Изменения отменены");
    }

    void UpdateDraftStatus(string? message = null)
    {
        bool pending = HasPendingChanges;
        DraftStatus.Text = message ?? (pending ? "Есть несохранённые изменения" : "Изменений нет");
        Use(DraftDot, System.Windows.Shapes.Shape.FillProperty, pending ? "Accent" : "Muted");
        DiscardDraftButton.IsEnabled = pending;
        SaveDraftButton.IsEnabled = pending;
        if (pending == displayedPendingChanges) return;
        displayedPendingChanges = pending;
        PendingChangesChanged?.Invoke(this, EventArgs.Empty);
    }

    void SaveDraftClick(object sender, RoutedEventArgs e) => SaveDraft();

    void RevertDraftClick(object sender, RoutedEventArgs e) => RevertDraft();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key != Key.S || Keyboard.Modifiers != ModifierKeys.Control || !HasPendingChanges) return;
        SaveDraftButton.Focus();
        SaveDraft();
        e.Handled = true;
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (((App)Application.Current).Exiting) return;
        e.Cancel = true;
        Hide();
    }

    void OnStatusChanged(string message)
    {
        StatusText.Text = message;
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            message.StartsWith("Оформление активно", StringComparison.Ordinal) ? "Positive" : "Muted");
    }

    void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void CloseClick(object sender, RoutedEventArgs e) => Close();

    Style FindStyle(string key) => (Style)FindResource(key);
    static void Use(FrameworkElement element, DependencyProperty property, string key) => element.SetResourceReference(property, key);

    public void Notify(string text)
    {
        NoticeText.Text = text;
        NoticeBox.Visibility = Visibility.Visible;
        PageScroll.ScrollToTop();
    }

    void UpdateTheme()
    {
        bool light = settings.Theme == "Light";
        if (settings.Theme == "System")
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = (key?.GetValue("AppsUseLightTheme") as int? ?? 1) != 0;
        }
        if (light == lastLightTheme && settings.AccentColor == lastAccent) return;
        lastLightTheme = light;
        lastAccent = settings.AccentColor;

        var resources = Application.Current.Resources;
        var colors = new (string Key, string Light, string Dark)[]
        {
            ("WindowBg", "#F3F4FB", "#151622"), ("SidebarBg", "#EDEDF8", "#191A27"),
            ("CardBg", "#FFFFFF", "#1D1F2D"), ("SurfaceRaised", "#F0F0F9", "#27293B"),
            ("Line", "#DCDDEE", "#33354A"), ("Text", "#1E2034", "#F5F4FD"),
            ("Muted", "#575A76", "#A8A8BF"), ("Hover", "#E7E8F7", "#2A2B3C"),
            ("Danger", "#A02D51", "#FF9AAB"), ("Positive", "#187B53", "#83DDB2"),
            ("TrackBg", "#C1C4DA", "#45465B")
        };
        foreach (var (key, day, night) in colors) resources[key] = StudioVisuals.Brush(light ? day : night);

        Color chosen = StudioVisuals.Parse(settings.AccentColor);
        Color accent = light ? Mix(chosen, StudioVisuals.Parse("#251B4E"), .63) : Mix(chosen, Colors.White, .10);
        if (!light && (.2126 * accent.R + .7152 * accent.G + .0722 * accent.B) < 145)
            accent = Mix(accent, Colors.White, .57);
        resources["Accent"] = StudioVisuals.Brush(accent);
        resources["AccentText"] = StudioVisuals.Brush(light ? "#FFFFFF" : "#1D1930");
        resources["AccentSubtle"] = StudioVisuals.Brush(Mix(light ? StudioVisuals.Parse("#F7F6FF") : StudioVisuals.Parse("#202135"), chosen, light ? .17 : .14));
        resources["Focus"] = StudioVisuals.Brush(light ? accent : Mix(accent, Colors.White, .30));
    }

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    internal void ShowPage(string key)
    {
        page = key;
        PageContent.Children.Clear();
        presetOptions.Clear();
        opacityControl = null;
        NoticeBox.Visibility = Visibility.Collapsed;
        PageScroll.ScrollToTop();
        navigation[key].IsChecked = true;
        var section = Pages.First(s => s.Key == key);
        PageTitle.Text = section.Title;
        PageSubtitle.Text = section.Subtitle;
        PreviewFrame.Visibility = key is "Appearance" or "Taskbar" ? Visibility.Visible : Visibility.Collapsed;

        switch (key)
        {
            case "Appearance": AppearancePage(); break;
            case "General": GeneralPage(); break;
            case "Taskbar": TaskbarPage(); break;
            case "Animation": AnimationPage(); break;
            case "Advanced": AdvancedPage(); break;
            case "About": AboutPage(); break;
        }
        UpdatePreview();
    }

    void AppearancePage()
    {
        Header("Стиль панели");
        var options = new UniformGrid { Columns = 5, Margin = new(-3, 0, -3, 12) };
        foreach (var preset in StudioVisuals.Presets)
        {
            var content = new StackPanel();
            content.Children.Add(StudioVisuals.Thumbnail(preset));
            content.Children.Add(new TextBlock { Text = preset.Name, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new(1, 8, 0, 0) });
            content.Children.Add(MutedText(preset.Description, 10, new(1, 2, 0, 1)));
            var option = new RadioButton
            {
                Content = content, GroupName = presetGroup, Margin = new(3, 0, 3, 0),
                Style = FindStyle("PresetOption"), IsChecked = settings.TaskbarStyle == preset.Key
            };
            AutomationProperties.SetName(option, preset.Name);
            option.Checked += (_, _) => settings.TaskbarStyle = preset.Key;
            presetOptions.Add(preset.Key, option);
            options.Children.Add(option);
        }
        PageContent.Children.Add(options);
        ColorControls();
        Header("Геометрия и прозрачность");
        opacityControl = SliderRow("Непрозрачность", "Насыщенность фона панели", nameof(Settings.Opacity), 0, 1, .01, "P0");
        UpdateOpacityAvailability();
        SliderRow("Скругление", "Заметнее всего в парящем стиле", nameof(Settings.CornerRadius), 0, 30, 1, "0 'пкс'");
        Header("Параметры превью");
        SliderRow("Размытие обоев", "Имитирует размытие под панелью в превью", nameof(Settings.BlurStrength), 0, 40, 1, "0");
        SliderRow("Тень", "Глубина тени в превью", nameof(Settings.ShadowStrength), 0, 1, .05, "P0");
        SliderRow("Обводка", "Интенсивность контура в превью", nameof(Settings.BorderOpacity), 0, 1, .05, "P0");
    }

    void GeneralPage()
    {
        Header("Приложение");
        Switch("Оформление панели", "После сохранения Windows вернёт исходный вид, если оформление отключено", nameof(Settings.Enabled));
        Switch("Запускать вместе с Windows", "Автозапуск для текущего пользователя", nameof(Settings.StartWithWindows));
        Switch("Запускать свёрнутым", "Оставлять только значок в трее", nameof(Settings.StartMinimized));
        Switch("Сворачивать в трей", "По кнопке сворачивания окна", nameof(Settings.MinimizeToTray));
        Switch("Проверять обновления при запуске", "При наличии настроенного HTTPS-источника", nameof(Settings.AutoCheckUpdates));
    }

    void TaskbarPage()
    {
        Header("Расположение");
        Choices("Выравнивание", "Доступный центр учитывает ширину трея", nameof(Settings.Alignment),
            ("Слева", "Left"), ("По центру", "Center"), ("Доступный центр", "Dynamic Center"));
        SliderRow("Внешний отступ", "Для парящего стиля", nameof(Settings.TaskbarMargin), 0, 16, 1, "0 'пкс'");
        Header("Системные элементы");
        Switch("Кнопка «Пуск»", "В одной группе со значками приложений", nameof(Settings.ShowStart));
        Switch("Поиск", "Если кнопка есть на системной панели", nameof(Settings.ShowSearch));
        Switch("Представление задач", null, nameof(Settings.ShowTaskView));
        Switch("Системный трей", null, nameof(Settings.ShowTray));
        Switch("Часы", null, nameof(Settings.ShowClock));
        Header("Дисплеи");
        Switch("Применять ко всем мониторам", "При отключении оформление применяется только на основном", nameof(Settings.AllMonitors));
        var monitors = engine.MonitorSummary();
        if (!string.IsNullOrWhiteSpace(monitors)) BodyText(monitors);
        Header("Размер в превью");
        SliderRow("Высота панели", "Реальную высоту задаёт Windows", nameof(Settings.TaskbarHeight), 32, 64, 1, "0 'пкс'");
        SliderRow("Размер значков", "На рабочем столе используется размер Explorer", nameof(Settings.IconSize), 16, 32, 1, "0 'пкс'");
    }

    void AnimationPage()
    {
        Header("Движение");
        Switch("Плавные переходы", null, nameof(Settings.AnimationEnabled));
        Switch("Анимировать центрирование", "При изменении ширины группы значков", nameof(Settings.AnimateRepositioning));
        SliderRow("Продолжительность", null, nameof(Settings.AnimationDuration), 0, 1000, 10, "0 'мс'");
        Choices("Кривая движения", null, nameof(Settings.AnimationType), ("Плавная", "Ease"),
            ("С замедлением", "Ease Out"), ("Пружина", "Spring"), ("Линейная", "Linear"));
    }

    void AdvancedPage()
    {
        Header("Оболочка Windows");
        ActionRow("Восстановить панель Windows", "Исходное расположение вернётся после сохранения", "Восстановить",
            () => EditDraft(draft => draft.Enabled = false));
        ActionRow("Перезапустить Explorer", "Открытые окна Проводника закроются", "Перезапустить…", RestartExplorer);
        Switch("Эффекты композиции", "Экспериментальные оттенки и акрил в Windows 10", nameof(Settings.ExperimentalEffects));
        Header("Настройки и диагностика");
        ActionRow("Сбросить настройки", "Загрузить значения по умолчанию для просмотра", "Сбросить…", Reset);
        ActionRow("Папка конфигурации", "Настройки, журнал и данные восстановления", "Открыть",
            () => Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { Log.Folder } }));
        Switch("Подробный журнал", @"%AppData%\Win11Taskbar\Logs\latest.log", nameof(Settings.DebugLogging));
    }

    void AboutPage()
    {
        Header("Win11Taskbar");
        BodyText($"Версия {App.VersionLabel} · x64 · .NET {Environment.Version}\nWindows {Environment.OSVersion.Version}");
        ActionRow("Обновления", "Проверка без автоматической установки", "Проверить",
            async () => await ((App)Application.Current).CheckUpdates(true));
        BodyText("Лицензия MIT. Оформление системной панели поддерживается на Windows 10 22H2.");
        ActionRow("Завершить Win11Taskbar", "Вернуть исходную панель и убрать значок из трея", "Выход",
            () => ((App)Application.Current).ExitApp());
    }

    void Header(string title) => PageContent.Children.Add(new TextBlock
    {
        Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold,
        Margin = new(1, PageContent.Children.Count == 0 ? 1 : 12, 0, 9)
    });

    static TextBlock MutedText(string text, double size, Thickness margin)
    {
        var label = new TextBlock { Text = text, FontSize = size, Margin = margin, TextWrapping = TextWrapping.Wrap };
        Use(label, TextBlock.ForegroundProperty, "Muted");
        return label;
    }

    void BodyText(string text) => PageContent.Children.Add(MutedText(text, 12, new(2, 8, 2, 10)));

    void Card(string title, string? description, FrameworkElement control)
    {
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 15, 0) };
        labels.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold });
        if (description != null) labels.Children.Add(MutedText(description, 11, new(0, 4, 0, 0)));
        layout.Children.Add(labels);
        control.VerticalAlignment = VerticalAlignment.Center;
        layout.Children.Add(control);
        Grid.SetColumn(control, 1);

        var card = new Border { BorderThickness = new(1), CornerRadius = new(9), Padding = new(14, 12, 14, 12), Margin = new(0, 0, 0, 6), MinHeight = 56, Child = layout };
        Use(card, Border.BackgroundProperty, "CardBg");
        Use(card, Border.BorderBrushProperty, "Line");
        PageContent.Children.Add(card);
    }

    Binding Bind(string name) => new(name)
    {
        Source = settings, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
    };

    void Switch(string title, string? description, string property)
    {
        var control = new ToggleButton();
        AutomationProperties.SetName(control, title);
        control.SetBinding(ToggleButton.IsCheckedProperty, Bind(property));
        Card(title, description, control);
    }

    void Choices(string title, string? description, string property, params (string Label, string Value)[] values)
    {
        var control = new ComboBox
        {
            ItemsSource = values.Select(value => new Choice(value.Label, value.Value)),
            DisplayMemberPath = nameof(Choice.Label), SelectedValuePath = nameof(Choice.Value)
        };
        AutomationProperties.SetName(control, title);
        control.SetBinding(Selector.SelectedValueProperty, Bind(property));
        Card(title, description, control);
    }

    sealed record Choice(string Label, string Value)
    {
        public override string ToString() => Label;
    }

    Slider? opacityControl;
    Slider SliderRow(string title, string? description, string property, double min, double max, double tick, string format)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var control = new Slider { Minimum = min, Maximum = max, TickFrequency = tick, SmallChange = tick, LargeChange = tick * 5 };
        AutomationProperties.SetName(control, title);
        control.SetBinding(Slider.ValueProperty, Bind(property));
        panel.Children.Add(control);
        var value = new TextBlock { Width = 61, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, FontSize = 12 };
        Use(value, TextBlock.ForegroundProperty, "Accent");
        value.SetBinding(TextBlock.TextProperty, new Binding(property) { Source = settings, StringFormat = "{0:" + format + "}" });
        panel.Children.Add(value);
        Card(title, description, panel);
        return control;
    }

    void UpdateOpacityAvailability()
    {
        if (opacityControl == null) return;
        opacityControl.IsEnabled = settings.TaskbarStyle != "Solid";
        opacityControl.ToolTip = opacityControl.IsEnabled ? null : "В сплошном стиле фон всегда непрозрачный";
    }

    void ColorControls()
    {
        Header("Палитра");
        var tiles = new Grid { Margin = new(0, 0, 0, 7) };
        tiles.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        tiles.ColumnDefinitions.Add(new() { Width = new GridLength(8) });
        tiles.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });

        var background = ColorTile("Фон панели", "Оттенок панели задач", nameof(Settings.BackgroundColor), hex =>
        {
            settings.FollowSystemTaskbarTheme = false;
            settings.BackgroundColor = hex;
        });
        var accent = ColorTile("Акцент приложения", "Кнопки и выделение", nameof(Settings.AccentColor),
            hex => settings.AccentColor = hex);
        Grid.SetColumn(accent, 2);
        tiles.Children.Add(background);
        tiles.Children.Add(accent);
        PageContent.Children.Add(tiles);
        Switch("Системный цвет панели", "Подстраиваться под светлый или тёмный режим Windows", nameof(Settings.FollowSystemTaskbarTheme));
        Choices("Тема приложения", null, nameof(Settings.Theme), ("Как в Windows", "System"), ("Светлая", "Light"), ("Тёмная", "Dark"));
    }

    Button ColorTile(string title, string description, string property, Action<string> onChoose)
    {
        bool isBackground = property == nameof(Settings.BackgroundColor);
        var button = new Button
        {
            Style = FindStyle("StudioColorTile"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ToolTip = description
        };
        AutomationProperties.SetName(button, title);
        AutomationProperties.SetHelpText(button, "Открыть палитру и ввести значение в формате HEX.");
        var contents = new Grid();
        contents.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        contents.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        contents.ColumnDefinitions.Add(new() { Width = GridLength.Auto });

        var swatch = new Border
        {
            Width = 36, Height = 36, CornerRadius = new(8), BorderThickness = new(1),
            Margin = new(0, 0, 11, 0), VerticalAlignment = VerticalAlignment.Center
        };
        Use(swatch, Border.BorderBrushProperty, "Line");
        if (isBackground)
        {
            var background = new MultiBinding { Converter = BackgroundColorDisplay.Instance };
            background.Bindings.Add(new Binding(property) { Source = settings });
            background.Bindings.Add(new Binding(nameof(Settings.FollowSystemTaskbarTheme)) { Source = settings });
            background.Bindings.Add(new Binding(nameof(Border.Background)) { Source = PreviewTint });
            swatch.SetBinding(Border.BackgroundProperty, background);
        }
        else
        {
            swatch.SetBinding(Border.BackgroundProperty, new Binding(property) { Source = settings });
        }
        contents.Children.Add(swatch);

        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 7, 0) };
        labels.Children.Add(new TextBlock
        {
            Text = title, TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 13, FontWeight = FontWeights.SemiBold
        });
        var code = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"), FontSize = 11, Margin = new Thickness(0, 4, 0, 0)
        };
        Use(code, TextBlock.ForegroundProperty, "Muted");
        if (isBackground)
        {
            var text = new MultiBinding { Converter = BackgroundColorDisplay.Instance };
            text.Bindings.Add(new Binding(property) { Source = settings });
            text.Bindings.Add(new Binding(nameof(Settings.FollowSystemTaskbarTheme)) { Source = settings });
            code.SetBinding(TextBlock.TextProperty, text);
        }
        else
        {
            code.SetBinding(TextBlock.TextProperty, new Binding(property) { Source = settings });
        }
        labels.Children.Add(code);
        Grid.SetColumn(labels, 1);
        contents.Children.Add(labels);

        var arrow = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,1 L4,5 L0,9"), StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center
        };
        Use(arrow, System.Windows.Shapes.Shape.StrokeProperty, "Muted");
        Grid.SetColumn(arrow, 2);
        contents.Children.Add(arrow);
        button.Content = contents;
        button.Click += (_, _) =>
        {
            var startingColor = StudioVisuals.Parse(isBackground ? settings.BackgroundColor : settings.AccentColor);
            if (isBackground && settings.FollowSystemTaskbarTheme)
                startingColor = TaskbarMaterial.Resolve(settings).Tint;
            startingColor.A = 255;
            var dialog = new HexColorDialog(title, startingColor) { Owner = this };
            if (dialog.ShowDialog() == true && dialog.SelectedHex is { } hex)
                onChoose(hex);
        };
        return button;
    }

    sealed class BackgroundColorDisplay : IMultiValueConverter
    {
        public static BackgroundColorDisplay Instance { get; } = new();
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (targetType == typeof(string))
                return values[1] is true ? "Цвет Windows" : values[0] as string ?? "";

            if (values[1] is true && values[2] is SolidColorBrush tint)
                return StudioVisuals.Brush(Color.FromRgb(tint.Color.R, tint.Color.G, tint.Color.B));
            return values[0] is string hex && HexColorDialog.TryParseHex(hex, out var color)
                ? StudioVisuals.Brush(color) : Brushes.Transparent;
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    void ActionRow(string title, string? description, string label, Action action)
    {
        var button = new Button { Content = label };
        button.Click += (_, _) => action();
        Card(title, description, button);
    }

    void UpdatePresetSelection()
    {
        if (presetOptions.TryGetValue(settings.TaskbarStyle, out var selected))
            selected.IsChecked = true;
    }

    void UpdatePreview()
    {
        if (PreviewFrame.Visibility != Visibility.Visible) return;
        var material = TaskbarMaterial.Resolve(settings);
        PreviewStyleLabel.Text = StudioVisuals.Presets.FirstOrDefault(p => p.Key == settings.TaskbarStyle)?.Name ?? settings.TaskbarStyle;
        PreviewBar.Height = settings.TaskbarHeight;
        PreviewBar.Margin = new(material.SideInset, 0, material.SideInset, material.Floating ? Math.Max(3, material.VerticalInset) : 0);
        PreviewBar.CornerRadius = new(Math.Min(material.Radius, settings.TaskbarHeight / 2));
        PreviewTint.Background = StudioVisuals.Brush(material.Tint);
        PreviewBar.Opacity = settings.Enabled ? 1 : .52;
        var rim = material.Light ? Color.FromArgb((byte)(settings.BorderOpacity * 255), 35, 40, 63)
                                : Color.FromArgb((byte)(settings.BorderOpacity * 255), 255, 255, 255);
        PreviewBar.BorderBrush = StudioVisuals.Brush(rim);
        PreviewBar.Effect = settings.ShadowStrength > .01
            ? new DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Color = Colors.Black, Opacity = settings.ShadowStrength, RenderingBias = RenderingBias.Performance }
            : null;
        PreviewGlass.Effect = material.Frosted && settings.BlurStrength > 0
            ? new BlurEffect { Radius = settings.BlurStrength * .38, RenderingBias = RenderingBias.Performance }
            : null;
        PreviewGlass.Opacity = material.Frosted ? .42 : 0;

        PreviewIcons.HorizontalAlignment = settings.Alignment == "Left" ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        PreviewIcons.Margin = settings.Alignment switch
        {
            "Left" => new(10, 0, 0, 0),
            "Dynamic Center" when settings.ShowTray => new(0, 0, settings.ShowClock ? 103 : 57, 0),
            _ => new Thickness(0)
        };
        var content = (material.Light, settings.ShowStart, settings.ShowSearch, settings.ShowTaskView, settings.ShowTray,
            settings.ShowClock, settings.IconSize, settings.AccentColor);
        if (content != previewContent)
        {
            previewContent = content;
            PopulatePreview(material.Light);
        }
    }

    void PopulatePreview(bool light)
    {
        PreviewIcons.Children.Clear();
        PreviewTray.Children.Clear();
        var foreground = StudioVisuals.Brush(light ? "#202846" : "#F6F7FF");
        double size = Math.Min(settings.IconSize, 32);

        void AddIcon(string key, bool running = false, bool active = false)
        {
            var cell = new Grid { Width = Math.Max(27, size + 11), Height = size + 7 };
            if (active)
            {
                var highlight = new Border
                {
                    Background = StudioVisuals.Brush(light ? "#18262E50" : "#22FFFFFF"),
                    CornerRadius = new(6), Margin = new(1, 0, 1, 0)
                };
                cell.Children.Add(highlight);
            }
            var image = StudioVisuals.LauncherIcon(key, size, foreground);
            image.HorizontalAlignment = HorizontalAlignment.Center;
            image.VerticalAlignment = VerticalAlignment.Center;
            cell.Children.Add(image);
            if (running)
            {
                var indicator = new Border
                {
                    Width = active ? 10 : 4, Height = 2, CornerRadius = new(1),
                    VerticalAlignment = VerticalAlignment.Bottom, Margin = new(0, 0, 0, 1),
                    Background = active ? StudioVisuals.Brush(settings.AccentColor) : foreground,
                    Opacity = active ? 1 : .55
                };
                cell.Children.Add(indicator);
            }
            PreviewIcons.Children.Add(cell);
        }

        if (settings.ShowStart) AddIcon("Start");
        if (settings.ShowSearch) AddIcon("Search");
        if (settings.ShowTaskView) AddIcon("View");
        AddIcon("Browser", running: true, active: true);
        AddIcon("Files", running: true);
        AddIcon("Chat");
        AddIcon("Terminal");

        if (!settings.ShowTray) return;
        foreach (string key in new[] { "Tray", "Network", "Sound" })
        {
            var icon = StudioVisuals.Glyph(key, 12, foreground);
            icon.Margin = new(0, 0, 6, 0);
            PreviewTray.Children.Add(icon);
        }
        if (settings.ShowClock)
        {
            var now = DateTime.Now;
            var clock = new TextBlock
            {
                Text = $"{now:HH:mm}\n{now:dd.MM.yyyy}", TextAlignment = TextAlignment.Right,
                Foreground = foreground, FontSize = 9, LineHeight = 11, Margin = new(2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            PreviewTray.Children.Add(clock);
        }
    }

    public void ShowWelcome()
    {
        var welcome = new Window
        {
            Title = "Настройка Win11Taskbar", Width = 475, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            FontFamily = FontFamily, FontSize = 13, ShowInTaskbar = false
        };
        Use(welcome, BackgroundProperty, "WindowBg");
        Use(welcome, ForegroundProperty, "Text");
        var layout = new StackPanel { Margin = new(25, 23, 25, 25) };
        layout.Children.Add(new TextBlock { Text = "Настройте свою панель", FontSize = 25, FontWeight = FontWeights.SemiBold });
        layout.Children.Add(MutedText("Выберите стартовое оформление. Цвет, прозрачность и расположение значков можно поменять в настройках.", 13, new(0, 8, 0, 16)));
        var preview = StudioVisuals.Thumbnail(StudioVisuals.Presets[1]);
        preview.Height = 75;
        layout.Children.Add(preview);
        var startWithWindows = new CheckBox { Content = "Запускать вместе с Windows", Margin = new(0, 15, 0, 15), IsChecked = settings.StartWithWindows };
        Use(startWithWindows, ForegroundProperty, "Text");
        layout.Children.Add(startWithWindows);
        var finish = new Button { Content = "Сохранить и начать", IsDefault = true };
        Use(finish, BackgroundProperty, "Accent");
        Use(finish, ForegroundProperty, "AccentText");
        finish.Click += (_, _) =>
        {
            EditDraft(draft =>
            {
                draft.StartWithWindows = startWithWindows.IsChecked == true;
                draft.OnboardingComplete = true;
            });
            if (SaveDraft()) welcome.Close();
            else MessageBox.Show(welcome, "Не удалось сохранить настройки. Попробуйте ещё раз.", "Win11Taskbar",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        layout.Children.Add(finish);
        welcome.Content = layout;
        welcome.ShowDialog();
    }

    void Reset()
    {
        if (MessageBox.Show(this, "Загрузить значения по умолчанию? Панель и автозапуск изменятся после сохранения.", "Сброс настроек",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var defaults = new Settings { OnboardingComplete = true };
        EditDraft(draft => SettingsDraft.Copy(defaults, draft));
        ShowPage(page);
    }

    public void RestartExplorer()
    {
        if (MessageBox.Show(this, "Перезапустить оболочку Windows? Открытые окна Проводника закроются.", "Перезапуск Explorer",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        engine.Restore();
        var explorerProcesses = new HashSet<uint>();
        foreach (var bar in NativeMethods.Taskbars())
        {
            NativeMethods.GetWindowThreadProcessId(bar, out uint id);
            if (id != 0) explorerProcesses.Add(id);
        }
        foreach (var id in explorerProcesses)
        {
            try { using var process = Process.GetProcessById((int)id); process.Kill(); }
            catch (Exception ex) { Log.Write("WARNING", ex.Message); }
        }
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        engine.Queue();
    }
}
