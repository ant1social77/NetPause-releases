using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace NetPause;

public sealed partial class Engine
{
    /// <summary>Прочитать настройку под блокировкой.</summary>
    public T Setting<T>(Func<State, T> get) { lock (_lock) return get(_state); }

    /// <summary>Изменить настройки и сохранить их.</summary>
    public void ChangeSettings(Action<State> change)
    {
        lock (_lock)
        {
            change(_state);
            _dirty = true;
        }
        Save();
    }

    public AppEntry? FindApp(string key) { lock (_lock) return _byKey.TryGetValue(key, out var e) ? e : null; }

    /// <summary>Только прочитать сохранённое состояние — для служебной отрисовки картинок (без учёта и правил).</summary>
    public void LoadForPreview() => Load();

    /// <summary>Файлы, которым NetPause сам временно разрешил интернет (по имени файла).</summary>
    readonly HashSet<string> _tempAllowFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Временно разрешить программу независимо от её переключателя, например движок окна входа
    /// на сайт трекера. Возвращает блокировки как было, когда разрешение снимают.
    /// Ждёт, пока правила файрвола действительно обновятся.
    /// </summary>
    public void SetTempAllow(string fileName, bool allow)
    {
        lock (_lock)
        {
            if (allow) _tempAllowFiles.Add(fileName);
            else _tempAllowFiles.Remove(fileName);
        }
        RequestSync();
        Task t;
        lock (_lock) t = _syncChain;
        try { t.Wait(10000); } catch { }
    }
}

/// <summary>
/// «Транслятор»: следит за скоростью расхода трафика, показывает табло поверх окон
/// и поднимает тревогу, когда трафик уходит слишком быстро.
/// </summary>
public sealed class TranslatorController
{
    readonly Engine _engine;
    readonly Action _changed;
    readonly DispatcherTimer _timer;
    readonly Queue<(DateTime T, long Total, Dictionary<string, long> Apps)> _samples = new();
    long _lastTotal = -1;
    OverlayWindow? _overlay;
    AlertWindow? _alert;
    DateTime _snoozeUntil = DateTime.MinValue;

    /// <summary>Сколько потрачено с запуска NetPause (для табло и пересчёта остатка между ответами трекера).</summary>
    public long UsedSinceOn { get; private set; }
    /// <summary>Расход за последнюю минуту, МБ.</summary>
    public double MbPerMin { get; private set; }
    /// <summary>Последний ответ трекера.</summary>
    public TrackerResult? Last { get; private set; }
    /// <summary>Счётчик трафика в интернет в момент последнего ответа трекера.</summary>
    long _inetAtReading;
    /// <summary>Счётчик трафика в интернет в начале суток трекера (00:00 UTC).</summary>
    long _inetAtDayStart;
    DateTime _utcDay;
    DateTime _nextRead = DateTime.MinValue;
    bool _reading;
    bool _dumped;
    DateTime _lastOkLog = DateTime.MinValue;

    /// <summary>Сутки у трекера начинаются в 00:00 по UTC: тогда сайт выдаёт новую норму.</summary>
    public static DateTime NextResetLocal => DateTime.UtcNow.Date.AddDays(1).ToLocalTime();

    /// <summary>
    /// Суточная норма трекера текущей сети, МБ: заданная вручную, иначе узнанная на сайте
    /// (ежедневное начисление), иначе 1000.
    /// </summary>
    public double DailyQuotaMb => QuotaInfo().Mb;

    /// <summary>Норма и откуда она: "manual" — задана вручную, "site" — с сайта, "default" — 1000 по умолчанию.</summary>
    public (double Mb, string Source) QuotaInfo()
    {
        var t = ActiveTracker();
        if (t == null) return (1000, "default");
        var origin = TrackerReader.Origin(t);
        return _engine.Setting(s =>
            s.TrackerDailyMb.TryGetValue(origin, out var m) && m > 0 ? ((double)m, "manual")
            : s.TrackerDailyAutoMb.TryGetValue(origin, out var a) && a > 0 ? ((double)a, "site")
            : (1000d, "default"));
    }

    /// <summary>Норму с сайта уже узнавали в эти сутки.</summary>
    bool _quotaChecked;

    // ---------- устройства в сессии ----------

    public sealed class SessionDevice
    {
        public string Ip = "";
        public string Name = "";
        /// <summary>Этот компьютер.</summary>
        public bool Self;
        /// <summary>Главное устройство: на нём включили интернет (его отключить нельзя, только присоединённые).</summary>
        public bool Primary;
    }

    /// <summary>Устройства в интернет-сессии на сайте трекера (ноутбук, присоединённый телефон…).</summary>
    public List<SessionDevice> Devices { get; private set; } = new();
    /// <summary>Кроме этого компьютера в сессии есть другие устройства.</summary>
    public bool OthersPresent => Devices.Any(d => !d.Self);
    /// <summary>
    /// Сколько с начала суток трекера потратили другие устройства, МБ: насколько общий остаток
    /// уменьшился сверх того, что ушло в интернет с этого компьютера. Оценка с шагом в минуту.
    /// </summary>
    public double OtherTodayMb => Math.Max(0, _otherBalanceMb);
    /// <summary>Расход других устройств за последнюю минуту, МБ/мин.</summary>
    public double OtherMbPerMin { get; private set; }

