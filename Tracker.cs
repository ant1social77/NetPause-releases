using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace NetPause;

/// <summary>Что удалось узнать на сайте трекера.</summary>
public sealed class TrackerResult
{
    public bool Ok;
    /// <summary>Пропуск устарел или его нет — нужно войти снова.</summary>
    public bool NeedLogin;
    public string? Error;
    /// <summary>Остаток: байты (если IsBytes) или секунды.</summary>
    public double? Remaining;
    public double? Allocated;
    public bool IsBytes = true;
    public string? Provider;
    /// <summary>Денежный баланс на сайте, $.</summary>
    public double? Balance;
    /// <summary>Интернет на сайте включён (status != off).</summary>
    public bool DataOn = true;
    /// <summary>Номер провайдера из ответа сайта.</summary>
    public int? ProviderNo;
    /// <summary>Тариф пользователя на сайте, например «1GB Daily».</summary>
    public string? Plan;
    /// <summary>Адрес главного устройства сессии (на котором включили интернет).</summary>
    public string? StatusIp;
    public DateTime At = DateTime.Now;
}

/// <summary>Включён ли интернет на сайте трекера и на каком устройстве.</summary>
public sealed class TrackerStatus
{
    public bool NeedLogin;
    /// <summary>Сайт не ответил (текст причины).</summary>
    public string? Error;
    /// <summary>Интернет выключен для всех устройств.</summary>
    public bool Off;
    public int? Provider;
    /// <summary>Адрес устройства, на котором интернет включили.</summary>
    public string? Ip;
}

