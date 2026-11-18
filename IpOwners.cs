using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;

namespace NetPause;

/// <summary>
/// Кому принадлежит IP-адрес. Работает по базе ip2asn, которая лежит рядом с программой:
/// адреса никуда не отправляются.
/// </summary>
public static class IpOwners
{
    static uint[] _s4 = Array.Empty<uint>(), _e4 = Array.Empty<uint>();
    static int[] _n4 = Array.Empty<int>();
    static UInt128[] _s6 = Array.Empty<UInt128>(), _e6 = Array.Empty<UInt128>();
    static int[] _n6 = Array.Empty<int>();
    static string[] _names = Array.Empty<string>();

    public static bool Loaded { get; private set; }

    // Понятные названия для крупных сетей (номер автономной системы → название).
    static readonly Dictionary<int, string> ByAsn = new()
    {
        [15169] = "Google", [396982] = "Google Cloud", [36040] = "YouTube", [43515] = "YouTube",
        [16509] = "Amazon (AWS)", [14618] = "Amazon (AWS)", [8987] = "Amazon (AWS)",
        [8075] = "Microsoft", [8068] = "Microsoft", [12076] = "Microsoft",
        [13335] = "Cloudflare", [209242] = "Cloudflare",
        [20940] = "Akamai", [16625] = "Akamai", [21342] = "Akamai", [33905] = "Akamai",
        [32934] = "Meta (Facebook, Instagram)", [54113] = "Fastly", [13414] = "X (Twitter)",
        [2906] = "Netflix", [40027] = "Netflix", [714] = "Apple", [6185] = "Apple",
        [36459] = "GitHub", [199524] = "G-Core Labs (доставка контента)",
        [47541] = "ВКонтакте", [47542] = "ВКонтакте", [13238] = "Яндекс", [208722] = "Яндекс",
        [138699] = "TikTok", [396986] = "TikTok", [62041] = "Telegram", [44907] = "Telegram",
        [49544] = "Discord", [32590] = "Valve (Steam)", [46489] = "Twitch", [399358] = "Anthropic (Claude)",
        [11414] = "Spotify", [8560] = "IONOS", [24940] = "Hetzner", [16276] = "OVH",
    };