    /// <summary>Расход этого компьютера с начала суток трекера, МБ (норма − остаток − другие устройства).</summary>
    public double SelfTodayMb => RemainingMb is double r ? Math.Max(0, DailyQuotaMb - r - OtherTodayMb) : 0;

    /// <summary>Подпись других устройств: имя одного или «другие устройства».</summary>
    public string OthersLabel
    {
        get
        {
            var others = Devices.Where(d => !d.Self).ToList();
            return others.Count == 1 ? others[0].Name : "другие устройства";
        }
    }

    double _otherBalanceMb;
    string? _myIp;
    double? _prevRem;
    long _prevInet;
    DateTime _prevAt;
    readonly Dictionary<string, string> _deviceNames = new();

    /// <summary>
    /// После каждого ответа трекера: кто сейчас в сессии и сколько потратили другие устройства.
    /// Разница копится со знаком, чтобы запоздалый ответ сайта не завышал расход телефона.
    /// </summary>
    async Task UpdateSession(string tracker, string token, TrackerResult r, long inet)
    {
        _myIp ??= await TrackerReader.ClientIpAsync(tracker);
        var joined = await TrackerReader.JoinedDevicesAsync(tracker, token)
                     ?? new HashSet<string>(Devices.Where(d => !d.Primary).Select(d => d.Ip)); // сайт не ответил — список прежний
        var list = new List<SessionDevice>();
        if (r.DataOn && r.StatusIp != null) list.Add(new SessionDevice { Ip = r.StatusIp, Primary = true });
        foreach (var ip in joined)
            if (!list.Any(d => d.Ip == ip)) list.Add(new SessionDevice { Ip = ip });
        foreach (var d in list)
        {
            d.Self = d.Ip == _myIp;
            if (d.Self) { d.Name = "Этот ноутбук"; continue; }
            if (!_deviceNames.TryGetValue(d.Ip, out var name))
                _deviceNames[d.Ip] = name = await TrackerReader.DeviceNameAsync(d.Ip) ?? "Устройство " + d.Ip;
            d.Name = name;
        }
        bool others = list.Any(d => !d.Self);

        if (r.IsBytes && r.Remaining is double rem)
        {
            if (_prevRem is double prev && rem <= prev && _prevAt.ToUniversalTime().Date == DateTime.UtcNow.Date)
            {
                double diff = (prev - rem) / 1048576.0 - (inet - _prevInet) / 1048576.0;
                if (others)
                {
                    _otherBalanceMb += diff;
                    double mins = Math.Max(0.5, (DateTime.Now - _prevAt).TotalMinutes);
                    OtherMbPerMin = Math.Max(0, diff) / mins;
                }
                else OtherMbPerMin = 0;
            }
            else OtherMbPerMin = 0; // первый ответ, новые сутки или остаток вырос (начисление)
            _prevRem = rem;
            _prevInet = inet;
            _prevAt = DateTime.Now;
        }

        var sig = string.Join(",", list.Select(d => d.Ip));
        if (sig != string.Join(",", Devices.Select(d => d.Ip)))
            TrackerLog.Write("в сессии: " + (list.Count == 0 ? "никого" : string.Join(", ", list.Select(d =>
                d.Name + " (" + d.Ip + (d.Primary ? ", главное" : ", присоединён") + ")"))));
        Devices = list;
    }

    /// <summary>Отключить присоединённое устройство от сессии на сайте.</summary>
    public async Task RemoveDevice(string ip)
    {
        var tracker = ActiveTracker();
        if (tracker == null || Token(tracker) is not string token) return;
        var (ok, needLogin, err) = await TrackerReader.RemoveDeviceAsync(tracker, token, ip);
        if (needLogin && await Relogin(tracker) is string fresh)
            (ok, _, err) = await TrackerReader.RemoveDeviceAsync(tracker, fresh, ip);
        var name = Devices.FirstOrDefault(d => d.Ip == ip)?.Name ?? ip;
        SetDataNote(ok ? "«" + name + "» отключён от сессии в " + DateTime.Now.ToString("HH:mm")
                       : "не удалось отключить «" + name + "»: " + (err ?? "сайт не принял вход"));
        if (ok) Devices = Devices.Where(d => d.Ip != ip).ToList();
        ReadNow();
        _changed();
    }

    /// <summary>
    /// Остаток на сегодня, МБ: что сообщил трекер минус то, что ушло в интернет после его ответа.
    /// Если с ответа трекера уже наступили новые сутки (00:00 UTC), считаем от свежей нормы,
    /// пока трекер не ответит снова. null — трекер ещё не отвечал или считает не в мегабайтах.
    /// </summary>
    public double? RemainingMb
    {
        get
        {
            if (Last is not { Ok: true, IsBytes: true, Remaining: double rem }) return null;
            long inet = _engine.InternetBytes;
            if (Last.At.ToUniversalTime().Date < DateTime.UtcNow.Date)
                return Math.Max(0, DailyQuotaMb - (inet - _inetAtDayStart) / 1048576.0);
            return Math.Max(0, (rem - (inet - _inetAtReading)) / 1048576.0);
        }
    }

    /// <summary>Доля остатка от суточной нормы (0…1) — для капли.</summary>
    public double? QuotaFraction => RemainingMb is double r ? Math.Clamp(r / DailyQuotaMb, 0, 1) : null;

