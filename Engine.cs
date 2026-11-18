using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetPause;

public sealed class AppEntry
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Имя службы Windows, если это служба внутри svchost.exe.</summary>
    public string? Service { get; set; }
    /// <summary>Часть Windows (лежит в папке Windows).</summary>
    public bool IsSystem { get; set; }
    /// <summary>Блокировать нельзя: без этого интернет сломается у всех.</summary>
    public bool Locked { get; set; }
    public bool Allowed { get; set; }
    public long BytesToday { get; set; }
    /// <summary>За всё время учёта.</summary>
    public long BytesTotal { get; set; }
    /// <summary>За текущую (или последнюю) сессию экономии: от включения до выключения.</summary>
    public long BytesSession { get; set; }

    /// <summary>Расход по сайтам за сегодня из прошлых запусков программы (читается из файла).</summary>
    [JsonIgnore]
    public Dictionary<string, long> SavedHosts { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Адреса (имена сайтов), которые этой программе запрещены при включённой экономии.</summary>
    public List<string> BlockedHosts { get; set; } = new();

    /// <summary>Расход по удалённым адресам за сегодня (только в памяти, с момента запуска).</summary>
    [JsonIgnore]
    public Dictionary<IPAddress, long> Remotes { get; } = new();

    [JsonIgnore]
    public string Key => Service == null ? Path : Path + " [" + Service + "]";
}

public sealed class HostInfo
{
    public string Host = "";
    public long Bytes;
    public int Addresses;
    public bool Blocked;
}

public sealed class State
{
    public bool EconomyOn { get; set; }
    public string Day { get; set; } = "";
    public long SystemBytesToday { get; set; }
    public long SystemBytesTotal { get; set; }
    public long SystemBytesSession { get; set; }
    /// <summary>День, с которого ведётся учёт «за всё время».</summary>
    public string Since { get; set; } = "";
    public DateTime? SessionStart { get; set; }
    public DateTime? SessionEnd { get; set; }
    /// <summary>Строгий режим: при экономии запрещено всё, кроме отмеченного.</summary>
    public bool Strict { get; set; }
    /// <summary>Сейчас запрет по умолчанию действительно включён в файрволе Windows.</summary>
    public bool StrictApplied { get; set; }
    /// <summary>Какими были настройки «исходящие по умолчанию» до строгого режима (3 профиля).</summary>
    public int[]? SavedOutbound { get; set; }
    // ---------- оформление ----------
    /// <summary>Оформление: system, blood, graphite, orbit, paper.</summary>
    public string Theme { get; set; } = "system";
    /// <summary>Значок у часов: auto (как у оформления), wifi (капля и Wi-Fi), pause.</summary>
    public string TrayIcon { get; set; } = "auto";

    // ---------- «Транслятор» ----------
    /// <summary>Какой версией программы записаны эти данные (для копии перед обновлением).</summary>
    public string? AppVersion { get; set; }
    /// <summary>Значок уже один раз вынесли из скрытых к часам.</summary>
    public bool TrayPromoted { get; set; }
    /// <summary>Показывать табло поверх всех окон.</summary>
    public bool TranslatorOn { get; set; }
    /// <summary>Сайты, где провайдер показывает остаток трафика.</summary>
    public List<string> Trackers { get; set; } = new();
    /// <summary>Какой из трекеров отслеживается сейчас.</summary>
    public string? ActiveTracker { get; set; }
    /// <summary>Пропуски трекеров (адрес сайта → зашифрованный пропуск).</summary>
    public Dictionary<string, string> TrackerTokens { get; set; } = new();
    /// <summary>Последний логин на трекере (подставляется в окно входа).</summary>
    public string? TrackerUser { get; set; }
    /// <summary>Сохранённые по просьбе человека пароли трекеров (адрес сайта → зашифрованный пароль).</summary>
    public Dictionary<string, string> TrackerPasswords { get; set; } = new();
    /// <summary>Провайдер, выбранный на сайте трекера (если сайт не сообщит включённый).</summary>
    public int? TrackerProvider { get; set; }
    /// <summary>Суточная норма у трекера, заданная человеком, МБ (адрес сайта → норма).</summary>
    public Dictionary<string, int> TrackerDailyMb { get; set; } = new();
    /// <summary>Суточная норма, которую NetPause узнал на сайте (ежедневное начисление), МБ.</summary>
    public Dictionary<string, int> TrackerDailyAutoMb { get; set; } = new();
    /// <summary>Включать интернет на сайте трекера самостоятельно, когда компьютер подключается к сети или включается.</summary>
    public bool AutoDataOn { get; set; } = true;
    /// <summary>Тревога при резком расходе трафика.</summary>
    public bool AlertOn { get; set; } = true;
    /// <summary>Порог тревоги: мегабайт за минуту.</summary>
    public int AlertMbPerMin { get; set; } = 30;
    public bool AlertSound { get; set; } = true;
    /// <summary>Где на экране стоит табло (null — в правом верхнем углу).</summary>
    public double? OverlayX { get; set; }
    public double? OverlayY { get; set; }

    /// <summary>Сколько сегодня ушло через каждую сеть (ключ — сеть).</summary>
    public Dictionary<string, long> NetBytesToday { get; set; } = new();

    /// <summary>Сети, которые NetPause видел или нашёл в сохранённых сетях Windows, и правила для них.</summary>
    public List<KnownNet> Networks { get; set; } = new();
    /// <summary>Узнавать раздачу с телефона и включать на ней экономию самостоятельно.</summary>
    public bool AutoHotspot { get; set; } = true;
    /// <summary>Экономию последний раз включил или выключил NetPause сам (по правилу сети), а не человек.</summary>
    public bool EconomyAuto { get; set; }
    /// <summary>Сеть, к которой были подключены при последней проверке.</summary>
    public string? LastNetId { get; set; }
    /// <summary>Chrome переведён на системный DNS, чтобы NetPause видел имена сайтов.</summary>
    public bool ChromeNames { get; set; }
    /// <summary>Прежние политики Chrome (null — политики не было).</summary>
    public Dictionary<string, string?>? ChromeSaved { get; set; }
    /// <summary>NetPause сейчас ограничивает службу «Оптимизация доставки» через её настройки.</summary>
    public bool DoLimitApplied { get; set; }
    /// <summary>Прежние значения настроек «Оптимизации доставки» (null — значения не было).</summary>
    public Dictionary<string, int?>? DoSaved { get; set; }
    /// <summary>При экономии помечать текущую сеть Wi-Fi лимитной.</summary>
    public bool Metered { get; set; }
    /// <summary>Сеть, которую пометил NetPause (чтобы снять пометку именно с неё).</summary>
    public string? MeteredProfile { get; set; }
    public List<AppEntry> Apps { get; set; } = new();
}

public sealed class HostsFile
{
    public string Day { get; set; } = "";
    public Dictionary<string, Dictionary<string, long>> Apps { get; set; } = new();
}

public enum Period { Session, Day, Week, All }

public sealed class PeriodRow
{
    public string Name = "";
    public string? Path;
    public long Bytes;
}

/// <summary>
/// Считает трафик по программам и службам (ETW) и держит блокирующие правила
/// во встроенном файрволе Windows для неразрешённых.
/// </summary>
public sealed partial class Engine : IDisposable
{
    const string Group = "NetPause";
    const string RulePrefix = "NetPause: ";
    const string KernelPath = "System";
    const string SharedService = "*";

