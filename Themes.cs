using System;
using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace NetPause;

/// <summary>Одно оформление: набор цветов и вид шкалы остатка.</summary>
public sealed class ThemeDef
{
    public string Id = "";
    public string Name = "";
    public string Description = "";
    public bool Dark;
    /// <summary>Шкала остатка трафика: drop (капля), ring (кольцо), orbit (орбита), bar (полоса), none.</summary>
    public string Gauge = "none";
    /// <summary>Значок у часов по умолчанию: wifi (капля и Wi-Fi) или pause.</summary>
    public string TrayStyle = "pause";
    /// <summary>Демонята у заблокированных программ и капля в шапке.</summary>
    public bool Demons;
    public Dictionary<string, string> Colors = new();

    public Color C(string key) => (Color)ColorConverter.ConvertFromString(Colors[key]);
}

/// <summary>Оформления программы. Меняются на лету: все цвета панели — динамические ресурсы.</summary>
public static class ThemeManager
{
    public static ThemeDef Current { get; private set; } = new();

    /// <summary>Оформление сменилось (обновить значок у часов, шкалы, строки).</summary>
    public static event Action? Changed;

    public static readonly ThemeDef[] All =
    {
        new()
        {
            Id = "system", Name = "Системное", Description = "Как Windows: светлое или тёмное", Gauge = "none", TrayStyle = "pause",
        },
        new()
        {
            Id = "blood", Name = "Кровь", Description = "Капля остатка, демонята охраняют трафик", Dark = true,
            Gauge = "drop", TrayStyle = "wifi", Demons = true,
            Colors = new()
            {
                ["Bg"] = "#0F0A0B", ["Card"] = "#1A1012", ["Fg"] = "#F4ECEC", ["Fg2"] = "#A89496",
                ["Line"] = "#2B1A1D", ["SwitchOff"] = "#3A2A2D", ["On"] = "#E01E37", ["Warn"] = "#FF5A6E",
                ["Hover"] = "#16FF7A88", ["BadgeBg"] = "#2A1215", ["BadgeFg"] = "#FF8A99",
                ["GaugeFill"] = "#C8102E", ["GaugeEmpty"] = "#241012", ["GaugeTrack"] = "#2A1A1D",
            },
        },
        new()
        {
            Id = "graphite", Name = "Графит и янтарь", Description = "Спокойный, «приборный»: кольцо остатка", Dark = true,
            Gauge = "ring", TrayStyle = "pause",
            Colors = new()
            {
                ["Bg"] = "#16171A", ["Card"] = "#1F2024", ["Fg"] = "#ECECEE", ["Fg2"] = "#9A9BA3",
                ["Line"] = "#2A2B30", ["SwitchOff"] = "#3A3B41", ["On"] = "#F5A524", ["Warn"] = "#F0715C",
                ["Hover"] = "#14FFFFFF", ["BadgeBg"] = "#2D2615", ["BadgeFg"] = "#F5C46B",
                ["GaugeFill"] = "#F5A524", ["GaugeEmpty"] = "#2A2B30", ["GaugeTrack"] = "#2A2B30",
            },
        },
        new()
        {
            Id = "orbit", Name = "Орбита", Description = "Космос в духе Starlink: спутник на орбите остатка", Dark = true,
            Gauge = "orbit", TrayStyle = "pause",
            Colors = new()
            {
                ["Bg"] = "#0B1222", ["Card"] = "#121A2E", ["Fg"] = "#E6EEF8", ["Fg2"] = "#8FA3C0",
                ["Line"] = "#1C2840", ["SwitchOff"] = "#2A3650", ["On"] = "#38BDF8", ["Warn"] = "#FB7185",
                ["Hover"] = "#1438BDF8", ["BadgeBg"] = "#1E1A3D", ["BadgeFg"] = "#A78BFA",
                ["GaugeFill"] = "#38BDF8", ["GaugeEmpty"] = "#1C2840", ["GaugeTrack"] = "#24324F",
            },
        },
        new()
        {
            Id = "paper", Name = "Бумага", Description = "Светлый минимализм: полоса остатка", Dark = false,
            Gauge = "bar", TrayStyle = "pause",
            Colors = new()
            {
                ["Bg"] = "#F6F4F0", ["Card"] = "#FFFFFF", ["Fg"] = "#1C1B1A", ["Fg2"] = "#6E6A63",
                ["Line"] = "#E2DED6", ["SwitchOff"] = "#CFCAC1", ["On"] = "#2E9E5B", ["Warn"] = "#E2553D",
                ["Hover"] = "#0D000000", ["BadgeBg"] = "#FBE9E5", ["BadgeFg"] = "#B33A24",
                ["GaugeFill"] = "#2E9E5B", ["GaugeEmpty"] = "#E9E6E0", ["GaugeTrack"] = "#E9E6E0",
            },
        },
    };

    public static ThemeDef Find(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];

