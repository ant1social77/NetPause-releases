using System;
using System.Windows;
using System.Windows.Media;

namespace NetPause;

/// <summary>Уведомление о резком расходе трафика в правом нижнем углу экрана.</summary>
public partial class AlertWindow : Window
{
    /// <summary>Нажата главная кнопка («Выключить …»).</summary>
    public event Action? Act;

    bool _closing;

    public AlertWindow(string title, string text, string action)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = text;
        ActBtn.Content = action;

        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth - 12;
            Top = wa.Bottom - ActualHeight - 12;
            // мягко выезжает справа и проявляется
            Motion.SlideIn(Card, 36, 0, 460);
        };
    }

    void Act_Click(object sender, RoutedEventArgs e)
    {
        Act?.Invoke();
        FadeAway();
    }

    void Later_Click(object sender, RoutedEventArgs e) => FadeAway();

    /// <summary>Плавно уехать вправо и погаснуть, затем закрыться.</summary>
    void FadeAway()
    {
        if (_closing) return;
        _closing = true;
        Motion.Go(Motion.Shift(Card), TranslateTransform.XProperty, 28, 220, Motion.InOut);
        Motion.Go(Card, OpacityProperty, 0, 220, Motion.InOut, Close);
    }
}
