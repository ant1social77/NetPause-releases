using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace NetPause;

/// <summary>
/// Личные данные одного пользователя Windows: логины, пароли и пропуски трекеров.
/// Лежат в его собственной папке (%LOCALAPPDATA%\NetPause), а не в общих настройках программы,
/// поэтому у каждого пользователя и на каждом компьютере они свои. Пароли и пропуски
/// к тому же зашифрованы ключом его учётной записи Windows.
/// </summary>
public sealed class UserSecrets
{
    public Dictionary<string, string> TrackerTokens { get; set; } = new();
    public Dictionary<string, string> TrackerPasswords { get; set; } = new();
    public string? TrackerUser { get; set; }
}

public sealed partial class Engine
{
    static string UserDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetPause");
    static string SecretsFile => Path.Combine(UserDir, "secrets.json");

    /// <summary>Версия программы, например «1.0».</summary>
    public static string AppVersion
    {
        get
        {
            var v = typeof(Engine).Assembly.GetName().Version;
            return v == null ? "1.0" : v.Major + "." + v.Minor + (v.Build > 0 ? "." + v.Build : "");
        }
    }

    /// <summary>Подставить личные данные пользователя в настройки после загрузки (вызывать под _lock).</summary>
    void LoadSecrets()
    {
        try
        {
            if (!File.Exists(SecretsFile)) return; // первый запуск: данные (если были) переедут при сохранении
            var s = JsonSerializer.Deserialize<UserSecrets>(File.ReadAllText(SecretsFile));
            if (s == null) return;
            _state.TrackerTokens = s.TrackerTokens ?? new();
            _state.TrackerPasswords = s.TrackerPasswords ?? new();
            _state.TrackerUser = s.TrackerUser;
        }
        catch { }
    }

    /// <summary>Записать личные данные в папку пользователя (вызывать под _lock). Возвращает текст для файла.</summary>
    string SecretsJson() => JsonSerializer.Serialize(new UserSecrets
    {
        TrackerTokens = _state.TrackerTokens,
        TrackerPasswords = _state.TrackerPasswords,
        TrackerUser = _state.TrackerUser,
    }, new JsonSerializerOptions { WriteIndented = true });

    static void WriteSecrets(string json)
    {
        try
        {
            Directory.CreateDirectory(UserDir);
            var tmp = SecretsFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, SecretsFile, true);
        }
        catch { }
    }

    /// <summary>
    /// Папка с данными программы: читать её могут все, а менять — только администраторы и система
    /// (то есть сам NetPause). Так обычная программа не подменит настройки и правила.
    /// </summary>
    static void ProtectDataDir(string dir)
    {
        try
        {
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false); // не наследуем разрешения от ProgramData
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(dir).SetAccessControl(sec);
        }
        catch { } // без прав администратора (служебная отрисовка картинок) — оставляем как есть
    }

    /// <summary>
    /// Запуск новой версии: сначала копия данных прежней (backups\версия-дата), потом работа.
    /// Хранятся последние пять копий. Вызывать до загрузки настроек.
    /// </summary>
    void BackupOnUpgrade(string dir)
    {
        try
        {
            var state = Path.Combine(dir, "state.json");
            if (!File.Exists(state)) return;
            string? old = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(state));
                if (doc.RootElement.TryGetProperty("AppVersion", out var v) && v.ValueKind == JsonValueKind.String) old = v.GetString();
            }
            catch { }
            if (old == AppVersion) return;
            var target = Path.Combine(dir, "backups", (old ?? "старая") + "-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm"));
            Directory.CreateDirectory(target);
            foreach (var name in new[] { "state.json", "history.json", "hosts-today.json" })
            {
                var f = Path.Combine(dir, name);
                if (File.Exists(f)) File.Copy(f, Path.Combine(target, name), true);
            }
            var all = new DirectoryInfo(Path.Combine(dir, "backups")).GetDirectories()
                .OrderByDescending(d => d.CreationTimeUtc).Skip(5);
            foreach (var d in all) { try { d.Delete(true); } catch { } }
        }
        catch { }
    }

    /// <summary>
    /// Вынести значок NetPause из скрытых значков к часам — один раз, при первом запуске.
    /// Windows 11 хранит это в HKCU\Control Panel\NotifyIconSettings (параметр IsPromoted).
    /// Если человек потом спрячет значок сам, NetPause больше его не трогает.
    /// </summary>
    public void PromoteTrayIconOnce()
    {
        lock (_lock) if (_state.TrayPromoted) return;
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true);
            if (root == null) return;
            var me = Environment.ProcessPath ?? "";
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name, true);
                if (k?.GetValue("ExecutablePath") is not string path) continue;
                // Windows пишет путь и как обычный, и через код папки ({6D809377-…}\NetPause\NetPause.exe),
                // поэтому сравниваем по концу пути
                var tail = Path.Combine(Path.GetFileName(Path.GetDirectoryName(me) ?? ""), Path.GetFileName(me));
                if (!path.EndsWith("\\" + tail, StringComparison.OrdinalIgnoreCase)) continue;
                k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                lock (_lock) { _state.TrayPromoted = true; _dirty = true; }
                return;
            }
        }
        catch { }
    }
}