    /// <summary>Последнее, что NetPause делал с включением интернета на сайте (для вкладки).</summary>
    public string? DataNote { get; private set; }

    void SetDataNote(string text)
    {
        DataNote = text;
        TrackerLog.Write("интернет на сайте: " + text);
    }

    /// <summary>Когда проверить, не сеть ли это с трекером (сеть судна).</summary>
    DateTime _probeAt = DateTime.MaxValue;
    string? _probeNet;
    int _probeTries;
    bool _probing;
    /// <summary>Когда проверить, включён ли интернет на сайте, и включить его.</summary>
    DateTime _dataOnAt = DateTime.MaxValue;
    int _dataOnTries;
    bool _dataOnBusy;

    /// <summary>Есть ли сохранённый вход для выбранного трекера.</summary>
    public bool HasLogin => ActiveTracker() is string t && Token(t) != null;

    /// <summary>Трекер сети, к которой подключены сейчас (null — у этой сети трекера нет).</summary>
    public string? ActiveTracker() => _engine.CurrentTracker();

    /// <summary>Последний ответ трекера для каждой сети: при возврате в сеть остаток виден сразу.</summary>
    readonly Dictionary<string, TrackerResult> _byNet = new();

    /// <summary>
    /// Сменилась сеть (или её трекер): расход до переключения к остатку новой сети не относится,
    /// поэтому начинаем отсчёт заново, показываем последнее известное значение и сразу спрашиваем трекер.
    /// </summary>
    void OnNetworkChanged()
    {
        _inetAtReading = _engine.InternetBytes;
        // в другой сети у компьютера другой адрес и, возможно, другая сессия
        _myIp = null;
        _prevRem = null;
        Devices = new();
        OtherMbPerMin = 0;
        var net = _engine.CurrentNetId;
        var tracker = ActiveTracker();
        Last = net != null && tracker != null && _byNet.TryGetValue(net, out var cached) ? cached : null;
        ReadNow();
        TrackerLog.Write("сеть: " + (_engine.CurrentNetwork?.Name ?? "нет подключения") +
                         (tracker != null ? ", трекер " + new Uri(tracker).Host : ""));

        // сеть без трекера, которую ещё не проверяли: может, это другая сеть судна — спросим сайт трекера
        _probeNet = net;
        _probeTries = 0;
        _probeAt = net != null && tracker == null && _engine.NeedsTrackerCheck(net) ? DateTime.Now.AddSeconds(3) : DateTime.MaxValue;

        // подключились (или переподключились) к сети с трекером: включить интернет на сайте, если он выключен
        _dataOnTries = 0;
        _dataOnAt = net != null && tracker != null && _engine.Setting(s => s.AutoDataOn)
            ? DateTime.Now.AddSeconds(3) : DateTime.MaxValue;

        UpdateOverlay();
        _changed();
    }

    /// <summary>
    /// Проверка сети без трекера: если сайт трекера отвечает изнутри неё и видит этот компьютер
    /// по местному адресу, это сеть судна — привязываем к ней трекер сами.
    /// </summary>
    async void ProbeNetwork()
    {
        _probeAt = DateTime.MaxValue;
        if (_probing) return;
        var net = _probeNet;
        if (net == null || net != _engine.CurrentNetId || !_engine.NeedsTrackerCheck(net)) return;
        var trackers = _engine.Setting(s => s.Trackers.Where(TrackerReader.IsSupported).ToList());
        if (trackers.Count == 0) return;
        _probing = true;
        try
        {
            bool unknown = false;
            foreach (var t in trackers)
            {
                var r = await TrackerReader.ProbeAsync(t);
                var name = _engine.CurrentNetwork?.Name ?? net;
                TrackerLog.Write("проверка сети «" + name + "» на " + new Uri(t).Host + ": " +
                                 (r == true ? "это сеть с этим трекером" : r == false ? "трекера здесь нет" : "сайт не ответил"));
                if (net != _engine.CurrentNetId) return; // пока ждали, сеть сменилась
                if (r == true)
                {
                    _engine.SetNetworkTracker(net, t, auto: true);
                    _engine.Notice("Сеть «" + name + "» подключена к трекеру " + new Uri(t).Host +
                                   ": капля показывает остаток с него.");
                    return;
                }
                if (r == null) unknown = true;
            }
            if (!unknown) _engine.MarkTrackerChecked(net);
            else if (++_probeTries < 5) _probeAt = DateTime.Now.AddSeconds(60); // сайт молчит — спросим ещё раз позже
        }
        finally { _probing = false; }
    }

