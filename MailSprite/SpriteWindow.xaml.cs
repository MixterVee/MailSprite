using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using WpfAnimatedGif;

namespace MailSprite;

public partial class SpriteWindow : Window
{
    private readonly DispatcherTimer _dismissTimer;
    private readonly DispatcherTimer _driftTimer;
    private readonly string _placement;
    private readonly bool _envelope;
    private readonly bool _elegant;
    private readonly bool _minimal;
    private readonly bool _swoop;
    private readonly bool _mailbox;
    private readonly bool _custom;
    private readonly bool _preview;
    private readonly bool _persistent;
    private bool _closing;
    private int _driftStep;
    private double _baseLeft;
    private double _baseTop;
    public event Action? ClearedByUser;

    public SpriteWindow(string character, int seconds,
        string placement = "LeftOfNotification", bool persistent = false, int count = 1,
        string gifPath = "", bool preview = false)
    {
        InitializeComponent();
        _placement = placement;
        _persistent = persistent;
        _preview = preview;
        _envelope = character.Equals("Envelope", StringComparison.OrdinalIgnoreCase);
        _minimal = character.Equals("Minimal", StringComparison.OrdinalIgnoreCase);
        _swoop = character.Equals("Swoop", StringComparison.OrdinalIgnoreCase);
        _mailbox = character.Equals("Mailbox", StringComparison.OrdinalIgnoreCase);
        _custom = character.Equals("Custom", StringComparison.OrdinalIgnoreCase);
        _elegant = character.Equals("Elegant", StringComparison.OrdinalIgnoreCase);

        ButlerSprite.Visibility = _envelope || _minimal || _swoop || _mailbox || _custom || _elegant
            ? Visibility.Collapsed : Visibility.Visible;
        EnvelopeSprite.Visibility = _envelope ? Visibility.Visible : Visibility.Collapsed;
        MinimalNotice.Visibility = _minimal ? Visibility.Visible : Visibility.Collapsed;
        SwoopNotice.Visibility = _swoop ? Visibility.Visible : Visibility.Collapsed;
        MailboxNotice.Visibility = _mailbox ? Visibility.Visible : Visibility.Collapsed;
        ElegantNotice.Visibility = _elegant ? Visibility.Visible : Visibility.Collapsed;
        CustomHeader.Visibility = _custom ? Visibility.Visible : Visibility.Collapsed;
        CustomGifImage.Visibility = _custom ? Visibility.Visible : Visibility.Collapsed;
        if (_minimal || _swoop || _mailbox || _custom || _elegant)
        {
            MessageBubble.Visibility = Visibility.Collapsed;
            BubbleTail.Visibility = Visibility.Collapsed;
            SparkleLayer.Visibility = Visibility.Collapsed;
            Width = _custom ? 390 : 374;
            Height = _custom ? 405 : (_swoop || _mailbox ? 210 : 148);
        }
        else
        {
            Width = 390;
            Height = 405;
        }

        if (_custom)
        {
            if (!File.Exists(gifPath))
                throw new FileNotFoundException("Custom animated GIF not found.", gifPath);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(Path.GetFullPath(gifPath));
            bmp.EndInit();
            ImageBehavior.SetAnimatedSource(CustomGifImage, bmp);
            ImageBehavior.SetRepeatBehavior(CustomGifImage, RepeatBehavior.Forever);
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
        MinimalCountText.Text = _preview
            ? "Preview only — no email detected"
            : count == 1 ? "1 alert since last clear" : count + " alerts since last clear";
        MinimalHintText.Text = _persistent ? "Click to clear" : "Click to dismiss";
        string countLabel = _preview ? "Preview only — no email detected" :
            count == 1 ? "1 alert since last clear" : count + " alerts since last clear";
        SwoopCountText.Text = countLabel;
        MailboxCountText.Text = countLabel;
        SwoopHintText.Text = _persistent ? "Click to clear" : "Click to dismiss";
        MailboxHintText.Text = _persistent ? "Click to clear" : "Click to dismiss";
        CustomLabelText.Text = _preview ? "Preview — click to clear"
            : count == 1 ? "New mail received" : count + " mail alerts";
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

        if (_minimal)
        {
            MinimalCardMove.BeginAnimation(TranslateTransform.YProperty,
                Tween(9, 0, 330, 0, settle));
        }
        else if (_swoop)
        {
            SwoopSlide.BeginAnimation(TranslateTransform.XProperty,
                Tween(86, 0, 740, 0, new CubicEase { EasingMode = EasingMode.EaseOut }));
            SwoopFlapRotation.BeginAnimation(RotateTransform.AngleProperty,
                Tween(-42, 0, 650, 440, settle));
            SwoopTrail.BeginAnimation(OpacityProperty,
                Tween(0.75, 0, 690, 180, settle));
        }
        else if (_mailbox)
        {
            MailboxRise.BeginAnimation(TranslateTransform.YProperty,
                Tween(13, 0, 530, 0, settle));
            MailboxFlagAngle.BeginAnimation(RotateTransform.AngleProperty,
                Tween(-65, 0, 800, 460,
                    new BackEase { Amplitude = 0.15, EasingMode = EasingMode.EaseOut }));
        }
        else if (_custom)
        {
            // The GIF supplies its own motion.
        }
        else if (_elegant)
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
