using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace NetPause;

/// <summary>
/// Плавная прокрутка для ScrollViewer: колесо мыши, тачпад, клавиатура и щелчок по полосе прокрутки
/// не прыгают рывком, а доезжают до цели с замедлением.
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);
    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);

    /// <summary>Пикселей на единицу колеса: щелчок колеса (120) ≈ 36 px — спокойнее, чем обычно в Windows.</summary>
    const double PixelsPerWheelUnit = 0.3;
    /// <summary>Шаг для стрелок на клавиатуре и кнопок полосы прокрутки.</summary>
    public const double LineStep = 40;
    /// <summary>Какую долю оставшегося пути проезжаем за 1/60 секунды. Меньше — плавнее и медленнее.</summary>
    const double Ease = 0.13;

    sealed class State
    {
        public double Target;
        public bool Running;
        public EventHandler? Frame;
        public readonly Stopwatch Clock = new();
    }

    static readonly ConditionalWeakTable<ScrollViewer, State> States = new();

    static State Get(ScrollViewer sv) => States.GetValue(sv, _ => new State { Target = sv.VerticalOffset });

    static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv || !(bool)e.NewValue) return;

        sv.PreviewMouseWheel += OnWheel;
        // пока анимации нет, цель следует за реальным положением (например, после перетаскивания ползунка)
        sv.ScrollChanged += (_, _) =>
        {
            var st = Get(sv);
            if (!st.Running) st.Target = sv.VerticalOffset;
        };

        // щелчки по полосе прокрутки тоже плавные; привязки на самом элементе важнее встроенных
        void Bind(RoutedCommand cmd, Func<double> delta) =>
            sv.CommandBindings.Add(new CommandBinding(cmd, (_, a) => { By(sv, delta()); a.Handled = true; }));
        Bind(ScrollBar.LineUpCommand, () => -LineStep);
        Bind(ScrollBar.LineDownCommand, () => LineStep);
        Bind(ScrollBar.PageUpCommand, () => -Page(sv));
        Bind(ScrollBar.PageDownCommand, () => Page(sv));
    }

    public static double Page(ScrollViewer sv) => Math.Max(LineStep, sv.ViewportHeight * 0.85);

    static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;
        if (sv.ScrollableHeight <= 0) return; // прокручивать нечего — отдаём событие дальше
        e.Handled = true;
        // тачпад присылает много мелких порций, колесо — крупные; формула одна и та же,
        // а сглаживание превращает и то, и другое в ровное движение
        By(sv, -e.Delta * PixelsPerWheelUnit);
    }

    /// <summary>Плавно прокрутить на dy пикселей (вниз — положительное).</summary>
    public static void By(ScrollViewer sv, double dy)
    {
        var st = Get(sv);
        var from = st.Running ? st.Target : sv.VerticalOffset;
        To(sv, from + dy);
    }

    /// <summary>Плавно прокрутить к положению y.</summary>
    public static void To(ScrollViewer sv, double y)
    {
        var st = Get(sv);
        st.Target = Math.Clamp(y, 0, Math.Max(0, sv.ScrollableHeight));
        if (st.Running) return;
        st.Running = true;
        st.Clock.Restart();
        st.Frame = (_, _) => Step(sv, st);
        CompositionTarget.Rendering += st.Frame;
    }

    static void Step(ScrollViewer sv, State st)
    {
        double dt = st.Clock.Elapsed.TotalSeconds;
        st.Clock.Restart();
        st.Target = Math.Clamp(st.Target, 0, Math.Max(0, sv.ScrollableHeight));

        double cur = sv.VerticalOffset;
        double diff = st.Target - cur;
        if (Math.Abs(diff) < 0.5 || !sv.IsVisible)
        {
            sv.ScrollToVerticalOffset(st.Target);
            Stop(st);
            return;
        }
        // одинаковая скорость при любой частоте кадров монитора
        double k = 1 - Math.Pow(1 - Ease, Math.Max(dt, 0.001) * 60);
        sv.ScrollToVerticalOffset(cur + diff * k);
    }

    static void Stop(State st)
    {
        st.Running = false;
        if (st.Frame != null) CompositionTarget.Rendering -= st.Frame;
        st.Frame = null;
    }
}