    /// <summary>
    /// Включить интернет на сайте трекера, если он выключен, — то же, что переключатель на сайте.
    /// Если интернет включён на другом устройстве, присоединяем к нему этот компьютер (как кнопка «Join» на сайте).
    /// Выключать интернет NetPause не умеет и не пытается.
    /// </summary>
    public async Task EnsureDataOn(bool manual)
    {
        if (_dataOnBusy) return;
        var tracker = ActiveTracker();
        if (tracker == null || !TrackerReader.IsSupported(tracker)) return;
        if (!manual && !_engine.Setting(s => s.AutoDataOn)) return;
        var token = Token(tracker);
        if (token == null)
        {
            if (manual) SetDataNote("сначала войдите на сайт трекера");
            return;
        }
        _dataOnBusy = true;
        var net = _engine.CurrentNetId;
        try
        {
            var st = await TrackerReader.GetStatusAsync(tracker, token);
            if (st.NeedLogin && await Relogin(tracker) is string fresh)
            {
                token = fresh;
                st = await TrackerReader.GetStatusAsync(tracker, token);
            }
            if (net != _engine.CurrentNetId) return; // сеть успела смениться
            if (st.NeedLogin) { SetDataNote("сайт не принял вход — нажмите «Сменить данные» и войдите заново"); return; }
            if (st.Error != null)
            {
                // сразу после подключения сайт может ещё не отвечать — пробуем ещё пару раз
                if (!manual && ++_dataOnTries < 4) _dataOnAt = DateTime.Now.AddSeconds(20);
                SetDataNote("сайт не ответил: " + st.Error);
                return;
            }
            if (st.Provider is int pr && pr != _engine.Setting(s => s.TrackerProvider))
                _engine.ChangeSettings(s => s.TrackerProvider = pr);

            if (st.Off)
            {
                int provider = st.Provider ?? _engine.Setting(s => s.TrackerProvider) ?? 0;
                var (ok, needLogin, err) = await TrackerReader.TurnOnAsync(tracker, token, provider);
                if (ok)
                {
                    var check = await TrackerReader.GetStatusAsync(tracker, token);
                    ok = check.Error != null || !check.Off; // сайт не ответил на проверку — верим первому ответу
                    if (!ok) err = "сайт не включил интернет (возможно, закончилась норма)";
                }
                if (ok)
                {
                    SetDataNote("включил NetPause в " + DateTime.Now.ToString("HH:mm"));
                    if (!manual) _engine.Notice("Интернет на сайте трекера был выключен — NetPause включил его.");
                }
                else SetDataNote("не удалось включить: " + (needLogin ? "сайт не принял вход" : err));
            }
            else
            {
                var myIp = await TrackerReader.ClientIpAsync(tracker);
                if (myIp != null && st.Ip != null && st.Ip != myIp)
                {
                    // интернет включён на другом устройстве (или у этого компьютера сменился адрес)
                    var joined = await TrackerReader.JoinedDevicesAsync(tracker, token);
                    if (joined != null && joined.Contains(myIp))
                        TrackerLog.Write("интернет на сайте: включён, этот компьютер к нему уже присоединён");
                    else
                    {
                        var (ok, _, err) = await TrackerReader.JoinAsync(tracker, token, myIp);
                        SetDataNote(ok ? "был включён на другом устройстве — NetPause присоединил к нему этот компьютер в " +
                                         DateTime.Now.ToString("HH:mm")
                                       : "включён на другом устройстве, присоединиться не удалось: " + err);
                    }
                }
                else TrackerLog.Write("интернет на сайте: уже включён (на сайте адрес " + (st.Ip ?? "?") +
                                      ", у этого компьютера " + (myIp ?? "?") + ")");
            }
            ReadNow();
        }
        catch (Exception ex) { SetDataNote("ошибка: " + ex.Message); }
        finally
        {
            _dataOnBusy = false;
            UpdateOverlay();
            _changed();
        }
    }

    /// <summary>
    /// Кнопка «Войти и открыть сайт»: входим на трекер с сохранёнными данными (а если их нет —
    /// спрашиваем один раз и запоминаем), включаем интернет, если он выключен,
    /// и открываем сайт в браузере по умолчанию.
    /// </summary>
    public async Task ConnectAndOpen(Window owner)
    {
        var tracker = ActiveTracker();
        if (tracker == null) return;
        if (!TrackerReader.IsSupported(tracker)) { Browser.Open(tracker); return; }

        var origin = TrackerReader.Origin(tracker);
        bool saved = _engine.Setting(s => !string.IsNullOrEmpty(s.TrackerUser) && s.TrackerPasswords.ContainsKey(origin));
        if (!saved && !LoginWithPassword(owner)) return; // данных нет — спрашиваем; отказались — ничего не делаем

        Browser.Open(origin + "/");
        // пропуска нет — входим сохранёнными данными; если пропуск есть, но устарел, войдёт EnsureDataOn
        if (Token(tracker) == null && await Relogin(tracker) == null)
        {
            SetDataNote("сохранённые данные не подошли — нажмите «Сменить данные»");
            _changed();
            return;
        }
        await EnsureDataOn(true);
    }

    string? Token(string tracker)
    {
        var origin = TrackerReader.Origin(tracker);
        return TrackerReader.Unprotect(_engine.Setting(s => s.TrackerTokens.TryGetValue(origin, out var v) ? v : null));
    }

    /// <summary>Открыть окно сайта, чтобы человек вошёл сам. true — вход получен.</summary>
    public bool Login(Window owner)
    {
        var tracker = ActiveTracker();
        if (tracker == null) return false;
        // окно сайта работает на движке Edge WebView2: на время входа разрешаем ему интернет,
        // даже если экономия его блокирует, а после закрытия окна возвращаем как было
        _engine.SetTempAllow("msedgewebview2.exe", true);
        bool? ok;
        var w = new TrackerLoginWindow(tracker) { Owner = owner.IsVisible ? owner : null };
        try { ok = w.ShowDialog(); }
        finally { _engine.SetTempAllow("msedgewebview2.exe", false); }
        if (ok != true || w.Token == null) return false;
        var origin = TrackerReader.Origin(tracker);
        var prot = TrackerReader.Protect(w.Token);
        _engine.ChangeSettings(s =>
        {
            s.TrackerTokens[origin] = prot;
            if (w.ProviderNo != null) s.TrackerProvider = w.ProviderNo;
        });
        ReadNow();
        return true;
    }

