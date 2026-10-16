using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MailSprite;

public partial class SpriteWindow : Window
{
    private readonly DispatcherTimer _dismissTimer;
    private readonly string _placement;
    private readonly bool _envelope;
    private bool _closing;

    public SpriteWindow(string character, int seconds, string placement = "LeftOfNotification")
    {
        InitializeComponent();
        _placement = placement;
        _envelope = character.Equals("Envelope", StringComparison.OrdinalIgnoreCase);
        ButlerSprite.Visibility = _envelope ? Visibility.Collapsed : Visibility.Visible;
        EnvelopeSprite.Visibility = _envelope ? Visibility.Visible : Visibility.Collapsed;

        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _dismissTimer.Tick += (_, _) => Dismiss();
        Loaded += (_, _) => Animate();
        Closed += (_, _) =>
        {
            _dismissTimer.Stop();
            _closing = true;
        };
    }

    private static DoubleAnimation Tween(
        double from, double to, int durationMs, int delayMs = 0,
        IEasingFunction? ease = null)
    {
        return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(durationMs))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };
    }

    private static DoubleAnimation Oscillate(
        double from, double to, int durationMs, int delayMs, double repeats,
        IEasingFunction? ease = null)
    {
        return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(durationMs))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(repeats),
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
    }

    private void PositionWindow()
    {
        Rect area = SystemParameters.WorkArea;
        // Windows toast notifications are usually near the bottom-right of the
        // primary display. Reserve ~550 px on that side by default.
        double x, y;
        switch (_placement)
        {
            case "BottomLeft":
                x = area.Left + 20;
                y = area.Bottom - Height - 10;
                break;
            case "TopLeft":
                x = area.Left + 20;
                y = area.Top + 24;
                break;
            case "TopRight":
                x = area.Right - Width - 22;
                y = area.Top + 24;
                break;
            case "LeftOfNotification":
            default:
                x = area.Right - Width - 575;
                y = area.Bottom - Height - 12;
                // On narrow screens avoid overlapping the toast.
                if (x < area.Left + 15)
                {
                    x = area.Left + 16;
                    y = area.Top + 20;
                }
                break;
        }
        Left = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }

    private void Animate()
    {
        PositionWindow();
        var spring = new BackEase { Amplitude = 0.32, EasingMode = EasingMode.EaseOut };
        var settle = new CubicEase { EasingMode = EasingMode.EaseOut };
        var sine = new SineEase { EasingMode = EasingMode.EaseInOut };

        Root.BeginAnimation(OpacityProperty, Tween(0, 1, 340, 0, settle));

        // The message bubble pops up slightly AFTER the character arrives.
        BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            Tween(0.62, 1, 490, 210, spring));
        BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            Tween(0.62, 1, 490, 210, spring));
        SparkleLayer.BeginAnimation(OpacityProperty,
            Oscillate(0.15, 1, 650, 480, 3, sine));

        if (_envelope)
        {
            EnvelopeMove.BeginAnimation(TranslateTransform.XProperty,
                Tween(-110, 0, 860, 0, spring));
            EnvelopeMove.BeginAnimation(TranslateTransform.YProperty,
                Tween(80, 0, 820, 0, spring));
            EnvelopeMove.BeginAnimation(TranslateTransform.YProperty,
                Oscillate(10, -10, 780, 960, 3.6, sine));
            LeftWingAngle.BeginAnimation(RotateTransform.AngleProperty,
                Oscillate(-12, 16, 210, 280, 10, sine));
            RightWingAngle.BeginAnimation(RotateTransform.AngleProperty,
                Oscillate(12, -16, 210, 280, 10, sine));
        }
        else
        {
            ButlerMove.BeginAnimation(TranslateTransform.YProperty,
                Tween(105, 0, 790, 0, spring));
            ButlerMove.BeginAnimation(TranslateTransform.XProperty,
                Tween(25, 0, 700, 0, settle));
            ArmRotation.BeginAnimation(RotateTransform.AngleProperty,
                Oscillate(-23, 23, 310, 640, 4, sine));
            HeadNod.BeginAnimation(RotateTransform.AngleProperty,
                Oscillate(-3.5, 4.5, 430, 1050, 2, sine));
            HatTip.BeginAnimation(RotateTransform.AngleProperty,
                Oscillate(0, -13, 320, 880, 1, sine));

            // A quick blink after the greeting. Triggered on its own timer
            // so it doesn't distort the rest of the face.
            EyeBlink.BeginAnimation(ScaleTransform.ScaleYProperty,
                Oscillate(1, 0.1, 85, 1900, 1, sine));
        }

        _dismissTimer.Start();
    }

    private void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _dismissTimer.Stop();

        var fade = Tween(Root.Opacity, 0, 310, 0,
            new QuadraticEase { EasingMode = EasingMode.EaseIn });
        fade.Completed += (_, _) =>
        {
            if (IsLoaded) Close();
        };
        Root.BeginAnimation(OpacityProperty, fade);
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        Dismiss();
}