/// <summary>
/// Чтение остатка с сайта Zwana Internet Cafe (ic.zwana.io). Те же запросы, что делает
/// сама страница сайта: статус, активный провайдер, баланс.
/// </summary>
public static class TrackerReader
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Умеет ли NetPause читать этот трекер.</summary>
    public static bool IsSupported(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.EndsWith("zwana.io", StringComparison.OrdinalIgnoreCase);

    public static string Origin(string url) => new Uri(url).GetLeftPart(UriPartial.Authority);

    public static async Task<TrackerResult> ReadAsync(string url, string token, int? fallbackProvider)
    {
        var r = new TrackerResult();
        var api = Origin(url) + "/api/";
        try
        {
            // какой провайдер сейчас включён
            int provider = fallbackProvider ?? 0;
            using (var status = await Get(api + "UserProvider/GetStatus", token))
            {
                if (status == null) { r.NeedLogin = true; return r; }
                var s = status.RootElement;
                var st = Str(s, "status");
                r.DataOn = st == null || !st.Equals("off", StringComparison.OrdinalIgnoreCase);
                r.StatusIp = Str(s, "ip");
                if (Num(s, "provider") is double p)
                {
                    r.ProviderNo = (int)p;
                    if (r.DataOn) provider = (int)p;
                }
            }

            using (var active = await Get(api + "UserProvider/GetActive?providerNo=" + provider, token))
            {
                if (active == null) { r.NeedLogin = true; return r; }
                var a = active.RootElement;
                r.Remaining = Num(a, "Remainder");
                r.Allocated = Num(a, "Allocated");
                if (Prop(a, "User") is JsonElement u && u.ValueKind == JsonValueKind.Object)
                    r.Plan = Str(u, "CronProfileName")?.Trim();
                if (Prop(a, "Provider") is JsonElement prov && prov.ValueKind == JsonValueKind.Object)
                {
                    r.Provider = Str(prov, "ShortName") ?? Str(prov, "Name");
                    if (Prop(prov, "IsByteType") is JsonElement b && (b.ValueKind == JsonValueKind.True || b.ValueKind == JsonValueKind.False))
                        r.IsBytes = b.GetBoolean();
                }
            }

            try
            {
                using var bal = await Get(api + "Balance/GetForCurrentUser", token);
                if (bal != null) r.Balance = Num(bal.RootElement, "Balance");
            }
            catch { } // баланс в долларах — необязательная часть

            r.Ok = r.Remaining != null;
            if (!r.Ok) r.Error = "сайт не прислал остаток";
        }
        catch (TaskCanceledException) { r.Error = "сайт не ответил вовремя"; }
        catch (HttpRequestException ex) { r.Error = "нет связи с сайтом (" + ex.Message + ")"; }
        catch (Exception ex) { r.Error = ex.Message; }
        return r;
    }

    /// <summary>GET с пропуском. null — пропуск не принят (нужно войти снова).</summary>
    static async Task<JsonDocument?> Get(string url, string token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", token);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var resp = await Http.SendAsync(req);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
            return null;
        resp.EnsureSuccessStatusCode();
        // без действующего пропуска этот сайт отвечает не ошибкой, а своей HTML-страницей
        var type = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (!type.Contains("json", StringComparison.OrdinalIgnoreCase)) return null;
        var text = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text);
    }

    static JsonElement? Prop(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    static double? Num(JsonElement e, string name) =>
        Prop(e, name) is JsonElement v
            ? v.ValueKind == JsonValueKind.Number ? v.GetDouble()
            : v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null
            : null;

    static string? Str(JsonElement e, string name) =>
        Prop(e, name) is JsonElement v && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Суточная норма: размер последнего ежедневного начисления в истории начислений сайта
    /// (тип пополнения 4 — «Recurring»), байты. null — сайт не ответил или таких начислений нет.
    /// </summary>
    public static async Task<double?> DailyAllocationAsync(string url, string token)
    {
        try
        {
            using var doc = await Get(Origin(url) + "/api/Allocation/GetHistoryForCurrentUser", token);
            if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            double? best = null;
            string bestDate = "";
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (Num(e, "Allocation") is not double amount || amount <= 0) continue;
                bool recurring = Prop(e, "CreditHistory") is JsonElement ch && Num(ch, "TopUpMethod") == 4;
                if (!recurring) continue;
                var date = Str(e, "Date") ?? ""; // даты в виде 2026-11-18T00:00:14 — сравниваются как строки
                if (best == null || string.CompareOrdinal(date, bestDate) > 0) { best = amount; bestDate = date; }
            }
            return best;
        }
        catch { return null; }
    }

    // ---------- включение интернета на сайте ----------

    /// <summary>Включён ли интернет на сайте и на каком устройстве (запрос главной страницы сайта).</summary>
    public static async Task<TrackerStatus> GetStatusAsync(string url, string token)
    {
        var st = new TrackerStatus();
        try
        {
            using var doc = await Get(Origin(url) + "/api/UserProvider/GetStatus", token);
            if (doc == null) { st.NeedLogin = true; return st; }
            var s = doc.RootElement;
            var v = Str(s, "status");
            st.Off = v != null && v.Equals("off", StringComparison.OrdinalIgnoreCase);
            if (Num(s, "provider") is double p) st.Provider = (int)p;
            st.Ip = Str(s, "ip");
        }
        catch (TaskCanceledException) { st.Error = "сайт не ответил вовремя"; }
        catch (HttpRequestException ex) { st.Error = "нет связи с сайтом (" + ex.Message + ")"; }
        catch (Exception ex) { st.Error = ex.Message; }
        return st;
    }

    /// <summary>Включить интернет — то же, что переключатель на главной странице сайта.</summary>
    public static async Task<(bool Ok, bool NeedLogin, string? Error)> TurnOnAsync(string url, string token, int provider)
    {
        try
        {
            var (code, text, login) = await Post(Origin(url) + "/api/Account/UpdateForCurrentUser", token,
                new { Started = true, ProviderNo = provider });
            if (login) return (false, true, null);
            if (code is >= 200 and < 300) return (true, false, null);
            return (false, false, SiteMessage(text) ?? "сайт ответил кодом " + code);
        }
        catch (TaskCanceledException) { return (false, false, "сайт не ответил вовремя"); }
        catch (Exception ex) { return (false, false, ex.Message); }
    }

    /// <summary>Присоединить этот компьютер к интернету, уже включённому на другом устройстве (кнопка «Join» на сайте).</summary>
    public static async Task<(bool Ok, bool NeedLogin, string? Error)> JoinAsync(string url, string token, string ip)
    {
        try
        {
            var (code, text, login) = await Post(Origin(url) + "/api/Device/JoinDevice", token, new { ip });
            if (login) return (false, true, null);
            if (code is >= 200 and < 300 || code == 409) return (true, false, null); // 409 — уже присоединён
            return (false, false, code == 400 ? "сайт считает, что интернет у вас не включён"
                : SiteMessage(text) ?? "сайт ответил кодом " + code);
        }
        catch (TaskCanceledException) { return (false, false, "сайт не ответил вовремя"); }
        catch (Exception ex) { return (false, false, ex.Message); }
    }

    /// <summary>Отключить присоединённое устройство от сессии (как «Leave» на сайте). Само оно может присоединиться снова.</summary>
    public static async Task<(bool Ok, bool NeedLogin, string? Error)> RemoveDeviceAsync(string url, string token, string ip)
    {
        try
        {
            var (code, text, login) = await Post(Origin(url) + "/api/Device/RemoveDevice", token, new { ip });
            if (login) return (false, true, null);
            if (code is >= 200 and < 300 || code == 404) return (true, false, null); // 404 — его уже нет в сессии
            return (false, false, SiteMessage(text) ?? "сайт ответил кодом " + code);
        }
        catch (TaskCanceledException) { return (false, false, "сайт не ответил вовремя"); }
        catch (Exception ex) { return (false, false, ex.Message); }
    }

    /// <summary>
    /// Понятное имя устройства по его адресу: сервер судна знает имена устройств
    /// (например, «oneplus-8-pro.zwana» → «Oneplus 8 Pro»). null — имени нет.
    /// </summary>
    public static async Task<string?> DeviceNameAsync(string ip)
    {
        try
        {
            var lookup = System.Net.Dns.GetHostEntryAsync(ip);
            if (await Task.WhenAny(lookup, Task.Delay(3000)) != lookup) return null;
            var host = lookup.Result.HostName;
            if (string.IsNullOrEmpty(host) || host == ip) return null;
            var first = host.Split('.')[0];
            var words = first.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w.Substring(1) : w);
            var name = string.Join(" ", words);
            return name.Length > 0 ? name : null;
        }
        catch { return null; }
    }

    /// <summary>Адреса устройств, уже присоединённых к включённому интернету. null — сайт не ответил.</summary>
    public static async Task<HashSet<string>?> JoinedDevicesAsync(string url, string token)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Origin(url) + "/api/Device/GetJoinedDevices");
            req.Headers.TryAddWithoutValidation("Authorization", token);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var resp = await Http.SendAsync(req);
            var set = new HashSet<string>();
            if (resp.StatusCode == HttpStatusCode.NotFound) return set; // никого не присоединяли
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!resp.IsSuccessStatusCode || !type.Contains("json", StringComparison.OrdinalIgnoreCase)) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
                foreach (var p in root.EnumerateObject()) set.Add(p.Name); // сайт хранит их как «адрес → сведения»
            else if (root.ValueKind == JsonValueKind.Array)
                foreach (var e in root.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String) set.Add(e.GetString()!);
                    else if (Str(e, "ip") is string ip) set.Add(ip);
            return set;
        }
        catch { return null; }
    }

    /// <summary>Адрес этого компьютера, каким его видит сайт трекера (вход не нужен). null — сайт не ответил.</summary>
    public static async Task<string?> ClientIpAsync(string url)
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var req = new HttpRequestMessage(HttpMethod.Get, Origin(url) + "/api/UserProvider/GetClientIP");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var resp = await Http.SendAsync(req, cts.Token);
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!resp.IsSuccessStatusCode || !type.Contains("json", StringComparison.OrdinalIgnoreCase)) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            return Str(doc.RootElement, "ip");
        }
        catch { return null; }
    }

    /// <summary>
    /// Работает ли этот трекер внутри сети, к которой подключены: сайт судна стоит на сервере в самой сети
    /// и видит компьютер по его адресу в этой сети. Из другой сети сайт либо не найдётся,
    /// либо увидит внешний адрес. true — да, false — нет, null — пока не ясно (сайт не ответил).
    /// </summary>
    public static async Task<bool?> ProbeAsync(string url)
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var req = new HttpRequestMessage(HttpMethod.Get, Origin(url) + "/api/UserProvider/GetClientIP");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var resp = await Http.SendAsync(req, cts.Token);
            if ((int)resp.StatusCode >= 500) return null; // сбой на сайте — спросим позже
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!resp.IsSuccessStatusCode || !type.Contains("json", StringComparison.OrdinalIgnoreCase)) return false;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var ip = Str(doc.RootElement, "ip");
            return ip != null && LocalIPv4().Contains(ip);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.NameResolutionError)
        {
            return false; // в этой сети такого сайта нет
        }
        catch { return null; }
    }

    static HashSet<string> LocalIPv4()
    {
        var set = new HashSet<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var u in ni.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        set.Add(u.Address.ToString());
            }
        }
        catch { }
        return set;
    }

    /// <summary>POST с пропуском. Login — пропуск не принят (сайт отправил на страницу входа).</summary>
    static async Task<(int Code, string Text, bool Login)> Post(string url, string token, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", token);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var resp = await Http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        var type = resp.Content.Headers.ContentType?.MediaType ?? "";
        bool login = resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Gone ||
                     (type.Contains("html", StringComparison.OrdinalIgnoreCase) &&
                      (resp.RequestMessage?.RequestUri?.AbsolutePath.Contains("/app/", StringComparison.OrdinalIgnoreCase) ?? false));
        return ((int)resp.StatusCode, text, login);
    }

    /// <summary>Текст ошибки из ответа сайта, если он есть.</summary>
    static string? SiteMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            var m = r.ValueKind == JsonValueKind.String ? r.GetString()
                : Str(r, "Message") ?? Str(r, "message") ?? Str(r, "error_description") ?? Str(r, "error");
            return string.IsNullOrWhiteSpace(m) ? null : "сайт пишет: «" + m.Trim() + "»";
        }
        catch { return null; }
    }

    // ---------- сведения для проверки ----------

    static readonly System.Text.RegularExpressions.Regex Personal = new(
        @"^(name|surname|firstname|lastname|fullname|e-?mail|mail|phone|phonenumber|mobile|username|login|password|" +
        @"token|access_token|address|cabin|rank|position|passport)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Записать, что сайт отвечает на запросы своей главной страницы (без имён, почты и пропуска), —
    /// по этим ответам видно, где сайт хранит суточную норму. Файл лежит рядом с настройками NetPause.
    /// </summary>
    public static async Task DumpAsync(string url, string token, int? provider, string file)
    {
        var api = Origin(url) + "/api/";
        var eps = new[]
        {
            "UserProvider/GetStatus", "UserProvider/GetActive?providerNo=" + (provider ?? 0),
            "UserProvider/ListForCurrentUser", "Provider/ListWithCap", "Balance/GetForCurrentUser",
            "CronProfile/GetRecurringMode", "Allocation/GetHistoryForCurrentUser", "Device/GetJoinedDevices",
        };
        var root = new System.Text.Json.Nodes.JsonObject
        {
            ["local"] = DateTime.Now.ToString("s"),
            ["utc"] = DateTime.UtcNow.ToString("s"),
        };
        foreach (var e in eps)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, api + e);
                req.Headers.TryAddWithoutValidation("Authorization", token);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                using var resp = await Http.SendAsync(req);
                var type = resp.Content.Headers.ContentType?.MediaType ?? "";
                var text = await resp.Content.ReadAsStringAsync();
                if (type.Contains("json", StringComparison.OrdinalIgnoreCase))
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(text);
                    Redact(node);
                    var s = node?.ToJsonString() ?? "null";
                    root[e] = s.Length > 20000 ? s.Substring(0, 20000) + "…" : node;
                }
                else root[e] = (int)resp.StatusCode + " " + type + " (" + text.Length + " байт)";
            }
            catch (Exception ex) { root[e] = "ошибка: " + ex.Message; }
        }
        File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    static void Redact(System.Text.Json.Nodes.JsonNode? n)
    {
        if (n is System.Text.Json.Nodes.JsonObject o)
        {
            foreach (var kv in o.ToList())
            {
                if (Personal.IsMatch(kv.Key) && kv.Value is System.Text.Json.Nodes.JsonValue v &&
                    v.TryGetValue<string>(out var str) && str.Length > 0)
                    o[kv.Key] = "<скрыто>";
                else Redact(kv.Value);
            }
        }
        else if (n is System.Text.Json.Nodes.JsonArray a)
        {
            foreach (var x in a) Redact(x);
        }
    }

    /// <summary>
    /// Вход без браузера: тот же запрос, что делает кнопка «Login» на сайте.
    /// Возвращает пропуск («тип токен») или текст ошибки.
    /// </summary>
    public static async Task<(string? Token, string? Error)> LoginAsync(string url, string user, string password)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { grant_type = "password", username = user, password });
            using var req = new HttpRequestMessage(HttpMethod.Post, Origin(url) + "/api/account/token")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var resp = await Http.SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            var type = resp.Content.Headers.ContentType?.MediaType ?? "";
            // для журнала: код ответа и названия cookie (без значений) — по ним видно, как сайт держит вход
            TrackerLog.Write("вход по паролю: код " + (int)resp.StatusCode + ", cookie: " +
                (resp.Headers.TryGetValues("Set-Cookie", out var sc) ? string.Join(", ", sc.Select(c => c.Split('=')[0].Trim())) : "нет"));
            if (resp.IsSuccessStatusCode && type.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                using var doc = JsonDocument.Parse(text);
                var tok = Str(doc.RootElement, "access_token");
                var kind = Str(doc.RootElement, "token_type") ?? "Bearer";
                if (!string.IsNullOrEmpty(tok)) return (kind + " " + tok, null);
            }
            if ((int)resp.StatusCode is 400 or 401 or 403)
            {
                string? msg = null;
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    msg = Str(doc.RootElement, "error_description") ?? Str(doc.RootElement, "Message") ?? Str(doc.RootElement, "message");
                }
                catch { }
                return (null, msg != null ? "Сайт ответил: " + msg : "Неверный логин или пароль.");
            }
            // сайт иногда отвечает просто строкой, например "The server is currently offline."
            string? said = null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                said = doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString()
                    : Str(doc.RootElement, "Message") ?? Str(doc.RootElement, "message");
            }
            catch { }
            if (said != null && said.Contains("offline", StringComparison.OrdinalIgnoreCase))
                return (null, "Сервер сайта сейчас недоступен (сайт пишет: «" + said.Trim() + "»). Это на стороне сайта, попробуйте позже.");
            return (null, "Сайт не пустил (код " + (int)resp.StatusCode + (said != null ? ": «" + said.Trim() + "»" : "") + ").");
        }
        catch (TaskCanceledException) { return (null, "Сайт не ответил вовремя. Проверьте подключение к Starlink."); }
        catch (HttpRequestException ex) { return (null, "Нет связи с сайтом: " + ex.Message); }
        catch (Exception ex) { return (null, ex.Message); }
    }

    // ---------- хранение пропуска ----------

    /// <summary>Пропуск шифруется ключом вашей учётной записи Windows: другой пользователь его не прочтёт.</summary>
    public static string Protect(string token) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? data)
    {
        if (string.IsNullOrEmpty(data)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(data), null, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }
}

