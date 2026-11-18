using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace NetPause;

/// <summary>Строка главного списка: программа или папка.</summary>
public interface IRowVm
{
    long SortBytes { get; }
    string Name { get; }
    void Refresh();
}

/// <summary>Выбирает вид строки: программа или папка.</summary>
public sealed class RowSelector : DataTemplateSelector
{
    public DataTemplate? AppTemplate { get; set; }
    public DataTemplate? FolderTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is FolderVm ? FolderTemplate : AppTemplate;
}

/// <summary>Папка в главном списке: все части Windows или все разовые установщики.</summary>
public sealed class FolderVm : IRowVm, INotifyPropertyChanged
{
    public const string Windows = "windows";
    public const string Installers = "installers";

    readonly Engine _engine;
    public string Kind { get; }
    public List<AppVm> Members { get; set; } = new();

    public FolderVm(Engine engine, string kind)
    {
        _engine = engine;
        Kind = kind;
    }

    public string Name => Kind == Windows ? "Windows" : "Разовые установщики";
    public string Glyph => Kind == Windows ? "" : "";

    public string Note => Kind == Windows
        ? "Службы и программы самой Windows. Без переключателя — то, без чего интернет перестанет работать совсем. Службу «Оптимизация доставки» NetPause ограничивает отдельно."
        : "Установщики, удалённые программы и то, что запускалось из временных папок. Обычно они нужны один раз; общий переключатель разрешает или запрещает их все.";

    public long SortBytes => Members.Sum(m => m.Entry.BytesToday);

    public string Sub
    {
        get
        {
            var s = Count(Members.Count) + " · " + (SortBytes > 0 ? Fmt.Bytes(SortBytes) + " сегодня" : "нет трафика");
            if (_engine.EconomyOn)
            {
                int blocked = Members.Count(m => !m.Entry.Allowed && !m.Entry.Locked);
                if (blocked > 0) s += " · заблокировано: " + blocked;
            }
            return s;
        }
    }

    // у папки Windows общего переключателя нет: разом выключить всю Windows слишком опасно
    public Visibility SwitchVisibility => Kind == Windows ? Visibility.Collapsed : Visibility.Visible;

    public bool Allowed
    {
        get => Members.Where(m => !m.Entry.Locked).All(m => m.Entry.Allowed);
        set
        {
            foreach (var m in Members.Where(m => !m.Entry.Locked))
                _engine.SetAllowed(m.Entry.Key, value);
            Refresh();
        }
    }

    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Sub)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Allowed)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    static string Count(int n)
    {
        int m10 = n % 10, m100 = n % 100;
        var w = m10 == 1 && m100 != 11 ? "программа"
            : m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14) ? "программы" : "программ";
        return n + " " + w;
    }

    // ---------- в какую папку попадает программа ----------

    static readonly Regex InstallerName = new(
        @"setup|install|uninst|unins\d*\.exe$|^is-|\.tmp$|_is[0-9a-f]+|^msiexec\.exe$|redist|vc_redist|dotnet-.*-win",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly string[] TempDirs =
    {
        Path.GetTempPath(),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemTemp"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
    };

    /// <summary>Папка для программы или null, если программа показывается в списке сама по себе.</summary>
    public static string? FolderOf(AppEntry e)
    {
        if (e.IsSystem) return Windows;
        if (e.Path == "System") return Windows;
        var file = Path.GetFileName(e.Path);
        if (InstallerName.IsMatch(file)) return Installers;
        if (TempDirs.Any(d => e.Path.StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) return Installers;
        // программы уже нет на диске — значит, её удалили или это был разовый запуск
        if (!File.Exists(e.Path)) return Installers;
        return null;
    }
}
