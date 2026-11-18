using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace NetPause;

/// <summary>
/// Плавные анимации интерфейса в духе Windows 11. У всей программы одна «физика»:
/// быстрое начало и долгий мягкий «доезд», поэтому всё движется одинаково и спокойно.
/// </summary>
public static class Motion
{
    /// <summary>Главное замедление: быстрый старт, длинный мягкий доезд.</summary>
    public static readonly IEasingFunction Out = Make(new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 });
    /// <summary>Для ухода и затухания: плавный разгон и плавное торможение.</summary>
    public static readonly IEasingFunction InOut = Make(new CubicEase { EasingMode = EasingMode.EaseInOut });
    /// <summary>Лёгкий «пружинный» перелёт — для ползунков переключателей и точек.</summary>
    public static readonly IEasingFunction Spring = Make(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 });

    static IEasingFunction Make(EasingFunctionBase e) { e.Freeze(); return e; }

    static Duration Ms(int ms) => new(TimeSpan.FromMilliseconds(ms));

    /// <summary>Анимировать свойство к значению to (с текущего значения или с from).</summary>
    public static void Go(IAnimatable target, DependencyProperty p, double to, int ms,
        IEasingFunction? ease = null, Action? done = null, double? from = null)
    {
        var a = new DoubleAnimation { To = to, Duration = Ms(ms), EasingFunction = ease ?? Out };
        if (from != null) a.From = from;
        if (done != null) a.Completed += (_, _) => done();
        target.BeginAnimation(p, a);
    }

    /// <summary>Сдвиг элемента, который можно анимировать (замороженный заменяется новым).</summary>
    public static TranslateTransform Shift(UIElement el)
    {
        if (el.RenderTransform is TranslateTransform t && !t.IsFrozen) return t;
        var nt = new TranslateTransform();
        el.RenderTransform = nt;
        return nt;
    }

    /// <summary>Вернуть элемент в покой: полностью видим и на своём месте.</summary>
    public static void Reset(UIElement el)
    {
        el.BeginAnimation(UIElement.OpacityProperty, null);
        el.Opacity = 1;
        if (el.RenderTransform is TranslateTransform t && !t.IsFrozen)
        {
            t.BeginAnimation(TranslateTransform.XProperty, null);
            t.BeginAnimation(TranslateTransform.YProperty, null);
            t.X = 0;
            t.Y = 0;
        }
    }

    /// <summary>Выполнить действие через ms миллисекунд (в потоке интерфейса).</summary>
    public static void After(int ms, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    }

    /// <summary>Элемент проявляется и мягко въезжает на место со сдвига (dx, dy).</summary>
    public static void SlideIn(UIElement el, double dx, double dy = 0, int ms = 360)
    {
        var t = Shift(el);
        Go(el, UIElement.OpacityProperty, 1, Math.Min(ms, 280), InOut, from: 0);
        Go(t, TranslateTransform.XProperty, 0, ms, from: dx);
        Go(t, TranslateTransform.YProperty, 0, ms, from: dy);
    }

    /// <summary>Плавно погасить элемент.</summary>
    public static void FadeOut(UIElement el, int ms, Action? done = null) =>
        Go(el, UIElement.OpacityProperty, 0, ms, InOut, done);

    // ---------- переходы между экранами ----------

    /// <summary>
    /// Открыть экран поверх другого: он въезжает справа и проявляется,
    /// а нижний экран чуть уходит влево и притухает — как в приложениях Windows 11.
    /// </summary>
    public static void Push(FrameworkElement front, UIElement back, double distance)
    {
        front.Visibility = Visibility.Visible;
        var ft = Shift(front);
        var bt = Shift(back);
        Go(front, UIElement.OpacityProperty, 1, 260, InOut, from: 0);
        Go(ft, TranslateTransform.XProperty, 0, 420, from: distance);
        Go(bt, TranslateTransform.XProperty, -distance * 0.25, 420);
        Go(back, UIElement.OpacityProperty, 0.4, 320, InOut);
    }

    /// <summary>Закрыть верхний экран: он уезжает вправо, нижний возвращается на место.</summary>
    public static void Pop(FrameworkElement front, UIElement back, double distance, Action done)
    {
        var ft = Shift(front);
        var bt = Shift(back);
        Go(ft, TranslateTransform.XProperty, distance, 320, InOut, done);
        Go(front, UIElement.OpacityProperty, 0, 260, InOut);
        Go(bt, TranslateTransform.XProperty, 0, 420);
        Go(back, UIElement.OpacityProperty, 1, 320, InOut);
    }

    // ---------- «гармошка»: раскрытие и сворачивание ----------

    /// <summary>Плавно раскрыть элемент (он уже видим): высота растёт от нуля до нужной.</summary>
    public static void Expand(FrameworkElement el)
    {
        el.BeginAnimation(FrameworkElement.HeightProperty, null);
        el.Height = double.NaN;
        double w = el.ActualWidth > 0 ? el.ActualWidth
            : (el.Parent as FrameworkElement)?.ActualWidth ?? 300;
        el.Measure(new Size(w, double.PositiveInfinity));
        double h = el.DesiredSize.Height;
        if (h <= 0) return;
        Go(el, FrameworkElement.HeightProperty, h, 340, done: () =>
        {
            el.BeginAnimation(FrameworkElement.HeightProperty, null);
            el.Height = double.NaN;
        }, from: 0);
        Go(el, UIElement.OpacityProperty, 1, 300, InOut, from: 0);
    }

    /// <summary>Плавно свернуть элемент; done вызывается, когда он схлопнулся.</summary>
    public static void Collapse(FrameworkElement el, Action done)
    {
        double h = el.ActualHeight;
        Go(el, FrameworkElement.HeightProperty, 0, 260, InOut, () =>
        {
            done();
            el.BeginAnimation(FrameworkElement.HeightProperty, null);
            el.Height = double.NaN;
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 1;
        }, from: h);
        Go(el, UIElement.OpacityProperty, 0, 200, InOut);
    }

    /// <summary>Плавно повернуть элемент (стрелка раскрытия группы).</summary>
    public static void Rotate(UIElement el, double angle)
    {
        if (el.RenderTransform is not RotateTransform r || r.IsFrozen)
        {
            r = new RotateTransform((el.RenderTransform as RotateTransform)?.Angle ?? 0);
            el.RenderTransform = r;
        }
        Go(r, RotateTransform.AngleProperty, angle, 300);
    }

    // ---------- подчёркивания и «таблетки» выбора ----------

    /// <summary>Плавно передвинуть и растянуть указатель выбранного (подчёркивание вкладки, «таблетка»).</summary>
    public static void MoveTo(FrameworkElement el, TranslateTransform t, double x, double width, bool animate)
    {
        if (!animate)
        {
            t.BeginAnimation(TranslateTransform.XProperty, null);
            el.BeginAnimation(FrameworkElement.WidthProperty, null);
            t.X = x;
            el.Width = width;
            return;
        }
        Go(t, TranslateTransform.XProperty, x, 400);
        Go(el, FrameworkElement.WidthProperty, width, 400);
    }

    // ---------- строки списков появляются по очереди ----------

    /// <summary>
    /// Первые строки списка появляются одна за другой, с лёгким подъёмом.
    /// Вызывать, когда список только что показан; дальние строки появляются сразу.
    /// </summary>
    public static void Stagger(ItemsControl? list, int max = 12, int stepMs = 24)
    {
        if (list == null) return;
        list.Dispatcher.BeginInvoke(() =>
        {
            int n = Math.Min(list.Items.Count, max + 1);
            for (int i = 0; i < n; i++)
            {
                if (list.ItemContainerGenerator.ContainerFromIndex(i) is not UIElement c) continue;
                var delay = TimeSpan.FromMilliseconds(i * stepMs);
                var len = TimeSpan.FromMilliseconds(360);

                var fade = new DoubleAnimationUsingKeyFrames { Duration = delay + len };
                fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(delay)));
                fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(delay + len), InOut));
                fade.Completed += (_, _) => c.BeginAnimation(UIElement.OpacityProperty, null);

                var rise = new DoubleAnimationUsingKeyFrames { Duration = delay + len };
                rise.KeyFrames.Add(new DiscreteDoubleKeyFrame(10, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                rise.KeyFrames.Add(new DiscreteDoubleKeyFrame(10, KeyTime.FromTimeSpan(delay)));
                rise.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(delay + len), Out));

                c.BeginAnimation(UIElement.OpacityProperty, fade);
                Shift(c).BeginAnimation(TranslateTransform.YProperty, rise);
            }
        }, DispatcherPriority.Loaded);
    }

    // ---------- переключатели (ползунки) ----------

    public static readonly DependencyProperty SmoothSwitchProperty = DependencyProperty.RegisterAttached(
        "SmoothSwitch", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnSmoothSwitch));
    public static void SetSmoothSwitch(DependencyObject d, bool v) => d.SetValue(SmoothSwitchProperty, v);
    public static bool GetSmoothSwitch(DependencyObject d) => (bool)d.GetValue(SmoothSwitchProperty);

    static void OnSmoothSwitch(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ToggleButton b || !(bool)e.NewValue) return;
        // при появлении — сразу в нужном положении; при щелчке или смене состояния — плавно
        b.Loaded += (_, _) => SyncSwitch(b, false);
        b.Checked += (_, _) => SyncSwitch(b, b.IsLoaded && b.IsVisible);
        b.Unchecked += (_, _) => SyncSwitch(b, b.IsLoaded && b.IsVisible);
    }

    static void SyncSwitch(ToggleButton b, bool animate)
    {
        b.ApplyTemplate();
        if (b.Template?.FindName("thumb", b) is not FrameworkElement thumb) return;
        var on = b.Template.FindName("trackOn", b) as UIElement;
        bool isOn = b.IsChecked == true;
        var t = Shift(thumb);
        double x = isOn ? 18 : 0; // дорожка 40, кружок 16, отступы по 3
        if (!animate)
        {
            t.BeginAnimation(TranslateTransform.XProperty, null);
            t.X = x;
            if (on != null) { on.BeginAnimation(UIElement.OpacityProperty, null); on.Opacity = isOn ? 1 : 0; }
            return;
        }
        Go(t, TranslateTransform.XProperty, x, 340, Spring);
        if (on != null) Go(on, UIElement.OpacityProperty, isOn ? 1 : 0, 260, InOut);
    }

    // ---------- вкладки и сегменты: текст плавно светлеет и гаснет ----------

    public static readonly DependencyProperty FadeTabProperty = DependencyProperty.RegisterAttached(
        "FadeTab", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnFadeTab));
    public static void SetFadeTab(DependencyObject d, bool v) => d.SetValue(FadeTabProperty, v);
    public static bool GetFadeTab(DependencyObject d) => (bool)d.GetValue(FadeTabProperty);

    static void OnFadeTab(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ToggleButton b || !(bool)e.NewValue) return;
        b.Loaded += (_, _) => TabLevel(b, false);
        b.Checked += (_, _) => TabLevel(b, b.IsLoaded);
        b.Unchecked += (_, _) => TabLevel(b, b.IsLoaded);
        b.MouseEnter += (_, _) => TabLevel(b, true);
        b.MouseLeave += (_, _) => TabLevel(b, true);
        b.IsEnabledChanged += (_, _) => TabLevel(b, b.IsLoaded);
    }

    static void TabLevel(ToggleButton b, bool animate)
    {
        double to = !b.IsEnabled ? 0.35 : b.IsChecked == true ? 1 : b.IsMouseOver ? 0.88 : 0.6;
        if (!animate)
        {
            b.BeginAnimation(UIElement.OpacityProperty, null);
            b.Opacity = to;
            return;
        }
        Go(b, UIElement.OpacityProperty, to, 240, InOut);
    }

    // ---------- круглые переключатели выбора: точка плавно вырастает ----------

    public static readonly DependencyProperty SmoothRadioProperty = DependencyProperty.RegisterAttached(
        "SmoothRadio", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnSmoothRadio));
    public static void SetSmoothRadio(DependencyObject d, bool v) => d.SetValue(SmoothRadioProperty, v);
    public static bool GetSmoothRadio(DependencyObject d) => (bool)d.GetValue(SmoothRadioProperty);

    static void OnSmoothRadio(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ToggleButton b || !(bool)e.NewValue) return;
        b.Loaded += (_, _) => SyncRadio(b, false);
        b.Checked += (_, _) => SyncRadio(b, b.IsLoaded);
        b.Unchecked += (_, _) => SyncRadio(b, b.IsLoaded);
    }

    static void SyncRadio(ToggleButton b, bool animate)
    {
        b.ApplyTemplate();
        if (b.Template?.FindName("dot", b) is not UIElement dot) return;
        if (dot.RenderTransform is not ScaleTransform s || s.IsFrozen)
        {
            s = new ScaleTransform(0, 0);
            dot.RenderTransform = s;
        }
        double v = b.IsChecked == true ? 1 : 0;
        if (!animate)
        {
            s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            s.ScaleX = v;
            s.ScaleY = v;
            return;
        }
        var ease = v > 0 ? Spring : InOut;
        Go(s, ScaleTransform.ScaleXProperty, v, 300, ease);
        Go(s, ScaleTransform.ScaleYProperty, v, 300, ease);
    }

    // ---------- подсветка строки под мышью: плавно проявляется и гаснет ----------

    public static readonly DependencyProperty HoverProperty = DependencyProperty.RegisterAttached(
        "Hover", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnHover));
    public static void SetHover(DependencyObject d, bool v) => d.SetValue(HoverProperty, v);
    public static bool GetHover(DependencyObject d) => (bool)d.GetValue(HoverProperty);

    static void OnHover(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border b || !(bool)e.NewValue) return;
        b.MouseEnter += (_, _) => HoverTo(b, true);
        b.MouseLeave += (_, _) => HoverTo(b, false);
    }

    static void HoverTo(Border b, bool on)
    {
        var c = (Application.Current?.TryFindResource("Hover") as SolidColorBrush)?.Color
                ?? Color.FromArgb(0x14, 0x80, 0x80, 0x80);
        var clear = Color.FromArgb(0, c.R, c.G, c.B);
        if (b.Background is not SolidColorBrush br || br.IsFrozen)
        {
            br = new SolidColorBrush(clear); // прозрачная, но «ловит» мышь, как и прежде
            b.Background = br;
        }
        var anim = new ColorAnimation(on ? c : clear, Ms(on ? 160 : 360)) { EasingFunction = on ? Out : InOut };
        br.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }
}