/// <summary>Журнал работы с трекером (без паролей и пропусков) — чтобы разбираться, если что-то не так.</summary>
public static class TrackerLog
{
    static readonly object Gate = new();

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetPause");

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                var f = Path.Combine(Dir, "tracker-log.txt");
                var fi = new FileInfo(f);
                if (fi.Exists && fi.Length > 512 * 1024)
                {
                    File.Copy(f, f + ".old", true);
                    File.Delete(f);
                }
                File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
            }
        }
        catch { }
    }
}

/// <summary>Браузер по умолчанию.</summary>
public static class Browser
{
    /// <summary>
    /// Открыть адрес в браузере по умолчанию от имени обычного пользователя. NetPause работает
    /// с правами администратора, и браузер, запущенный напрямую, получил бы их тоже; поэтому просим
    /// открыть адрес Проводник — он работает без этих прав.
    /// </summary>
    public static bool Open(string url)
    {
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(explorer, "\"" + url + "\"") { UseShellExecute = false });
            return true;
        }
        catch
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
            catch { return false; }
        }
    }
}

/// <summary>
/// Вход на трекер без браузера: логин и пароль уходят прямо на сайт тем же запросом,
/// что и кнопка «Login» на нём. Пароль запоминается в зашифрованном виде, если галочка не снята.
/// </summary>
public sealed class TrackerPasswordWindow : Window
{
    readonly string _url;
    readonly TextBox _user = new() { Padding = new Thickness(6, 4, 6, 4) };
    readonly PasswordBox _pass = new() { Padding = new Thickness(6, 4, 6, 4) };
    readonly CheckBox _remember = new()
    {
        Content = "Запомнить логин и пароль на этом компьютере (в зашифрованном виде): NetPause будет входить сам",
        Margin = new Thickness(0, 10, 0, 0),
        IsChecked = true,
    };
    readonly TextBlock _error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    readonly Button _ok = new() { Content = "Войти", Padding = new Thickness(18, 5, 18, 5), IsDefault = true };

