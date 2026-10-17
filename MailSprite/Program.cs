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

    public TrayApp(System.Windows.Application app)
    {
        _app = app;
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
        menu.Items.Add("MailSprite 0.11 - Stay visible").Enabled = false;
        menu.Items.Add(new Forms.ToolStripSeparator());
        // One test command always previews the active animation choice.
        menu.Items.Add("Test current selection", null,
            (_, _) => ShowPreview(_settings.Character));
        menu.Items.Add("Clear preview", null, (_, _) => _preview?.Clear());
        menu.Items.Add(new Forms.ToolStripSeparator());

        var character = new Forms.ToolStripMenuItem("Default animation");
        character.DropDownItems.Add(new Forms.ToolStripMenuItem("Minimal alert (recommended)", null, (_, _) => SetCharacter("Minimal"))
        { Checked = _settings.Character == "Minimal" });
        character.DropDownItems.Add(new Forms.ToolStripMenuItem("Custom animated GIF", null, (_, _) => SetCharacter("Custom"))
        { Checked = _settings.Character == "Custom", Enabled = File.Exists(_settings.CustomGifPath) });
        character.DropDownItems.Add(new Forms.ToolStripMenuItem("Envelope Swoop (original)", null, (_, _) => SetCharacter("Swoop"))
        { Checked = _settings.Character == "Swoop" });
        character.DropDownItems.Add(new Forms.ToolStripMenuItem("Mailbox Flag (original)", null, (_, _) => SetCharacter("Mailbox"))
        { Checked = _settings.Character == "Mailbox" });


        menu.Items.Add(character);
        menu.Items.Add("Choose custom animated GIF...", null, (_, _) => ChooseGif());

        var duration = new Forms.ToolStripMenuItem("Timed alert duration (if auto-hide enabled)");
        foreach (int seconds in new[] { 4, 7, 10 })
        {
            int current = seconds;
            duration.DropDownItems.Add(new Forms.ToolStripMenuItem(current + " seconds", null, (_, _) =>
            {
                _settings.Seconds = current;
                SaveAndRefresh();
            }) { Checked = _settings.Seconds == current });
        }
        menu.Items.Add(duration);

        var placement = new Forms.ToolStripMenuItem("Animation position");
        foreach (var choice in new (string Value, string Label)[]
        {
            ("LeftOfNotification", "Left of Windows notifications (recommended)"),
            ("BottomLeft", "Bottom left"),
            ("TopLeft", "Top left"),
            ("TopRight", "Top right")
        })
        {
            var position = choice.Value;
            placement.DropDownItems.Add(new Forms.ToolStripMenuItem(choice.Label, null, (_, _) =>
            {
                _settings.Placement = position;
                SaveAndRefresh();
                if (_sprite?.IsVisible == true)
                    RefreshWaitingAlert();
                else ShowPreview(_settings.Character);
            }) { Checked = _settings.Placement == position });
        }
        menu.Items.Add(placement);

        menu.Items.Add(new Forms.ToolStripMenuItem("Keep visible until cleared", null, (_, _) =>
        {
            _settings.KeepVisibleUntilCleared = !_settings.KeepVisibleUntilCleared;
            _settings.Save();
            RefreshWaitingAlert();
            BuildMenu();
        }) { Checked = _settings.KeepVisibleUntilCleared });
        menu.Items.Add("Clear waiting mail alert", null, (_, _) => ClearWaitingAlert());
        menu.Items.Add(new Forms.ToolStripSeparator());
        var soundMenu = new Forms.ToolStripMenuItem("Notification sound");
        foreach (var item in new (string Value, string Name)[]
        {
            ("Off", "Off"),
            ("SoftChime", "Soft chime (default)"),
            ("PaperFlick", "Paper flick"),
            ("ClearBell", "Clear bell")
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
            (_, _) => SetSpeechMode("Off")) { Checked = _settings.AnnouncementMode == "Off" });
        speechMenu.DropDownItems.Add(new Forms.ToolStripMenuItem("Windows system voice", null,
            (_, _) => SetSpeechMode("Windows")) { Checked = _settings.AnnouncementMode == "Windows" });
        speechMenu.DropDownItems.Add(new Forms.ToolStripMenuItem("My recorded WAV", null,
            (_, _) => SetSpeechMode("Wav")) { Checked = _settings.AnnouncementMode == "Wav",
            Enabled = File.Exists(_settings.RecordedWavPath) });
        speechMenu.DropDownItems.Add("Choose WAV announcement...", null,
            (_, _) => ChooseWav());
        menu.Items.Add(speechMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Enable Windows notification listener (permission)", null,
            async (_, _) => await EnableWindowsNotifications());
        menu.Items.Add("Copy notification listener diagnostic report", null,
            (_, _) => CopyDiagnostics());
        menu.Items.Add("Stop notification listener", null, (_, _) =>
        {
            _settings.AutoMonitor = false;
            _settings.Save();
            StopWatcher();
            System.Windows.MessageBox.Show("Notification monitoring stopped.", "MailSprite");
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Start with Windows", null, (_, _) =>
        {
            bool newValue = !_settings.StartWithWindows;
            if (SetStartup(newValue))
            {
                _settings.StartWithWindows = newValue;
                SaveAndRefresh();
            }
        }) { Checked = _settings.StartWithWindows });

        menu.Items.Add("About notification access", null, (_, _) =>
            System.Windows.MessageBox.Show(
                "MailSprite 0.11\n\nUses the Windows notification listener API, not taskbar badges or email logins." +
                "\n\nWindows may require an MSIX package identity for notification access. If access is " +
                "denied or notifications remain invisible, copy the diagnostic report and we'll package the app." +
                "\n\nMailSprite checks notification origin only. It does not read subjects, senders or bodies.",
                "MailSprite", MessageBoxButton.OK, MessageBoxImage.Information));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit MailSprite", null, (_, _) => _app.Shutdown());
        var old = _icon.ContextMenuStrip;
        _icon.ContextMenuStrip = menu;
        old?.Dispose();
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
                if (chimeEnabled) BuiltInSounds.PlaySync(chime);
                if (announcement != "Off")
                {
                    if (chimeEnabled) System.Threading.Thread.Sleep(160);
                    PlayAnnouncementSync(announcement, wavPath);
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

    private async Task EnableWindowsNotifications(bool silent = false)
    {
        if (_notificationWatcher?.IsWatching == true)
        {
            System.Windows.MessageBox.Show("Windows notification listener is already running.", "MailSprite");
            return;
        }
        StopWatcher();
        var watcher = new WindowsNotificationWatcher(_app.Dispatcher, () =>
        {
            if ((DateTime.UtcNow - _lastNotice).TotalSeconds < 10) return;
            _lastNotice = DateTime.UtcNow;
            IncomingMail();
        });
        _notificationWatcher = watcher;
        bool enabled = await watcher.RequestAccessAndStartAsync();
        if (!enabled)
        {
            _settings.AutoMonitor = false;
            _settings.Save();
            if (silent)
            {
                _icon.ShowBalloonTip(5000, "MailSprite",
                    "Notification listener did not start. Enable it from the tray menu.",
                    Forms.ToolTipIcon.Warning);
                return;
            }
            System.Windows.MessageBox.Show(
                "Windows has not started the notification listener. No emails will trigger animations." +
                "\n\nPlease select 'Copy notification listener diagnostic report' and paste the result here." +
                "\n\nIf Windows requires an app package, we'll use a properly permissioned MSIX build.",
                "MailSprite Listener Setup");
        }
        else
        {
            _settings.AutoMonitor = true;
            _settings.Save();
            if (silent) return;
            System.Windows.MessageBox.Show(
                "Notification listener started. Send a NEW email after this dialog closes." +
                "\n\nIf you don't see the animation, copy the diagnostic report.",
                "MailSprite Listener");
        }
    }

    private void CopyDiagnostics()
    {
        try
        {
            string report = _notificationWatcher?.GetDiagnosticReport() ??
                "MailSprite Test 5: listener hasn't been enabled yet. Select " +
                "'Enable Windows notification listener (permission)' first.";
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
        StopWatcher();
        _sprite?.Close();
        _preview?.Close();
        _icon.Visible = false;
        _icon.Dispose();
        _mailSpriteIcon.Dispose();
    }
}
