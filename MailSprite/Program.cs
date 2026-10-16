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
    public bool WatchMailbird { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public string Character { get; set; } = "Butler";
    public int Seconds { get; set; } = 7;

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
    private readonly MailSpriteSettings _settings = MailSpriteSettings.Load();
    private MailbirdWatcher? _watcher;
    private SpriteWindow? _sprite;
    private DateTime _lastNotice = DateTime.MinValue;
    private bool _disposed;

    public TrayApp(System.Windows.Application app)
    {
        _app = app;
        _icon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Information,
            Text = "MailSprite - animated email notifier",
            Visible = true
        };
        _icon.DoubleClick += (_, _) => ShowSprite(_settings.Character);
        BuildMenu();
        // Migration to 0.3: old detection rules caused repeated false alerts.
        // Never automatically re-enable the watcher from 0.1/0.2 settings.
        // User can opt in explicitly for diagnostics.
        if (_settings.WatchMailbird)
        {
            _settings.WatchMailbird = false;
            _settings.Save();
        }
    }

    private void BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("MailSprite 0.3 - Preview").Enabled = false;
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Test: Waving Butler", null, (_, _) => ShowSprite("Butler"));
        menu.Items.Add("Test: Flying Envelope", null, (_, _) => ShowSprite("Envelope"));
        menu.Items.Add(new Forms.ToolStripSeparator());

        var character = new Forms.ToolStripMenuItem("Default animation");
        character.DropDownItems.Add(new Forms.ToolStripMenuItem("Waving Butler", null, (_, _) => SetCharacter("Butler"))
        { Checked = _settings.Character == "Butler" });
        character.DropDownItems.Add(new Forms.ToolStripMenuItem("Flying Envelope", null, (_, _) => SetCharacter("Envelope"))
        { Checked = _settings.Character == "Envelope" });
        menu.Items.Add(character);

        var duration = new Forms.ToolStripMenuItem("Animation duration");
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

        menu.Items.Add(new Forms.ToolStripMenuItem("Sound effect", null, (_, _) =>
        {
            _settings.Sound = !_settings.Sound;
            SaveAndRefresh();
        }) { Checked = _settings.Sound });

        menu.Items.Add(new Forms.ToolStripMenuItem("Speak: You've got mail!", null, (_, _) =>
        {
            _settings.Voice = !_settings.Voice;
            SaveAndRefresh();
        }) { Checked = _settings.Voice });

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Experimental: watch Mailbird pop-ups", null, (_, _) =>
        {
            _settings.WatchMailbird = !_settings.WatchMailbird;
            if (_settings.WatchMailbird) StartWatcher();
            else StopWatcher();
            SaveAndRefresh();
        }) { Checked = _settings.WatchMailbird });

        menu.Items.Add("Copy Mailbird diagnostic report", null, (_, _) => CopyDiagnostics());
        menu.Items.Add("Probe unread badge (copies report)", null, (_, _) =>
        {
            try
            {
                string result = MailbirdWatcher.ProbeTaskbarBadge();
                System.Windows.Clipboard.SetText(result);
                System.Windows.MessageBox.Show(
                    "Taskbar badge probe copied to clipboard. Paste it into our chat. " +
                    "The app does not collect sender names or message contents.",
                    "MailSprite Taskbar Badge Probe");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Could not probe badge: " + ex.Message,
                    "MailSprite Taskbar Badge Probe");
            }
        });
        menu.Items.Add(new Forms.ToolStripMenuItem("Start with Windows", null, (_, _) =>
        {
            bool newValue = !_settings.StartWithWindows;
            if (SetStartup(newValue))
            {
                _settings.StartWithWindows = newValue;
                SaveAndRefresh();
            }
        }) { Checked = _settings.StartWithWindows });

        menu.Items.Add("About / detection limitations", null, (_, _) =>
            System.Windows.MessageBox.Show(
                "MailSprite 0.3\n\nTest Butler and Test Envelope work without email access.\n\n" +
                "Mailbird detection is EXPERIMENTAL and observes Windows popup/show events. " +
                "It cannot reliably observe every type of Windows notification. " +
                "It may miss some new emails or occasionally trigger on unrelated Mailbird windows.\n\n" +
                "MailSprite does not read messages, connect to Gmail/Outlook, or request passwords.",
                "MailSprite", MessageBoxButton.OK, MessageBoxImage.Information));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit MailSprite", null, (_, _) => _app.Shutdown());
        var old = _icon.ContextMenuStrip;
        _icon.ContextMenuStrip = menu;
        old?.Dispose();
    }

    private void SetCharacter(string character)
    {
        _settings.Character = character;
        SaveAndRefresh();
    }

    private void SaveAndRefresh()
    {
        _settings.Save();
        BuildMenu();
    }

    private void ShowSprite(string kind)
    {
        if (_disposed) return;
        try
        {
            _sprite?.Close();
            var window = new SpriteWindow(kind, Math.Clamp(_settings.Seconds, 3, 20));
            _sprite = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_sprite, window)) _sprite = null;
            };
            window.Show();
            if (_settings.Sound) SystemSounds.Asterisk.Play();
            if (_settings.Voice)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        var type = Type.GetTypeFromProgID("SAPI.SpVoice");
                        if (type is null) return;
                        dynamic voice = Activator.CreateInstance(type)!;
                        voice.Speak("You've got mail!", 0);
                    }
                    catch { /* Speech is optional if SAPI is unavailable. */ }
                });
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Could not show animation: " + ex.Message, "MailSprite");
        }
    }

    private void StartWatcher()
    {
        if (_watcher is not null) return;
        try
        {
            _watcher = new MailbirdWatcher(() =>
            {
                _app.Dispatcher.BeginInvoke(() =>
                {
                    if ((DateTime.UtcNow - _lastNotice).TotalSeconds < 12) return;
                    _lastNotice = DateTime.UtcNow;
                    ShowSprite(_settings.Character);
                });
            });
        }
        catch (Exception ex)
        {
            _settings.WatchMailbird = false;
            _settings.Save();
            _icon.ShowBalloonTip(5000, "MailSprite", "Mailbird watcher could not start: " + ex.Message,
                Forms.ToolTipIcon.Warning);
        }
    }

    private void CopyDiagnostics()
    {
        try
        {
            string report = _watcher?.GetDiagnosticReport() ??
                "MailSprite detector is OFF. Right-click the tray icon and enable " +
                "'Experimental: watch Mailbird pop-ups' before sending a test email.";
            System.Windows.Clipboard.SetText(report);
            System.Windows.MessageBox.Show(
                "Diagnostic report copied to your clipboard. Paste it into our chat after " +
                "a test email arrives. MailSprite does not record your email subjects or senders.",
                "MailSprite Diagnostics");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Unable to copy diagnostic report: " + ex.Message,
                "MailSprite Diagnostics");
        }
    }

    private void StopWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
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
        _icon.Visible = false;
        _icon.Dispose();
    }
}