    public string? Token { get; private set; }
    public string? User => _user.Text.Trim();
    public string Password => _pass.Password;
    public bool Remember => _remember.IsChecked == true;

    public TrackerPasswordWindow(string url, string? lastUser)
    {
        _url = url;
        Title = "NetPause — вход на " + new Uri(url).Host;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _user.Text = lastUser ?? "";

        var cancel = new Button { Content = "Отмена", Padding = new Thickness(14, 5, 14, 5), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(_ok);
        buttons.Children.Add(cancel);

        _remember.Content = new TextBlock { Text = (string)_remember.Content, TextWrapping = TextWrapping.Wrap };

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "Те же логин и пароль, что на сайте " + new Uri(url).Host + ".",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });
        panel.Children.Add(new TextBlock { Text = "Логин" });
        panel.Children.Add(_user);
        panel.Children.Add(new TextBlock { Text = "Пароль", Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(_pass);
        panel.Children.Add(_remember);
        panel.Children.Add(_error);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => { if (_user.Text.Length > 0) _pass.Focus(); else _user.Focus(); };
        _ok.Click += async (_, _) => await Submit();
    }

    async Task Submit()
    {
        _error.Text = "";
        if (User!.Length == 0) { _error.Text = "Введите логин."; _user.Focus(); return; }
        if (Password.Length == 0) { _error.Text = "Введите пароль."; _pass.Focus(); return; }
        _ok.IsEnabled = false;
        _ok.Content = "Входим…";
        var (token, err) = await TrackerReader.LoginAsync(_url, User, Password);
        // пока ждали сайт, окно могли закрыть — тогда результат уже некому отдавать
        if (!IsVisible) return;
        _ok.IsEnabled = true;
        _ok.Content = "Войти";
        if (token == null) { _error.Text = err ?? "Не удалось войти."; return; }
        Token = token;
        try { DialogResult = true; } catch (InvalidOperationException) { }
    }
}

/// <summary>
/// Окно с настоящим сайтом трекера. Человек входит сам; NetPause забирает только
/// пропуск, который сайт сохраняет у себя после входа. Пароль программа не видит.
/// </summary>
public sealed class TrackerLoginWindow : Window
{
    readonly string _url;
    readonly WebView2 _web = new();
    readonly TextBlock _status;
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1.5) };

    public string? Token { get; private set; }
    public int? ProviderNo { get; private set; }

    public TrackerLoginWindow(string url)
    {
        _url = url;
        Title = "NetPause — вход на сайт трекера";
        Width = 1000;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _status = new TextBlock
        {
            Text = "Войдите на сайт как обычно. Как только вход пройдёт, окно закроется само. " +
                   "Логин и пароль вы вводите на самом сайте: NetPause их не видит и не сохраняет.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 8, 12, 8),
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(_status);
        Grid.SetRow(_web, 1);
        grid.Children.Add(_web);
        Content = grid;

        Loaded += async (_, _) => await Start();
        _poll.Tick += async (_, _) => await Check();
        Closed += (_, _) => _poll.Stop();
    }

    async Task Start()
    {
        try
        {
            // отдельная папка браузера NetPause: вход сохранится, и в следующий раз вводить пароль не придётся
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetPause", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, folder);
            await _web.EnsureCoreWebView2Async(env);
            _web.Source = new Uri(_url);
            _poll.Start();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _status.Text = "Не найден компонент Microsoft Edge WebView2, без него окно сайта не открыть. " +
                           "Обычно он уже есть в Windows 11; установить его можно с сайта Microsoft.";
        }
        catch (Exception ex)
        {
            _status.Text = "Не удалось открыть сайт: " + ex.Message;
        }
    }

    /// <summary>Сайт после входа кладёт пропуск в своё хранилище (localStorage, ключ «…logged_user»).</summary>
    async Task Check()
    {
        if (_web.CoreWebView2 == null) return;
        try
        {
            const string script =
                "(() => { const ks = Object.keys(localStorage);" +
                " const k = ks.find(x => x.endsWith('logged_user')); if (!k) return null;" +
                " const u = JSON.parse(localStorage.getItem(k) || 'null'); if (!u || !u.access_token) return null;" +
                " const pk = ks.find(x => x.endsWith('selectedProvider'));" +
                " return JSON.stringify({ t: u.access_token, p: pk ? localStorage.getItem(pk) : null, full: Object.keys(u).length > 1 }); })()";
            var raw = await _web.CoreWebView2.ExecuteScriptAsync(script);
            if (string.IsNullOrEmpty(raw) || raw == "null") return;
            var json = JsonSerializer.Deserialize<string>(raw); // ответ приходит строкой JSON внутри JSON
            if (json == null) return;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // ждём, пока сайт загрузит данные пользователя: значит, вход завершён полностью
            if (!root.GetProperty("full").GetBoolean()) return;
            Token = root.GetProperty("t").GetString();
            var p = root.GetProperty("p");
            if (p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString()?.Trim('"'), out var pn)) ProviderNo = pn;
            if (string.IsNullOrEmpty(Token)) return;
            _poll.Stop();
            DialogResult = true;
        }
        catch { }
    }
}
