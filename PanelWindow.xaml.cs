using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NetPause;

public sealed class AppVm : INotifyPropertyChanged, IRowVm
{
    readonly Engine _engine;
    public AppEntry Entry { get; }
    public long SortBytes => Entry.BytesToday;
    public ImageSource? Icon => _icon;
    public string Name => Entry.Name;
    public string Path => Entry.Path;

    public AppVm(Engine engine, AppEntry entry)
    {
        _engine = engine;
        Entry = entry;
        // значок, который уже загружали, берём сразу; новый грузим в фоне, чтобы панель открывалась без задержки
        if (IconCache.TryGetValue(entry.Path, out var cached)) _icon = cached;
        else
        {
            var path = entry.Path;
            var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            System.Threading.Tasks.Task.Run(() =>
            {
                var img = LoadIcon(path);
                ui.BeginInvoke(() =>
                {
                    _icon = img;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
                });
            });
        }
    }

    ImageSource? _icon;

    public Visibility SwitchVisibility => Entry.Locked ? Visibility.Hidden : Visibility.Visible;

    public bool Allowed
    {
        get => Entry.Allowed;
        set
        {
            _engine.SetAllowed(Entry.Key, value);
            Refresh();
        }
    }

    /// <summary>Самая прожорливая программа за сегодня — в строке это отмечено.</summary>
    public bool IsTop { get; set; }

    bool BlockedNow => _engine.EconomyOn && !Entry.Allowed && !Entry.Locked;

    public string Sub
    {
        get
        {
            var file = Entry.Service == null ? System.IO.Path.GetFileName(Entry.Path)
                : Entry.Service == "*" ? "svchost.exe"
                : "служба " + Entry.Service;
            if (Entry.IsSystem) file = "Windows · " + file;
            var used = Entry.BytesToday > 0 ? Fmt.Bytes(Entry.BytesToday) + " сегодня" : "нет трафика";
            var s = file + " · " + used;
            if (IsTop) s += " · ест больше всех";
            return s;
        }
    }

    /// <summary>Подпись самой прожорливой программы — цветом оформления.</summary>
    public Brush SubBrush => (Brush)Application.Current.Resources[IsTop ? "BadgeFg" : "Fg2"];

    // плашка статуса справа: её не обрезает многоточие, как раньше подпись «заблокировано»
    public Visibility ChipVisibility => BlockedNow ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DemonVisibility =>
        ThemeManager.Current.Demons && Entry.Service != "DoSvc" ? Visibility.Visible : Visibility.Collapsed;

    public string ChipText
    {
        get
        {
            bool demons = ThemeManager.Current.Demons;
            if (Entry.Service == "DoSvc") return "≤ 1 КБ/с";
            // «заблокировано» пишем, только когда правило действительно стоит в файрволе
            if (!_engine.IsBlocked(Entry.Key)) return demons ? "не пускаю…" : "блокируется…";
            return demons ? "не пускаю" : "заблокировано";
        }
    }

    static readonly string[] Changing =
        { nameof(Sub), nameof(Allowed), nameof(SubBrush), nameof(ChipVisibility), nameof(ChipText), nameof(DemonVisibility) };

    public void Refresh()
    {
        foreach (var p in Changing) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource?> IconCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Значок программы (готовый, замороженный — можно грузить в любом потоке).</summary>
    public static ImageSource? LoadIcon(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (IconCache.TryGetValue(path, out var cached)) return cached;
        ImageSource? result = null;
        try
        {
            using var ic = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (ic != null)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(ic.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                result = src;
            }
        }
        catch { }
        IconCache[path] = result;
        return result;
    }
}

/// <summary>Строка списка «Все сети».</summary>
public sealed class NetVm
{
    public string Id { get; init; } = "";
    public string Glyph { get; init; } = "";
    public string Name { get; init; } = "";
    public string Sub { get; init; } = "";
    public string RuleText { get; init; } = "";
    public Brush? RuleBg { get; init; }
    public Brush? RuleFg { get; init; }
    public FontWeight Weight { get; init; } = FontWeights.Normal;
    public Visibility ForgetVisibility { get; init; } = Visibility.Visible;
    public NetMode? Mode { get; init; }

    public static string GlyphFor(string kind) => kind switch
    {
        "Wi-Fi" => "",
        "Кабель" => "",
        "Bluetooth" => "",
        _ => "", // телефон: USB-модем, мобильная сеть
    };
}

/// <summary>Строка вкладки «Статистика».</summary>
public sealed class StatVm : INotifyPropertyChanged
{
    public ImageSource? Icon { get; init; }
    public string Name { get; init; } = "";
    public string? Path { get; init; }
    string _bytesText = "", _share = "";
    public string BytesText { get => _bytesText; init => _bytesText = value; }
    public string Share { get => _share; init => _share = value; }

    /// <summary>Обновить числа на месте, не пересоздавая строку.</summary>
    public void Set(string bytesText, string share)
    {
        if (_bytesText != bytesText)
        {
            _bytesText = bytesText;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BytesText)));
        }
        if (_share != share)
        {
            _share = share;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Share)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Одна строка на экране подробностей: сайт, куда ходила программа.</summary>
public sealed class HostVm : INotifyPropertyChanged
{
    readonly Engine _engine;
    readonly AppEntry _app;
    bool _blocked;
    int _addresses;

    public string Host { get; }
    public long Bytes { get; private set; }
    public Visibility SwitchVisibility { get; }

    public HostVm(Engine engine, AppEntry app, HostInfo info)
    {
        _engine = engine;
        _app = app;
        Host = info.Host;
        // «прочие адреса» — сборная строка, блокировать её нечем
        SwitchVisibility = app.Locked || info.Host == "прочие адреса" ? Visibility.Hidden : Visibility.Visible;
        Update(info);
    }

    public void Update(HostInfo info)
    {
        Bytes = info.Bytes;
        _addresses = info.Addresses;
        _blocked = info.Blocked;
        Raise();
    }

    public bool Allowed
    {
        get => !_blocked;
        set
        {
            _blocked = !value;
            _engine.SetHostBlocked(_app.Key, Host, _blocked);
            Raise();
        }
    }

    public string Sub
    {
        get
        {
            var s = Bytes > 0 ? Fmt.Bytes(Bytes) : "нет трафика";
            if (_addresses > 1) s += " · адресов: " + _addresses;
            if (_blocked) s += _engine.EconomyOn ? " · заблокировано" : " · заблокируется при экономии";
            return s;
        }
    }

    void Raise()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Sub)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Allowed)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class Fmt
{
    public static string Bytes(long b)
    {
        double kb = b / 1024.0, mb = kb / 1024.0, gb = mb / 1024.0;
        if (gb >= 1) return gb.ToString("0.00") + " ГБ";
        if (mb >= 1) return mb.ToString(mb >= 100 ? "0" : "0.0") + " МБ";
        return Math.Max(1, Math.Round(kb)).ToString("0") + " КБ";
    }
}

public partial class PanelWindow : Window
{
    readonly Engine _engine;
    readonly ObservableCollection<IRowVm> _apps = new();
    /// <summary>Строки программ, созданные один раз и переиспользуемые (значки грузятся медленно).</summary>
    readonly System.Collections.Generic.Dictionary<AppEntry, AppVm> _vms = new();
    readonly System.Collections.Generic.Dictionary<string, FolderVm> _folders = new();
    readonly ObservableCollection<AppVm> _folderApps = new();
    FolderVm? _openFolder;
    readonly DispatcherTimer _timer;
    AppVm? _detail;
    bool _suppressHide;
    bool _auto;
    DateTime _hiddenAt = DateTime.MinValue;

    public event Action? EconomyChanged;
    public event Action? ExitRequested;

