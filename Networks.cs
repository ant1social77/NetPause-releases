using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using WinNet = Windows.Networking.Connectivity;

namespace NetPause;

/// <summary>Что делать с экономией при подключении к сети.</summary>
public enum NetMode
{
    /// <summary>Не трогать: экономию не включать и не выключать.</summary>
    None,
    /// <summary>Включать экономию.</summary>
    On,
    /// <summary>Выключать экономию.</summary>
    Off,
}

public sealed class KnownNet
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Wi-Fi, Кабель, USB-модем, Bluetooth, Мобильная сеть.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Правило, выбранное человеком; null — решает автоматика.</summary>
    public NetMode? Mode { get; set; }
    /// <summary>Похожа на раздачу с телефона.</summary>
    public bool Phone { get; set; }
    public string? PhoneReason { get; set; }
    /// <summary>Когда были подключены последний раз; null — сеть только сохранена в Windows.</summary>
    public DateTime? LastSeen { get; set; }
    /// <summary>Сайт-трекер с остатком трафика для этой сети (null — у сети трекера нет).</summary>
    public string? Tracker { get; set; }
    /// <summary>Трекер для сети уже выбран человеком или проверен автоматически — больше не угадываем.</summary>
    public bool TrackerChecked { get; set; }
    /// <summary>Трекер привязан автоматически: сайт трекера отвечает изнутри этой сети.</summary>
    public bool TrackerAuto { get; set; }
}

public sealed class CurrentNet
{
    public string Id = "";
    public string Name = "";
    public string Kind = "";
    public bool Phone;
    public string? Reason;
}

/// <summary>Распознавание сетей и автоматическое включение экономии.</summary>
public sealed partial class Engine
{
    /// <summary>NetPause сам переключил экономию по правилу сети; текст — для уведомления.</summary>
    public event Action<string>? AutoSwitched;
    public CurrentNet? CurrentNetwork { get; private set; }

    /// <summary>Сеть, через которую идёт трафик сейчас (null — подключения нет).</summary>
    volatile string? _curNet;
    public string? CurrentNetId => _curNet;

    /// <summary>Сменилась сеть или трекер текущей сети (вызывается не в потоке интерфейса).</summary>
    public event Action? NetworkChanged;

    /// <summary>Трекер остатка для сети, к которой подключены сейчас.</summary>
    public string? CurrentTracker()
    {
        var id = _curNet;
        if (id == null) return null;
        lock (_lock) return _state.Networks.FirstOrDefault(n => n.Id == id)?.Tracker;
    }

    /// <summary>Назначить сети трекер (null — у сети трекера нет). auto — нашёл сам NetPause, а не человек.</summary>
    public void SetNetworkTracker(string netId, string? url, bool auto = false)
    {
        lock (_lock)
        {
            var k = _state.Networks.FirstOrDefault(n => n.Id == netId);
            if (k == null) return;
            k.TrackerChecked = true; // выбор человека автоматика больше не трогает
            if (k.Tracker == url) { _dirty = true; return; }
            k.Tracker = url;
            k.TrackerAuto = auto && url != null;
            _dirty = true;
        }
        NetworkChanged?.Invoke();
    }

    /// <summary>Нужно ли ещё проверить, не сеть ли это с трекером (у сети нет трекера и её не проверяли).</summary>
    public bool NeedsTrackerCheck(string netId)
    {
        lock (_lock)
        {
            var k = _state.Networks.FirstOrDefault(n => n.Id == netId);
            return k != null && k.Tracker == null && !k.TrackerChecked;
        }
    }

    /// <summary>Проверили сеть: трекера в ней нет.</summary>
    public void MarkTrackerChecked(string netId)
    {
        lock (_lock)
        {
            var k = _state.Networks.FirstOrDefault(n => n.Id == netId);
            if (k == null || k.TrackerChecked) return;
            k.TrackerChecked = true;
            _dirty = true;
        }
    }

