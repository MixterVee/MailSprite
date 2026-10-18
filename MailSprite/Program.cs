using System.Diagnostics;
using System.IO;
using System.Media;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace MailSprite;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleton = new System.Threading.Mutex(true, @"Local\MailSprite-single-instance", out bool firstInstance);
        if (!firstInstance)
        {
            System.Windows.MessageBox.Show("MailSprite is already running in the system tray.", "MailSprite");
            return;
        }

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var trayApp = new TrayApp(app);
        app.Run();
    }
}

internal sealed class MailSpriteSettings
{
    public bool Sound { get; set; } = true;
    public bool Voice { get; set; } = false;
    public bool WatchMailbird { get; set; } = false; // Legacy setting; ignored as a safety measure.
    public bool StartWithWindows { get; set; } = false;
    public string Character { get; set; } = "Elegant";
    public int Seconds { get; set; } = 7;
    public string Placement { get; set; } = "LeftOfNotification";
    public bool KeepVisibleUntilCleared { get; set; } = true;
    public bool AutoMonitor { get; set; } = false;
    public int PendingAlerts { get; set; } = 0;
    public int SettingsVersion { get; set; } = 0;
    public string RecordedWavPath { get; set; } = "";
    public string CustomGifPath { get; set; } = "";
    public string AnnouncementMode { get; set; } = "Off";
    public string SoundStyle { get; set; } = "SoftChime";
    public string MonitoredClient { get; set; } = "Mailbird";

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MailSprite", "settings.json");

    public static MailSpriteSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<MailSpriteSettings>(File.ReadAllText(SettingsPath)) ?? new();
        }
        catch { /* Recover from a damaged settings file. */ }
        return new MailSpriteSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Cannot save settings: " + ex.Message, "MailSprite");
        }
    }
}

internal sealed class TrayApp : IDisposable
{
    private readonly System.Windows.Application _app;
    private readonly Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _mailSpriteIcon;
    private readonly MailSpriteSettings _settings = MailSpriteSettings.Load();
    private WindowsNotificationWatcher? _notificationWatcher;
    private SpriteWindow? _sprite;
    private SpriteWindow? _preview;
    private DateTime _lastNotice = DateTime.MinValue;
    private bool _disposed;
    // Serialize sounds and WAV/voice announcements so concurrent alerts
    // cannot overlap each other or reverse playback order.
    private readonly object _audioPlaybackLock = new();
    private readonly System.Windows.Threading.DispatcherTimer _startupListenerRetryTimer;
    private readonly DateTime _startedLocal = DateTime.Now;
    private int _startupListenerAttempts;
    private bool _startingListener;