    /// <summary>Вход без браузера: окно «логин и пароль». true — вход получен.</summary>
    public bool LoginWithPassword(Window owner)
    {
        var tracker = ActiveTracker();
        if (tracker == null) return false;
        var w = new TrackerPasswordWindow(tracker, _engine.Setting(s => s.TrackerUser)) { Owner = owner.IsVisible ? owner : null };
        if (w.ShowDialog() != true || w.Token == null) return false;
        var origin = TrackerReader.Origin(tracker);
        var tok = TrackerReader.Protect(w.Token);
        string? pass = w.Remember ? TrackerReader.Protect(w.Password) : null;
        var user = w.User;
        _engine.ChangeSettings(s =>
        {
            s.TrackerTokens[origin] = tok;
            s.TrackerUser = user;
            if (pass != null) s.TrackerPasswords[origin] = pass;
            else s.TrackerPasswords.Remove(origin);
        });
        ReadNow();
        return true;
    }

    readonly System.Threading.SemaphoreSlim _loginGate = new(1, 1);
    DateTime _lastRelogin = DateTime.MinValue;

    /// <summary>
    /// Пропуск устарел: если пароль сохранён, входим заново сами. Одновременно входим только раз:
    /// если другой запрос только что вошёл, берём его свежий пропуск.
    /// </summary>
    async Task<string?> Relogin(string tracker)
    {
        await _loginGate.WaitAsync();
        try
        {
            if (DateTime.Now - _lastRelogin < TimeSpan.FromSeconds(20) && Token(tracker) is string recent) return recent;
            var origin = TrackerReader.Origin(tracker);
            var user = _engine.Setting(s => s.TrackerUser);
            var pass = TrackerReader.Unprotect(_engine.Setting(s => s.TrackerPasswords.TryGetValue(origin, out var v) ? v : null));
            if (string.IsNullOrEmpty(user) || pass == null) return null;
            var (token, err) = await TrackerReader.LoginAsync(tracker, user, pass);
            TrackerLog.Write(token != null ? "вошли заново сохранёнными данными" : "войти заново не удалось: " + err);
            if (token == null) return null;
            var prot = TrackerReader.Protect(token);
            _engine.ChangeSettings(s => s.TrackerTokens[origin] = prot);
            _lastRelogin = DateTime.Now;
            return token;
        }
        finally { _loginGate.Release(); }
    }

    /// <summary>Забыть сохранённый вход (и пароль, если был) для выбранного трекера.</summary>
    public void Logout()
    {
        var tracker = ActiveTracker();
        if (tracker == null) return;
        var origin = TrackerReader.Origin(tracker);
        _engine.ChangeSettings(s =>
        {
            s.TrackerTokens.Remove(origin);
            s.TrackerPasswords.Remove(origin);
        });
        Last = null;
        UpdateOverlay();
    }

    /// <summary>Спросить трекер прямо сейчас, не дожидаясь минуты.</summary>
    public void ReadNow() => _nextRead = DateTime.MinValue;

    /// <summary>Раз в минуту спрашиваем остаток у трекера (ответ — несколько килобайт).</summary>
    async void PollTracker()
    {
        if (_reading || DateTime.Now < _nextRead) return;
        var tracker = ActiveTracker();
        if (tracker == null || !TrackerReader.IsSupported(tracker)) { Last = null; return; }
        var token = Token(tracker);
        if (token == null) { Last = new TrackerResult { NeedLogin = true }; return; }

        _reading = true;
        _nextRead = DateTime.Now.AddSeconds(60);
        var net = _engine.CurrentNetId;
        try
        {
            long inetBefore = _engine.InternetBytes;
            var r = await TrackerReader.ReadAsync(tracker, token, _engine.Setting(s => s.TrackerProvider));
            if (r.NeedLogin)
            {
                TrackerLog.Write("сайт не принял пропуск");
                if (await Relogin(tracker) is string fresh)
                {
                    token = fresh;
                    r = await TrackerReader.ReadAsync(tracker, fresh, _engine.Setting(s => s.TrackerProvider));
                }
            }
            if (r.Ok)
            {
                if (DateTime.Now - _lastOkLog > TimeSpan.FromMinutes(10))
                {
                    _lastOkLog = DateTime.Now;
                    TrackerLog.Write("остаток " + FormatMb((r.Remaining ?? 0) / 1048576.0) + ", начислено всего " +
                                     FormatMb((r.Allocated ?? 0) / 1048576.0) + ", интернет " + (r.DataOn ? "включён" : "выключен") +
                                     (r.ProviderNo is int pn ? ", провайдер " + pn : ""));
                }
                // суточная норма — размер ежедневного начисления на сайте; узнаём раз в сутки
                if (!_quotaChecked)
                {
                    _quotaChecked = true;
                    if (await TrackerReader.DailyAllocationAsync(tracker, token) is double daily)
                    {
                        int mb = (int)Math.Round(daily / 1048576.0);
                        var origin = TrackerReader.Origin(tracker);
                        if (_engine.Setting(s => s.TrackerDailyAutoMb.TryGetValue(origin, out var old) ? old : 0) != mb)
                        {
                            _engine.ChangeSettings(s => s.TrackerDailyAutoMb[origin] = mb);
                            TrackerLog.Write("суточная норма с сайта: " + mb + " МБ");
                        }
                    }
                }
                // один раз за запуск записываем ответы сайта — по ним видно, где он хранит суточную норму
                if (!_dumped)
                {
                    _dumped = true;
                    var tk = token;
                    var file = System.IO.Path.Combine(TrackerLog.Dir, "tracker-debug.json");
                    _ = Task.Run(() => TrackerReader.DumpAsync(tracker, tk, _engine.Setting(s => s.TrackerProvider), file));
                }
            }
            else if (!r.NeedLogin) TrackerLog.Write("остаток не прочитан: " + r.Error);
            else TrackerLog.Write("войти не удалось — нужен вход");
            // пока ждали ответ, сеть сменилась — этот ответ уже не про текущую сеть
            if (_engine.CurrentNetId != net || ActiveTracker() != tracker) return;
            if (r.Ok)
            {
                Last = r;
                _inetAtReading = inetBefore;
                if (net != null) _byNet[net] = r;
                try { await UpdateSession(tracker, token, r, inetBefore); } catch { }
            }
            else if (r.NeedLogin || Last == null || !Last.Ok)
            {
                Last = r;
            }
            else
            {
                // временная ошибка: показываем прежний остаток, но запоминаем, что связь пропала
                Last.Error = r.Error;
            }
            if (r.Ok && Last != null) Last.Error = null;
            if (!r.Ok && !r.NeedLogin) _nextRead = DateTime.Now.AddSeconds(30);
        }
        finally
        {
            _reading = false;
            UpdateOverlay();
        }
    }

