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
    private WindowsNotificationWatcher? _notificationWatcher;
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
        menu.Items.Add("MailSprite 0.5 - Windows notification listener").Enabled = false;
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
        menu.Items.Add("Enable Windows notification listener (permission)", null,
            async (_, _) => await EnableWindowsNotifications());
        menu.Items.Add("Copy notification listener diagnostic report", null,
            (_, _) => CopyDiagnostics());
        menu.Items.Add("Stop notification listener", null, (_, _) =>
        {
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
                "MailSprite 0.5\n\nUses the Windows notification listener API, not taskbar badges or email logins." +
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

    private async Task EnableWindowsNotifications()
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
            ShowSprite(_settings.Character);
        });
        _notificationWatcher = watcher;
        bool enabled = await watcher.RequestAccessAndStartAsync();
        if (!enabled)
        {
            System.Windows.MessageBox.Show(
                "Windows has not started the notification listener. No emails will trigger animations." +
                "\n\nPlease select 'Copy notification listener diagnostic report' and paste the result here." +
                "\n\nIf Windows requires an app package, we'll use a properly permissioned MSIX build.",
                "MailSprite Listener Setup");
        }
        else
        {
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
        _icon.Visible = false;
        _icon.Dispose();
    }
}