    /// <summary>Применить оформление ко всей программе.</summary>
    public static void Apply(string? id)
    {
        var t = Find(id);
        if (t.Id == "system") t = SystemTheme();
        var res = Application.Current.Resources;
        foreach (var kv in t.Colors)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(kv.Value));
            b.Freeze();
            res[kv.Key] = b;
        }
        Current = t;
        OverlayWindow.HotColor = t.C("Warn");
        Changed?.Invoke();
    }

    /// <summary>«Системное»: светлое или тёмное, как настроено в Windows.</summary>
    static ThemeDef SystemTheme()
    {
        bool dark;
        try
        {
            var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            dark = v is int i && i == 0;
        }
        catch { dark = false; }

        var t = new ThemeDef { Id = "system", Name = "Системное", Dark = dark, Gauge = "none", TrayStyle = "pause" };
        t.Colors = dark
            ? new()
            {
                ["Bg"] = "#202020", ["Card"] = "#2C2C2C", ["Fg"] = "#FFFFFF", ["Fg2"] = "#A6A6A6",
                ["Line"] = "#383838", ["SwitchOff"] = "#5C5C5C", ["Hover"] = "#17FFFFFF",
                ["BadgeBg"] = "#1E3A29", ["BadgeFg"] = "#7FD3A0",
            }
            : new()
            {
                ["Bg"] = "#F3F3F3", ["Card"] = "#FFFFFF", ["Fg"] = "#1B1B1B", ["Fg2"] = "#646464",
                ["Line"] = "#DCDCDC", ["SwitchOff"] = "#B4B4B4", ["Hover"] = "#0E000000",
                ["BadgeBg"] = "#E2F3E8", ["BadgeFg"] = "#1F6B3F",
            };
        t.Colors["On"] = "#2EA05A";
        t.Colors["Warn"] = "#D9534F";
        t.Colors["GaugeFill"] = "#2EA05A";
        t.Colors["GaugeEmpty"] = dark ? "#383838" : "#E4E4E4";
        t.Colors["GaugeTrack"] = dark ? "#383838" : "#E4E4E4";
        return t;
    }
}

/// <summary>
/// Значок у часов, нарисованный под размер значков Windows (с учётом масштаба экрана),
/// чтобы он был чётким, а не размытым.
/// </summary>
public static class TrayIcons
{
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    /// <summary>Нарисовать значок. style: wifi или pause; on — экономия включена.</summary>
    public static System.Drawing.Icon Make(string style, bool on, Color accent)
    {
        int s = Math.Max(16, System.Windows.Forms.SystemInformation.SmallIconSize.Width);
        using var bmp = Draw(style, on, accent, s);
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>Освободить значок, созданный Make (у Icon.FromHandle дескриптор сам не освобождается).</summary>
    public static void Free(System.Drawing.Icon? icon)
    {
        if (icon == null) return;
        try { DestroyIcon(icon.Handle); } catch { }
        icon.Dispose();
    }

    /// <summary>Картинка значка для показа в настройках.</summary>
    public static System.Windows.Media.Imaging.BitmapSource Preview(string style, bool on, Color accent, int size = 24)
    {
        using var bmp = Draw(style, on, accent, size);
        var h = bmp.GetHbitmap(System.Drawing.Color.FromArgb(0, 0, 0, 0));
        try
        {
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { DeleteObject(h); }
    }

    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    static System.Drawing.Bitmap Draw(string style, bool on, Color accent, int s)
    {
        var bmp = new System.Drawing.Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(System.Drawing.Color.Transparent);
        float k = s / 32f;
        g.ScaleTransform(k, k); // рисуем в сетке 32×32, Windows получает ровно свой размер

        var main = on ? System.Drawing.Color.FromArgb(accent.R, accent.G, accent.B) : System.Drawing.Color.FromArgb(150, 150, 150);
        if (style == "wifi") DrawVampireWifi(g, main, on);
        else DrawPause(g, main, on);
        return bmp;
    }

    /// <summary>«Вампирский» Wi-Fi: две дуги, вместо точки — капля крови, с внешней дуги стекает капелька.</summary>
    static void DrawVampireWifi(System.Drawing.Graphics g, System.Drawing.Color c, bool on)
    {
        using var pen = new System.Drawing.Pen(c, 3.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        // дуги с общим центром внизу, раскрыты вверх
        g.DrawArc(pen, 16 - 14, 24 - 14, 28, 28, 225, 90);
        g.DrawArc(pen, 16 - 8, 24 - 8, 16, 16, 225, 90);
        using var fill = new System.Drawing.SolidBrush(c);
        DrawDrop(g, fill, 16, 18.6f, 12.4f);
        if (on) DrawDrop(g, fill, 26.4f, 14.2f, 7.2f); // стекающая капля
        using var shine = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(on ? 120 : 70, 255, 255, 255));
        g.FillEllipse(shine, 13.3f, 24.0f, 2.3f, 3.6f);
    }

    /// <summary>Капля: острие сверху в (cx, top), высота h.</summary>
    static void DrawDrop(System.Drawing.Graphics g, System.Drawing.Brush b, float cx, float top, float h)
    {
        float w = h * 0.68f, r = w / 2, cy = top + h - r;
        using var p = new GraphicsPath();
        p.AddBezier(cx, top, cx, top, cx + r, top + h * 0.42f, cx + r, cy);
        p.AddArc(cx - r, cy - r, w, w, 0, 180);
        p.AddBezier(cx - r, cy, cx - r, top + h * 0.42f, cx, top, cx, top);
        p.CloseFigure();
        g.FillPath(b, p);
    }

    /// <summary>Круг цвета оформления: «пауза» — экономия включена, «плей» — интернет свободен.</summary>
    static void DrawPause(System.Drawing.Graphics g, System.Drawing.Color c, bool on)
    {
        using var back = new System.Drawing.SolidBrush(c);
        g.FillEllipse(back, 1, 1, 30, 30);
        if (on)
        {
            g.FillRectangle(System.Drawing.Brushes.White, 10, 9, 4, 14);
            g.FillRectangle(System.Drawing.Brushes.White, 18, 9, 4, 14);
        }
        else
        {
            g.FillPolygon(System.Drawing.Brushes.White, new[]
            {
                new System.Drawing.PointF(12, 8), new System.Drawing.PointF(24, 16), new System.Drawing.PointF(12, 24),
            });
        }
    }
}