    static readonly Regex LegalSuffix = new(
        @"[,\s]+(Inc\.?|LLC|L\.L\.C\.|Ltd\.?|Limited|PBC|S\.A\.?|SA|GmbH|AG|B\.V\.?|BV|Corp\.?|Corporation|Co\.?|PLC|LLP|S\.R\.L\.?|SRL|OOO|JSC|PJSC|LTD|INC)\.?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static void LoadAsync(string path) =>
        new Thread(() => Load(path)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "NetPause ip2asn" }.Start();

    static void Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var s4 = new List<uint>(); var e4 = new List<uint>(); var n4 = new List<int>();
            var s6 = new List<UInt128>(); var e6 = new List<UInt128>(); var n6 = new List<int>();
            var names = new List<string>();
            var index = new Dictionary<string, int>();

            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var sr = new StreamReader(gz);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                var p = line.Split('\t');
                if (p.Length < 5 || p[2] == "0") continue; // «Not routed»
                if (!int.TryParse(p[2], out int asn)) continue;
                var owner = Friendly(asn, p[4]);
                if (!index.TryGetValue(owner, out int ni)) { ni = names.Count; names.Add(owner); index[owner] = ni; }

                if (!IPAddress.TryParse(p[0], out var a) || !IPAddress.TryParse(p[1], out var b)) continue;
                if (a.AddressFamily == AddressFamily.InterNetwork)
                {
                    s4.Add(V4(a)); e4.Add(V4(b)); n4.Add(ni);
                }
                else
                {
                    s6.Add(V6(a)); e6.Add(V6(b)); n6.Add(ni);
                }
            }
            _s4 = s4.ToArray(); _e4 = e4.ToArray(); _n4 = n4.ToArray();
            _s6 = s6.ToArray(); _e6 = e6.ToArray(); _n6 = n6.ToArray();
            _names = names.ToArray();
            Loaded = true;
        }
        catch { }
    }

    /// <summary>«GOOGLE - Google LLC» → «Google»; известные сети — по своему названию.</summary>
    static string Friendly(int asn, string raw)
    {
        if (ByAsn.TryGetValue(asn, out var known)) return known;
        var s = raw;
        int dash = s.IndexOf(" - ", StringComparison.Ordinal);
        if (dash >= 0) s = s[(dash + 3)..];
        else
        {
            // «GCORE G-Core Labs S.A.»: первое слово заглавными — служебное имя сети, отбрасываем
            var parts = s.Split(' ', 2);
            if (parts.Length == 2 && parts[0].Length > 1 && parts[0] == parts[0].ToUpperInvariant() && parts[1].Any(char.IsLower))
                s = parts[1];
        }
        for (int i = 0; i < 2; i++) s = LegalSuffix.Replace(s, "");
        s = s.Trim().Trim(',', '.').Trim();
        return s.Length > 0 ? s : raw;
    }

    static uint V4(IPAddress ip) => BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
    static UInt128 V6(IPAddress ip) => BinaryPrimitives.ReadUInt128BigEndian(ip.GetAddressBytes());

    /// <summary>Владелец публичного адреса или null, если неизвестен или база ещё грузится.</summary>
    public static string? Owner(IPAddress ip)
    {
        if (!Loaded) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            int i = Find(_s4, V4(ip));
            return i >= 0 && V4(ip) <= _e4[i] ? _names[_n4[i]] : null;
        }
        var v = V6(ip);
        int j = Find(_s6, v);
        return j >= 0 && v <= _e6[j] ? _names[_n6[j]] : null;
    }

    /// <summary>Индекс последнего диапазона, который начинается не позже v.</summary>
    static int Find<T>(T[] starts, T v) where T : IComparable<T>
    {
        int lo = 0, hi = starts.Length - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (starts[mid].CompareTo(v) <= 0) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    // ---------- домашняя сеть ----------

    static HashSet<string> _gateways = new();
    static DateTime _gatewaysAt = DateTime.MinValue;

    static HashSet<string> Gateways()
    {
        if ((DateTime.UtcNow - _gatewaysAt).TotalSeconds < 15) return _gateways;
        var set = new HashSet<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var g in ni.GetIPProperties().GatewayAddresses)
                    set.Add(g.Address.ToString());
        }
        catch { }
        _gateways = set;
        _gatewaysAt = DateTime.UtcNow;
        return set;
    }

    static List<(uint Net, uint Mask)> _subnets = new();
    static DateTime _subnetsAt = DateTime.MinValue;

    /// <summary>Входит ли адрес в одну из подсетей, к которым компьютер подключён напрямую.</summary>
    static bool InLocalSubnet(IPAddress ip)
    {
        if ((DateTime.UtcNow - _subnetsAt).TotalSeconds > 15)
        {
            var list = new List<(uint, uint)>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var u in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null) continue;
                        uint m = V4(u.IPv4Mask);
                        list.Add((V4(u.Address) & m, m));
                    }
                }
            }
            catch { }
            _subnets = list;
            _subnetsAt = DateTime.UtcNow;
        }
        uint v = V4(ip);
        return _subnets.Any(s => (v & s.Mask) == s.Net);
    }

    /// <summary>
    /// Подпись для адреса домашней или служебной сети; null — адрес публичный.
    /// </summary>
    public static string? Local(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var s = ip.ToString();
        if (Gateways().Contains(s)) return "Ваш роутер";
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            if (s == "224.0.0.251") return "Поиск устройств в сети (mDNS)";
            if (s == "239.255.255.250") return "Поиск устройств в сети (SSDP)";
            if (s == "224.0.0.252") return "Поиск имён в сети (LLMNR)";
            if (b[0] >= 224 && b[0] <= 239) return "Служебная рассылка в сети";
            if (s == "255.255.255.255" || b[3] == 255) return "Рассылка всем устройствам в сети";
            if (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168))
                // частный адрес из чужой подсети — это не устройство рядом, а что-то за роутером
                return InLocalSubnet(ip) ? "Устройство в домашней сети" : "Сеть провайдера, прокси или VPN";
            if (b[0] == 169 && b[1] == 254) return "Устройство в домашней сети";
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return "Сеть провайдера";
            return null;
        }
        if (s == "ff02::fb") return "Поиск устройств в сети (mDNS)";
        if (s == "ff02::1:3") return "Поиск имён в сети (LLMNR)";
        if (b[0] == 0xff) return "Служебная рассылка в сети";
        if (ip.IsIPv6LinkLocal || (b[0] & 0xfe) == 0xfc) return "Устройство в домашней сети";
        return null;
    }
}
