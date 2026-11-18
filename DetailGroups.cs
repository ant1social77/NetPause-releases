using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Windows;
using System.Windows.Media;

namespace NetPause;

/// <summary>Одна строка внутри группы на экране подробностей: сайт или адрес.</summary>
public sealed class ChildVm : INotifyPropertyChanged
{
    readonly Engine _engine;
    readonly string _appKey;
    bool _blocked;

    public string Host { get; init; } = "";
    public string Label { get; init; } = "";
    public string Detail { get; init; } = "";
    long _bytes;
    public long Bytes
    {
        get => _bytes;
        init => _bytes = value;
    }
    public Visibility SwitchVisibility { get; init; } = Visibility.Visible;
    public bool Blocked => _blocked;

    public ChildVm(Engine engine, string appKey, bool blocked)
    {
        _engine = engine;
        _appKey = appKey;
        _blocked = blocked;
    }

    /// <summary>Обновить число на месте, не пересоздавая строку.</summary>
    public void SetBytes(long bytes)
    {
        if (_bytes == bytes) return;
        _bytes = bytes;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Sub)));
    }

    public bool Allowed
    {
        get => !_blocked;
        set
        {
            _blocked = !value;
            _engine.SetHostBlocked(_appKey, Host, _blocked);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Sub)));
        }
    }

    public string Sub
    {
        get
        {
            var parts = new List<string>();
            if (Detail.Length > 0) parts.Add(Detail);
            parts.Add(Bytes > 0 ? Fmt.Bytes(Bytes) : "нет трафика");
            if (_blocked) parts.Add(_engine.EconomyOn ? "заблокировано" : "заблокируется при экономии");
            return string.Join(" · ", parts);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Группа на экране подробностей: сайт со всеми поддоменами, владелец адресов, домашняя сеть.</summary>
public sealed class GroupVm : INotifyPropertyChanged
{
    readonly Engine _engine;
    readonly string _appKey;
    bool _blocked;
    bool _expanded;

    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Letter { get; init; } = "";
    public Brush AvatarBg { get; init; } = Brushes.Gray;
    /// <summary>Иконка сайта из Chrome; если её нет, показывается буква в кружке.</summary>
    public ImageSource? Icon { get; init; }
    public Visibility IconVisibility => Icon != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LetterVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;
    long _bytes;
    public long Bytes
    {
        get => _bytes;
        init => _bytes = value;
    }
    public bool Blocked => _blocked;

    /// <summary>Перенести новые числа из свежей группы той же формы, не пересоздавая строки.</summary>
    public void UpdateFrom(GroupVm fresh)
    {
        for (int i = 0; i < Children.Count && i < fresh.Children.Count; i++)
            Children[i].SetBytes(fresh.Children[i].Bytes);
        if (_bytes == fresh.Bytes) return;
        _bytes = fresh.Bytes;
        Raise(nameof(Sub));
    }
    /// <summary>Домен, запрет которого закрывает все его поддомены; null — группа из отдельных адресов.</summary>
    public string? Domain { get; init; }
    public List<ChildVm> Children { get; init; } = new();
    public bool BottomGroup { get; init; }
    public Visibility SwitchVisibility { get; init; } = Visibility.Visible;

    public GroupVm(Engine engine, string appKey, bool blocked, bool expanded)
    {
        _engine = engine;
        _appKey = appKey;
        _blocked = blocked;
        _expanded = expanded;
    }

    public bool Expandable => Children.Count > 1;
    public string Chevron => !Expandable ? "" : _expanded ? "" : "";
    /// <summary>Стрелка «вправо»; при раскрытии она плавно поворачивается вниз.</summary>
    public string ChevronGlyph => Expandable ? "" : "";
    public double ChevronAngle => _expanded ? 90 : 0;
    public Visibility ChildrenVisibility => Expandable && _expanded ? Visibility.Visible : Visibility.Collapsed;
    public string Tip => Expandable ? (_expanded ? "Свернуть" : "Показать всё, что внутри") : (Children.FirstOrDefault()?.Host ?? Title);

    public bool Expanded
    {
        get => _expanded;
        set
        {
            _expanded = value;
            Raise(nameof(Chevron)); Raise(nameof(ChildrenVisibility)); Raise(nameof(Tip));
        }
    }

    public bool Allowed
    {
        get => !_blocked;
        set
        {
            _blocked = !value;
            _engine.SetGroupBlocked(_appKey, Domain, Children.Select(c => c.Host).ToList(), _blocked);
            Raise(nameof(Sub));
        }
    }

    public string Sub
    {
        get
        {
            var parts = new List<string>();
            if (Detail.Length > 0) parts.Add(Detail);
            parts.Add(Bytes > 0 ? Fmt.Bytes(Bytes) : "нет трафика");
            if (Children.Count > 1) parts.Add(Plural(Children.Count));
            if (_blocked) parts.Add(_engine.EconomyOn ? "заблокировано" : "заблокируется при экономии");
            return string.Join(" · ", parts);
        }
    }

    static string Plural(int n)
    {
        int m10 = n % 10, m100 = n % 100;
        var w = m10 == 1 && m100 != 11 ? "адрес"
            : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? "адреса" : "адресов";
        return n + " " + w;
    }

    void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Собирает «сырые» адреса и сайты программы в понятные группы.</summary>
public static class DetailGrouper
{
    const string Unknown = "Неизвестные адреса";
    const string HomeNet = "Домашняя сеть";

    // Понятные названия для известных доменов (основной домен → название).
    static readonly Dictionary<string, string> Friendly = new(StringComparer.OrdinalIgnoreCase)
    {
        ["googlevideo.com"] = "YouTube (видео)", ["ytimg.com"] = "YouTube (картинки)", ["youtube.com"] = "YouTube",
        ["youtu.be"] = "YouTube", ["ggpht.com"] = "Google (картинки)", ["gstatic.com"] = "Google (служебное)",
        ["googleapis.com"] = "Google (сервисы)", ["google.com"] = "Google", ["googleusercontent.com"] = "Google (файлы)",
        ["doubleclick.net"] = "Реклама Google", ["googlesyndication.com"] = "Реклама Google",
        ["googleadservices.com"] = "Реклама Google", ["google-analytics.com"] = "Google Analytics",
        ["googletagmanager.com"] = "Google (статистика сайтов)", ["1e100.net"] = "Google",
        ["gvt1.com"] = "Google (обновления)", ["gvt2.com"] = "Google (обновления)", ["dns.google"] = "Google DNS",
        ["windowsupdate.com"] = "Центр обновления Windows", ["microsoft.com"] = "Microsoft",
        ["msftconnecttest.com"] = "Проверка интернета Windows", ["live.com"] = "Microsoft", ["office.com"] = "Microsoft Office",
        ["msn.com"] = "MSN", ["bing.com"] = "Bing", ["azureedge.net"] = "Microsoft Azure", ["windows.net"] = "Microsoft Azure",
        ["akamaiedge.net"] = "Akamai (доставка контента)", ["akamaized.net"] = "Akamai (доставка контента)",
        ["akamai.net"] = "Akamai (доставка контента)", ["akamaihd.net"] = "Akamai (доставка контента)",
        ["cloudfront.net"] = "Amazon CloudFront", ["amazonaws.com"] = "Amazon (AWS)", ["fastly.net"] = "Fastly (доставка контента)",
        ["cloudflare.com"] = "Cloudflare", ["cloudflare-dns.com"] = "Cloudflare DNS",
        ["facebook.com"] = "Facebook", ["fbcdn.net"] = "Facebook", ["instagram.com"] = "Instagram",
        ["cdninstagram.com"] = "Instagram", ["whatsapp.net"] = "WhatsApp", ["whatsapp.com"] = "WhatsApp",
        ["telegram.org"] = "Telegram", ["t.me"] = "Telegram", ["tiktok.com"] = "TikTok", ["tiktokcdn.com"] = "TikTok",
        ["twitch.tv"] = "Twitch", ["ttvnw.net"] = "Twitch", ["discord.com"] = "Discord", ["discordapp.com"] = "Discord",
        ["discordapp.net"] = "Discord", ["discord.gg"] = "Discord",
        ["steampowered.com"] = "Steam", ["steamcontent.com"] = "Steam", ["steamstatic.com"] = "Steam",
        ["steamserver.net"] = "Steam", ["nvidia.com"] = "NVIDIA", ["anthropic.com"] = "Claude", ["claude.ai"] = "Claude",
        ["openai.com"] = "OpenAI", ["yandex.ru"] = "Яндекс", ["yandex.net"] = "Яндекс", ["yastatic.net"] = "Яндекс",
        ["vk.com"] = "ВКонтакте", ["userapi.com"] = "ВКонтакте", ["vkuser.net"] = "ВКонтакте", ["mail.ru"] = "Mail.ru",
        ["netflix.com"] = "Netflix", ["nflxvideo.net"] = "Netflix", ["spotify.com"] = "Spotify", ["scdn.co"] = "Spotify",
        ["apple.com"] = "Apple", ["icloud.com"] = "Apple iCloud", ["github.com"] = "GitHub",
        ["githubusercontent.com"] = "GitHub", ["lencr.org"] = "Проверка сертификатов (Let's Encrypt)",
        ["digicert.com"] = "Проверка сертификатов (DigiCert)", ["wikipedia.org"] = "Википедия",
        ["reddit.com"] = "Reddit", ["redd.it"] = "Reddit", ["x.com"] = "X (Twitter)", ["twimg.com"] = "X (Twitter)",
    };

    // вторые уровни, после которых основной домен — третий уровень (bbc.co.uk, site.com.ua)
    static readonly HashSet<string> SecondLevel = new(StringComparer.OrdinalIgnoreCase)
        { "co", "com", "net", "org", "gov", "edu", "ac", "or", "ne", "go" };

    static readonly Color[] Palette =
    {
        Color.FromRgb(0x4F, 0x7C, 0xC9), Color.FromRgb(0x3E, 0x9C, 0x84), Color.FromRgb(0xC2, 0x6B, 0x4A),
        Color.FromRgb(0x8E, 0x67, 0xC2), Color.FromRgb(0xB8, 0x8A, 0x2E), Color.FromRgb(0xC2, 0x55, 0x7C),
        Color.FromRgb(0x5A, 0x8F, 0x3C),
    };

    public static string MainDomain(string host)
    {
        var l = host.Split('.');
        if (l.Length <= 2) return host;
        bool third = l[^1].Length == 2 && SecondLevel.Contains(l[^2]);
        return string.Join(".", l.Skip(l.Length - (third ? 3 : 2)));
    }

    static Brush Avatar(string key)
    {
        int h = 0;
        foreach (var c in key) h = h * 31 + c;
        var b = new SolidColorBrush(Palette[Math.Abs(h % Palette.Length)]);
        b.Freeze();
        return b;
    }

    sealed class Bucket
    {
        public string Title = "";
        public string Detail = "";
        public string? Domain;
        public string? Owner;
        public bool Bottom;
        public readonly List<(HostInfo Info, string Label, string Detail, bool NoSwitch)> Items = new();
    }

    public static List<GroupVm> Build(Engine engine, AppEntry app, List<HostInfo> infos, ISet<string> expanded, bool economyOn)
    {
        var buckets = new Dictionary<string, Bucket>(StringComparer.OrdinalIgnoreCase);
        Bucket B(string key, Func<Bucket> make)
        {
            if (!buckets.TryGetValue(key, out var b)) buckets[key] = b = make();
            return b;
        }

        foreach (var h in infos)
        {
            if (h.Host == "прочие адреса")
            {
                B("u:", () => new Bucket { Title = Unknown, Detail = "Владельца определить не удалось", Bottom = true })
                    .Items.Add((h, "Прочие адреса", "их слишком много, чтобы показывать по отдельности", true));
                continue;
            }

            if (IPAddress.TryParse(h.Host, out var ip))
            {
                var local = IpOwners.Local(ip);
                if (local != null)
                {
                    if (local.StartsWith("Сеть провайдера"))
                        B("p:", () => new Bucket { Title = "Сеть провайдера, прокси или VPN", Detail = "Частные адреса за вашим роутером" })
                            .Items.Add((h, h.Host, "", false));
                    else
                        B("l:", () => new Bucket { Title = HomeNet, Detail = "Роутер и устройства рядом", Bottom = true })
                            .Items.Add((h, local, h.Host, false));
                    continue;
                }
                var owner = IpOwners.Owner(ip);
                if (owner != null)
                {
                    B("o:" + owner, () => new Bucket { Title = owner, Detail = "Адреса без имени сайта", Owner = owner })
                        .Items.Add((h, h.Host, "", false));
                    continue;
                }
                B("u:", () => new Bucket { Title = Unknown, Detail = "Владельца определить не удалось", Bottom = true })
                    .Items.Add((h, h.Host, "", false));
                continue;
            }

            // имя сайта: собираем все поддомены под основным доменом
            var main = MainDomain(h.Host);
            var title = Friendly.TryGetValue(main, out var f) ? f : main;
            B("d:" + main, () => new Bucket { Title = title, Detail = title == main ? "" : main, Domain = main })
                .Items.Add((h, h.Host, h.Addresses > 1 ? "адресов: " + h.Addresses : "", false));
        }

        var result = new List<GroupVm>();
        foreach (var kv in buckets)
        {
            var b = kv.Value;
            var children = b.Items
                .OrderByDescending(i => i.Info.Bytes).ThenBy(i => i.Label)
                .Select(i => new ChildVm(engine, app.Key, i.Info.Blocked)
                {
                    Host = i.Info.Host,
                    Label = i.Label,
                    Detail = i.Detail,
                    Bytes = i.Info.Bytes,
                    SwitchVisibility = app.Locked || i.NoSwitch ? Visibility.Hidden : Visibility.Visible,
                }).ToList();

            // группа заблокирована, если заблокировано всё, что в ней можно блокировать
            var blockable = b.Items.Where(i => !i.NoSwitch).ToList();
            bool blocked = blockable.Count > 0 && blockable.All(i => i.Info.Blocked);
            bool noSwitch = app.Locked || blockable.Count == 0;

            // одиночная строка: показываем её имя прямо в заголовке группы
            string detail = b.Detail;
            if (children.Count == 1 && b.Domain != null && children[0].Host != b.Domain)
                detail = children[0].Host;
            if (children.Count == 1 && b.Domain == null && b.Items[0].Label != b.Title)
                detail = b.Items[0].Label == b.Items[0].Info.Host
                    ? b.Items[0].Info.Host
                    : b.Items[0].Label + " · " + b.Items[0].Info.Host;

            result.Add(new GroupVm(engine, app.Key, blocked, expanded.Contains(kv.Key))
            {
                Key = kv.Key,
                Title = b.Title,
                Detail = detail,
                Letter = b.Title.Length > 0 ? char.ToUpper(b.Title.TrimStart('(')[0]).ToString() : "?",
                AvatarBg = b.Bottom ? new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)) : Avatar(kv.Key),
                Icon = b.Domain != null ? Favicons.ForDomain(b.Domain) : b.Owner != null ? Favicons.ForOwner(b.Owner) : null,
                Bytes = b.Items.Sum(i => i.Info.Bytes),
                Domain = b.Domain,
                Children = children,
                BottomGroup = b.Bottom,
                SwitchVisibility = noSwitch ? Visibility.Hidden : Visibility.Visible,
            });
        }

        // сначала понятные группы по расходу, внизу — домашняя сеть и неизвестные адреса
        return result
            .OrderBy(g => g.BottomGroup)
            .ThenByDescending(g => g.Bytes)
            .ThenBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
