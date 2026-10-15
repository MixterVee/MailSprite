using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MailSprite;

public partial class SpriteWindow : Window
{
    private readonly DispatcherTimer _dismissTimer;

    public SpriteWindow(string character, int seconds)
    {
        InitializeComponent();
        bool envelope = character.Equals("Envelope", StringComparison.OrdinalIgnoreCase);
        ButlerSprite.Visibility = envelope ? Visibility.Collapsed : Visibility.Visible;
        EnvelopeSprite.Visibility = envelope ? Visibility.Visible : Visibility.Collapsed;

        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _dismissTimer.Tick += (_, _) => Close();
        Loaded += (_, _) => Animate(envelope);
        Closed += (_, _) => _dismissTimer.Stop();
    }

    private void Animate(bool envelope)
    {
        var area = SystemParameters.WorkArea;
        Left = Math.Max(area.Left, area.Right - Width - 18);
        Top = Math.Max(area.Top, area.Bottom - Height - 12);

        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)));
        var easing = new BackEase { Amplitude = 0.48, EasingMode = EasingMode.EaseOut };
        var slide = new DoubleAnimation(70, 0, TimeSpan.FromMilliseconds(720)) { EasingFunction = easing };
        if (envelope)
        {
            EnvelopeMove.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
        }
        else
        {
            ButlerMove.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
            var wave = new DoubleAnimation(-17, 15, TimeSpan.FromMilliseconds(330))
            {
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(4),
                BeginTime = TimeSpan.FromMilliseconds(650)
            };
            ArmRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, wave);
        }
        _dismissTimer.Start();
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Close();
}
