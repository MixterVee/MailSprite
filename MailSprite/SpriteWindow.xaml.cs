using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MailSprite;

public partial class SpriteWindow : Window
{
    private readonly DispatcherTimer _dismissTimer;
    private readonly DispatcherTimer _driftTimer;
    private readonly string _placement;
    private readonly bool _envelope;
    private readonly bool _elegant;
    private readonly bool _persistent;
    private bool _closing;
    private int _driftStep;
    private double _baseLeft;
    private double _baseTop;
    public event Action? ClearedByUser;

    public SpriteWindow(string character, int seconds,
        string placement = "LeftOfNotification", bool persistent = false, int count = 1)
    {
        InitializeComponent();
        _placement = placement;
        _persistent = persistent;
        _envelope = character.Equals("Envelope", StringComparison.OrdinalIgnoreCase);
        _elegant = !character.Equals("Butler", StringComparison.OrdinalIgnoreCase) && !_envelope;

        ButlerSprite.Visibility = _elegant || _envelope ? Visibility.Collapsed : Visibility.Visible;
        EnvelopeSprite.Visibility = _envelope ? Visibility.Visible : Visibility.Collapsed;
        ElegantNotice.Visibility = _elegant ? Visibility.Visible : Visibility.Collapsed;
        if (_elegant)
        {
            Width = 374;
            Height = 182;
            MessageBubble.Visibility = Visibility.Collapsed;
            BubbleTail.Visibility = Visibility.Collapsed;
            SparkleLayer.Visibility = Visibility.Collapsed;
        }
        else
        {
            Width = 390;
            Height = 405;
        }

        UpdateCount(count);
        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _dismissTimer.Tick += (_, _) => Dismiss();
        _driftTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(50) };
        _driftTimer.Tick += (_, _) => DriftOneStep();
        Loaded += (_, _) => Animate();
        Closed += (_, _) =>
        {
            _dismissTimer.Stop();
            _driftTimer.Stop();
            _closing = true;
        };
    }

    public void UpdateCount(int count)
    {
        count = Math.Max(1, count);
        AlertCountText.Text = count == 1
            ? "1 alert since last clear"
            : count + " alerts since last clear";
        ClearHintText.Text = _persistent ? "Click to clear · stays visible" : "Click to dismiss";
        LegacySubtitle.Text = count <= 1 ? "SPECIAL DELIVERY" : count + " ALERTS SINCE CLEAR";
        if (IsLoaded && _elegant)
        {
            var bounce = new DoubleAnimation(0.88, 1.0, TimeSpan.FromMilliseconds(330))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            MailGlyphScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
            MailGlyphScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
        }
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
        double x, y;
        switch (_placement)
        {
            case "BottomLeft":
                x = area.Left + 28;
                y = area.Bottom - Height - 34;
                break;
            case "TopLeft":
                x = area.Left + 28;
                y = area.Top + 42;
                break;
            case "TopRight":
                x = area.Right - Width - 40;
                y = area.Top + 42;
                break;
            case "LeftOfNotification":
            default:
                x = area.Right - Width - 600;
                y = area.Bottom - Height - 55;
                if (x < area.Left + 50)
                {
                    x = area.Left + 34;
                    y = area.Top + 52;
                }
                break;
        }
        _baseLeft = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - Width));
        _baseTop = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - Height));
        Left = _baseLeft;
        Top = _baseTop;
    }

    private void DriftOneStep()
    {
        if (_closing || !_persistent || !IsVisible) return;
        _driftStep = (_driftStep + 1) % 6;
        // Changes BOTH the screen position and the corresponding OLED pixels
        // every 50 seconds, without creating a distracting constant animation.
        var offsets = new (double X, double Y)[]
        {
            (0, 0), (23, -16), (-21, 13), (12, 22), (-13, -19), (17, 7)
        };
        Rect area = SystemParameters.WorkArea;
        var (dx, dy) = offsets[_driftStep];
        double targetX = Math.Clamp(_baseLeft + dx,
            area.Left + 2, Math.Max(area.Left + 2, area.Right - Width - 2));
        double targetY = Math.Clamp(_baseTop + dy,
            area.Top + 2, Math.Max(area.Top + 2, area.Bottom - Height - 2));
        var easing = new SineEase { EasingMode = EasingMode.EaseInOut };
        BeginAnimation(LeftProperty, Tween(Left, targetX, 2400, 0, easing));
        BeginAnimation(TopProperty, Tween(Top, targetY, 2400, 0, easing));
    }

    private void Animate()
    {
        PositionWindow();
        var spring = new BackEase { Amplitude = 0.32, EasingMode = EasingMode.EaseOut };
        var settle = new CubicEase { EasingMode = EasingMode.EaseOut };
        var sine = new SineEase { EasingMode = EasingMode.EaseInOut };

        Root.BeginAnimation(OpacityProperty, Tween(0, 1, 360, 0, settle));

        if (_elegant)
        {
            ElegantCardMove.BeginAnimation(TranslateTransform.YProperty,
                Tween(17, 0, 570, 0, settle));
            MailGlyphScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                Tween(0.8, 1, 430, 120, settle));
            MailGlyphScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                Tween(0.8, 1, 430, 120, settle));
        }
        else
        {
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

                var floatIn = new DoubleAnimationUsingKeyFrames();
                floatIn.KeyFrames.Add(new EasingDoubleKeyFrame(80,
                    KeyTime.FromTimeSpan(TimeSpan.Zero)));
                floatIn.KeyFrames.Add(new EasingDoubleKeyFrame(0,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(820)), spring));
                floatIn.KeyFrames.Add(new EasingDoubleKeyFrame(-9,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1370)), sine));
                floatIn.KeyFrames.Add(new EasingDoubleKeyFrame(5,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2100)), sine));
                floatIn.KeyFrames.Add(new EasingDoubleKeyFrame(-7,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2850)), sine));
                floatIn.KeyFrames.Add(new EasingDoubleKeyFrame(0,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(3550)), sine));
                EnvelopeMove.BeginAnimation(TranslateTransform.YProperty, floatIn);
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
                EyeBlink.BeginAnimation(ScaleTransform.ScaleYProperty,
                    Oscillate(1, 0.1, 85, 1900, 1, sine));
            }
        }

        if (_persistent) _driftTimer.Start();
        else _dismissTimer.Start();
    }

    public void Clear()
    {
        if (_closing) return;
        ClearedByUser?.Invoke();
        Dismiss();
    }

    private void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _dismissTimer.Stop();
        _driftTimer.Stop();
        var fade = Tween(Root.Opacity, 0, 290, 0,
            new QuadraticEase { EasingMode = EasingMode.EaseIn });
        fade.Completed += (_, _) => { if (IsLoaded) Close(); };
        Root.BeginAnimation(OpacityProperty, fade);
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Clear();
}