    /// <summary>Трекер текущей сети найден автоматически.</summary>
    public bool CurrentTrackerAuto()
    {
        var id = _curNet;
        if (id == null) return false;
        lock (_lock) return _state.Networks.FirstOrDefault(n => n.Id == id)?.TrackerAuto == true;
    }

    /// <summary>Сколько сегодня ушло через сеть.</summary>
    public long NetToday(string? id)
    {
        if (id == null) return 0;
        lock (_lock) return _state.NetBytesToday.TryGetValue(id, out var v) ? v : 0;
    }

    System.Threading.Timer? _netTimer;
    int _netBusy;
    bool _netRuleDirty;

    // Имена, которые телефоны дают своей раздаче по умолчанию. Это слабый признак,
    // поэтому в интерфейсе всегда видно, почему сеть сочтена телефоном.
    static readonly Regex PhoneName = new(
        @"iphone|ipad|android|galaxy|redmi|poco|xiaomi|\bmi\s?\d|huawei|honor|pixel|oneplus|realme|oppo|vivo|" +
        @"nokia|motorola|\bmoto\b|nothing|tecno|infinix|\bzte\b|xperia|phone|телефон|смартфон",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly Regex TetherAdapter = new(
        @"Remote NDIS|RNDIS|Apple Mobile Device|Android|iPhone",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Подключение пропадало (Wi-Fi отключался), даже если на ту же сеть вернулись так быстро,
    /// что проверка сети этого не застала. После такого переподключения сайт судна снова
    /// требует включить интернет, поэтому сообщаем о смене сети заново.
    /// </summary>
    volatile bool _sawDisconnect;

    void StartNetworkWatch()
    {
        new Thread(ImportSavedProfiles) { IsBackground = true, Name = "NetPause profiles" }.Start();
        try
        {
            WinNet.NetworkInformation.NetworkStatusChanged += _ =>
            {
                try { if (CurrentProfile() == null) _sawDisconnect = true; } catch { }
                KickNetwork(1500);
            };
        }
        catch { }
        // раз в 10 секунд проверяем и так — на случай, если Windows не сообщит о смене сети
        _netTimer = new System.Threading.Timer(_ => CheckNetwork(), null, 2000, 10000);
    }

    void KickNetwork(int delayMs)
    {
        try { _netTimer?.Change(delayMs, 10000); } catch { }
    }

    void CheckNetwork()
    {
        if (Interlocked.Exchange(ref _netBusy, 1) == 1) return;
        try
        {
            if (_disposed) return;
            var cur = DetectNetwork();
            CurrentNetwork = cur;
            if (cur == null) _sawDisconnect = true;
            // Сменилась сеть или переподключились к той же — Транслятор пересчитает остаток
            // по трекеру этой сети и при необходимости снова включит интернет на сайте.
            bool reconnected = cur != null && _sawDisconnect;
            if (cur != null) _sawDisconnect = false;
            if (cur?.Id != _curNet || reconnected)
            {
                _curNet = cur?.Id;
                try { NetworkChanged?.Invoke(); } catch { }
            }
            // Нет подключения: ничего не меняем, чтобы экономия не мигала при переподключении.
            if (cur == null) return;

            bool? want = null;
            string? msg = null;
            lock (_lock)
            {
                var k = _state.Networks.FirstOrDefault(n => n.Id == cur.Id);
                if (k == null)
                {
                    k = new KnownNet { Id = cur.Id };
                    _state.Networks.Add(k);
                }
                k.Name = cur.Name;
                k.Kind = cur.Kind;
                k.LastSeen = DateTime.Now;
                if (cur.Phone)
                {
                    // запоминаем: признак «лимитная» может пропасть, когда NetPause сам пометит сеть
                    k.Phone = true;
                    k.PhoneReason = cur.Reason;
                }
                _dirty = true;

                bool changed = _state.LastNetId != cur.Id;
                _state.LastNetId = cur.Id;
                if (changed || _netRuleDirty)
                {
                    _netRuleDirty = false;
                    var mode = EffectiveMode(k);
                    if (mode == NetMode.On && !_state.EconomyOn)
                    {
                        want = true;
                        msg = "Сеть «" + k.Name + "»: экономия включена" +
                              (k.Mode == null ? ". Похоже на раздачу с телефона." : ".");
                    }
                    else if (mode == NetMode.Off && _state.EconomyOn)
                    {
                        want = false;
                        msg = "Сеть «" + k.Name + "»: экономия выключена.";
                    }
                    else if (mode == NetMode.None && changed && _state.EconomyOn && _state.EconomyAuto)
                    {
                        // экономию включил сам NetPause в прошлой сети — уходя из неё, выключаем
                        want = false;
                        msg = "Сеть «" + k.Name + "»: экономия выключена, раздача с телефона закончилась.";
                    }
                }
            }
            if (want != null)
            {
                SetEconomy(want.Value, true);
                AutoSwitched?.Invoke(msg!);
            }
        }
        catch { }
        finally { Interlocked.Exchange(ref _netBusy, 0); }
    }

    /// <summary>Правило, которое действует для сети: выбранное человеком или автоматическое. Вызывать под _lock.</summary>
    NetMode EffectiveMode(KnownNet k) =>
        k.Mode ?? (_state.AutoHotspot && k.Phone ? NetMode.On : NetMode.None);

    public NetMode EffectiveModeOf(string id)
    {
        lock (_lock)
        {
            var k = _state.Networks.FirstOrDefault(n => n.Id == id);
            return k == null ? NetMode.None : EffectiveMode(k);
        }
    }

    /// <summary>
    /// Подключение, через которое идёт интернет. Если Windows его не называет (например, на судне
    /// интернет ещё не включён на сайте и Windows пишет «Нет подключения к интернету»),
    /// берём сеть Wi-Fi, к которой компьютер всё же подключён.
    /// </summary>
    static WinNet.ConnectionProfile? CurrentProfile()
    {
        var p = WinNet.NetworkInformation.GetInternetConnectionProfile();
        if (p != null) return p;
        foreach (var c in WinNet.NetworkInformation.GetConnectionProfiles())
        {
            try
            {
                if (c.IsWlanConnectionProfile &&
                    c.GetNetworkConnectivityLevel() != WinNet.NetworkConnectivityLevel.None)
                    return c;
            }
            catch { }
        }
        return null;
    }

    CurrentNet? DetectNetwork()
    {
        var p = CurrentProfile();
        if (p == null) return null;

        string? ssid = null;
        if (p.IsWlanConnectionProfile)
        {
            try { ssid = p.WlanConnectionProfileDetails?.GetConnectedSsid(); } catch { }
            if (string.IsNullOrEmpty(ssid)) ssid = p.ProfileName; // имя профиля Wi-Fi обычно совпадает с именем сети
        }

        NetworkInterface? ni = null;
        try
        {
            var id = "{" + p.NetworkAdapter.NetworkAdapterId.ToString() + "}";
            ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        var desc = ni?.Description ?? "";
        string? gateway = null;
        try
        {
            gateway = ni?.GetIPProperties().GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)?.ToString();
        }
        catch { }

        var cur = new CurrentNet();
        var reasons = new List<string>();

        if (p.IsWwanConnectionProfile) { cur.Kind = "Мобильная сеть"; reasons.Add("мобильный модем"); }
        else if (TetherAdapter.IsMatch(desc)) { cur.Kind = "USB-модем"; reasons.Add("телефон подключён по USB"); }
        else if (desc.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) { cur.Kind = "Bluetooth"; reasons.Add("интернет через Bluetooth"); }
        else cur.Kind = ssid != null ? "Wi-Fi" : "Кабель";

        // iPhone всегда раздаёт интернет с этого адреса
        if (gateway == "172.20.10.1") reasons.Add("адрес раздачи iPhone");

        try
        {
            // Android и iPhone сообщают Windows, что их раздача лимитная, и Windows помечает сеть сама.
            // Пометку, поставленную самим NetPause, за признак не считаем.
            var cost = p.GetConnectionCost();
            bool ours;
            lock (_lock) ours = ssid != null && ssid == _state.MeteredProfile;
            if (!ours && cost.NetworkCostType != WinNet.NetworkCostType.Unrestricted &&
                cost.NetworkCostType != WinNet.NetworkCostType.Unknown)
                reasons.Add("Windows считает сеть лимитной");
            if (cost.Roaming) reasons.Add("роуминг");
        }
        catch { }

        if (ssid != null && PhoneName.IsMatch(ssid)) reasons.Add("имя похоже на телефон");

        cur.Name = ssid ?? p.ProfileName ?? "Сеть";
        cur.Id = ssid != null ? "wifi:" + ssid : "net:" + cur.Kind + ":" + (p.ProfileName ?? cur.Name);
        cur.Phone = reasons.Count > 0;
        cur.Reason = reasons.Count > 0 ? string.Join(", ", reasons) : null;
        return cur;
    }

    /// <summary>Добавляет в список сети Wi-Fi, сохранённые в Windows, чтобы правило можно было задать заранее.</summary>
    void ImportSavedProfiles()
    {
        try
        {
            if (Netsh("wlan show profiles", out var text) != 0) return;
            var names = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                // строки вида «    All User Profile     : Имя сети» (подпись зависит от языка Windows)
                var m = Regex.Match(raw.TrimEnd('\r'), @"^\s{2,}[^:<]+?\s:\s(.+?)\s*$");
                if (m.Success) names.Add(m.Groups[1].Value);
            }
            lock (_lock)
            {
                foreach (var n in names.Distinct())
                {
                    var id = "wifi:" + n;
                    if (_state.Networks.Any(k => k.Id == id)) continue;
                    var phone = PhoneName.IsMatch(n);
                    _state.Networks.Add(new KnownNet
                    {
                        Id = id,
                        Name = n,
                        Kind = "Wi-Fi",
                        Phone = phone,
                        PhoneReason = phone ? "имя похоже на телефон" : null,
                    });
                    _dirty = true;
                }
            }
        }
        catch { }
    }

    // ---------- для интерфейса ----------

    public bool AutoHotspot { get { lock (_lock) return _state.AutoHotspot; } }
    public bool EconomyAuto { get { lock (_lock) return _state.EconomyAuto; } }

    public List<KnownNet> GetNetworks()
    {
        lock (_lock)
            return _state.Networks.Select(n => new KnownNet
            {
                Id = n.Id, Name = n.Name, Kind = n.Kind, Mode = n.Mode,
                Phone = n.Phone, PhoneReason = n.PhoneReason, LastSeen = n.LastSeen, Tracker = n.Tracker,
                TrackerChecked = n.TrackerChecked, TrackerAuto = n.TrackerAuto,
            }).ToList();
    }

    public void SetNetworkMode(string id, NetMode? mode)
    {
        lock (_lock)
        {
            var k = _state.Networks.FirstOrDefault(n => n.Id == id);
            if (k == null || k.Mode == mode) return;
            k.Mode = mode;
            if (id == _state.LastNetId) _netRuleDirty = true; // правило текущей сети применяем сразу
            _dirty = true;
        }
        KickNetwork(0);
    }

    public void SetAutoHotspot(bool on)
    {
        lock (_lock)
        {
            if (_state.AutoHotspot == on) return;
            _state.AutoHotspot = on;
            _netRuleDirty = true;
            _dirty = true;
        }
        KickNetwork(0);
    }

    public void ForgetNetwork(string id)
    {
        lock (_lock)
        {
            if (id == _state.LastNetId) return; // текущую сеть не забываем, она тут же появится снова
            _state.Networks.RemoveAll(n => n.Id == id);
            _dirty = true;
        }
    }
}