    /// <summary>Короткая строка про остаток — для табло и вкладки.</summary>
    public string RemainText()
    {
        var tracker = ActiveTracker();
        if (tracker == null) return "Сегодня потрачено " + Fmt.Bytes(_engine.TotalToday); // у этой сети трекера нет
        if (!TrackerReader.IsSupported(tracker)) return "Остаток: этот сайт пока не поддерживается";
        if (Last == null) return HasLogin ? "Остаток: спрашиваем трекер…" : "Остаток: войдите на сайт трекера";
        if (Last.NeedLogin) return "Остаток: войдите на сайт трекера";
        if (!Last.Ok) return "Остаток: " + (Last.Error ?? "нет ответа");
        string s;
        if (Last.IsBytes)
        {
            double rem = RemainingMb ?? 0, quota = DailyQuotaMb;
            // коротко, чтобы табло не разрасталось: «Осталось 818 из 1000 МБ»
            s = "Осталось " + (rem > quota * 1.02 ? FormatMb(rem) + " (больше нормы)"
                : rem < 1024 && quota < 1024 ? rem.ToString(rem >= 100 ? "0" : "0.0") + " из " + FormatMb(quota)
                : FormatMb(rem) + " из " + FormatMb(quota));
        }
        else s = "Осталось " + Math.Round((Last.Remaining ?? 0) / 60) + " мин";
        if (Last.Balance is double b && b > 0) s += " · $" + b.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        return s;
    }

    /// <param name="changed">Вызывается, когда тревога что-то заблокировала (обновить значок и панель).</param>
    public TranslatorController(Engine engine, Action changed)
    {
        _engine = engine;
        _changed = changed;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Tick();
        // смена сети приходит из фонового потока — обрабатываем её в потоке интерфейса
        var ui = Dispatcher.CurrentDispatcher;
        _engine.NetworkChanged += () => ui.BeginInvoke(OnNetworkChanged);
    }

    public void Start()
    {
        _timer.Start();
        ApplyOverlay();
    }

    public void Stop()
    {
        _timer.Stop();
        _overlay?.Close();
        _overlay = null;
        _alert?.Close();
        _alert = null;
    }

    /// <summary>Показать или убрать табло по текущей настройке.</summary>
    public void ApplyOverlay()
    {
        bool on = _engine.Setting(s => s.TranslatorOn);
        if (on && _overlay == null)
        {
            _overlay = new OverlayWindow(_engine.Setting(s => s.OverlayX), _engine.Setting(s => s.OverlayY));
            _overlay.Moved += (x, y) => _engine.ChangeSettings(s => { s.OverlayX = x; s.OverlayY = y; });
            _overlay.Show();
            UpdateOverlay();
        }
        else if (!on && _overlay != null)
        {
            _overlay.FadeClose(); // табло плавно гаснет
            _overlay = null;
        }
    }