    // службы, без которых сеть не работает вообще
    static readonly HashSet<string> CriticalServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dnscache", "Dhcp", "NlaSvc", "netprofm", "iphlpsvc", "WinHttpAutoProxySvc", "Wcmsvc",
        "WlanSvc", "lmhosts", "RpcSs", "RpcEptMapper", "mpssvc", "BFE", "nsi", "CryptSvc",
    };

    static readonly HashSet<string> CriticalFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "lsass.exe", "services.exe", "wininit.exe", "csrss.exe", "smss.exe", "winlogon.exe",
    };

    // папки внутри Windows, откуда запускаются чужие установщики и обновлялки
    static readonly string[] NotSystemDirs = { "Temp\\", "SystemTemp\\", "Installer\\", "Tasks\\", "Downloaded Program Files\\" };

    static readonly string WinDir =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    readonly object _lock = new();
    readonly object _saveLock = new();
    readonly Dictionary<string, AppEntry> _byKey = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, AppEntry?> _pidCache = new();
    readonly Dictionary<int, string> _exeByPid = new();
    readonly string _stateFile;
    readonly string _self;

    State _state = new();
    bool _dirty;
    bool _disposed;
    int _ticks;
    TraceEventSession? _session;
    System.Threading.Timer? _timer;
    System.Threading.Timer? _resume;
    Task _syncChain = Task.CompletedTask;
    volatile HashSet<string> _applied = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<int, List<(string Name, string Display)>> _services = new();
    DateTime _servicesAt = DateTime.MinValue;

    const string DayFormat = "yyyy-MM-dd";
    const string UnknownKey = "?";
    const int HistoryDays = 60;
    // день -> (ключ программы -> байты); только завершённые дни, сегодняшний живёт в state.json
    Dictionary<string, Dictionary<string, long>> _history = new();
    string HistoryFile => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_stateFile)!, "history.json");
    string HostsPath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_stateFile)!, "hosts-today.json");

    const int MaxRemotesPerApp = 400;
    const string HostRuleMark = " > ";
    const string AllowMark = "+ ";
    static readonly Guid DnsClientProvider = new("1C95126E-7EEA-49A9-A3FE-A378B03DDB4D");
    TraceEventSession? _dnsSession;
    HashSet<IPAddress> _local = new();
    readonly Dictionary<IPAddress, string> _ipNames = new();       // адрес -> имя сайта
    readonly HashSet<IPAddress> _reverseAsked = new();
    readonly Dictionary<string, string> _hostRuleAddrs = new(StringComparer.OrdinalIgnoreCase); // только из очереди правил

    public event Action<AppEntry>? AppAdded;
    /// <summary>Правила файрвола только что приведены в соответствие (или попытка не удалась).</summary>
    public event Action? SyncCompleted;
    public string? MonitorError { get; private set; }
    public string? FirewallError { get; private set; }

    public Engine()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetPause");
        Directory.CreateDirectory(dir);
        ProtectDataDir(dir);
        _stateFile = System.IO.Path.Combine(dir, "state.json");
        _self = Environment.ProcessPath ?? "";
    }

    // ---------- состояние ----------

    public bool EconomyOn { get { lock (_lock) return _state.EconomyOn; } }

    /// <summary>Сколько ушло именно в интернет с запуска NetPause (без обмена внутри местной сети).</summary>
    long _inetBytes;
    public long InternetBytes { get { lock (_lock) return _inetBytes; } }

    /// <summary>
    /// Адрес в интернете, а не в местной сети: не частный (10.*, 172.16–31.*, 192.168.*),
    /// не адрес провайдера (100.64.*), не локальный и не рассылка.
    /// </summary>
    static bool IsInternet(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            if (b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224) return false;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
            if (b[0] == 192 && b[1] == 168) return false;
            if (b[0] == 169 && b[1] == 254) return false;
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;
            return true;
        }
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal) return false;
        if ((b[0] & 0xfe) == 0xfc) return false; // уникальные локальные адреса fc00::/7
        return true;
    }

    /// <summary>Показать уведомление у часов (тем же способом, что и о смене экономии).</summary>
    public void Notice(string text) => AutoSwitched?.Invoke(text);

    public long SystemToday { get { lock (_lock) return _state.SystemBytesToday; } }
    public long TotalToday { get { lock (_lock) return _state.SystemBytesToday + _state.Apps.Sum(a => a.BytesToday); } }
    public int AllowedCount { get { lock (_lock) return _state.Apps.Count(a => a.Allowed && !a.IsSystem); } }
    public List<AppEntry> GetApps() { lock (_lock) return _state.Apps.ToList(); }

    /// <summary>Есть ли сейчас в файрволе действующее блокирующее правило для этой записи.</summary>
    public bool IsBlocked(string key) => _applied.Contains(key);

    public void Start()
    {
        BackupOnUpgrade(System.IO.Path.GetDirectoryName(_stateFile)!);
        Load();
        lock (_lock)
        {
            LoadSecrets(); // логины и пароли — из папки этого пользователя Windows
            if (_state.AppVersion != AppVersion) { _state.AppVersion = AppVersion; _dirty = true; }
        }
        IpOwners.LoadAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "Data", "ip2asn-combined.tsv.gz"));
        RefreshLocal();
        StartMonitor();
        StartDnsMonitor();
        _timer = new System.Threading.Timer(_ => Tick(), null, 5000, 5000);
        RequestSync();
        StartNetworkWatch();
    }

    static State? TryRead(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<State>(File.ReadAllText(file)) : null;
        }
        catch { return null; }
    }

    void Load()
    {
        _state = TryRead(_stateFile) ?? TryRead(_stateFile + ".bak") ?? new State();
        _state.Apps ??= new List<AppEntry>();
        _state.Apps.RemoveAll(a => a == null || string.IsNullOrEmpty(a.Path));
        _state.Trackers ??= new List<string>();
        _state.TrackerTokens ??= new Dictionary<string, string>();
        _state.TrackerPasswords ??= new Dictionary<string, string>();
        _state.TrackerDailyMb ??= new Dictionary<string, int>();
        _state.TrackerDailyAutoMb ??= new Dictionary<string, int>();
        if (_state.AlertMbPerMin <= 0) _state.AlertMbPerMin = 30;
        _state.Networks ??= new List<KnownNet>();
        _state.Networks.RemoveAll(n => n == null || string.IsNullOrEmpty(n.Id));
        _state.NetBytesToday ??= new Dictionary<string, long>();

        // Раньше трекер был один на все сети. Переносим его на сети Starlink:
        // остаток с трекера относится именно к ним.
        if (_state.ActiveTracker != null && !_state.Networks.Any(n => n.Tracker != null))
            foreach (var n in _state.Networks.Where(n => n.Name.Contains("starlink", StringComparison.OrdinalIgnoreCase)))
                n.Tracker = _state.ActiveTracker;

        // убираем записи о старых версиях программ, если уже есть новая на диске
        var existing = _state.Apps.Where(a => File.Exists(a.Path)).ToList();
        _state.Apps = _state.Apps
            .Where(a => File.Exists(a.Path) || !existing.Any(e => IsTwin(e, a.Path, a.Description)))
            .GroupBy(a => a.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .ToList();

        foreach (var a in _state.Apps)
        {
            // признаки «системное» и «нельзя блокировать» всегда вычисляем заново, файлу не верим
            Classify(a.Path, a.Service, out bool system, out bool locked);
            a.IsSystem = system;
            a.Locked = locked;
            a.BlockedHosts ??= new List<string>();
            if (a.BytesTotal < a.BytesToday) a.BytesTotal = a.BytesToday; // данные до появления статистики
            _byKey[a.Key] = a;
        }
        if (_state.SystemBytesTotal < _state.SystemBytesToday) _state.SystemBytesTotal = _state.SystemBytesToday;
        // экономия уже была включена до появления статистики — сессию считаем с этого запуска
        if (_state.EconomyOn && _state.SessionStart == null) _state.SessionStart = DateTime.Now;
        if (string.IsNullOrEmpty(_state.Since))
            _state.Since = string.IsNullOrEmpty(_state.Day) ? DateTime.Now.ToString(DayFormat) : _state.Day;

        try
        {
            if (File.Exists(HistoryFile))
                _history = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, long>>>(
                    File.ReadAllText(HistoryFile)) ?? new();
        }
        catch { _history = new(); }

        RollDay();

        // расход по сайтам за сегодня, накопленный до перезапуска
        try
        {
            if (File.Exists(HostsPath))
            {
                var hf = JsonSerializer.Deserialize<HostsFile>(File.ReadAllText(HostsPath));
                if (hf != null && hf.Day == _state.Day && hf.Apps != null)
                    foreach (var kv in hf.Apps)
                        if (kv.Value != null && _byKey.TryGetValue(kv.Key, out var e))
                            foreach (var h in kv.Value) e.SavedHosts[h.Key] = h.Value;
            }
        }
        catch { }
    }

    static void Classify(string path, string? service, out bool system, out bool locked)
    {
        if (path == KernelPath) { system = true; locked = false; return; }
        system = IsWindowsPath(path);
        if (service != null)
            locked = service == SharedService || CriticalServices.Contains(service);
        else
            locked = system && CriticalFiles.Contains(System.IO.Path.GetFileName(path));
    }

    static bool IsWindowsPath(string path)
    {
        if (!path.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = path.Substring(WinDir.Length);
        return !NotSystemDirs.Any(d => rest.StartsWith(d, StringComparison.OrdinalIgnoreCase));
    }

    void Save(bool force = false)
    {
        lock (_saveLock)
        {
            string json, secrets;
            lock (_lock)
            {
                if (!_dirty && !force) return;
                _dirty = false;
                // логины, пароли и пропуски — в папку пользователя, а в общие настройки не попадают
                secrets = SecretsJson();
                var (tokens, passwords, user) = (_state.TrackerTokens, _state.TrackerPasswords, _state.TrackerUser);
                _state.TrackerTokens = new();
                _state.TrackerPasswords = new();
                _state.TrackerUser = null;
                try { json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }); }
                finally
                {
                    _state.TrackerTokens = tokens;
                    _state.TrackerPasswords = passwords;
                    _state.TrackerUser = user;
                }
            }
            WriteSecrets(secrets);
            try
            {
                // пишем во временный файл и подменяем: обрыв записи не испортит настройки
                var tmp = _stateFile + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(_stateFile)) File.Replace(tmp, _stateFile, _stateFile + ".bak");
                else File.Move(tmp, _stateFile);
            }
            catch { }
        }
    }

    void Tick()
    {
        bool resync;
        bool refreshLocal;
        lock (_lock)
        {
            if (_disposed) return;
            RollDay();
            _ticks++;
            refreshLocal = _ticks % 6 == 0;
            if (_ticks % 6 == 0)
            {
                // PID переиспользуются, службы запускаются и останавливаются
                _pidCache.Clear();
                _services.Clear();
                _servicesAt = DateTime.MinValue;
            }
            // раз в минуту сверяем правила с файрволом: вдруг их удалили снаружи
            resync = _ticks % 12 == 0 && _state.EconomyOn;
        }
        Save();
        if (refreshLocal)
        {
            RefreshLocal();
            WriteHostsSnapshot();
        }
        if (resync) RequestSync();
    }

    /// <summary>Раз в полминуты пишет в hosts-today.json, куда ходила каждая программа (для проверки и отладки).</summary>
    void WriteHostsSnapshot()
    {
        try
        {
            var snap = new HostsFile();
            lock (_lock) snap.Day = _state.Day;
            foreach (var a in GetApps())
            {
                var top = GetDetails(a.Key, false).Where(h => h.Bytes > 0)
                    .OrderByDescending(h => h.Bytes).Take(60).ToDictionary(h => h.Host, h => h.Bytes);
                if (top.Count > 0) snap.Apps[a.Key] = top;
            }
            var tmp = HostsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snap, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            File.Move(tmp, HostsPath, true);
        }
        catch { }
    }

    /// <summary>Собственные адреса компьютера: по ним отличаем «наш» конец соединения от удалённого.</summary>
    void RefreshLocal()
    {
        try
        {
            var set = new HashSet<IPAddress>();
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                foreach (var u in ni.GetIPProperties().UnicastAddresses)
                    set.Add(Norm(u.Address));
            lock (_lock) _local = set;
        }
        catch { }
    }

    static IPAddress Norm(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) return ip.MapToIPv4();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && ip.ScopeId != 0)
            return new IPAddress(ip.GetAddressBytes());
        return ip;
    }

    void RollDay()
    {
        var today = DateTime.Now.ToString(DayFormat);
        if (_state.Day == today) return;

        // закончившийся день уходит в историю
        if (!string.IsNullOrEmpty(_state.Day))
        {
            var day = new Dictionary<string, long>();
            foreach (var a in _state.Apps)
                if (a.BytesToday > 0) day[a.Key] = a.BytesToday;
            if (_state.SystemBytesToday > 0) day[UnknownKey] = _state.SystemBytesToday;
            if (day.Count > 0) _history[_state.Day] = day;

            var oldest = DateTime.Now.Date.AddDays(-HistoryDays).ToString(DayFormat);
            foreach (var k in _history.Keys.Where(k => string.CompareOrdinal(k, oldest) < 0).ToList())
                _history.Remove(k);
            try
            {
                var tmp = HistoryFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_history));
                File.Move(tmp, HistoryFile, true);
            }
            catch { }
        }

        _state.Day = today;
        _state.SystemBytesToday = 0;
        _state.NetBytesToday.Clear();
        foreach (var a in _state.Apps)
        {
            a.BytesToday = 0;
            a.Remotes.Clear();
            a.SavedHosts.Clear();
        }
        _dirty = true;
    }

    public void SetAllowed(string key, bool allowed)
    {
        lock (_lock)
        {
            if (!_byKey.TryGetValue(key, out var a) || a.Locked || a.Allowed == allowed) return;
            a.Allowed = allowed;
            _dirty = true;
        }
        RequestSync();
    }

    /// <param name="auto">true, если переключает сам NetPause по правилу сети, а не человек.</param>
    public void SetEconomy(bool on, bool auto = false)
    {
        lock (_lock)
        {
            if (_state.EconomyOn == on) return;
            _state.EconomyOn = on;
            _state.EconomyAuto = auto;
            if (on)
            {
                // новая сессия экономии: счётчики сессии с нуля
                _state.SessionStart = DateTime.Now;
                _state.SessionEnd = null;
                _state.SystemBytesSession = 0;
                foreach (var a in _state.Apps) a.BytesSession = 0;
            }
            else
            {
                _state.SessionEnd = DateTime.Now; // итог сессии остаётся виден до следующего включения
            }
            _dirty = true;
        }
        RequestSync();
        Save();
    }

    // ---------- статистика по периодам ----------

    /// <summary>Расход по программам за период, от большего к меньшему.</summary>
    public List<PeriodRow> GetPeriod(Period period, out long total, out string caption)
    {
        var rows = new List<PeriodRow>();
        long unknown = 0;
        lock (_lock)
        {
            switch (period)
            {
                case Period.Session:
                    foreach (var a in _state.Apps)
                        if (a.BytesSession > 0) rows.Add(new PeriodRow { Name = a.Name, Path = a.Path, Bytes = a.BytesSession });
                    unknown = _state.SystemBytesSession;
                    if (_state.SessionStart == null)
                        caption = "Экономия ещё не включалась";
                    else if (_state.EconomyOn || _state.SessionEnd == null)
                        caption = "Экономия включена с " + When(_state.SessionStart.Value);
                    else
                        caption = "Последняя сессия: " + When(_state.SessionStart.Value) + " — " + When(_state.SessionEnd.Value);
                    break;

                case Period.Day:
                    foreach (var a in _state.Apps)
                        if (a.BytesToday > 0) rows.Add(new PeriodRow { Name = a.Name, Path = a.Path, Bytes = a.BytesToday });
                    unknown = _state.SystemBytesToday;
                    caption = "Сегодня, " + DateTime.Now.ToString("d MMMM");
                    break;

                case Period.Week:
                    var sum = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                    foreach (var a in _state.Apps)
                        if (a.BytesToday > 0) sum[a.Key] = a.BytesToday;
                    unknown = _state.SystemBytesToday;
                    int daysWithData = 1;
                    for (int i = 1; i <= 6; i++)
                    {
                        if (!_history.TryGetValue(DateTime.Now.Date.AddDays(-i).ToString(DayFormat), out var day)) continue;
                        daysWithData++;
                        foreach (var kv in day)
                        {
                            if (kv.Key == UnknownKey) { unknown += kv.Value; continue; }
                            sum.TryGetValue(kv.Key, out long cur);
                            sum[kv.Key] = cur + kv.Value;
                        }
                    }
                    foreach (var kv in sum)
                    {
                        if (_byKey.TryGetValue(kv.Key, out var a))
                            rows.Add(new PeriodRow { Name = a.Name, Path = a.Path, Bytes = kv.Value });
                        else
                            rows.Add(new PeriodRow { Name = System.IO.Path.GetFileName(kv.Key), Bytes = kv.Value });
                    }
                    caption = daysWithData >= 7
                        ? "Последние 7 дней"
                        : "Последние 7 дней (данные есть за " + daysWithData + ")";
                    break;

                default:
                    foreach (var a in _state.Apps)
                        if (a.BytesTotal > 0) rows.Add(new PeriodRow { Name = a.Name, Path = a.Path, Bytes = a.BytesTotal });
                    unknown = _state.SystemBytesTotal;
                    caption = DateTime.TryParse(_state.Since, out var since)
                        ? "За всё время, с " + since.ToString("d MMMM yyyy")
                        : "За всё время";
                    break;
            }
        }
        if (unknown > 0) rows.Add(new PeriodRow { Name = "Не удалось опознать", Bytes = unknown });
        rows = rows.OrderByDescending(r => r.Bytes).ThenBy(r => r.Name).ToList();
        total = rows.Sum(r => r.Bytes);
        return rows;
    }

    static string When(DateTime t) =>
        t.Date == DateTime.Now.Date ? t.ToString("HH:mm") : t.ToString("d MMMM, HH:mm");

    // ---------- учёт трафика ----------

    void StartMonitor()
    {
        try
        {
            _session = new TraceEventSession("NetPauseSession") { StopOnDispose = true };
            _session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.NetworkTCPIP |
                KernelTraceEventParser.Keywords.Process |
                KernelTraceEventParser.Keywords.ImageLoad);
            var k = _session.Source.Kernel;
            // Запоминаем, какой файл запущен под каким номером процесса: программа может
            // закрыться раньше, чем до нас дойдут её сетевые события, и спросить будет некого.
            k.ProcessStart += d =>
            {
                lock (_lock) { _exeByPid.Remove(d.ProcessID); _pidCache.Remove(d.ProcessID); }
            };
            k.ImageLoad += d =>
            {
                var f = d.FileName;
                if (string.IsNullOrEmpty(f) || !f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
                lock (_lock)
                {
                    if (_exeByPid.Count > 5000) _exeByPid.Clear();
                    if (_exeByPid.TryAdd(d.ProcessID, f)) _pidCache.Remove(d.ProcessID);
                }
            };
            k.TcpIpSend += d => OnBytes(d.ProcessID, d.size, d.saddr, d.daddr);
            k.TcpIpRecv += d => OnBytes(d.ProcessID, d.size, d.saddr, d.daddr);
            k.TcpIpSendIPV6 += d => OnBytes(d.ProcessID, d.size, d.saddr, d.daddr);
            k.TcpIpRecvIPV6 += d => OnBytes(d.ProcessID, d.size, d.saddr, d.daddr);
            k.UdpIpSend += d => OnBytes(d.ProcessID, d.size, d.saddr, d.daddr);
            k.UdpIpRecv += d => OnBytes(d.ProcessID, d.size, d.saddr, d.daddr);
            k.UdpIpSendIPV6 += d => OnBytes(d.ProcessID, d.size, d.saddr, d.daddr);
            k.UdpIpRecvIPV6 += d => OnBytes(d.ProcessID, d.size, d.saddr, d.daddr);
            var session = _session;
            new System.Threading.Thread(() =>
            {
                try
                {
                    session.Source.Process();
                    if (!_disposed) MonitorError = "сеанс учёта был остановлен, перезапустите программу";
                }
                catch (Exception ex) { MonitorError = ex.Message; }
            }) { IsBackground = true, Name = "NetPause ETW" }.Start();
        }
        catch (Exception ex)
        {
            MonitorError = ex.Message;
        }
    }

    void OnBytes(int pid, int size, IPAddress a, IPAddress b)
    {
        try
        {
            if (size <= 0) return;
            if (IPAddress.IsLoopback(a) || IPAddress.IsLoopback(b)) return; // обмен внутри компьютера
            bool needSync;
            lock (_lock)
            {
                if (_disposed) return;
                int before = _state.Apps.Count;
                var e = Resolve(pid);
                needSync = _state.Apps.Count != before && _state.EconomyOn;
                // расход по сетям: сколько сегодня ушло через сеть, к которой подключены сейчас
                if (_curNet is string net)
                {
                    _state.NetBytesToday.TryGetValue(net, out long nb);
                    _state.NetBytesToday[net] = nb + size;
                }
                // Отдельно — то, что ушло именно в интернет. Обмен с устройствами и серверами
                // внутри сети (например, с сайтом трекера на сервере судна) квоту не тратит.
                var other = _local.Contains(Norm(a)) ? Norm(b) : Norm(a);
                if (IsInternet(other)) _inetBytes += size;
                if (e == null)
                {
                    _state.SystemBytesToday += size;
                    _state.SystemBytesTotal += size;
                    if (_state.EconomyOn) _state.SystemBytesSession += size;
                }
                else
                {
                    e.BytesToday += size;
                    e.BytesTotal += size;
                    if (_state.EconomyOn) e.BytesSession += size;
                    var na = Norm(a);
                    var remote = _local.Contains(na) ? Norm(b) : na;
                    if (!e.Remotes.TryGetValue(remote, out long cur))
                    {
                        if (e.Remotes.Count >= MaxRemotesPerApp)
                        {
                            remote = IPAddress.None; // слишком много адресов — складываем в «прочие»
                            e.Remotes.TryGetValue(remote, out cur);
                        }
                        else if (_state.EconomyOn && e.BlockedHosts.Count > 0 &&
                                 e.BlockedHosts.Any(bh => HostMatches(bh, HostOf(remote))))
                        {
                            needSync = true; // у запрещённого сайта появился новый адрес
                        }
                    }
                    e.Remotes[remote] = cur + size;
                }
                _dirty = true;
            }
            if (needSync) RequestSync();
        }
        catch { } // одно битое событие не должно останавливать учёт
    }

    // ---------- адреса и имена сайтов ----------

    /// <summary>Слушаем ответы DNS: так узнаём, какому сайту принадлежит адрес.</summary>
    void StartDnsMonitor()
    {
        try
        {
            _dnsSession = new TraceEventSession("NetPauseDnsSession") { StopOnDispose = true };
            _dnsSession.EnableProvider(DnsClientProvider);
            _dnsSession.Source.Dynamic.All += d =>
            {
                int id = (int)d.ID;
                if (id != 3006 && id != 3008) return; // «запрос DNS начат» и «завершён»
                try
                {
                    // Запрос DNS делает сама программа, поэтому так мы узнаём о программах,
                    // которые пытаются выйти в интернет, даже если они заблокированы.
                    Touch(d.ProcessID);
                    if (id == 3008)
                        OnDns(d.PayloadByName("QueryName") as string, d.PayloadByName("QueryResults") as string);
                }
                catch { }
            };
            var session = _dnsSession;
            new System.Threading.Thread(() =>
            {
                try { session.Source.Process(); } catch { }
            }) { IsBackground = true, Name = "NetPause DNS" }.Start();
        }
        catch { } // без имён сайтов программа работает, просто показывает числовые адреса
    }

    void OnDns(string? name, string? results)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(results)) return;
        name = name.Trim().TrimEnd('.').ToLowerInvariant();
        bool sync = false;
        lock (_lock)
        {
            if (_disposed) return;
            if (_ipNames.Count > 20000) _ipNames.Clear();
            foreach (var part in results.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!IPAddress.TryParse(part, out var ip)) continue; // строки «type: 5 …» — это псевдонимы
                _ipNames[Norm(ip)] = name;
                sync = true;
            }
            sync = sync && _state.EconomyOn && _state.Apps.Any(a => a.BlockedHosts.Any(b => HostMatches(b, name)));
        }
        if (sync) RequestSync();
    }

    /// <summary>Заносит программу в список, даже если трафика у неё ещё не было.</summary>
    void Touch(int pid)
    {
        bool sync;
        lock (_lock)
        {
            if (_disposed) return;
            int before = _state.Apps.Count;
            Resolve(pid);
            sync = _state.Apps.Count != before && _state.EconomyOn;
            if (_state.Apps.Count != before) _dirty = true;
        }
        if (sync) RequestSync();
    }

    /// <summary>Запрет сайта действует и на все его поддомены: «nvidia.com» закрывает и «gfe.nvidia.com».</summary>
    static bool HostMatches(string blocked, string host) =>
        string.Equals(blocked, host, StringComparison.OrdinalIgnoreCase) ||
        (host.Length > blocked.Length &&
         host.EndsWith("." + blocked, StringComparison.OrdinalIgnoreCase) &&
         !IPAddress.TryParse(blocked, out _));

    string HostOf(IPAddress ip)
    {
        if (ip.Equals(IPAddress.None)) return "прочие адреса";
        return _ipNames.TryGetValue(ip, out var n) ? n : ip.ToString();
    }

    /// <summary>Куда ходила программа сегодня: по сайтам, плюс запрещённые сайты без трафика.</summary>
    public List<HostInfo> GetDetails(string key, bool resolve = true)
    {
        var ask = new List<IPAddress>();
        List<HostInfo> result;
        lock (_lock)
        {
            if (!_byKey.TryGetValue(key, out var e)) return new List<HostInfo>();
            var map = new Dictionary<string, HostInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in e.Remotes)
            {
                var h = HostOf(kv.Key);
                if (!map.TryGetValue(h, out var hi)) map[h] = hi = new HostInfo { Host = h };
                hi.Bytes += kv.Value;
                hi.Addresses++;
                // имени нет — попробуем узнать его обратным запросом DNS
                if (resolve && !kv.Key.Equals(IPAddress.None) && !_ipNames.ContainsKey(kv.Key) &&
                    ask.Count < 16 && _reverseAsked.Add(kv.Key))
                    ask.Add(kv.Key);
            }
            // то, что накопилось сегодня до перезапуска программы
            foreach (var kv in e.SavedHosts)
            {
                if (!map.TryGetValue(kv.Key, out var hi)) map[kv.Key] = hi = new HostInfo { Host = kv.Key };
                hi.Bytes += kv.Value;
            }
            foreach (var h in e.BlockedHosts)
                if (!map.ContainsKey(h)) map[h] = new HostInfo { Host = h };
            foreach (var hi in map.Values)
                hi.Blocked = e.BlockedHosts.Any(b => HostMatches(b, hi.Host));
            result = map.Values.ToList();
        }
        foreach (var ip in ask) _ = ReverseAsync(ip);
        return result;
    }

    /// <summary>
    /// Забыть сайты в подробностях программы: убрать их адреса и накопленный за день трафик из списка.
    /// Если сайт снова потратит трафик, он появится заново. Расход самой программы и запреты сайтов не меняются.
    /// </summary>
    public void ForgetHosts(string key, IEnumerable<string> hosts)
    {
        var set = new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0) return;
        lock (_lock)
        {
            if (!_byKey.TryGetValue(key, out var e)) return;
            foreach (var ip in e.Remotes.Keys.Where(ip => set.Contains(HostOf(ip))).ToList()) e.Remotes.Remove(ip);
            foreach (var h in set) e.SavedHosts.Remove(h);
            _dirty = true;
        }
        WriteHostsSnapshot(); // сразу, чтобы после перезапуска сайт не вернулся из файла
    }

    async Task ReverseAsync(IPAddress ip)
    {
        try
        {
            var he = await Dns.GetHostEntryAsync(ip).ConfigureAwait(false);
            var n = he.HostName?.Trim().TrimEnd('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(n) || n == ip.ToString()) return;
            lock (_lock)
            {
                if (!_ipNames.ContainsKey(ip)) _ipNames[ip] = n;
            }
        }
        catch { }
    }

    public void SetHostBlocked(string key, string host, bool blocked)
    {
        lock (_lock)
        {
            if (!_byKey.TryGetValue(key, out var e) || e.Locked) return;
            bool has = e.BlockedHosts.Any(b => HostMatches(b, host));
            if (has == blocked) return;
            if (blocked) e.BlockedHosts.Add(host);
            else e.BlockedHosts.RemoveAll(b => HostMatches(b, host)); // снимаем и запрет «родительского» сайта
            _dirty = true;
        }
        RequestSync();
    }

    /// <summary>
    /// Запретить или разрешить целую группу. Для сайта запрещается основной домен — это закрывает
    /// и все поддомены; для группы адресов — каждый адрес.
    /// </summary>
    public void SetGroupBlocked(string key, string? domain, List<string> members, bool blocked)
    {
        lock (_lock)
        {
            if (!_byKey.TryGetValue(key, out var e) || e.Locked) return;
            var targets = domain != null ? new List<string> { domain } : members;
            if (blocked)
            {
                foreach (var t in targets)
                    if (!e.BlockedHosts.Any(b => HostMatches(b, t))) e.BlockedHosts.Add(t);
            }
            else
            {
                // снимаем и запреты на отдельные поддомены внутри группы, и запрет «сверху»
                var all = targets.Concat(members).ToList();
                e.BlockedHosts.RemoveAll(b => all.Any(t => HostMatches(b, t) || HostMatches(t, b)));
            }
            _dirty = true;
        }
        RequestSync();
    }

    /// <summary>Все известные адреса сайта для программы — для правила файрвола. Вызывать под _lock.</summary>
    string AddressesFor(AppEntry e, string host)
    {
        var ips = new HashSet<IPAddress>();
        if (IPAddress.TryParse(host, out var literal)) ips.Add(Norm(literal));
        foreach (var kv in _ipNames)
            if (HostMatches(host, kv.Value)) ips.Add(kv.Key);
        foreach (var ip in e.Remotes.Keys)
            if (!ip.Equals(IPAddress.None) && HostMatches(host, HostOf(ip))) ips.Add(ip);
        return string.Join(",", ips.Select(i => i.ToString()).OrderBy(s => s, StringComparer.Ordinal).Take(200));
    }

    /// <summary>Запись для процесса или null, если его не удалось опознать.</summary>
    AppEntry? Resolve(int pid)
    {
        if (pid == 4) return GetOrCreate(KernelPath, null, "Ядро Windows (общий доступ к файлам и др.)");
        if (pid < 4) return null;
        if (_pidCache.TryGetValue(pid, out var cached)) return cached;

        AppEntry? entry = null;
        bool cache = true;
        var path = QueryPath(pid);
        if (path == null && _exeByPid.TryGetValue(pid, out var remembered)) path = remembered; // процесс уже закрылся
        if (path != null && !string.Equals(path, _self, StringComparison.OrdinalIgnoreCase))
        {
            bool svchost = IsWindowsPath(path) &&
                System.IO.Path.GetFileName(path).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase);
            if (svchost)
            {
                var svcs = ServicesFor(pid, out bool known);
                if (svcs.Count == 1)
                    entry = GetOrCreate(path, svcs[0].Name, svcs[0].Display);
                else
                {
                    entry = GetOrCreate(path, SharedService, "Службы Windows (общая группа)");
                    cache = known; // список служб ещё не обновлялся — спросим снова при следующем пакете
                }
            }
            else
            {
                entry = GetOrCreate(path, null, null);
            }
        }
        if (cache) _pidCache[pid] = entry;
        return entry;
    }

    AppEntry GetOrCreate(string path, string? service, string? display)
    {
        var key = service == null ? path : path + " [" + service + "]";
        if (_byKey.TryGetValue(key, out var e)) return e;

        Classify(path, service, out bool system, out bool locked);
        string desc = "";
        if (service == null && path != KernelPath)
        {
            try { desc = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? ""; } catch { }
        }
        // новая версия уже известной программы наследует её разрешение (берём самую свежую запись)
        var twin = service == null && desc.Length > 0
            ? _state.Apps.LastOrDefault(a => IsTwin(a, path, desc))
            : null;
        e = new AppEntry
        {
            Path = path,
            Service = service,
            Description = desc,
            Name = display ?? (desc.Length > 0 ? desc : System.IO.Path.GetFileNameWithoutExtension(path)),
            IsSystem = system,
            Locked = locked,
            // части Windows по умолчанию разрешены, обычные программы — нет
            Allowed = system || (twin?.Allowed ?? false),
        };
        _state.Apps.Add(e);
        _byKey[key] = e;
        AppAdded?.Invoke(e);
        return e;
    }

    static bool IsTwin(AppEntry a, string path, string desc) =>
        a.Service == null && desc.Length > 0 &&
        !string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(System.IO.Path.GetFileName(a.Path), System.IO.Path.GetFileName(path), StringComparison.OrdinalIgnoreCase) &&
        a.Description == desc;

    static string? QueryPath(int pid)
    {
        var h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int n = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString(0, n) : null;
        }
        finally { CloseHandle(h); }
    }

    // ---------- службы Windows ----------

    /// <param name="known">false, если список служб для этого процесса получить пока не удалось.</param>
    List<(string Name, string Display)> ServicesFor(int pid, out bool known)
    {
        if (!_services.ContainsKey(pid) && (DateTime.UtcNow - _servicesAt).TotalSeconds > 5)
        {
            try { _services = EnumServices(); } catch { }
            _servicesAt = DateTime.UtcNow;
        }
        known = _services.TryGetValue(pid, out var l);
        return l ?? new List<(string, string)>();
    }

    static Dictionary<int, List<(string, string)>> EnumServices()
    {
        var map = new Dictionary<int, List<(string, string)>>();
        var scm = OpenSCManager(null, null, 0x0004);
        if (scm == IntPtr.Zero) return map;
        const int size = 256 * 1024;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            int resume = 0;
            int sz = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESS>();
            bool more;
            do
            {
                bool ok = EnumServicesStatusEx(scm, 0, 0x30, 1, buf, size, out _, out int count, ref resume, null);
                more = !ok && Marshal.GetLastWin32Error() == 234; // ERROR_MORE_DATA
                if (!ok && !more) break;
                for (int i = 0; i < count; i++)
                {
                    var s = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESS>(buf + i * sz);
                    var name = Marshal.PtrToStringUni(s.lpServiceName) ?? "";
                    var disp = Marshal.PtrToStringUni(s.lpDisplayName) ?? name;
                    if (s.processId == 0 || name.Length == 0) continue;
                    if (!map.TryGetValue(s.processId, out var l)) map[s.processId] = l = new List<(string, string)>();
                    l.Add((name, disp));
                }
            } while (more);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
            CloseServiceHandle(scm);
        }
        return map;
    }

    // ---------- файрвол Windows ----------
    // Все изменения правил идут строго по очереди через _syncChain.

    public void RequestSync()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _syncChain = _syncChain.ContinueWith(_ => RunSync(false));
        }
    }

    /// <summary>Снимает все блокировки и ждёт завершения не дольше указанного времени.</summary>
    void ClearRules(int waitMs)
    {
        Task t;
        lock (_lock)
        {
            _syncChain = _syncChain.ContinueWith(_ => RunSync(true));
            t = _syncChain;
        }
        try { t.Wait(waitMs); } catch { }
    }

    void RunSync(bool clearAll)
    {
        try { SyncNow(clearAll); }
        catch (Exception ex) { FirewallError = ex.Message; }
        try { SyncCompleted?.Invoke(); } catch { }
    }

    void SyncNow(bool clearAll)
    {
        bool active, strict, metered;
        var lookups = new List<string>();
        lock (_lock)
        {
            active = !clearAll && !_disposed && _state.EconomyOn;
            strict = active && _state.Strict;
            metered = active && _state.Metered;
            if (active)
                lookups = _state.Apps.Where(a => a.Allowed && !a.Locked).SelectMany(a => a.BlockedHosts)
                    .Where(h => !IPAddress.TryParse(h, out _))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList();
        }

        // Заранее узнаём адреса запрещённых сайтов, чтобы блокировать их до первого подключения.
        foreach (var h in lookups)
        {
            try
            {
                var t = Dns.GetHostAddressesAsync(h);
                if (!t.Wait(1500)) continue;
                lock (_lock)
                    foreach (var ip in t.Result) _ipNames[Norm(ip)] = h.ToLowerInvariant();
            }
            catch { }
        }

        // ключ правила -> что это за правило
        var desired = new Dictionary<string, RuleSpec>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            if (active)
            {
                if (strict && _self.Length > 0)
                    // сам NetPause должен выходить в сеть всегда: например, чтобы спрашивать остаток у трекера
                    desired[AllowMark + "NetPause"] = new RuleSpec { Label = "NetPause", Path = _self, Allow = true };

                foreach (var a in _state.Apps)
                {
                    // временно разрешено самим NetPause (окно входа на сайт трекера)
                    if (_tempAllowFiles.Contains(System.IO.Path.GetFileName(a.Path)))
                    {
                        if (strict) desired[AllowMark + a.Key] = new RuleSpec { Label = a.Name, Path = a.Path, Service = a.Service, Allow = true };
                        continue;
                    }
                    if (a.Locked)
                    {
                        // необходимое для работы сети в строгом режиме разрешаем явно
                        if (strict && a.Service != SharedService)
                            desired[AllowMark + a.Key] = new RuleSpec { Label = a.Name, Path = a.Path, Service = a.Service, Allow = true };
                        continue;
                    }
                    if (!a.Allowed)
                    {
                        desired[a.Key] = new RuleSpec { Label = a.Name, Path = a.Path, Service = a.Service };
                        continue;
                    }
                    if (strict)
                        desired[AllowMark + a.Key] = new RuleSpec { Label = a.Name, Path = a.Path, Service = a.Service, Allow = true };
                    // программа разрешена, но отдельные сайты ей запрещены
                    foreach (var h in a.BlockedHosts)
                    {
                        var addrs = AddressesFor(a, h);
                        if (addrs.Length > 0)
                            desired[a.Key + HostRuleMark + h] = new RuleSpec { Label = a.Name, Path = a.Path, Service = a.Service, Remote = addrs };
                    }
                }
                if (strict)
                {
                    // без DNS и DHCP не заработает ничего, это служебные крохи трафика
                    desired[AllowMark + "DNS (UDP)"] = new RuleSpec { Label = "DNS", Allow = true, Protocol = 17, Ports = "53" };
                    desired[AllowMark + "DNS (TCP)"] = new RuleSpec { Label = "DNS", Allow = true, Protocol = 6, Ports = "53" };
                    desired[AllowMark + "DHCP"] = new RuleSpec { Label = "DHCP", Allow = true, Protocol = 17, Ports = "67,68,546,547" };
                }
            }
        }

        dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
        dynamic rules = policy.Rules;

        // выходим из строгого режима раньше, чем уберём разрешающие правила
        if (!strict) RestoreOutbound((object)policy);

        // каждый раз читаем настоящие правила из файрвола, а не полагаемся на память
        HashSet<string> existing = ReadOwnRules(rules);
        string? error = null;

        // лишние правила, а также правила по адресам, у которых список адресов устарел
        foreach (var k in existing.Where(k =>
                     !desired.TryGetValue(k, out var d) ||
                     (d.Remote != null && (!_hostRuleAddrs.TryGetValue(k, out var old) || old != d.Remote))).ToList())
        {
            try { rules.Remove(RulePrefix + k); existing.Remove(k); _hostRuleAddrs.Remove(k); }
            catch (Exception ex) { error = ex.Message; }
        }

        foreach (var kv in desired.Where(kv => !existing.Contains(kv.Key)).ToList())
        {
            try
            {
                dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
                rule.Name = RulePrefix + kv.Key;
                rule.Description = "Создано программой NetPause. Удаляется при выключении экономии.";
                rule.Grouping = Group;
                var s = kv.Value;
                if (s.Path != null) rule.ApplicationName = s.Path;
                if (s.Service != null) rule.serviceName = s.Service;
                if (s.Protocol >= 0) rule.Protocol = s.Protocol; // протокол задаётся раньше портов
                if (s.Ports != null) rule.RemotePorts = s.Ports;
                rule.Direction = 2;              // исходящие
                rule.Action = s.Allow ? 1 : 0;   // разрешить / блокировать
                rule.Profiles = 0x7FFFFFFF;      // все сети
                if (s.Remote != null) rule.RemoteAddresses = s.Remote;
                rule.Enabled = true;
                rules.Add(rule);
                existing.Add(kv.Key);
                if (s.Remote != null) _hostRuleAddrs[kv.Key] = s.Remote;
            }
            catch (Exception ex) { error = kv.Value.Label + ": " + ex.Message; }
        }

        // запрет по умолчанию включаем только когда разрешающие правила уже на месте
        if (strict)
        {
            try { ApplyStrictOutbound((object)policy); }
            catch (Exception ex) { error = "строгий режим: " + ex.Message; }
        }

        try { ApplyMetered(metered); } catch { }

        // «Оптимизацию доставки» файрвол остановить не может: у системных служб свои
        // встроенные разрешения, которые срабатывают раньше наших правил.
        bool limitDo;
        lock (_lock)
            limitDo = active && _state.Apps.Any(a => a.Service == DoService && !a.Allowed && !a.Locked);
        try { ApplyDoLimit(limitDo); }
        catch (Exception ex) { error = "Оптимизация доставки: " + ex.Message; }

        _applied = existing;
        FirewallError = error;
    }

    sealed class RuleSpec
    {
        public string Label = "";
        public string? Path;
        public string? Service;
        public string? Remote;
        public bool Allow;
        public int Protocol = -1;
        public string? Ports;
    }

    // ---------- строгий режим: запрет всего исходящего по умолчанию ----------

    static readonly int[] Profiles = { 1, 2, 4 }; // домен, частная, общедоступная

    static int GetOutbound(object policy, int profile) => Convert.ToInt32(policy.GetType().InvokeMember(
        "DefaultOutboundAction", System.Reflection.BindingFlags.GetProperty, null, policy, new object[] { profile }));

    static void SetOutbound(object policy, int profile, int action) => policy.GetType().InvokeMember(
        "DefaultOutboundAction", System.Reflection.BindingFlags.SetProperty, null, policy, new object[] { profile, action });

    void ApplyStrictOutbound(object policy)
    {
        bool applied;
        lock (_lock) applied = _state.StrictApplied;
        if (!applied)
        {
            // сначала запоминаем прежние настройки на диск, и только потом меняем их
            var prev = Profiles.Select(p => GetOutbound(policy, p)).ToArray();
            lock (_lock)
            {
                _state.SavedOutbound = prev;
                _state.StrictApplied = true;
            }
            Save(true);
        }
        foreach (var p in Profiles)
            if (GetOutbound(policy, p) != 0) SetOutbound(policy, p, 0); // 0 = блокировать
    }

    void RestoreOutbound(object policy)
    {
        int[]? saved;
        lock (_lock)
        {
            if (!_state.StrictApplied) return;
            saved = _state.SavedOutbound;
        }
        for (int i = 0; i < Profiles.Length; i++)
            SetOutbound(policy, Profiles[i], saved != null && saved.Length == Profiles.Length ? saved[i] : 1); // 1 = разрешать
        lock (_lock)
        {
            _state.StrictApplied = false;
            _state.SavedOutbound = null;
        }
        Save(true);
    }

    public bool Strict { get { lock (_lock) return _state.Strict; } }
    public bool Metered { get { lock (_lock) return _state.Metered; } }

    // ---------- имена сайтов в Chrome ----------
    // Chrome по умолчанию узнаёт адреса сайтов сам (встроенный DNS, иногда DNS через HTTPS),
    // и Windows этих запросов не видит. Две политики Chrome возвращают его к системному DNS.

    const string ChromePolicyKey = @"SOFTWARE\Policies\Google\Chrome";
    static readonly (string Name, object Value, Microsoft.Win32.RegistryValueKind Kind)[] ChromePolicies =
    {
        ("BuiltInDnsClientEnabled", 0, Microsoft.Win32.RegistryValueKind.DWord),
        ("DnsOverHttpsMode", "off", Microsoft.Win32.RegistryValueKind.String),
    };

    public bool ChromeNames { get { lock (_lock) return _state.ChromeNames; } }

    public void SetChromeNames(bool on)
    {
        lock (_lock) if (_state.ChromeNames == on) return;
        using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(ChromePolicyKey, true)!;
        if (on)
        {
            // прежние значения сохраняем, чтобы при выключении вернуть ровно их
            var saved = new Dictionary<string, string?>();
            foreach (var (name, _, kind) in ChromePolicies)
            {
                var v = key.GetValue(name);
                saved[name] = v == null ? null : (kind == Microsoft.Win32.RegistryValueKind.DWord ? "d:" : "s:") + v;
            }
            lock (_lock)
            {
                _state.ChromeSaved = saved;
                _state.ChromeNames = true;
                _dirty = true;
            }
            Save(true);
            foreach (var (name, value, kind) in ChromePolicies) key.SetValue(name, value, kind);
        }
        else
        {
            Dictionary<string, string?>? saved;
            lock (_lock) saved = _state.ChromeSaved;
            foreach (var (name, _, _) in ChromePolicies)
            {
                if (saved != null && saved.TryGetValue(name, out var old) && old != null && old.Length > 2)
                {
                    if (old.StartsWith("d:") && int.TryParse(old.Substring(2), out var n))
                        key.SetValue(name, n, Microsoft.Win32.RegistryValueKind.DWord);
                    else
                        key.SetValue(name, old.Substring(2), Microsoft.Win32.RegistryValueKind.String);
                }
                else key.DeleteValue(name, false);
            }
            lock (_lock)
            {
                _state.ChromeNames = false;
                _state.ChromeSaved = null;
                _dirty = true;
            }
            Save(true);
        }
    }

    // ---------- «Оптимизация доставки» (DoSvc) ----------

    const string DoService = "DoSvc";
    const string DoPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization";

    // скорость в КБ/с (0 означает «без ограничений», поэтому ставим 1); режим 0 — без обмена с другими ПК
    static readonly (string Name, int Value)[] DoLimits =
    {
        ("DOMaxBackgroundDownloadBandwidth", 1),
        ("DOMaxForegroundDownloadBandwidth", 1),
        ("DODownloadMode", 0),
    };

    /// <summary>Ограничивает службу через её же настройки Windows или возвращает прежние.</summary>
    void ApplyDoLimit(bool on)
    {
        bool applied;
        lock (_lock) applied = _state.DoLimitApplied;
        if (on == applied) return;

        using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(DoPolicyKey, true)!;
        if (on)
        {
            // сначала запоминаем прежние значения на диск, и только потом меняем
            var saved = new Dictionary<string, int?>();
            foreach (var (name, _) in DoLimits)
                saved[name] = key.GetValue(name) is int v ? v : null;
            lock (_lock)
            {
                _state.DoSaved = saved;
                _state.DoLimitApplied = true;
            }
            Save(true);
            foreach (var (name, value) in DoLimits)
                key.SetValue(name, value, Microsoft.Win32.RegistryValueKind.DWord);
        }
        else
        {
            Dictionary<string, int?>? saved;
            lock (_lock) saved = _state.DoSaved;
            foreach (var (name, _) in DoLimits)
            {
                if (saved != null && saved.TryGetValue(name, out var old) && old.HasValue)
                    key.SetValue(name, old.Value, Microsoft.Win32.RegistryValueKind.DWord);
                else
                    key.DeleteValue(name, false);
            }
            lock (_lock)
            {
                _state.DoLimitApplied = false;
                _state.DoSaved = null;
            }
            Save(true);
        }

        // служба читает настройки при запуске: останавливаем её, Windows запустит её снова сама
        // уже с новыми настройками; текущие закачки при этом обрываются
        RunTool("sc.exe", "stop " + DoService);
    }

    static void RunTool(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false })!;
            p.WaitForExit(15000);
        }
        catch { }
    }
    /// <summary>Имя Wi-Fi сети, которую NetPause сейчас держит лимитной, или пояснение, почему не вышло.</summary>
    public string MeteredStatus { get; private set; } = "";

    public void SetStrict(bool on)
    {
        lock (_lock)
        {
            if (_state.Strict == on) return;
            _state.Strict = on;
            _dirty = true;
        }
        RequestSync();
        Save();
    }

    public void SetMetered(bool on)
    {
        lock (_lock)
        {
            if (_state.Metered == on) return;
            _state.Metered = on;
            _dirty = true;
        }
        RequestSync();
        Save();
    }

    // ---------- лимитное подключение для текущей сети Wi-Fi ----------

    void ApplyMetered(bool on)
    {
        string? current;
        lock (_lock) current = _state.MeteredProfile;

        if (!on)
        {
            if (current == null) { MeteredStatus = ""; return; }
            Netsh($"wlan set profileparameter name=\"{current}\" cost=Unrestricted", out _);
            lock (_lock) { _state.MeteredProfile = null; _dirty = true; }
            MeteredStatus = "";
            Save(true);
            return;
        }

        var ssid = CurrentSsid();
        if (ssid == null) { MeteredStatus = "нет подключения по Wi-Fi"; return; }
        if (ssid.Contains('"')) { MeteredStatus = "имя сети содержит кавычки"; return; }
        if (ssid == current) { MeteredStatus = "сеть «" + ssid + "» помечена лимитной"; return; }

        // сеть сменилась: с прежней пометку снимаем
        if (current != null) Netsh($"wlan set profileparameter name=\"{current}\" cost=Unrestricted", out _);
        if (Netsh($"wlan set profileparameter name=\"{ssid}\" cost=Fixed", out _) == 0)
        {
            lock (_lock) { _state.MeteredProfile = ssid; _dirty = true; }
            MeteredStatus = "сеть «" + ssid + "» помечена лимитной";
        }
        else
        {
            lock (_lock) { _state.MeteredProfile = null; _dirty = true; }
            MeteredStatus = "не удалось пометить сеть «" + ssid + "»";
        }
        Save(true);
    }

    static string? CurrentSsid()
    {
        if (Netsh("wlan show interfaces", out var text) != 0) return null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            // подпись «SSID» не переводится; строку «BSSID» пропускаем
            if (!line.StartsWith("SSID", StringComparison.OrdinalIgnoreCase)) continue;
            int i = line.IndexOf(':');
            if (i < 0) continue;
            var v = line.Substring(i + 1).Trim();
            if (v.Length > 0) return v;
        }
        return null;
    }

    static int Netsh(string args, out string output)
    {
        output = "";
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var enc = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            using var p = Process.Start(new ProcessStartInfo("netsh.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                StandardOutputEncoding = enc,
            })!;
            output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch { return -1; }
    }

    static HashSet<string> ReadOwnRules(dynamic rules)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (dynamic r in rules)
        {
            string? g = r.Grouping;
            string? n = r.Name;
            if (g == Group && n != null && n.StartsWith(RulePrefix))
                set.Add(n.Substring(RulePrefix.Length));
        }
        return set;
    }

    /// <summary>
    /// Аварийная очистка без запущенной программы (ключ --cleanup): убирает все правила NetPause,
    /// выключает строгий режим и снимает пометку «лимитная» с сети.
    /// </summary>
    public static void Cleanup()
    {
        var e = new Engine();
        e._state = TryRead(e._stateFile) ?? TryRead(e._stateFile + ".bak") ?? new State();
        e._state.Apps ??= new List<AppEntry>();
        e.SyncNow(true);
    }

    /// <summary>Включён ли файрвол Windows в текущей сети (без него блокировка не работает).</summary>
    public bool IsWindowsFirewallOn()
    {
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            int current = policy.CurrentProfileTypes;
            foreach (int p in new[] { 1, 2, 4 })
                if ((current & p) != 0 && !(bool)policy.FirewallEnabled[p]) return false;
            return true;
        }
        catch { return true; }
    }

    /// <summary>
    /// Windows завершает сеанс: снимаем блокировки, но остаёмся рабочими.
    /// Если выключение отменят, через 20 секунд правила вернутся.
    /// </summary>
    public void PrepareForSessionEnd()
    {
        Save(true);
        ClearRules(4000);
        lock (_lock)
        {
            if (_disposed) return;
            _resume?.Dispose();
            _resume = new System.Threading.Timer(_ => RequestSync(), null, 20000, System.Threading.Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true; // с этого момента новые правила не создаются
        }
        _timer?.Dispose();
        _netTimer?.Dispose();
        _resume?.Dispose();
        try { _session?.Dispose(); } catch { }
        try { _dnsSession?.Dispose(); } catch { }
        Save(true);
        // при выходе снимаем все свои блокировки, чтобы ничего не осталось заблокированным
        ClearRules(30000);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ENUM_SERVICE_STATUS_PROCESS
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        public int serviceType, currentState, controlsAccepted, win32ExitCode, serviceExitCode, checkPoint, waitHint;
        public int processId;
        public int serviceFlags;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool EnumServicesStatusEx(IntPtr scm, int infoLevel, uint type, uint state, IntPtr buf,
        int size, out int needed, out int returned, ref int resume, string? group);

    [DllImport("advapi32.dll")]
    static extern bool CloseServiceHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder sb, ref int size);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);
}
