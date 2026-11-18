using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;

namespace NetPause;

/// <summary>
/// Иконки сайтов из базы, которую ведёт сам Chrome (файл Favicons в каждом профиле).
/// Ничего не скачивается: берутся только иконки сайтов, где вы уже были.
/// </summary>
public static class Favicons
{
    static Dictionary<string, ImageSource> _icons = new(StringComparer.OrdinalIgnoreCase);
    static DateTime _loadedAt = DateTime.MinValue;
    static int _loading;

    // служебные домены без своих страниц → домен, чья иконка им подходит
    static readonly Dictionary<string, string> Alias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["googlevideo.com"] = "youtube.com", ["ytimg.com"] = "youtube.com", ["youtu.be"] = "youtube.com",
        ["ggpht.com"] = "google.com", ["gstatic.com"] = "google.com", ["googleapis.com"] = "google.com",
        ["googleusercontent.com"] = "google.com", ["1e100.net"] = "google.com", ["gvt1.com"] = "google.com",
        ["gvt2.com"] = "google.com", ["googletagmanager.com"] = "google.com", ["google-analytics.com"] = "google.com",
        ["doubleclick.net"] = "google.com", ["googlesyndication.com"] = "google.com", ["googleadservices.com"] = "google.com",
        ["fbcdn.net"] = "facebook.com", ["cdninstagram.com"] = "instagram.com", ["whatsapp.net"] = "whatsapp.com",
        ["ttvnw.net"] = "twitch.tv", ["steamcontent.com"] = "steampowered.com", ["steamstatic.com"] = "steampowered.com",
        ["steamserver.net"] = "steampowered.com", ["userapi.com"] = "vk.com", ["vkuser.net"] = "vk.com",
        ["yastatic.net"] = "yandex.ru", ["yandex.net"] = "yandex.ru", ["nflxvideo.net"] = "netflix.com",
        ["scdn.co"] = "spotify.com", ["twimg.com"] = "x.com", ["discordapp.com"] = "discord.com",
        ["discordapp.net"] = "discord.com", ["discord.gg"] = "discord.com", ["tiktokcdn.com"] = "tiktok.com",
        ["githubusercontent.com"] = "github.com", ["redd.it"] = "reddit.com", ["anthropic.com"] = "claude.ai",
        ["windowsupdate.com"] = "microsoft.com", ["live.com"] = "microsoft.com", ["msftconnecttest.com"] = "microsoft.com",
    };

    // владелец адресов (из базы ip2asn) → домен для иконки
    static readonly Dictionary<string, string> OwnerSite = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Google"] = "google.com", ["Google Cloud"] = "google.com", ["YouTube"] = "youtube.com",
        ["Cloudflare"] = "cloudflare.com", ["Microsoft"] = "microsoft.com", ["Amazon (AWS)"] = "amazon.com",
        ["Meta (Facebook, Instagram)"] = "facebook.com", ["Apple"] = "apple.com", ["GitHub"] = "github.com",
        ["ВКонтакте"] = "vk.com", ["Яндекс"] = "yandex.ru", ["TikTok"] = "tiktok.com", ["Telegram"] = "telegram.org",
        ["Discord"] = "discord.com", ["Valve (Steam)"] = "steampowered.com", ["Twitch"] = "twitch.tv",
        ["Anthropic (Claude)"] = "claude.ai", ["Netflix"] = "netflix.com", ["Spotify"] = "spotify.com",
        ["X (Twitter)"] = "x.com",
    };

    /// <summary>Иконка для основного домена сайта или null.</summary>
    public static ImageSource? ForDomain(string? domain)
    {
        EnsureFresh();
        if (string.IsNullOrEmpty(domain)) return null;
        if (_icons.TryGetValue(domain, out var img)) return img;
        return Alias.TryGetValue(domain, out var a) && _icons.TryGetValue(a, out img) ? img : null;
    }

    /// <summary>Иконка для владельца адресов («Google», «Cloudflare») или null.</summary>
    public static ImageSource? ForOwner(string owner)
    {
        EnsureFresh();
        return OwnerSite.TryGetValue(owner, out var d) && _icons.TryGetValue(d, out var img) ? img : null;
    }

    /// <summary>Раз в 10 минут перечитываем в фоне: появляются иконки новых сайтов.</summary>
    static void EnsureFresh()
    {
        if ((DateTime.UtcNow - _loadedAt).TotalMinutes < 10) return;
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;
        _loadedAt = DateTime.UtcNow;
        new Thread(() =>
        {
            try { _icons = Load(); }
            catch { }
            finally { Interlocked.Exchange(ref _loading, 0); }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "NetPause favicons" }.Start();
    }

    static Dictionary<string, ImageSource> Load()
    {
        // домен → (размер, картинка); берём размер, ближайший к 32 точкам
        var best = new Dictionary<string, (int Score, byte[] Png)>(StringComparer.OrdinalIgnoreCase);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google", "Chrome", "User Data");
        if (!Directory.Exists(root)) return new(StringComparer.OrdinalIgnoreCase);

        var tmp = Path.Combine(Path.GetTempPath(), "netpause-favicons.db");
        foreach (var dir in Directory.GetDirectories(root))
        {
            var src = Path.Combine(dir, "Favicons");
            if (!File.Exists(src)) continue;
            try
            {
                // Chrome держит файл открытым, поэтому читаем его копию
                using (var from = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var to = File.Create(tmp))
                    from.CopyTo(to);

                using var db = new SqliteConnection("Data Source=" + tmp + ";Mode=ReadOnly;Pooling=False");
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText =
                    "SELECT m.page_url, b.width, b.image_data FROM icon_mapping m " +
                    "JOIN favicon_bitmaps b ON b.icon_id = m.icon_id WHERE b.image_data IS NOT NULL";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!Uri.TryCreate(r.GetString(0), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) continue;
                    var domain = DetailGrouper.MainDomain(uri.Host.ToLowerInvariant());
                    int width = r.IsDBNull(1) ? 16 : r.GetInt32(1);
                    int score = Math.Abs(width - 32);
                    if (best.TryGetValue(domain, out var cur) && cur.Score <= score) continue;
                    best[domain] = (score, (byte[])r["image_data"]);
                }
            }
            catch { }
        }
        try { File.Delete(tmp); } catch { }

        var result = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in best)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(kv.Value.Png);
                bmp.EndInit();
                bmp.Freeze();
                result[kv.Key] = bmp;
            }
            catch { }
        }
        return result;
    }
}