    public PanelWindow(Engine engine)
    {
        _engine = engine;
        InitializeComponent();
        VersionText.Text = "NetPause " + Engine.AppVersion;
        AppList.ItemsSource = _apps;
        FolderList.ItemsSource = _folderApps;

        // оформление: применяем текущее и следим за сменой
        ThemeManager.Changed += () => Dispatcher.BeginInvoke(OnThemeChanged);
        OnThemeChanged();

        _engine.AppAdded += e => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible) RebuildRows();
        });

        // правила файрвола меняются в фоне: когда закончили, обновляем предупреждение и подписи
        _engine.SyncCompleted += () => Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible) return;
            UpdateWarning();
            RefreshNumbers();
        });

        // планировщик заданий опрашиваем в фоне, чтобы панель открывалась мгновенно
        System.Threading.Tasks.Task.Run(() =>
        {
            bool on = Autostart.IsEnabled();
            Dispatcher.BeginInvoke(() => SetAutoLabel(on));
        });

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshNumbers();
        Deactivated += (_, _) =>
        {
            if (_suppressHide) return;
            HidePanel();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int round = 2; // скруглённые углы Windows 11
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        if (App.Dark)
        {
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 только прячет панель; выход — через кнопку «Выход»
        e.Cancel = true;
        HidePanel();
    }

    public void Toggle()
    {
        if (IsVisible && !_hiding) { HidePanel(); return; }
        // щелчок по значку сначала прячет панель (потеря фокуса) — не открываем её тут же снова
        if ((DateTime.Now - _hiddenAt).TotalMilliseconds < 300) return;
        ShowPanel();
    }

    /// <summary>Панель сейчас плавно гаснет и вот-вот спрячется.</summary>
    bool _hiding;

    void HidePanel()
    {
        if (!IsVisible || _hiding) return;
        _hiding = true;
        _timer.Stop();
        _hiddenAt = DateTime.Now;
        // мягко гаснет и чуть опускается, затем окно прячется
        Motion.Go(Motion.Shift(Shell), TranslateTransform.YProperty, 10, 180, Motion.InOut);
        Motion.Go(Shell, OpacityProperty, 0, 180, Motion.InOut, () =>
        {
            if (!_hiding) return; // пока гасла, панель снова открыли
            _hiding = false;
            Hide();
            StopWave(); // спрятанной панели незачем анимировать каплю
        });
    }

    public void ShowPanel()
    {
        _hiding = false;
        CloseDetail();
        CloseFolder();
        RebuildRows();
        RefreshAll();

        // вокруг панели прозрачное поле под тень, оно же даёт отступ от края экрана
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width;
        Top = wa.Bottom - Height;
        Show();
        Activate();
        Focus(); // чтобы стрелки на клавиатуре сразу прокручивали список
        _timer.Start();
        // панель целиком мягко проявляется и всплывает снизу, как меню Windows 11
        Motion.SlideIn(Shell, 0, 18, 420);
        StartWave();
        Dispatcher.BeginInvoke(() =>
        {
            MoveIndicator(false);
            MovePeriodIndicator(false);
            MoveModePill(false);
        }, DispatcherPriority.Loaded);
        Motion.Stagger(ActiveList());
    }

    // ---------- итоговая карточка и шкала остатка ----------

    double? _lastFrac;
    double _waveLevel = double.NaN;

    /// <summary>Остаток по трекеру текущей сети известен (тогда видна шкала — капля, кольцо и т. д.).</summary>
    bool _remainKnown;

    /// <summary>
    /// Карточка вверху. Если у сети, к которой подключены, есть трекер и он сообщил остаток —
    /// показываем остаток и шкалу. Иначе шкалы нет: сколько потрачено сегодня и сколько через эту сеть.
    /// </summary>
    void UpdateSummary()
    {
        long today = _engine.TotalToday;
        var t = Translator;
        bool hasTracker = t?.ActiveTracker() != null;
        double? remainMb = hasTracker ? t?.RemainingMb : null;
        double rate = t?.MbPerMin ?? 0;
        string rateText = rate >= 0.05 ? rate.ToString(rate >= 100 ? "0" : "0.0") + " МБ/мин" : "";

        if (remainMb != null)
        {
            // Остаток суточной нормы трекера: норма выдаётся каждый день в 00:00 по UTC.
            double quota = t!.DailyQuotaMb;
            SumLabel.Text = "Осталось на сегодня";
            TotalText.Text = TranslatorController.FormatMb(remainMb.Value);
            var parts = new List<string>
            {
                remainMb.Value <= quota * 1.02 ? "из " + TranslatorController.FormatMb(quota)
                    : "больше суточной нормы " + TranslatorController.FormatMb(quota),
                "новая норма в " + TranslatorController.NextResetLocal.ToString("HH:mm"),
            };
            if (rateText.Length > 0) parts.Add(rateText);
            if (t.Last is { Ok: true, DataOn: false }) parts.Add("интернет на сайте выключен");
            SumSub.Text = string.Join(" · ", parts);
            _lastFrac = t.QuotaFraction;
            var since = DateTime.UtcNow.Date.ToLocalTime(); // когда началась норма этих суток
            SystemText.Text = t.OthersPresent
                // в сессии есть телефон — делим расход: ноутбук и он
                ? "С " + since.ToString("HH:mm") + ": ноутбук " + TranslatorController.FormatMb(t.SelfTodayMb) +
                  " · " + t.OthersLabel + " " + TranslatorController.FormatMb(t.OtherTodayMb)
                : "С " + since.ToString("HH:mm") + " израсходовано: " +
                  TranslatorController.FormatMb(Math.Max(0, quota - remainMb.Value));
        }
        else
        {
            SumLabel.Text = "Сегодня потрачено";
            TotalText.Text = Fmt.Bytes(today);
            var parts = new List<string>();
            var cur = _engine.CurrentNetwork;
            if (cur != null) parts.Add("в сети «" + cur.Name + "»: " + Fmt.Bytes(_engine.NetToday(cur.Id)));
            if (rateText.Length > 0) parts.Add("сейчас " + rateText);
            if (hasTracker) parts.Add(t!.Last?.NeedLogin == true ? "войдите в трекер" : "спрашиваем трекер…");
            SumSub.Text = string.Join(" · ", parts);
            _lastFrac = null;
            SystemText.Text = "Из них не удалось опознать: " + Fmt.Bytes(_engine.SystemToday);
        }
        SumSub.Visibility = SumSub.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SetGaugeShown(remainMb != null);
        UpdateGauge(_lastFrac);
    }

    /// <summary>Шкала видна, только когда остаток известен; появляется и исчезает плавно.</summary>
    void SetGaugeShown(bool known)
    {
        _remainKnown = known;
        var g = ThemeManager.Current.Gauge;
        bool box = known && (g is "drop" or "ring" or "orbit");
        bool bar = known && g == "bar";
        if (box != (GaugeBox.Visibility == Visibility.Visible))
        {
            GaugeBox.Visibility = box ? Visibility.Visible : Visibility.Collapsed;
            if (box && IsVisible) Motion.SlideIn(GaugeBox, -12, 0, 460);
        }
        if (bar != (BarGauge.Visibility == Visibility.Visible))
        {
            BarGauge.Visibility = bar ? Visibility.Visible : Visibility.Collapsed;
            if (bar && IsVisible) Motion.SlideIn(BarGauge, 0, 6, 460);
        }
    }

    /// <summary>Шкала остатка в виде, который задаёт оформление. frac — доля остатка или null, если неизвестна.</summary>
    void UpdateGauge(double? frac)
    {
        var theme = ThemeManager.Current;
        double f = Math.Clamp(frac ?? 0, 0, 1);
        switch (theme.Gauge)
        {
            case "drop":
                // без данных трекера капля полная — просто знак оформления
                double level = frac == null ? 1 : f;
                double y = 81 - level * 78;
                if (double.IsNaN(_waveLevel) || Math.Abs(y - _waveLevel) > 0.3)
                {
                    Motion.Go(WaveT, TranslateTransform.YProperty, y, double.IsNaN(_waveLevel) ? 1 : 1400, Motion.InOut);
                    _waveLevel = y;
                }
                break;
            case "ring":
            case "orbit":
                bool orbit = theme.Gauge == "orbit";
                double c = 35, r = orbit ? 30 : 31;
                RingArc.Data = Arc(c, r, frac == null ? 0 : f);
                RingText.Text = frac == null ? "—" : Math.Round(f * 100) + "%";
                if (orbit)
                {
                    double a = (-90 + 360 * (frac == null ? 0 : f)) * Math.PI / 180;
                    System.Windows.Controls.Canvas.SetLeft(Satellite, c + r * Math.Cos(a) - 5.5);
                    System.Windows.Controls.Canvas.SetTop(Satellite, c + r * Math.Sin(a) - 5.5);
                }
                break;
            case "bar":
                double w = BarGauge.ActualWidth * (frac == null ? 0 : f);
                if (Math.Abs(BarFill.Width - w) > 0.5 || double.IsNaN(BarFill.Width))
                    Motion.Go(BarFill, WidthProperty, w, 900, Motion.InOut);
                break;
        }
    }

    /// <summary>Дуга кольца: от верхней точки по часовой стрелке на долю f.</summary>
    static Geometry Arc(double c, double r, double f)
    {
        if (f <= 0.001) return Geometry.Empty;
        if (f >= 0.999) return new EllipseGeometry(new Point(c, c), r, r);
        double a0 = -Math.PI / 2, a1 = a0 + 2 * Math.PI * f;
        var fig = new PathFigure { StartPoint = new Point(c + r * Math.Cos(a0), c + r * Math.Sin(a0)), IsClosed = false };
        fig.Segments.Add(new ArcSegment(new Point(c + r * Math.Cos(a1), c + r * Math.Sin(a1)),
            new Size(r, r), 0, f > 0.5, SweepDirection.Clockwise, true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        g.Freeze();
        return g;
    }

    /// <summary>Поверхность в капле медленно колышется, пока панель открыта.</summary>
    void StartWave()
    {
        if (ThemeManager.Current.Gauge != "drop") { StopWave(); return; }
        WaveT.BeginAnimation(TranslateTransform.XProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 64, TimeSpan.FromSeconds(4.5))
            { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
    }

    void StopWave() => WaveT.BeginAnimation(TranslateTransform.XProperty, null);

    // ---------- оформление ----------

    sealed class ThemeVm
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public List<Brush> Swatches { get; init; } = new();
        public bool Selected { get; init; }
    }

    sealed class TrayIconVm
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public ImageSource? PreviewOn { get; init; }
        public ImageSource? PreviewOff { get; init; }
        public bool Selected { get; init; }
    }

    static Brush B(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>Списки «Оформление» и «Значок у часов» в настройках.</summary>
    void BuildDesignLists()
    {
        var cur = _engine.Setting(s => s.Theme);
        ThemeList.ItemsSource = ThemeManager.All.Select(t => new ThemeVm
        {
            Id = t.Id,
            Name = t.Name,
            Description = t.Description,
            Swatches = t.Id == "system"
                ? new List<Brush> { B("#F3F3F3"), B("#202020"), B("#2EA05A") }
                : new List<Brush> { B(t.Colors["Bg"]), B(t.Colors["Card"]), B(t.Colors["On"]), B(t.Colors["BadgeFg"]) },
            Selected = t.Id == (ThemeManager.Find(cur).Id),
        }).ToList();

        var choice = _engine.Setting(s => s.TrayIcon);
        var accent = ThemeManager.Current.C("On");
        TrayIconVm Item(string id, string name, string style) => new()
        {
            Id = id,
            Name = name,
            PreviewOn = TrayIcons.Preview(style, true, accent),
            PreviewOff = TrayIcons.Preview(style, false, accent),
            Selected = id == (choice is "wifi" or "pause" ? choice : "auto"),
        };
        TrayIconList.ItemsSource = new[]
        {
            Item("auto", "Как у оформления", ThemeManager.Current.TrayStyle),
            Item("wifi", "Капля и Wi-Fi", "wifi"),
            Item("pause", "Пауза", "pause"),
        };
    }

    void Theme_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ThemeVm vm) return;
        if (ThemeManager.Current.Id == vm.Id) return;
        _engine.ChangeSettings(s => s.Theme = vm.Id);
        // панель на мгновение притухает, цвета меняются, и она снова проявляется
        Motion.Go(Shell, OpacityProperty, 0.15, 150, Motion.InOut, () =>
        {
            ThemeManager.Apply(vm.Id);
            Motion.Go(Shell, OpacityProperty, 1, 380, Motion.InOut);
        });
    }

    void TrayIcon_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TrayIconVm vm) return;
        _engine.ChangeSettings(s => s.TrayIcon = vm.Id);
        (Application.Current as App)?.RebuildIcons();
    }

    /// <summary>Оформление сменилось: знак в шапке, вид шкалы, плашки в строках, списки в настройках.</summary>
    void OnThemeChanged()
    {
        var t = ThemeManager.Current;
        Logo.Visibility = t.Demons ? Visibility.Visible : Visibility.Collapsed;
        DropGauge.Visibility = t.Gauge == "drop" ? Visibility.Visible : Visibility.Collapsed;
        RingGauge.Visibility = t.Gauge is "ring" or "orbit" ? Visibility.Visible : Visibility.Collapsed;
        GaugeBox.Visibility = _remainKnown && t.Gauge is "drop" or "ring" or "orbit" ? Visibility.Visible : Visibility.Collapsed;
        BarGauge.Visibility = _remainKnown && t.Gauge == "bar" ? Visibility.Visible : Visibility.Collapsed;
        bool orbit = t.Gauge == "orbit";
        RingTrack.StrokeThickness = orbit ? 2 : 7;
        RingTrack.StrokeDashArray = orbit ? new DoubleCollection { 1.5, 2.5 } : null;
        RingTrack.Margin = new Thickness(orbit ? 4 : 0);
        RingArc.StrokeThickness = orbit ? 3 : 7;
        Satellite.Visibility = orbit ? Visibility.Visible : Visibility.Collapsed;
        // размеры шкалы у оформлений разные: пересчитываем раскладку карточки сразу
        GaugeBox.InvalidateMeasure();
        SummaryCard.InvalidateMeasure();
        SummaryCard.UpdateLayout();
        _waveLevel = double.NaN;
        if (IsVisible) StartWave(); else StopWave();
        BuildDesignLists();
        foreach (var vm in _vms.Values) vm.Refresh();
        if (IsVisible) RefreshAll();
        else UpdateGauge(_lastFrac);
    }

    /// <summary>Нарисовать панель в картинку (служебная проверка оформлений), без показа окна и без анимаций.</summary>
    public void RenderPreview(string file, bool settings, bool translator = false)
    {
        OnThemeChanged();
        RebuildRows();
        foreach (var p in AllOverlays) p.Visibility = Visibility.Collapsed;
        if (settings) SettingsView.Visibility = Visibility.Visible;
        if (translator) { TranslatorView.Visibility = Visibility.Visible; RefreshTranslator(true); }
        RefreshAll();
        var root = (FrameworkElement)Content;
        var size = new Size(Width, Height);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
        MoveIndicator(false);
        if (settings) { SettingsView.ScrollToBottom(); root.UpdateLayout(); }
        if (translator) { TranslatorView.ScrollToVerticalOffset(360); root.UpdateLayout(); }
        UpdateGauge(_lastFrac);
        root.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(file);
        enc.Save(fs);
    }

    /// <summary>Скругляем содержимое по углам панели.</summary>
    void ClipHost_SizeChanged(object sender, SizeChangedEventArgs e) =>
        ClipHost.Clip = new RectangleGeometry(new Rect(e.NewSize), 7, 7);

    /// <summary>Список на открытой сейчас вкладке.</summary>
    System.Windows.Controls.ItemsControl? ActiveList() => _tabIndex switch
    {
        0 => AppList,
        2 => StatList,
        3 => NetList,
        _ => null,
    };

    void RefreshAll()
    {
        bool on = _engine.EconomyOn;
        Master.IsChecked = on;
        StatusText.Text = on ? "Экономия включена" : "Экономия выключена";
        StatusSub.Text = on
            ? (_engine.Strict ? "Строгий режим: интернет только у отмеченных" : "Интернет только у отмеченных программ")
            : "Все программы работают как обычно";
        NetText.Text = "Сеть: " + CurrentNetwork();
        UpdateWarning();
        RefreshNumbers();
    }

    void SetAutoLabel(bool on)
    {
        _auto = on;
        AutoBtn.Content = "Автозапуск: " + (on ? "вкл" : "выкл");
    }

    void UpdateWarning()
    {
        string? warn = null;
        if (_engine.MonitorError != null) warn = "Учёт трафика не запустился: " + _engine.MonitorError;
        else if (_engine.FirewallError != null) warn = "Не удалось изменить правила файрвола: " + _engine.FirewallError;
        else if (!_engine.IsWindowsFirewallOn()) warn = "Файрвол Windows выключен, поэтому блокировка не сработает.";
        WarnText.Text = warn ?? "";
        WarnText.Visibility = warn == null ? Visibility.Collapsed : Visibility.Visible;
    }

    void RefreshNumbers()
    {
        UpdateSummary();

        // самая прожорливая программа за сегодня (если съела больше мегабайта)
        AppVm? top = null;
        foreach (var vm in _vms.Values)
            if (vm.Entry.BytesToday > (top?.Entry.BytesToday ?? 1048576)) top = vm;
        foreach (var vm in _vms.Values) vm.IsTop = vm == top;

        foreach (var a in _apps) a.Refresh();
        Resort();
        RefreshFolder();
        RefreshDetail();
        RefreshStats();
        RefreshSettings();
        RefreshNetworks();
        RefreshTranslator();
    }

    // ---------- вкладка «Статистика» ----------

    void Tab_Click(object sender, RoutedEventArgs e)
    {
        int idx = Array.FindIndex(TopTabs, t => t.IsChecked == true);
        if (idx < 0) idx = 0;
        if (idx == _tabIndex) return;
        int from = _tabIndex;
        int dir = idx > from ? 1 : -1;
        _tabIndex = idx;

        // новую страницу сначала наполняем, потом показываем — без пустого кадра
        var page = OverlayOf(idx);
        if (page != null) page.Visibility = Visibility.Visible;
        RefreshStats(true);
        RefreshSettings();
        RefreshNetworks(true);
        RefreshTranslator(true);

        SwitchPage(from, idx, dir);
        MoveIndicator(true);
        if (idx == 2) Dispatcher.BeginInvoke(() => MovePeriodIndicator(false), DispatcherPriority.Loaded);
        if (idx == 3) Dispatcher.BeginInvoke(() => MoveModePill(false), DispatcherPriority.Loaded);
    }

    // ---------- плавные переходы ----------

    int _tabIndex;
    string? _netSig, _trackerSig;

    System.Windows.Controls.RadioButton[] TopTabs =>
        new[] { TabApps, TabTranslator, TabStats, TabNetworks, TabSettings };

    /// <summary>Страница вкладки, которая накрывает список программ (у «Программ» такой нет).</summary>
    FrameworkElement? OverlayOf(int idx) => idx switch
    {
        1 => TranslatorView,
        2 => StatsView,
        3 => NetworksView,
        4 => SettingsView,
        _ => null,
    };

    FrameworkElement[] AllOverlays => new FrameworkElement[] { TranslatorView, StatsView, NetworksView, SettingsView };

    FrameworkElement[] AppsPage() => WarnText.Visibility == Visibility.Visible
        ? new FrameworkElement[] { SummaryCard, WarnText, AppsArea }
        : new FrameworkElement[] { SummaryCard, AppsArea };

    /// <summary>
    /// Перекрёстный переход между вкладками: новая страница проявляется и въезжает поверх старой,
    /// старая прячется, только когда новая уже на месте. При возврате на «Программы» старая
    /// страница плавно гаснет поверх, а список программ мягко въезжает под ней.
    /// </summary>
    void SwitchPage(int from, int to, int dir)
    {
        var oldPage = OverlayOf(from);
        var newPage = OverlayOf(to);
        foreach (var p in AllOverlays)
        {
            if (p == oldPage || p == newPage) continue;
            p.Visibility = Visibility.Collapsed;
            System.Windows.Controls.Panel.SetZIndex(p, 0);
            Motion.Reset(p);
        }

        double dx = 20 * dir;
        if (newPage != null)
        {
            newPage.Visibility = Visibility.Visible;
            System.Windows.Controls.Panel.SetZIndex(newPage, 3);
            if (oldPage != null) System.Windows.Controls.Panel.SetZIndex(oldPage, 2);
            Motion.SlideIn(newPage, dx, 0, 420);
            if (oldPage != null) Motion.After(440, () => HidePageIfStale(oldPage));
        }
        else
        {
            if (oldPage != null)
            {
                System.Windows.Controls.Panel.SetZIndex(oldPage, 3);
                Motion.FadeOut(oldPage, 260, () => HidePageIfStale(oldPage));
            }
            foreach (var el in AppsPage()) Motion.SlideIn(el, dx, 0, 420);
        }
    }

    /// <summary>Спрятать страницу, ушедшую со сцены, если её за это время снова не выбрали.</summary>
    void HidePageIfStale(FrameworkElement page)
    {
        if (OverlayOf(_tabIndex) == page) return;
        page.Visibility = Visibility.Collapsed;
        System.Windows.Controls.Panel.SetZIndex(page, 0);
        Motion.Reset(page);
    }

    /// <summary>Подчёркивание переезжает под выбранную вкладку.</summary>
    void MoveIndicator(bool animate)
    {
        var tab = TopTabs[_tabIndex];
        if (tab.ActualWidth <= 0) return;
        var p = tab.TransformToAncestor(TabBar).Transform(new Point(0, 0));
        Motion.MoveTo(TabIndicator, TabIndicatorX, TabBar.Margin.Left + p.X + 6, Math.Max(0, tab.ActualWidth - 12), animate);
    }

    // ---------- периоды статистики ----------

    int _periodIndex;

    System.Windows.Controls.RadioButton[] PeriodTabs => new[] { PSession, PDay, PWeek, PAll };

    void MovePeriodIndicator(bool animate)
    {
        var tab = PeriodTabs[_periodIndex];
        if (tab.ActualWidth <= 0) return;
        var p = tab.TransformToAncestor(PeriodBar).Transform(new Point(0, 0));
        Motion.MoveTo(PeriodIndicator, PeriodIndicatorX, p.X + 6, Math.Max(0, tab.ActualWidth - 12), animate);
    }

    // ---------- «таблетка» режима сети ----------

    int _modeIndex = -1;

    void ModeGrid_SizeChanged(object sender, SizeChangedEventArgs e) => MoveModePill(false);

    /// <summary>«Таблетка» под выбранным режимом сети плавно переезжает к нему.</summary>
    void MoveModePill(bool animate)
    {
        int idx = ModeOn.IsChecked == true ? 0 : ModeNone.IsChecked == true ? 1 : ModeOff.IsChecked == true ? 2 : -1;
        double w = ModeGrid.ActualWidth / 3;
        if (idx < 0 || w <= 0)
        {
            // режим не выбран или вкладка ещё не отрисована: прячем «таблетку», покажем, когда будет где
            if (_modeIndex >= 0) Motion.Go(ModePill, OpacityProperty, 0, 220, Motion.InOut);
            _modeIndex = -1;
            return;
        }
        bool slide = animate && _modeIndex >= 0 && _modeIndex != idx;
        if (_modeIndex < 0) Motion.Go(ModePill, OpacityProperty, 1, 260, Motion.InOut);
        if (slide || _modeIndex != idx || !animate) Motion.MoveTo(ModePill, ModePillX, idx * w, w, slide);
        _modeIndex = idx;
    }

    // ---------- вкладка «Транслятор» ----------

    /// <summary>Табло и тревога; назначается из App после создания панели.</summary>
    public TranslatorController? Translator { get; set; }

    sealed class TrackerVm
    {
        /// <summary>Адрес трекера; пустая строка — пункт «Нет трекера».</summary>
        public string Url { get; init; } = "";
        public string Label { get; init; } = "";
        /// <summary>К каким сетям привязан трекер.</summary>
        public string Networks { get; init; } = "";
        public Visibility NetworksVisibility => Networks.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ForgetVisibility => Url.Length > 0 ? Visibility.Visible : Visibility.Hidden;
        /// <summary>Выбран для сети, к которой подключены сейчас.</summary>
        public bool Active { get; init; }
        /// <summary>Выбирать можно, только когда есть подключение к сети.</summary>
        public bool Enabled { get; init; } = true;
    }

    void RefreshTranslator(bool force = false)
    {
        if (TranslatorView.Visibility != Visibility.Visible) return;
        TranslatorSwitch.IsChecked = _engine.Setting(s => s.TranslatorOn);
        AlertSwitch.IsChecked = _engine.Setting(s => s.AlertOn);
        AlertSoundSwitch.IsChecked = _engine.Setting(s => s.AlertSound);
        if (!AlertMbBox.IsKeyboardFocused) AlertMbBox.Text = _engine.Setting(s => s.AlertMbPerMin).ToString();

        if (Translator != null)
            TranslatorLive.Text = "Сейчас: " + Translator.MbPerMin.ToString(Translator.MbPerMin >= 100 ? "0" : "0.0") +
                                  " МБ/мин" + (_engine.Setting(s => s.TranslatorOn)
                                      ? " · с включения табло " + Fmt.Bytes(Translator.UsedSinceOn) : "");

        RefreshTrackerStatus();

        // для какой сети выбираем трекер
        var cur = _engine.CurrentNetwork;
        var active = _engine.CurrentTracker();
        TrackerNetText.Text = cur == null
            ? "Сейчас нет подключения к сети — трекер выберете, когда подключитесь."
            : "Сеть «" + cur.Name + "»: " + (active != null
                ? "трекер " + new Uri(active).Host + (_engine.CurrentTrackerAuto() ? " (найден автоматически)" : "")
                : "без трекера");

        if (!force && TrackerList.IsMouseOver) return;
        var trackers = _engine.Setting(s => s.Trackers.ToList());
        var nets = _engine.GetNetworks();
        // пересобираем список, только если он поменялся: иначе точки выбора «мигали» бы каждую секунду
        var sig = (cur?.Id ?? "") + "|" + active + "|" + string.Join("|", trackers) + "|" +
                  string.Join("|", nets.Where(n => n.Tracker != null).Select(n => n.Id + "=" + n.Tracker));
        if (sig == _trackerSig && TrackerList.ItemsSource != null) return;
        _trackerSig = sig;

        var items = new List<TrackerVm>
        {
            new() { Url = "", Label = "Нет трекера", Active = cur != null && active == null, Enabled = cur != null },
        };
        foreach (var u in trackers)
        {
            var bound = nets.Where(n => n.Tracker == u).Select(n => n.Name).ToList();
            items.Add(new TrackerVm
            {
                Url = u,
                Label = Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri.Host : u,
                Networks = bound.Count == 0 ? "ни к одной сети не привязан" : "сеть: " + string.Join(", ", bound),
                Active = cur != null && u == active,
                Enabled = cur != null,
            });
        }
        TrackerList.ItemsSource = items;
    }

    void TranslatorSwitch_Click(object sender, RoutedEventArgs e)
    {
        bool on = TranslatorSwitch.IsChecked == true;
        _engine.ChangeSettings(s => s.TranslatorOn = on);
        Translator?.ApplyOverlay();
        RefreshTranslator(true);
    }

    void TrackerBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        TrackerError.Visibility = Visibility.Collapsed;
        if (e.Key == System.Windows.Input.Key.Enter) AddTracker_Click(sender, e);
    }

    void AddTracker_Click(object sender, RoutedEventArgs e)
    {
        var text = TrackerBox.Text.Trim();
        if (text.Length == 0)
        {
            TrackerError.Text = "Введите адрес сайта.";
            TrackerError.Visibility = Visibility.Visible;
            return;
        }
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.Host.Contains('.') ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            TrackerError.Text = "Это не похоже на адрес сайта. Пример: starlink.example.com";
            TrackerError.Visibility = Visibility.Visible;
            return;
        }
        var url = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        _engine.ChangeSettings(s =>
        {
            if (!s.Trackers.Contains(url, StringComparer.OrdinalIgnoreCase)) s.Trackers.Add(url);
            s.ActiveTracker ??= url;
        });
        // новый трекер сразу привязываем к текущей сети, если у неё трекера ещё нет
        if (_engine.CurrentNetId is string net && _engine.CurrentTracker() == null)
            _engine.SetNetworkTracker(net, url);
        TrackerBox.Clear();
        TrackerError.Visibility = Visibility.Collapsed;
        RefreshTranslator(true);
    }

    /// <summary>Выбор трекера для сети, к которой подключены сейчас (или «Нет трекера»).</summary>
    void Tracker_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TrackerVm vm) return;
        if (_engine.CurrentNetId is not string net) return;
        _engine.SetNetworkTracker(net, vm.Url.Length > 0 ? vm.Url : null);
        RefreshTranslator(true);
    }

    void TrackerRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TrackerVm vm || vm.Url.Length == 0) return;
        _engine.ChangeSettings(s =>
        {
            s.Trackers.RemoveAll(u => u == vm.Url);
            if (s.ActiveTracker == vm.Url) s.ActiveTracker = s.Trackers.FirstOrDefault();
            // сети, к которым он был привязан, остаются без трекера
            foreach (var n in s.Networks.Where(n => n.Tracker == vm.Url)) n.Tracker = null;
        });
        RefreshTranslator(true);
        RefreshAll();
    }

    void AlertSwitch_Click(object sender, RoutedEventArgs e)
    {
        bool on = AlertSwitch.IsChecked == true;
        _engine.ChangeSettings(s => s.AlertOn = on);
    }

    void AlertSound_Click(object sender, RoutedEventArgs e)
    {
        bool on = AlertSoundSwitch.IsChecked == true;
        _engine.ChangeSettings(s => s.AlertSound = on);
    }

    void AlertMbBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        AlertMbError.Visibility = Visibility.Collapsed;
        if (e.Key == System.Windows.Input.Key.Enter) ApplyAlertMb();
    }

    void AlertMbBox_LostFocus(object sender, RoutedEventArgs e) => ApplyAlertMb();

    void ApplyAlertMb()
    {
        if (int.TryParse(AlertMbBox.Text.Trim(), out int mb) && mb >= 1 && mb <= 10000)
        {
            _engine.ChangeSettings(s => s.AlertMbPerMin = mb);
            AlertMbError.Visibility = Visibility.Collapsed;
        }
        else
        {
            AlertMbError.Visibility = Visibility.Visible;
        }
    }

    void TestAlert_Click(object sender, RoutedEventArgs e) => Translator?.ShowTestAlert();

    void RefreshTrackerStatus()
    {
        var t = Translator;
        if (t == null) return;
        var tracker = t.ActiveTracker();
        bool supported = tracker != null && TrackerReader.IsSupported(tracker);
        TrackerRemain.Text = t.RemainText();

        string info;
        if (tracker == null) info = "У этой сети нет трекера, поэтому капли нет и показывается расход. Если трекер есть — отметьте его выше.";
        else if (!supported) info = "NetPause пока умеет читать только ic.zwana.io. Пришлите мне адрес — добавлю и этот сайт.";
        else
        {
            var parts = new List<string>
            {
                "норма " + TranslatorController.FormatMb(t.DailyQuotaMb) + " в сутки, новая в " +
                TranslatorController.NextResetLocal.ToString("HH:mm"),
            };
            if (t.Last is { Ok: true } r)
            {
                if (r.Plan != null) parts.Add("тариф «" + r.Plan + "»");
                if (r.Provider != null) parts.Add("провайдер " + r.Provider);
                parts.Add("ответ сайта в " + r.At.ToString("HH:mm"));
                if (r.Error != null) parts.Add("сейчас нет связи: " + r.Error);
            }
            else if (t.HasLogin) parts.Add("вход сохранён");
            else parts.Add("нажмите «Войти и открыть сайт» и введите логин и пароль от трекера");
            info = string.Join(" · ", parts);
        }
        TrackerInfo.Text = info;

        // включён ли интернет на сайте и что NetPause с ним делал последним
        string? data = null;
        if (supported && t.Last is { Ok: true } lr) data = "Интернет на сайте " + (lr.DataOn ? "включён" : "выключен");
        if (supported && t.DataNote != null) data = (data ?? "Интернет на сайте") + " · " + t.DataNote;
        TrackerData.Text = data ?? "";
        TrackerData.Visibility = data != null ? Visibility.Visible : Visibility.Collapsed;
        TrackerData.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
            supported && t.Last is { Ok: true, DataOn: false } ? "Warn" : "Fg2");

        RefreshSession(t, supported);

        bool hasPassword = supported && _engine.Setting(s => s.TrackerPasswords.ContainsKey(TrackerReader.Origin(tracker!)));
        TrackerOpenBtn.Visibility = tracker != null ? Visibility.Visible : Visibility.Collapsed;
        TrackerOpenBtn.Content = supported ? "Войти и открыть сайт" : "Открыть сайт";
        TrackerRefreshBtn.Visibility = supported && t.HasLogin ? Visibility.Visible : Visibility.Collapsed;
        TrackerChangeBtn.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        TrackerLogoutBtn.Visibility = supported && (t.HasLogin || hasPassword) ? Visibility.Visible : Visibility.Collapsed;

        TrackerSettings.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        if (supported)
        {
            AutoDataSwitch.IsChecked = _engine.Setting(s => s.AutoDataOn);
            var (quotaMb, source) = t.QuotaInfo();
            if (!DailyMbBox.IsKeyboardFocused) DailyMbBox.Text = ((int)Math.Round(quotaMb)).ToString();
            DailyMbNote.Text = (source switch
            {
                "site" => "Норма взята с сайта: столько он начисляет каждые сутки.",
                "manual" => "Норма задана вручную. Очистите поле — NetPause возьмёт её с сайта.",
                _ => "Сайт ещё не сообщил норму, пока считаем 1000 МБ.",
            }) + " Новая норма приходит в " + TranslatorController.NextResetLocal.ToString("HH:mm") +
                 " по времени компьютера (00:00 UTC). Капля показывает остаток от неё.";
        }
    }

    /// <summary>
    /// «Войти и открыть сайт»: вход сохранёнными данными (или окно входа, если их ещё нет),
    /// включение интернета на сайте и сам сайт в браузере по умолчанию.
    /// </summary>
    async void TrackerOpen_Click(object sender, RoutedEventArgs e)
    {
        if (Translator == null) return;
        TrackerOpenBtn.IsEnabled = false;
        System.Threading.Tasks.Task task;
        _suppressHide = true; // окно входа не должно прятать панель
        try { task = Translator.ConnectAndOpen(this); } // окно входа (если нужно) показывается до первого ожидания
        finally { _suppressHide = false; }
        try { await task; } catch { }
        TrackerOpenBtn.IsEnabled = true;
        RefreshTranslator(true);
        RefreshAll();
    }

    sealed class SessionVm
    {
        public string Ip { get; init; } = "";
        public string Name { get; init; } = "";
        public string Sub { get; init; } = "";
        public Visibility RemoveVisibility { get; init; }
    }

    string? _sessionSig;

    /// <summary>Блок «В сессии»: виден, только когда кроме этого ноутбука к интернету присоединён кто-то ещё.</summary>
    void RefreshSession(TranslatorController t, bool supported)
    {
        bool show = supported && t.OthersPresent;
        SessionBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) { _sessionSig = null; return; }

        var others = t.Devices.Where(d => !d.Self).ToList();
        var items = t.Devices
            .OrderByDescending(d => d.Self)
            .Select(d => new SessionVm
            {
                Ip = d.Ip,
                Name = d.Name,
                Sub = (d.Primary ? "главное" : "присоединён") +
                      (d.Self ? " · " + TranslatorController.FormatMb(t.SelfTodayMb)
                       : others.Count == 1 ? " · " + TranslatorController.FormatMb(t.OtherTodayMb) : "") +
                      (d.Self ? "" : " · " + d.Ip),
                RemoveVisibility = !d.Self && !d.Primary ? Visibility.Visible : Visibility.Collapsed,
            })
            .ToList();
        var since = DateTime.UtcNow.Date.ToLocalTime().ToString("HH:mm");
        SessionNote.Text = (others.Count > 1 ? "Другие устройства с " + since + ": " +
                            TranslatorController.FormatMb(t.OtherTodayMb) + ". " : "") +
                           "Расход с " + since + ". Сколько тратят другие устройства, NetPause считает по тому, " +
                           "насколько общий остаток уменьшился сверх расхода ноутбука, — это оценка с шагом в минуту.";

        // пересобираем список, только если он изменился и мышь не над ним (иначе кнопка «мигала» бы)
        var sig = string.Join("|", items.Select(i => i.Ip + ":" + i.Sub));
        if (sig == _sessionSig || SessionList.IsMouseOver) return;
        _sessionSig = sig;
        SessionList.ItemsSource = items;
    }

    async void SessionRemove_Click(object sender, RoutedEventArgs e)
    {
        if (Translator == null || (sender as FrameworkElement)?.Tag is not string ip) return;
        if (sender is System.Windows.Controls.Button b) b.IsEnabled = false;
        await Translator.RemoveDevice(ip);
        RefreshTranslator(true);
    }

    void AutoDataSwitch_Click(object sender, RoutedEventArgs e)
    {
        bool on = AutoDataSwitch.IsChecked == true;
        _engine.ChangeSettings(s => s.AutoDataOn = on);
    }

    void DailyMbBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        DailyMbError.Visibility = Visibility.Collapsed;
        if (e.Key == System.Windows.Input.Key.Enter) ApplyDailyMb();
    }

    void DailyMbBox_LostFocus(object sender, RoutedEventArgs e) => ApplyDailyMb();

    void ApplyDailyMb()
    {
        var tracker = Translator?.ActiveTracker();
        if (tracker == null) return;
        var origin = TrackerReader.Origin(tracker);
        var text = DailyMbBox.Text.Trim();
        if (text.Length == 0)
        {
            // пустое поле — снова берём норму с сайта
            _engine.ChangeSettings(s => s.TrackerDailyMb.Remove(origin));
            DailyMbError.Visibility = Visibility.Collapsed;
            RefreshAll();
            return;
        }
        if (int.TryParse(text, out int mb) && mb >= 1 && mb <= 1000000)
        {
            // Число совпадает с нормой, которая действовала бы и так (с сайта или 1000 по умолчанию), —
            // вручную ничего не задаём: иначе простой уход из поля превращал бы норму сайта в ручную.
            int natural = _engine.Setting(s => s.TrackerDailyAutoMb.TryGetValue(origin, out var a) && a > 0 ? a : 1000);
            if (mb == natural) _engine.ChangeSettings(s => s.TrackerDailyMb.Remove(origin));
            else _engine.ChangeSettings(s => s.TrackerDailyMb[origin] = mb);
            DailyMbError.Visibility = Visibility.Collapsed;
            RefreshAll();
        }
        else DailyMbError.Visibility = Visibility.Visible;
    }

    void TrackerPassword_Click(object sender, RoutedEventArgs e)
    {
        if (Translator == null) return;
        _suppressHide = true;
        try { Translator.LoginWithPassword(this); }
        finally { _suppressHide = false; }
        RefreshTranslator(true);
    }

    void TrackerRefresh_Click(object sender, RoutedEventArgs e)
    {
        Translator?.ReadNow();
        TrackerInfo.Text = "Спрашиваем трекер…";
    }

    void TrackerLogout_Click(object sender, RoutedEventArgs e)
    {
        Translator?.Logout();
        RefreshTranslator(true);
    }

    // ---------- клавиатура ----------

    /// <summary>Список, который сейчас на экране.</summary>
    System.Windows.Controls.ScrollViewer ActiveScroll() =>
        _detail != null ? HostScroll
        : _openFolder != null ? FolderScroll
        : _tabIndex switch
        {
            1 => TranslatorView,
            2 => StatScroll,
            3 => NetworksView,
            4 => SettingsView,
            _ => AppScroll,
        };

    void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // в поле ввода стрелки и Home/End двигают курсор в тексте, а не список
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox && e.Key != System.Windows.Input.Key.Escape)
            return;
        var sv = ActiveScroll();
        switch (e.Key)
        {
            case System.Windows.Input.Key.Down: SmoothScroll.By(sv, SmoothScroll.LineStep); break;
            case System.Windows.Input.Key.Up: SmoothScroll.By(sv, -SmoothScroll.LineStep); break;
            case System.Windows.Input.Key.PageDown: SmoothScroll.By(sv, SmoothScroll.Page(sv)); break;
            case System.Windows.Input.Key.PageUp: SmoothScroll.By(sv, -SmoothScroll.Page(sv)); break;
            case System.Windows.Input.Key.Home: SmoothScroll.To(sv, 0); break;
            case System.Windows.Input.Key.End: SmoothScroll.To(sv, sv.ScrollableHeight); break;
            case System.Windows.Input.Key.Escape:
                // Esc: из подробностей — назад к списку, иначе спрятать панель
                if (_detail != null) BackFromDetail();
                else if (_openFolder != null) BackFromFolder();
                else HidePanel();
                break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Обновить панель снаружи (например, когда экономию переключила сеть).</summary>
    public void RefreshIfVisible()
    {
        if (IsVisible) RefreshAll();
    }

    // ---------- вкладка «Сети» ----------

    void RefreshNetworks(bool force = false)
    {
        if (NetworksView.Visibility != Visibility.Visible) return;
        var cur = _engine.CurrentNetwork;
        var all = _engine.GetNetworks();
        bool autoOn = _engine.AutoHotspot;
        AutoHotspotSwitch.IsChecked = autoOn;

        // текущая сеть
        var known = cur == null ? null : all.FirstOrDefault(n => n.Id == cur.Id);
        if (cur == null)
        {
            CurIcon.Text = "";
            CurName.Text = "Нет подключения";
            CurKind.Text = "Правило применится, когда компьютер подключится к сети.";
            CurPhoneBadge.Visibility = Visibility.Collapsed;
        }
        else
        {
            CurIcon.Text = NetVm.GlyphFor(cur.Kind);
            CurName.Text = cur.Name;
            CurKind.Text = cur.Kind;
            var reason = cur.Reason ?? known?.PhoneReason;
            bool phone = cur.Phone || known?.Phone == true;
            CurPhoneBadge.Visibility = phone ? Visibility.Visible : Visibility.Collapsed;
            CurPhoneText.Text = "Похоже на раздачу с телефона: " + reason;
        }

        bool has = cur != null && known != null;
        ModeOn.IsEnabled = ModeNone.IsEnabled = ModeOff.IsEnabled = has;
        if (has)
        {
            var eff = _engine.EffectiveModeOf(cur!.Id);
            ModeOn.IsChecked = eff == NetMode.On;
            ModeNone.IsChecked = eff == NetMode.None;
            ModeOff.IsChecked = eff == NetMode.Off;
            bool manual = known!.Mode != null;
            ModeAutoBtn.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
            ModeNote.Text = manual
                ? "Это правило выбрали вы."
                : eff == NetMode.On
                    ? "Выбрано автоматически, потому что сеть похожа на раздачу с телефона."
                    : autoOn
                        ? "Выбрано автоматически: обычная сеть, экономия сама не включается."
                        : "Автоматическое распознавание выключено.";
        }
        else
        {
            ModeOn.IsChecked = ModeNone.IsChecked = ModeOff.IsChecked = false;
            ModeAutoBtn.Visibility = Visibility.Collapsed;
            ModeNote.Text = "";
        }
        MoveModePill(true);

        if (NetList.IsMouseOver && !force) return; // не перестраиваем список под курсором
        var netSig = (cur?.Id ?? "") + autoOn + "|" + string.Join("|", all.Select(n => n.Id + n.Mode + n.Phone + n.Tracker + n.LastSeen?.ToString("g")));
        if (!force && netSig == _netSig) return;
        _netSig = netSig;

        // «Включать» — цветом оформления с белым текстом, остальные — светлые с тёмным текстом
        var onBg = (Brush)FindResource("On");
        var offBg = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));
        var darkFg = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
        var rows = all
            .OrderByDescending(n => cur != null && n.Id == cur.Id)
            .ThenByDescending(n => n.Mode != null)
            .ThenByDescending(n => n.LastSeen ?? DateTime.MinValue)
            .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(n =>
            {
                bool isCur = cur != null && n.Id == cur.Id;
                var eff = n.Mode ?? (autoOn && n.Phone ? NetMode.On : NetMode.None);
                var rule = n.Mode == null
                    ? (eff == NetMode.On ? "Авто: вкл" : "Авто")
                    : n.Mode switch { NetMode.On => "Включать", NetMode.Off => "Выключать", _ => "Не трогать" };
                var sub = n.Kind;
                if (isCur) sub += " · подключено сейчас";
                else if (n.LastSeen != null) sub += " · " + n.LastSeen.Value.ToString("d MMM, HH:mm");
                else sub += " · сохранена в Windows";
                if (n.Phone) sub += " · похожа на телефон";
                if (n.Tracker != null && Uri.TryCreate(n.Tracker, UriKind.Absolute, out var tu)) sub += " · трекер " + tu.Host;
                return new NetVm
                {
                    Id = n.Id,
                    Glyph = NetVm.GlyphFor(n.Kind),
                    Name = n.Name,
                    Sub = sub,
                    RuleText = rule,
                    RuleBg = eff == NetMode.On ? onBg : offBg,
                    RuleFg = eff == NetMode.On ? Brushes.White : darkFg,
                    Weight = isCur ? FontWeights.SemiBold : FontWeights.Normal,
                    ForgetVisibility = isCur ? Visibility.Hidden : Visibility.Visible,
                    Mode = n.Mode,
                };
            }).ToList();
        NetList.ItemsSource = rows;
    }

    void Mode_Click(object sender, RoutedEventArgs e)
    {
        var cur = _engine.CurrentNetwork;
        if (cur == null) return;
        var mode = ModeOn.IsChecked == true ? NetMode.On : ModeOff.IsChecked == true ? NetMode.Off : NetMode.None;
        _engine.SetNetworkMode(cur.Id, mode);
        RefreshNetworks(true);
        RefreshSoon();
    }

    void ModeAuto_Click(object sender, RoutedEventArgs e)
    {
        var cur = _engine.CurrentNetwork;
        if (cur == null) return;
        _engine.SetNetworkMode(cur.Id, null);
        RefreshNetworks(true);
        RefreshSoon();
    }

    void AutoHotspot_Click(object sender, RoutedEventArgs e)
    {
        _engine.SetAutoHotspot(AutoHotspotSwitch.IsChecked == true);
        RefreshNetworks(true);
        RefreshSoon();
    }

    void NetRule_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NetVm vm) return;
        // по кругу: авто → включать → выключать → не трогать → авто
        NetMode? next = vm.Mode switch
        {
            null => NetMode.On,
            NetMode.On => NetMode.Off,
            NetMode.Off => NetMode.None,
            _ => null,
        };
        _engine.SetNetworkMode(vm.Id, next);
        RefreshNetworks(true);
        RefreshSoon();
    }

    void NetForget_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NetVm vm) return;
        _engine.ForgetNetwork(vm.Id);
        RefreshNetworks(true);
    }

    /// <summary>Правило применяется в фоне: через секунду показываем, что получилось.</summary>
    void RefreshSoon()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            RefreshAll();
            EconomyChanged?.Invoke();
        };
        t.Start();
    }

    // ---------- вкладка «Настройки» ----------

    void RefreshSettings()
    {
        if (SettingsView.Visibility != Visibility.Visible) return;
        bool economy = _engine.EconomyOn;
        StrictSwitch.IsChecked = _engine.Strict;
        MeteredSwitch.IsChecked = _engine.Metered;
        ChromeSwitch.IsChecked = _engine.ChromeNames;
        StrictNote.Text = !_engine.Strict ? ""
            : economy ? "Сейчас действует." : "Начнёт действовать, когда включите экономию.";
        MeteredNote.Text = !_engine.Metered ? ""
            : !economy ? "Начнёт действовать, когда включите экономию."
            : _engine.MeteredStatus.Length > 0 ? "Сейчас: " + _engine.MeteredStatus + "." : "Применяется…";
    }

    void Strict_Click(object sender, RoutedEventArgs e)
    {
        bool on = StrictSwitch.IsChecked == true;
        if (on)
        {
            _suppressHide = true;
            var r = MessageBox.Show(this,
                "В строгом режиме при включённой экономии интернет пропадёт у всех программ, " +
                "которые не отмечены в списке, в том числе у тех, которых там ещё нет.\n\n" +
                "Если что-то нужное перестанет работать, найдите эту программу в списке и включите её. " +
                "Выключение экономии или строгого режима сразу возвращает всё как было.\n\n" +
                "Включить строгий режим?",
                "NetPause", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            _suppressHide = false;
            if (r != MessageBoxResult.Yes)
            {
                StrictSwitch.IsChecked = false;
                return;
            }
        }
        _engine.SetStrict(on);
        RefreshAll();
    }

    void Chrome_Click(object sender, RoutedEventArgs e)
    {
        try { _engine.SetChromeNames(ChromeSwitch.IsChecked == true); }
        catch (Exception ex)
        {
            _suppressHide = true;
            MessageBox.Show(this, "Не удалось изменить настройки Chrome: " + ex.Message, "NetPause");
            _suppressHide = false;
        }
        ChromeSwitch.IsChecked = _engine.ChromeNames;
    }

    void Metered_Click(object sender, RoutedEventArgs e)
    {
        _engine.SetMetered(MeteredSwitch.IsChecked == true);
        RefreshSettings();
    }

    void Period_Click(object sender, RoutedEventArgs e)
    {
        int idx = Array.FindIndex(PeriodTabs, t => t.IsChecked == true);
        if (idx < 0) idx = 0;
        if (idx == _periodIndex) return;
        int dir = idx > _periodIndex ? 1 : -1;
        _periodIndex = idx;
        RefreshStats(true);
        MovePeriodIndicator(true);
        // итог и список мягко въезжают с той стороны, куда переключили период
        Motion.SlideIn(StatCard, 16 * dir, 0, 400);
        Motion.SlideIn(StatListArea, 16 * dir, 0, 400);
        Motion.Stagger(StatList);
    }

    List<StatVm>? _statRows;
    string? _statShape;

    void RefreshStats(bool force = false)
    {
        if (StatsView.Visibility != Visibility.Visible) return;
        var period = PDay.IsChecked == true ? Period.Day
            : PWeek.IsChecked == true ? Period.Week
            : PAll.IsChecked == true ? Period.All
            : Period.Session;
        var rows = _engine.GetPeriod(period, out long total, out string caption);
        StatTotal.Text = total > 0 ? Fmt.Bytes(total) : "0 МБ";
        StatCaption.Text = caption;
        StatEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        static string Share(long b, long total)
        {
            if (total <= 0) return "";
            double p = b * 100.0 / total;
            return p.ToString(p >= 10 ? "0" : "0.0") + "% периода";
        }

        // если порядок строк тот же — только обновляем числа на месте: ничего не мигает
        var shape = period + "|" + string.Join("|", rows.Select(r => r.Name + "\u0001" + r.Path));
        if (!force && shape == _statShape && _statRows != null && _statRows.Count == rows.Count)
        {
            for (int i = 0; i < rows.Count; i++) _statRows[i].Set(Fmt.Bytes(rows[i].Bytes), Share(rows[i].Bytes, total));
            return;
        }
        if (StatList.IsMouseOver && !force) return; // порядок поменялся — перестроим, когда мышь уйдёт со списка
        _statShape = shape;
        _statRows = rows.Select(r => new StatVm
        {
            Icon = AppVm.LoadIcon(r.Path),
            Name = r.Name,
            Path = r.Path,
            BytesText = Fmt.Bytes(r.Bytes),
            Share = Share(r.Bytes, total),
        }).ToList();
        StatList.ItemsSource = _statRows;
    }

    // ---------- экран подробностей ----------

    /// <summary>На сколько выезжает экран подробностей или папки.</summary>
    double NavDistance => Math.Max(90, RootBorder.ActualWidth * 0.34);

    /// <summary>Номер перехода: отменяет устаревшее завершение анимации, если её перебили.</summary>
    int _navGen;

    void Row_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AppVm vm) return;
        _navGen++;
        _detail = vm;
        _expanded.Clear();
        _detailGroups = null;
        _detailShape = null;
        HostList.ItemsSource = null;
        DetailIcon.Source = vm.Icon;
        DetailName.Text = vm.Name;
        DetailView.Visibility = Visibility.Visible;
        HostScroll.ScrollToTop();
        RefreshDetail(true);
        // подробности выезжают справа, а экран под ними слегка уходит влево и притухает
        var back = _openFolder != null ? (UIElement)FolderView : MainLayer;
        Motion.Push(DetailView, back, NavDistance);
        Motion.Stagger(HostList);
    }

    void Back_Click(object sender, RoutedEventArgs e) => BackFromDetail();

    /// <summary>Назад из подробностей: они уезжают вправо, экран под ними возвращается на место.</summary>
    void BackFromDetail()
    {
        if (_detail == null) return;
        int gen = ++_navGen;
        _detail = null;
        var back = _openFolder != null ? (UIElement)FolderView : MainLayer;
        Motion.Pop(DetailView, back, NavDistance, () =>
        {
            if (gen != _navGen) return; // за это время открыли что-то другое
            FinishCloseDetail();
        });
    }

    void FinishCloseDetail()
    {
        _expanded.Clear();
        _detailGroups = null;
        _detailShape = null;
        HostList.ItemsSource = null;
        DetailView.Visibility = Visibility.Collapsed;
        Motion.Reset(DetailView);
    }

    /// <summary>Закрыть подробности сразу, без анимации (при повторном открытии панели).</summary>
    void CloseDetail()
    {
        _navGen++;
        _detail = null;
        FinishCloseDetail();
        Motion.Reset(MainLayer);
        Motion.Reset(FolderView);
    }

    /// <summary>Раскрытые группы на экране подробностей (по ключу группы).</summary>
    readonly System.Collections.Generic.HashSet<string> _expanded = new();

    /// <summary>Щелчок по группе сайтов: она плавно раскрывается «гармошкой», стрелка поворачивается.</summary>
    void Group_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GroupVm g || !g.Expandable) return;
        var header = (FrameworkElement)sender;
        var kids = (header.Parent as System.Windows.Controls.Panel)?.Children
            .OfType<System.Windows.Controls.ItemsControl>().FirstOrDefault();
        var chev = FindTagged(header, "chev");
        if (!g.Expanded)
        {
            g.Expanded = true;
            _expanded.Add(g.Key);
            if (kids != null) Motion.Expand(kids);
            if (chev != null) Motion.Rotate(chev, 90);
        }
        else
        {
            _expanded.Remove(g.Key);
            if (chev != null) Motion.Rotate(chev, 0);
            if (kids != null) Motion.Collapse(kids, () => g.Expanded = false);
            else g.Expanded = false;
        }
    }

    /// <summary>Найти в дереве элемент с указанной меткой Tag.</summary>
    static UIElement? FindTagged(DependencyObject root, string tag)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && Equals(fe.Tag, tag)) return fe;
            if (FindTagged(child, tag) is UIElement found) return found;
        }
        return null;
    }

    List<GroupVm>? _detailGroups;
    string? _detailShape;

    /// <summary>Правая кнопка (или касание двумя пальцами) → «Удалить из списка»: забыть сайт целиком или один адрес.</summary>
    void ForgetSite_Click(object sender, RoutedEventArgs e)
    {
        if (_detail == null || (sender as FrameworkElement)?.DataContext is not object dc) return;
        var hosts = dc switch
        {
            GroupVm g => g.Children.Select(c => c.Host).ToList(),
            ChildVm c => new List<string> { c.Host },
            _ => new List<string>(),
        };
        _engine.ForgetHosts(_detail.Entry.Key, hosts);
        RefreshDetail(true);
    }

    void RefreshDetail(bool force = false)
    {
        if (_detail == null) return;
        DetailSub.Text = _detail.Sub;

        var infos = _engine.GetDetails(_detail.Entry.Key);
        var groups = DetailGrouper.Build(_engine, _detail.Entry, infos, _expanded, _engine.EconomyOn);

        // «форма» списка: какие группы, в каком порядке, что внутри и что заблокировано
        var shape = string.Join("|", groups.Select(g =>
            g.Key + "\u0001" + g.Detail + "\u0001" + g.Blocked + "\u0001" + (g.Icon != null) + "\u0001" +
            string.Join(",", g.Children.Select(c => c.Host + (c.Blocked ? "!" : "")))));

        // форма та же — обновляем только числа, строки остаются на месте (не мигают, анимации не сбиваются)
        if (!force && shape == _detailShape && _detailGroups != null && _detailGroups.Count == groups.Count)
        {
            for (int i = 0; i < groups.Count; i++) _detailGroups[i].UpdateFrom(groups[i]);
            return;
        }
        // форма поменялась — перестраиваем, но не под курсором: строки не уедут из-под мыши
        if (HostList.IsMouseOver && !force) return;
        _detailShape = shape;
        _detailGroups = groups;
        HostList.ItemsSource = groups;
        DetailEmpty.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Держит список отсортированным по трафику, пока панель открыта.</summary>
    /// <summary>
    /// Пересобирает главный список: обычные программы по отдельности,
    /// части Windows и разовые установщики — в своих папках.
    /// </summary>
    void RebuildRows()
    {
        var groups = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<AppVm>>();
        var rows = new System.Collections.Generic.List<IRowVm>();
        foreach (var e in _engine.GetApps())
        {
            if (!_vms.TryGetValue(e, out var vm)) _vms[e] = vm = new AppVm(_engine, e);
            var kind = FolderVm.FolderOf(e);
            if (kind == null) { rows.Add(vm); continue; }
            if (!groups.TryGetValue(kind, out var list)) groups[kind] = list = new();
            list.Add(vm);
        }
        foreach (var kv in groups)
        {
            if (!_folders.TryGetValue(kv.Key, out var f)) _folders[kv.Key] = f = new FolderVm(_engine, kv.Key);
            f.Members = kv.Value;
            f.Refresh();
            rows.Add(f);
        }

        _apps.Clear();
        foreach (var r in rows.OrderByDescending(r => r.SortBytes).ThenBy(r => r.Name)) _apps.Add(r);
        EmptyText.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_openFolder != null) FillFolder();
    }

    // ---------- папки ----------

    void Folder_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FolderVm f) return;
        _navGen++;
        _openFolder = f;
        FolderGlyph.Text = f.Glyph;
        FolderName.Text = f.Name;
        FolderNote.Text = f.Note;
        FolderView.Visibility = Visibility.Visible;
        FillFolder();
        FolderScroll.ScrollToTop();
        // папка выезжает справа, список программ под ней слегка уходит влево и притухает
        Motion.Push(FolderView, MainLayer, NavDistance);
        Motion.Stagger(FolderList);
    }

    void FolderBack_Click(object sender, RoutedEventArgs e) => BackFromFolder();

    /// <summary>Назад из папки: она уезжает вправо, список программ возвращается на место.</summary>
    void BackFromFolder()
    {
        if (_openFolder == null) return;
        int gen = ++_navGen;
        _openFolder = null;
        Motion.Pop(FolderView, MainLayer, NavDistance, () =>
        {
            if (gen != _navGen) return;
            _folderApps.Clear();
            FolderView.Visibility = Visibility.Collapsed;
            Motion.Reset(FolderView);
        });
    }

    /// <summary>Закрыть папку сразу, без анимации (при повторном открытии панели).</summary>
    void CloseFolder()
    {
        _navGen++;
        _openFolder = null;
        _folderApps.Clear();
        FolderView.Visibility = Visibility.Collapsed;
        Motion.Reset(FolderView);
        Motion.Reset(MainLayer);
    }

    void FillFolder()
    {
        if (_openFolder == null) return;
        _folderApps.Clear();
        foreach (var m in _openFolder.Members.OrderByDescending(m => m.SortBytes).ThenBy(m => m.Name))
            _folderApps.Add(m);
        FolderSub.Text = _openFolder.Sub;
    }

    void RefreshFolder()
    {
        if (_openFolder == null) return;
        FolderSub.Text = _openFolder.Sub;
        foreach (var m in _folderApps) m.Refresh();
        if (FolderList.IsMouseOver) return;
        var sorted = _folderApps.OrderByDescending(m => m.SortBytes).ThenBy(m => m.Name).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int cur = _folderApps.IndexOf(sorted[i]);
            if (cur != i) _folderApps.Move(cur, i);
        }
    }

    void Resort()
    {
        // не двигаем строки под курсором, чтобы не промахнуться по переключателю
        if (AppList.IsMouseOver) return;
        var sorted = _apps
            .OrderByDescending(a => a.SortBytes)
            .ThenBy(a => a.Name)
            .ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int cur = _apps.IndexOf(sorted[i]);
            if (cur != i) _apps.Move(cur, i);
        }
    }

    void Master_Click(object sender, RoutedEventArgs e)
    {
        bool on = Master.IsChecked == true;
        if (on && _engine.AllowedCount == 0)
        {
            _suppressHide = true;
            var r = MessageBox.Show(this,
                "Ни одна программа не отмечена как разрешённая.\n" +
                "После включения интернет пропадёт у всех программ из списка, включая браузер.\n\n" +
                "Всё равно включить?",
                "NetPause", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            _suppressHide = false;
            if (r != MessageBoxResult.Yes)
            {
                Master.IsChecked = false;
                return;
            }
        }
        _engine.SetEconomy(on);
        RefreshAll();
        EconomyChanged?.Invoke();
    }

    void Exit_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    void Auto_Click(object sender, RoutedEventArgs e)
    {
        bool target = !_auto;
        AutoBtn.IsEnabled = false;
        System.Threading.Tasks.Task.Run(() =>
        {
            Autostart.Set(target);
            bool on = Autostart.IsEnabled();
            Dispatcher.BeginInvoke(() =>
            {
                SetAutoLabel(on);
                AutoBtn.IsEnabled = true;
            });
        });
    }

    static string CurrentNetwork()
    {
        try
        {
            var n = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(i =>
                i.OperationalStatus == OperationalStatus.Up &&
                i.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                i.GetIPProperties().GatewayAddresses.Count > 0);
            return n?.Name ?? "нет подключения";
        }
        catch { return "неизвестно"; }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