    void Tick()
    {
        var now = DateTime.Now;
        long total = _engine.TotalToday;
        if (_lastTotal >= 0 && total >= _lastTotal) UsedSinceOn += total - _lastTotal;
        _lastTotal = total;

        var apps = _engine.GetApps().ToDictionary(a => a.Key, a => a.BytesToday);
        // наступил новый день и счётчики обнулились — начинаем замер заново
        if (_samples.Count > 0 && total < _samples.Peek().Total) _samples.Clear();
        _samples.Enqueue((now, total, apps));
        while (_samples.Count > 1 && (now - _samples.Peek().T).TotalSeconds > 62) _samples.Dequeue();

        var first = _samples.Peek();
        double span = (now - first.T).TotalSeconds;
        MbPerMin = span >= 10 ? (total - first.Total) / 1048576.0 / span * 60 : 0;

        // Новые сутки у трекера (00:00 по UTC): на сайте снова полная норма. До ответа сайта
        // считаем остаток от нормы, а сам сайт спрашиваем чуть позже, когда он точно обновится.
        var utcDay = DateTime.UtcNow.Date;
        if (utcDay != _utcDay)
        {
            if (_utcDay != default)
            {
                _inetAtDayStart = _engine.InternetBytes;
                _nextRead = now.AddSeconds(90);
                _quotaChecked = false; // норма могла смениться — узнаем заново
                _otherBalanceMb = 0;   // расход других устройств считаем заново
                _prevRem = null;
            }
            _utcDay = utcDay;
        }

        UpdateOverlay();
        CheckAlert(now, span, first.Apps, apps, total - first.Total);
        PollTracker();
        if (now >= _probeAt) ProbeNetwork();
        if (now >= _dataOnAt)
        {
            _dataOnAt = DateTime.MaxValue;
            _ = EnsureDataOn(false);
        }
    }

    void UpdateOverlay()
    {
        if (_overlay == null) return;
        int threshold = _engine.Setting(s => s.AlertMbPerMin);
        // с трекером важнее, когда придёт новая норма; без него — сколько ушло
        string line2 = FormatMb(MbPerMin) + "/мин · " + (RemainingMb != null && ActiveTracker() != null
            ? "новая норма в " + NextResetLocal.ToString("HH:mm")
            : "потрачено " + Fmt.Bytes(UsedSinceOn));
        // присоединён телефон — короткая пометка о его расходе; без него табло прежнее
        if (OthersPresent && ActiveTracker() != null) line2 += " · 📱 " + FormatMb(OtherMbPerMin) + "/мин";
        if (Last is { Ok: true, Error: not null }) line2 += " · нет связи с трекером";
        else if (Last is { Ok: true, DataOn: false }) line2 += " · интернет на сайте выключен";
        _overlay.Update(RemainText(), line2, MbPerMin >= threshold);
    }

    public static string FormatMb(double mb) =>
        mb >= 1024 ? (mb / 1024).ToString("0.00") + " ГБ" : mb.ToString(mb >= 100 ? "0" : "0.0") + " МБ";

    // ---------- тревога ----------

    void CheckAlert(DateTime now, double span, Dictionary<string, long> before, Dictionary<string, long> after, long bytes)
    {
        if (!_engine.Setting(s => s.AlertOn)) return;
        if (_alert != null || now < _snoozeUntil) return;
        if (span < 30) return; // мало данных: не пугаем зря сразу после запуска
        int threshold = _engine.Setting(s => s.AlertMbPerMin);
        if (MbPerMin < threshold)
        {
            // ноутбук тратит мало, а общий остаток тает быстро — значит, ест присоединённое устройство
            if (OthersPresent && OtherMbPerMin >= threshold) ShowDeviceAlert();
            return;
        }

        // кто съел больше всех за эту минуту
        string? topKey = null;
        long topBytes = 0;
        foreach (var kv in after)
        {
            long d = kv.Value - (before.TryGetValue(kv.Key, out var b) ? b : 0);
            if (d > topBytes) { topBytes = d; topKey = kv.Key; }
        }
        ShowAlert(topKey == null ? null : _engine.FindApp(topKey), topBytes, bytes);
    }

    /// <summary>Показать пример уведомления (кнопка «Проверить» во вкладке).</summary>
    public void ShowTestAlert()
    {
        var top = _engine.GetApps().OrderByDescending(a => a.BytesToday).FirstOrDefault(a => !a.Locked);
        ShowAlert(top, top?.BytesToday ?? 0, 0, test: true);
    }

    void ShowAlert(AppEntry? top, long topBytes, long totalBytes, bool test = false)
    {
        _snoozeUntil = DateTime.Now.AddMinutes(5); // не повторяем тревогу чаще раза в 5 минут

        string title = test ? "Так будет выглядеть тревога" : "Трафик уходит слишком быстро";
        string text = test
            ? "Сейчас это только проверка, ничего не произошло."
            : "За последнюю минуту потрачено " + Fmt.Bytes(totalBytes) + ".";
        if (top != null)
            text += " Больше всех — «" + top.Name + "»" + (test ? "" : ": " + Fmt.Bytes(topBytes)) + ".";

        bool canBlock = top != null && !top.Locked;
        string action = canBlock ? "Выключить «" + Short(top!.Name) + "»" : "Включить экономию";

        _alert = new AlertWindow(title, text, action);
        _alert.Act += () =>
        {
            if (canBlock) _engine.SetAllowed(top!.Key, false);
            if (!_engine.EconomyOn) _engine.SetEconomy(true);
            _changed();
        };
        _alert.Closed += (_, _) => _alert = null;
        if (_engine.Setting(s => s.AlertSound)) System.Media.SystemSounds.Exclamation.Play();
        _alert.Show();
    }

