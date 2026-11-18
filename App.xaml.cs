using System;
using System.Windows;

namespace NetPause;

public partial class App : Application
{
    System.Threading.Mutex? _mutex;
    Engine? _engine;
    PanelWindow? _panel;
    System.Windows.Forms.NotifyIcon? _tray;
    System.Drawing.Icon? _iconOn, _iconOff;
    TranslatorController? _translator;

    public static bool Dark => ThemeManager.Current.Dark;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // служебный ключ: нарисовать панель во всех оформлениях в картинки и выйти (ничего не включает)
        int ri = Array.IndexOf(e.Args, "--render-previews");
        if (ri >= 0 && ri + 1 < e.Args.Length)
        {
            try { RenderPreviews(e.Args[ri + 1]); } catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(e.Args[ri + 1], "error.txt"), ex.ToString()); }
            Shutdown();
            return;
        }

        // служебный ключ: нарисовать значок программы в разных размерах (для файла .ico) и выйти
        int mi = Array.IndexOf(e.Args, "--make-icon");
        if (mi >= 0 && mi + 1 < e.Args.Length)
        {
            System.IO.Directory.CreateDirectory(e.Args[mi + 1]);
            var red = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E01E37");
            foreach (var size in new[] { 16, 24, 32, 48, 64, 256 })
                SavePng(TrayIcons.Preview("wifi", true, red, size),
                        System.IO.Path.Combine(e.Args[mi + 1], "icon-" + size + ".png"));
            Shutdown();
            return;
        }

        // аварийный ключ: только снять все блокировки NetPause и выйти
        if (Array.IndexOf(e.Args, "--cleanup") >= 0)
        {
            try { Engine.Cleanup(); } catch { }
            Shutdown();
            return;
        }

        if (Array.IndexOf(e.Args, "--autostart-on") >= 0) Autostart.Set(true);

        _mutex = new System.Threading.Mutex(true, @"Global\NetPause.SingleInstance", out bool first);
        if (!first)
        {
            // уже запущен: просим работающий экземпляр показать панель
            try { System.Threading.EventWaitHandle.OpenExisting(@"Global\NetPause.Show").Set(); } catch { }
            Shutdown();
            return;
        }

        var show = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, @"Global\NetPause.Show");
        new System.Threading.Thread(() =>
        {
            while (true)
            {
                show.WaitOne();
                Dispatcher.BeginInvoke(() => _panel?.ShowPanel());
            }
        }) { IsBackground = true }.Start();

        // при любом сбое сначала снимаем блокировки, чтобы интернет не остался отрезанным,
        // и показываем, что случилось (раньше программа молча закрывалась)
        DispatcherUnhandledException += (_, a) => { _engine?.Dispose(); ReportCrash(a.Exception, true); a.Handled = true; Shutdown(); };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => { _engine?.Dispose(); ReportCrash(a.ExceptionObject as Exception, true); };

        try { StartApp(e); }
        catch (Exception ex)
        {
            try { _engine?.Dispose(); } catch { }
            ReportCrash(ex, true);
            Shutdown();
        }
    }

    /// <summary>
    /// Записать причину сбоя в C:\ProgramData\NetPause\crash.txt (или во временную папку)
    /// и показать окно с понятным текстом — чтобы было что прислать автору.
    /// </summary>
    public static void ReportCrash(Exception? ex, bool show)
    {
        string text = "NetPause " + Engine.AppVersion + ", " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                      "Windows " + Environment.OSVersion.Version + (Environment.Is64BitOperatingSystem ? " x64" : " x86") + Environment.NewLine +
                      (ex?.ToString() ?? "неизвестная ошибка") + Environment.NewLine + Environment.NewLine;
        string? file = null;
        foreach (var dir in new[]
                 {
                     System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetPause"),
                     System.IO.Path.GetTempPath(),
                 })
        {
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                var f = System.IO.Path.Combine(dir, dir.EndsWith("NetPause") ? "crash.txt" : "NetPause-crash.txt");
                System.IO.File.AppendAllText(f, text);
                file = f;
                break;
            }
            catch { }
        }
        if (!show) return;
        try
        {
            MessageBox.Show(
                "NetPause не смог запуститься.\n\nПричина: " + (ex?.Message ?? "неизвестна") +
                (file != null ? "\n\nПодробности записаны в файл:\n" + file + "\nПришлите его автору программы." : ""),
                "NetPause", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { }
    }

    void StartApp(StartupEventArgs e)
    {
        _engine = new Engine();
        _engine.Start();

        // оформление выбирается в «Настройках»; значок у часов перерисовывается под него
        ThemeManager.Changed += RebuildIcons;
        ThemeManager.Apply(_engine.Setting(s => s.Theme));

        _panel = new PanelWindow(_engine);
        _panel.EconomyChanged += UpdateTray;
        _panel.ExitRequested += Quit;

        // табло и тревога о резком расходе трафика
        _translator = new TranslatorController(_engine, () =>
        {
            UpdateTray();
            _panel?.RefreshIfVisible();
        });
        _panel.Translator = _translator;
        _translator.Start();

        // экономию переключила сеть: обновляем значок и сообщаем уведомлением у часов
        _engine.AutoSwitched += msg => Dispatcher.BeginInvoke(() =>
        {
            UpdateTray();
            _panel?.RefreshIfVisible();
            try { _tray?.ShowBalloonTip(5000, "NetPause", msg, System.Windows.Forms.ToolTipIcon.Info); } catch { }
        });

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => _panel.ShowPanel());
        menu.Items.Add("Выход", null, (_, _) => Quit());
        _tray = new System.Windows.Forms.NotifyIcon { Visible = true, ContextMenuStrip = menu };
        _tray.MouseClick += (_, a) =>
        {
            if (a.Button == System.Windows.Forms.MouseButtons.Left) _panel.Toggle();
        };
        RebuildIcons();

        // первый запуск: выносим значок из скрытых к часам (Windows регистрирует его не сразу)
        foreach (var delay in new[] { 3, 30 })
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
            t.Tick += (_, _) => { t.Stop(); _engine.PromoteTrayIconOnce(); };
            t.Start();
        }

        // при автозапуске панель не показываем, только значок у часов
        if (Array.IndexOf(e.Args, "--tray") < 0) _panel.ShowPanel();

        // первый запуск: панель могла сразу спрятаться (фокус вернулся к установщику) —
        // явно говорим, что программа работает и где её искать
        if (_engine.FirstRun)
            try
            {
                _tray.ShowBalloonTip(10000, "NetPause работает",
                    "Значок NetPause — у часов (если его не видно, нажмите стрелку «^»). Нажмите на него, чтобы открыть панель.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
            catch { }
    }

    void UpdateTray()
    {
        if (_tray == null || _engine == null) return;
        bool on = _engine.EconomyOn;
        _tray.Icon = on ? _iconOn : _iconOff;
        _tray.Text = on ? "NetPause: экономия включена" : "NetPause: экономия выключена";
    }

    void Quit()
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        _translator?.Stop();
        _engine?.Dispose();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // выключение могут отменить, поэтому программу не останавливаем, только снимаем блокировки
        _engine?.PrepareForSessionEnd();
        base.OnSessionEnding(e);
    }

    /// <summary>Картинки панели во всех оформлениях (для проверки), без запуска учёта и правил файрвола.</summary>
    void RenderPreviews(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        var engine = new Engine();
        engine.LoadForPreview();
        ThemeManager.Apply("system");
        var panel = new PanelWindow(engine);
        foreach (var t in ThemeManager.All)
        {
            ThemeManager.Apply(t.Id);
            panel.RenderPreview(System.IO.Path.Combine(dir, "panel-" + t.Id + ".png"), false);
            panel.RenderPreview(System.IO.Path.Combine(dir, "settings-" + t.Id + ".png"), true);
            panel.RenderPreview(System.IO.Path.Combine(dir, "translator-" + t.Id + ".png"), false, true);
            foreach (var style in new[] { "wifi", "pause" })
                foreach (var on in new[] { true, false })
                    SavePng(TrayIcons.Preview(style, on, ThemeManager.Current.C("On"), 64),
                        System.IO.Path.Combine(dir, $"icon-{t.Id}-{style}-{(on ? "on" : "off")}.png"));
        }
    }

    static void SavePng(System.Windows.Media.Imaging.BitmapSource src, string file)
    {
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
        using var fs = System.IO.File.Create(file);
        enc.Save(fs);
    }

    /// <summary>Какой значок у часов: выбранный в настройках или тот, что подходит оформлению.</summary>
    public static string TrayStyleFor(string? choice) =>
        choice is "wifi" or "pause" ? choice : ThemeManager.Current.TrayStyle;

    /// <summary>Перерисовать значок у часов под текущее оформление и выбор в настройках.</summary>
    public void RebuildIcons()
    {
        if (_engine == null) return;
        var style = TrayStyleFor(_engine.Setting(s => s.TrayIcon));
        var accent = ThemeManager.Current.C("On");
        var oldOn = _iconOn;
        var oldOff = _iconOff;
        _iconOn = TrayIcons.Make(style, true, accent);
        _iconOff = TrayIcons.Make(style, false, accent);
        UpdateTray();
        TrayIcons.Free(oldOn); // прежние значки освобождаем, чтобы не копились
        TrayIcons.Free(oldOff);
    }
}