    public TrayApp(System.Windows.Application app)
    {
        _app = app;
        _startupListenerRetryTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(12)
        };
        _startupListenerRetryTimer.Tick += async (_, _) =>
        {
            // Retry only during an automatic startup session, not after the
            // user deliberately selects "Stop notification listener".
            if (_disposed || !_settings.AutoMonitor ||
                _notificationWatcher?.IsWatching == true ||
                _startupListenerAttempts >= 12)
            {
                _startupListenerRetryTimer.Stop();
                return;
            }
            if (!_startingListener) await EnableWindowsNotifications(silent: true);
        };
        // Use the same user-selected closed-envelope artwork for both the
        // tray icon and the EXE's Windows taskbar/file icon.
        using (var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("MailSprite.Assets.MailSprite.ico")
            ?? throw new InvalidOperationException("The embedded MailSprite envelope icon is missing."))
        {
            _mailSpriteIcon = new System.Drawing.Icon(stream);
        }
        _icon = new Forms.NotifyIcon
        {
            Icon = _mailSpriteIcon,
            Text = "MailSprite - animated email notifier",
            Visible = true
        };
        _icon.DoubleClick += (_, _) => ShowPreview(_settings.Character);
        // Earlier versions only offered cartoon characters. Switch the
        // existing installation to the new restrained default once.
        if (_settings.SettingsVersion < 9)
        {
            if (_settings.Character is "Butler" or "Envelope" or "Elegant")
                _settings.Character = "Minimal";
            if (_settings.AnnouncementMode == "Windows")
                _settings.AnnouncementMode = "Off";
            _settings.SoundStyle = "SoftChime";
            _settings.SettingsVersion = 9;
            _settings.Save();
        }
        BuildMenu();
        _app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_settings.KeepVisibleUntilCleared && _settings.PendingAlerts > 0)
                ShowWaitingAlert(playSound: false);
            if (_settings.AutoMonitor)
                await EnableWindowsNotifications(silent: true);
        }));
        // Old false-positive window watchers never auto-start. Explicit
        // Windows permission is required for the new notification listener.
        if (_settings.WatchMailbird)
        {
            _settings.WatchMailbird = false;
            _settings.Save();
        }
    }

    private void BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("MailSprite 0.17").Enabled = false;
        menu.Items.Add(new Forms.ToolStripSeparator());

        // Two primary actions remain easy to reach.
        menu.Items.Add("Test current selection", null,
            (_, _) => ShowPreview(_settings.Character));
        menu.Items.Add("Clear alert / preview", null, (_, _) =>
        {
            _preview?.Clear();
            ClearWaitingAlert();
        });
        menu.Items.Add(new Forms.ToolStripSeparator());

        var animation = new Forms.ToolStripMenuItem("Animation");
        foreach (var item in new (string Value, string Name)[]
        {
            ("Minimal", "Minimal alert"),
            ("Swoop", "Envelope swoop"),
            ("Mailbox", "Mailbox flag"),
            ("Custom", "Custom animated GIF")
        })
        {
            var selected = item.Value;
            animation.DropDownItems.Add(new Forms.ToolStripMenuItem(item.Name, null,
                (_, _) => SetCharacter(selected))
            {
                Checked = _settings.Character == selected,
                Enabled = selected != "Custom" || File.Exists(_settings.CustomGifPath)
            });
        }
        animation.DropDownItems.Add(new Forms.ToolStripSeparator());
        animation.DropDownItems.Add("Choose custom GIF...", null,
            (_, _) => ChooseGif());

        // Placement belongs with animation, not as its own top-level menu.
        var placement = new Forms.ToolStripMenuItem("Position");
        foreach (var choice in new (string Value, string Label)[]
        {
            ("LeftOfNotification", "Left of Windows notification"),
            ("BottomLeft", "Bottom left"),
            ("TopLeft", "Top left"),
            ("TopRight", "Top right")
        })
        {
            var position = choice.Value;
            placement.DropDownItems.Add(new Forms.ToolStripMenuItem(choice.Label, null,
                (_, _) =>
                {
                    _settings.Placement = position;
                    SaveAndRefresh();
                    if (_sprite?.IsVisible == true)
                        RefreshWaitingAlert();
                    else ShowPreview(_settings.Character);
                })
            { Checked = _settings.Placement == position });
        }
        animation.DropDownItems.Add(placement);
        menu.Items.Add(animation);

        // One mutually exclusive choice: either persist or automatically hide
        // after a specified number of seconds.
        var duration = new Forms.ToolStripMenuItem("Alert duration");
        duration.DropDownItems.Add(new Forms.ToolStripMenuItem(
            "Keep visible until cleared", null, (_, _) => SetAlertDuration(null))
        { Checked = _settings.KeepVisibleUntilCleared });
        duration.DropDownItems.Add(new Forms.ToolStripSeparator());
        foreach (int seconds in new[] { 4, 7, 10 })
        {
            int value = seconds;
            duration.DropDownItems.Add(new Forms.ToolStripMenuItem(
                value + " seconds (auto-hide)", null, (_, _) => SetAlertDuration(value))
            { Checked = !_settings.KeepVisibleUntilCleared && _settings.Seconds == value });
        }
        menu.Items.Add(duration);

        var soundMenu = new Forms.ToolStripMenuItem("Notification sound");
        foreach (var item in new (string Value, string Name)[]
        {
            ("Off", "Off"),
            ("SoftChime", "Soft chime (default)"),
            ("PaperFlick", "Paper flick"),
            ("ClearBell", "Clear bell"),
            ("SlowClang", "Slow clanging bell (three dings)"),
            ("UserChime", "Chime (your recording)")
        })
        {
            string value = item.Value;
            soundMenu.DropDownItems.Add(new Forms.ToolStripMenuItem(item.Name, null, (_, _) =>
            {
                _settings.SoundStyle = value;
                _settings.Sound = value != "Off";
                SaveAndRefresh();
                if (_settings.Sound) BuiltInSounds.Play(value);
            }) { Checked = (_settings.Sound ? _settings.SoundStyle : "Off") == value });
        }
        menu.Items.Add(soundMenu);

        var speechMenu = new Forms.ToolStripMenuItem("Voice announcement");
        speechMenu.DropDownItems.Add(new Forms.ToolStripMenuItem("Off", null,
            (_, _) => SetSpeechMode("Off"))
        { Checked = _settings.AnnouncementMode == "Off" });
        speechMenu.DropDownItems.Add(new Forms.ToolStripMenuItem("Windows system voice", null,
            (_, _) => SetSpeechMode("Windows"))
        { Checked = _settings.AnnouncementMode == "Windows" });
        speechMenu.DropDownItems.Add(new Forms.ToolStripMenuItem("My recorded WAV", null,
            (_, _) => SetSpeechMode("Wav"))
        {
            Checked = _settings.AnnouncementMode == "Wav",
            Enabled = File.Exists(_settings.RecordedWavPath)
        });
        speechMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
        speechMenu.DropDownItems.Add("Choose WAV announcement...", null,
            (_, _) => ChooseWav());
        menu.Items.Add(speechMenu);

        menu.Items.Add(new Forms.ToolStripSeparator());

        // Monitor one chosen client; two clients accessing the same inbox
        // would otherwise produce duplicate sound and voice announcements.
        var clientLabel = _settings.MonitoredClient == "WinoMail" ? "Wino Mail" : "Mailbird";
        var monitor = new Forms.ToolStripMenuItem("Email monitoring (" + clientLabel + ")");
        var monitorStatus = new Forms.ToolStripMenuItem("") { Enabled = false };
        monitor.DropDownItems.Add(monitorStatus);
        monitor.DropDownOpening += (_, _) =>
            monitorStatus.Text = _notificationWatcher?.IsWatching == true
                ? "Status: Monitoring " + clientLabel
                : "Status: Not monitoring";
        var clientMenu = new Forms.ToolStripMenuItem("Email client");
        clientMenu.DropDownItems.Add(new Forms.ToolStripMenuItem("Wino Mail", null,
            async (_, _) => await SetMonitoredClientAsync("WinoMail"))
        { Checked = _settings.MonitoredClient == "WinoMail" });
        clientMenu.DropDownItems.Add(new Forms.ToolStripMenuItem("Mailbird", null,
            async (_, _) => await SetMonitoredClientAsync("Mailbird"))
        { Checked = _settings.MonitoredClient != "WinoMail" });
        monitor.DropDownItems.Add(clientMenu);
        monitor.DropDownItems.Add(new Forms.ToolStripSeparator());
        monitor.DropDownItems.Add("Start / allow notifications...", null,
            async (_, _) => await EnableWindowsNotifications());
        monitor.DropDownItems.Add("Stop monitoring", null, (_, _) =>
        {
            _startupListenerRetryTimer.Stop();
            _settings.AutoMonitor = false;
            _settings.Save();
            StopWatcher();
            System.Windows.MessageBox.Show("Notification monitoring stopped.", "MailSprite");
        });
        monitor.DropDownItems.Add("Copy diagnostic report", null,
            (_, _) => CopyDiagnostics());
        menu.Items.Add(monitor);

        var options = new Forms.ToolStripMenuItem("Options");
        options.DropDownItems.Add(new Forms.ToolStripMenuItem("Start with Windows", null,
            (_, _) =>
            {
                bool newValue = !_settings.StartWithWindows;
                if (SetStartup(newValue))
                {
                    _settings.StartWithWindows = newValue;
                    SaveAndRefresh();
                }
            })
        { Checked = _settings.StartWithWindows });
        options.DropDownItems.Add(new Forms.ToolStripSeparator());
        options.DropDownItems.Add("About MailSprite / notification access", null, (_, _) =>
            System.Windows.MessageBox.Show(
                "MailSprite 0.17\n\nMonitors notifications from the selected email client without reading message content." +
                "\n\nChoose Mailbird or Wino Mail under Email monitoring > Email client." +
                "\n\nWindows may require a packaged identity or explicit permission for notification access.",
                "MailSprite", MessageBoxButton.OK, MessageBoxImage.Information));
        menu.Items.Add(options);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit MailSprite", null, (_, _) => _app.Shutdown());

        var old = _icon.ContextMenuStrip;
        _icon.ContextMenuStrip = menu;
        old?.Dispose();
    }

    private void SetAlertDuration(int? seconds)
    {
        // The persistent and timed modes are mutually exclusive.
        _settings.KeepVisibleUntilCleared = !seconds.HasValue;
        if (seconds.HasValue) _settings.Seconds = seconds.Value;
        _settings.Save();

        // An already-visible alert should immediately use the new behavior.
        RefreshWaitingAlert();
        _preview?.Clear();
        BuildMenu();
    }

    private void SetSpeechMode(string mode)
    {
        _settings.AnnouncementMode = mode;
        SaveAndRefresh();
    }

    private void ChooseWav()
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a natural voice announcement (WAV)",
            Filter = "WAV recording (*.wav)|*.wav", CheckFileExists = true
        };
        if (picker.ShowDialog() != true) return;
        _settings.RecordedWavPath = picker.FileName;
        _settings.AnnouncementMode = "Wav";
        SaveAndRefresh();
        _ = Task.Run(() => PlayWav(picker.FileName));
    }

    private void ChooseGif()
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose your own animated GIF",
            Filter = "Animated GIF (*.gif)|*.gif", CheckFileExists = true
        };
        if (picker.ShowDialog() != true) return;
        if (new FileInfo(picker.FileName).Length > 35 * 1024 * 1024)
        {
            System.Windows.MessageBox.Show("Choose a GIF smaller than 35 MB.", "MailSprite");
            return;
        }
        _settings.CustomGifPath = picker.FileName;
        _settings.Character = "Custom";
        SaveAndRefresh();
        RefreshWaitingAlert();
        ShowPreview("Custom");
    }

    private static void PlayWav(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            using var player = new SoundPlayer(path);
            player.PlaySync();
        }
        catch { }
    }

    // Called on the same background audio worker after the notification chime.
    // Keep this synchronous: a second Task.Run would start the voice too early.
    private static void PlayAnnouncementSync(string mode, string wavPath)
    {
        if (mode == "Wav")
        {
            PlayWav(wavPath);
            return;
        }
        if (mode != "Windows") return;
        try
        {
            var type = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (type is null) return;
            dynamic voice = Activator.CreateInstance(type)!;
            voice.Speak("You've got mail!", 0);
        }
        catch { /* Voice is optional; it must not interrupt the alert. */ }
    }

    private void SetCharacter(string character)
    {
        _settings.Character = character;
        SaveAndRefresh();
        if (_sprite?.IsVisible == true)
            RefreshWaitingAlert();
    }

    private void SaveAndRefresh()
    {
        _settings.Save();
        BuildMenu();
    }

    private void PlaySound()
    {
        // Snapshot selections when the notification arrives, so changes made
        // in the tray menu during playback affect the NEXT alert only.
        bool chimeEnabled = _settings.Sound && _settings.SoundStyle != "Off";
        string chime = _settings.SoundStyle;
        string announcement = _settings.AnnouncementMode;
        string wavPath = _settings.RecordedWavPath;
        if (!chimeEnabled && announcement == "Off") return;

        _ = Task.Run(() =>
        {
            // Await the chime's entire WAV, then announce. A small natural
            // pause makes the two parts sound intentional, not simultaneous.
            lock (_audioPlaybackLock)
            {
                if (_disposed) return;
                if (announcement == "Wav")
                {
                    // One audio session, with a quiet device warm-up before
                    // the chime, 160 ms between clips, and a silent tail.
                    AudioSequence.Play(chimeEnabled ? chime : "Off", wavPath);
                }
                else
                {
                    if (chimeEnabled) AudioSequence.Play(chime, "");
                    if (announcement == "Windows")
                    {
                        if (chimeEnabled) System.Threading.Thread.Sleep(160);
                        PlayAnnouncementSync(announcement, wavPath);
                    }
                }
            }
        });
    }

    private void ShowPreview(string kind)
    {
        if (_disposed) return;
        _preview?.Close();
        if (kind == "Custom" && !File.Exists(_settings.CustomGifPath))
        {
            System.Windows.MessageBox.Show("Choose an animated GIF first.", "MailSprite");
            return;
        }
        var preview = new SpriteWindow(kind, 5, _settings.Placement,
            persistent: _settings.KeepVisibleUntilCleared, count: 1,
            gifPath: _settings.CustomGifPath, preview: true);
        _preview = preview;
        preview.Closed += (_, _) =>
        {
            if (ReferenceEquals(_preview, preview)) _preview = null;
        };
        preview.Show();
        PlaySound();
    }

    private void IncomingMail()
    {
        if (_disposed) return;
        // This counts detected MailSprite notifications, not inbox unread mail.
        _settings.PendingAlerts = Math.Clamp(_settings.PendingAlerts + 1, 1, 9999);
        _settings.Save();
        if (_settings.KeepVisibleUntilCleared && _sprite?.IsVisible == true)
        {
            _sprite.UpdateCount(_settings.PendingAlerts);
            PlaySound();
            return;
        }
        ShowWaitingAlert(playSound: true);
    }

    private void RefreshWaitingAlert()
    {
        // Detach the old window BEFORE closing it, so its Closed callback
        // cannot mistake a settings refresh for dismissing a timed alert.
        var previous = _sprite;
        _sprite = null;
        previous?.Close();
        if (_settings.PendingAlerts > 0)
            ShowWaitingAlert(playSound: false);
    }

    private void ShowWaitingAlert(bool playSound)
    {
        if (_disposed || _settings.PendingAlerts <= 0) return;
        var previous = _sprite;
        _sprite = null;
        previous?.Close();
        var alert = new SpriteWindow(_settings.Character,
            Math.Clamp(_settings.Seconds, 3, 20), _settings.Placement,
            persistent: _settings.KeepVisibleUntilCleared,
            count: _settings.PendingAlerts, gifPath: _settings.CustomGifPath);
        _sprite = alert;
        alert.ClearedByUser += () =>
        {
            if (ReferenceEquals(_sprite, alert))
            {
                _settings.PendingAlerts = 0;
                _settings.Save();
            }
        };
        alert.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_sprite, alert)) return;
            _sprite = null;
            if (!_settings.KeepVisibleUntilCleared)
            {
                _settings.PendingAlerts = 0;
                _settings.Save();
            }
        };
        alert.Show();
        if (playSound) PlaySound();
    }

    private void ClearWaitingAlert()
    {
        _settings.PendingAlerts = 0;
        _settings.Save();
        if (_sprite is { } existing)
        {
            _sprite = null;
            existing.Clear();
        }
    }

    private async Task SetMonitoredClientAsync(string client)
    {
        if (client != "Mailbird" && client != "WinoMail") return;
        if (_settings.MonitoredClient == client) return;

        _settings.MonitoredClient = client;
        _settings.Save();

        // Switching must not replay already-visible notifications.
        _startupListenerRetryTimer.Stop();
        StopWatcher();
        _startupListenerAttempts = 0;
        _lastNotice = DateTime.MinValue;
        BuildMenu();
        await EnableWindowsNotifications(silent: false, recoverStartup: false);
    }

    private async Task EnableWindowsNotifications(bool silent = false, bool recoverStartup = true)
    {
        if (_startingListener) return;
        if (_notificationWatcher?.IsWatching == true)
        {
            if (!silent)
                System.Windows.MessageBox.Show(
                    "Windows notification listener is already running.", "MailSprite");
            _startupListenerRetryTimer.Stop();
            return;
        }

        _startingListener = true;
        try
        {
            if (silent) _startupListenerAttempts++;
            StopWatcher();
            var watcher = new WindowsNotificationWatcher(_app.Dispatcher, () =>
            {
                if ((DateTime.UtcNow - _lastNotice).TotalSeconds < 10) return;
                _lastNotice = DateTime.UtcNow;
                IncomingMail();
            }, _settings.MonitoredClient, recoverStartup);
            _notificationWatcher = watcher;
            bool enabled = await watcher.RequestAccessAndStartAsync();
            if (_disposed || !ReferenceEquals(_notificationWatcher, watcher)) return;

            if (!enabled)
            {
                if (silent)
                {
                    // A notification service may not yet be available during
                    // Windows login. Preserve AutoMonitor and retry rather
                    // than permanently disabling notification detection.
                    if (_settings.AutoMonitor && _startupListenerAttempts < 12)
                        _startupListenerRetryTimer.Start();
                    else
                        _startupListenerRetryTimer.Stop();
                    return;
                }
                System.Windows.MessageBox.Show(
                    "Windows has not started the notification listener. No emails will trigger animations." +
                    "\n\nSelect 'Copy notification listener diagnostic report' for details.",
                    "MailSprite Listener Setup");
                return;
            }

            _startupListenerRetryTimer.Stop();
            _settings.AutoMonitor = true;
            _settings.Save();
            if (!silent)
                System.Windows.MessageBox.Show(
                    "Notification listener started. Send a NEW email after this dialog closes." +
                    "\n\nIf you don't see an animation, copy the diagnostic report.",
                    "MailSprite Listener");
        }
        finally
        {
            _startingListener = false;
        }
    }

    private void CopyDiagnostics()
    {
        try
        {
            string report = "MailSprite Test 17 - Startup status\n" +
                "MailSprite started: " + _startedLocal.ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                "Start with Windows setting: " + _settings.StartWithWindows + "\n" +
                "Selected email client: " + (_settings.MonitoredClient == "WinoMail" ? "Wino Mail" : "Mailbird") + "\n" +
                "Automatic notification monitoring: " + _settings.AutoMonitor + "\n" +
                "Notification listener active: " + (_notificationWatcher?.IsWatching == true) + "\n" +
                "Automatic startup listener attempts: " + _startupListenerAttempts + "\n\n" +
                (_notificationWatcher?.GetDiagnosticReport() ??
                "Listener hasn't been enabled. Select 'Enable Windows notification listener (permission)' first.");
            System.Windows.Clipboard.SetText(report);
            System.Windows.MessageBox.Show(
                "Privacy-safe diagnostic report copied. Paste it into our chat.",
                "MailSprite Diagnostics");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Could not copy report: " + ex.GetType().Name,
                "MailSprite Diagnostics");
        }
    }

    private void StopWatcher()
    {
        _notificationWatcher?.Dispose();
        _notificationWatcher = null;
    }

    private static bool SetStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)
                ?? throw new InvalidOperationException("Windows startup registry key was not found.");

            if (enabled)
            {
                string executable = Environment.ProcessPath ?? "";
                if (!executable.EndsWith("MailSprite.exe", StringComparison.OrdinalIgnoreCase))
                {
                    System.Windows.MessageBox.Show("Start-with-Windows is available from the published MailSprite.exe, not from dotnet run.", "MailSprite");
                    return false;
                }
                key.SetValue("MailSprite", "\"" + executable + "\"");
            }
            else key.DeleteValue("MailSprite", false);
            return true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Could not change startup setting: " + ex.Message, "MailSprite");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _startupListenerRetryTimer.Stop();
        StopWatcher();
        _sprite?.Close();
        _preview?.Close();
        _icon.Visible = false;
        _icon.Dispose();
        _mailSpriteIcon.Dispose();
    }
}