    /// <summary>Тревога: трафик съедает другое устройство в сессии (обычно телефон).</summary>
    void ShowDeviceAlert()
    {
        _snoozeUntil = DateTime.Now.AddMinutes(5);
        var others = Devices.Where(d => !d.Self).ToList();
        // отключить можно только присоединённое устройство; главное — только на сайте
        var target = others.FirstOrDefault(d => !d.Primary);
        string who = others.Count == 1 ? "«" + others[0].Name + "»" : "Другие устройства в сессии";
        string text = who + " тратит около " + FormatMb(OtherMbPerMin) + " в минуту, а этот ноутбук — почти ничего.";
        string action = target != null ? "Отключить «" + Short(target.Name) + "»" : "Открыть сайт трекера";

        _alert = new AlertWindow("Трафик уходит на другом устройстве", text, action);
        _alert.Act += () =>
        {
            if (target != null) _ = RemoveDevice(target.Ip);
            else if (ActiveTracker() is string t) Browser.Open(TrackerReader.Origin(t) + "/");
        };
        _alert.Closed += (_, _) => _alert = null;
        if (_engine.Setting(s => s.AlertSound)) System.Media.SystemSounds.Exclamation.Play();
        _alert.Show();
    }

    static string Short(string name) => name.Length <= 22 ? name : name.Substring(0, 21) + "…";
}

/// <summary>Маленькое табло поверх всех окон, как счётчик кадров в играх. Перетаскивается мышью.</summary>
public sealed class OverlayWindow : Window
{
    readonly TextBlock _remain;
    readonly TextBlock _line2;
    static readonly Color NormalColor = Colors.White;
    /// <summary>Цвет тревожных цифр — берётся из оформления.</summary>
    public static Color HotColor { get; set; } = Color.FromRgb(0xFF, 0x9F, 0x43);
    /// <summary>Цвет главной строки: плавно перетекает в оранжевый при резком расходе и обратно.</summary>
    readonly SolidColorBrush _remainBrush = new(Colors.White);
    bool _hot;

    public event Action<double, double>? Moved;

    public OverlayWindow(double? x, double? y)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "NetPause — табло";

        var font = new FontFamily("Segoe UI Variable Text, Segoe UI");
        _remain = new TextBlock { Foreground = _remainBrush, FontSize = 16, FontWeight = FontWeights.SemiBold, FontFamily = font };
        _line2 = new TextBlock
        {
            Foreground = Freeze(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC))),
            FontSize = 12, FontFamily = font, Margin = new Thickness(0, 1, 0, 0),
        };
        var panel = new StackPanel();
        panel.Children.Add(_remain);
        panel.Children.Add(_line2);
        Content = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Freeze(new SolidColorBrush(Color.FromArgb(0xD9, 0x1C, 0x1C, 0x1C))),
            Padding = new Thickness(12, 7, 14, 8),
            Cursor = Cursors.SizeAll,
            ToolTip = "Табло NetPause. Перетащите, чтобы передвинуть.",
            Child = panel,
        };

        var wa = SystemParameters.WorkArea;
        bool valid = x != null && y != null &&
                     x >= SystemParameters.VirtualScreenLeft && x < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40 &&
                     y >= SystemParameters.VirtualScreenTop && y < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 20;
        Left = valid ? x!.Value : wa.Right - 260;
        Top = valid ? y!.Value : wa.Top + 16;

        MouseLeftButtonDown += (_, _) =>
        {
            try { DragMove(); } catch { }
            UpdateAnchor();
            Moved?.Invoke(Left, Top);
        };
        // текст табло меняет длину: не даём ему уезжать за край экрана
        SizeChanged += (_, _) => KeepOnScreen();
    }

    /// <summary>Табло стоит у правого края экрана: держим этот отступ справа, когда меняется ширина.</summary>
    double? _rightGap;

    static double ScreenRight => SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;

    void UpdateAnchor()
    {
        double gap = ScreenRight - (Left + ActualWidth);
        _rightGap = gap < 80 ? Math.Max(0, gap) : null;
    }

    void KeepOnScreen()
    {
        if (_rightGap is double g) Left = Math.Max(SystemParameters.VirtualScreenLeft, ScreenRight - g - ActualWidth);
        else if (Left + ActualWidth > ScreenRight) Left = Math.Max(SystemParameters.VirtualScreenLeft, ScreenRight - ActualWidth);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // не забирает фокус у игры или окна, в котором вы работаете, и не видно в Alt+Tab
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, -20);
        SetWindowLong(hwnd, -20, ex | 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */);
    }

    public void Update(string remain, string line2, bool hot)
    {
        _remain.Text = remain;
        _line2.Text = line2;
        if (hot == _hot) return;
        _hot = hot;
        _remainBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new System.Windows.Media.Animation.ColorAnimation(hot ? HotColor : NormalColor, TimeSpan.FromMilliseconds(600))
            { EasingFunction = Motion.InOut });
    }

    /// <summary>Табло плавно появляется — чуть опускаясь сверху.</summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        KeepOnScreen();
        UpdateAnchor();
        if (Content is UIElement c) Motion.SlideIn(c, 0, -10, 420);
    }

    bool _closing;

    /// <summary>Табло плавно гаснет и закрывается.</summary>
    public void FadeClose()
    {
        if (_closing) return;
        _closing = true;
        if (Content is UIElement c) Motion.Go(c, OpacityProperty, 0, 260, Motion.InOut, Close);
        else Close();
    }

    static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
}
