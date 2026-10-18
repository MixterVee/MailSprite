using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace MailSprite;

/// <summary>
/// Windows notification service listener. This uses the native Windows toast
/// collection (same general family of APIs as notification-logging apps).
/// It never reads subjects, recipients or message text. Only the originating
/// app name/ID and notification identifier are examined.
/// </summary>
internal sealed class WindowsNotificationWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action _onMailToast;
    private readonly string _monitoredClient;
    private readonly string _clientName;
    private readonly bool _recoverStartup;
    private readonly DispatcherTimer _pollTimer;
    private readonly HashSet<string> _seen = new();
    // Avoid announcing the same toast multiple times if Windows notification
    // access is temporarily unavailable and the watcher is restarted.
    private static readonly HashSet<string> SeenThisProcess = new();
    private static readonly TimeSpan StartupCatchup = TimeSpan.FromMinutes(2);
    private int _startupRecovered;
    private int _startupOlderIgnored;
    private readonly Queue<string> _recent = new();
    private UserNotificationListener? _listener;
    private bool _disposed;
    private bool _polling;
    private bool _initialized;
    private bool _watching;
    private bool _subscribed;
    private DateTime _lastAlertUtc = DateTime.MinValue;
    private int _polls;
    private int _visibleNotifications;
    private int _totalSourceMatches;
    private int _newSourceNotifications;
    private int _errors;
    private string _access = "Not yet requested";
    private string _identity = "Not yet checked";
    private string _eventSubscription = "Not attempted";

    public WindowsNotificationWatcher(Dispatcher dispatcher, Action onMailToast,
        string monitoredClient, bool recoverStartup = true)
    {
        _dispatcher = dispatcher;
        _onMailToast = onMailToast;
        _monitoredClient = monitoredClient is "WinoMail" or "BlueMail"
            ? monitoredClient : "Mailbird";
        _clientName = _monitoredClient switch
        {
            "WinoMail" => "Wino Mail",
            "BlueMail" => "BlueMail Email",
            _ => "Mailbird"
        };
        _recoverStartup = recoverStartup;
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(650)
        };
        _pollTimer.Tick += async (_, _) => await PollAsync();
        Record("Watcher constructed for " + _clientName + ". User permission required.");
    }

    public bool IsWatching => _watching;

    // Run this on the UI thread: Windows requires it for the permissions dialog.
    public async Task<bool> RequestAccessAndStartAsync()
    {
        if (_disposed) return false;
        try
        {
            _listener = UserNotificationListener.Current;
            _identity = Windows.ApplicationModel.Package.Current?.Id?.FullName is string id
                ? "Packaged app identity present" : "No package identity";
        }
        catch (Exception ex)
        {
            _identity = "No usable package identity (" + ex.GetType().Name + ")";
        }

        try
        {
            _listener ??= UserNotificationListener.Current;
            // RequestAccessAsync is intentionally called from this UI context.
            var status = await _listener.RequestAccessAsync();
            _access = status.ToString();
            Record("Windows notification permission: " + _access);

            if (status != UserNotificationListenerAccessStatus.Allowed)
            {
                _watching = false;
                Record("Not enabled: Windows has not granted notification access.");
                return false;
            }

            // Take a baseline before announcing anything. Old notifications
            // cannot trigger the animation when MailSprite starts.
            await PollAsync();
            _initialized = true;

            try
            {
                _listener.NotificationChanged += NotificationChanged;
                _subscribed = true;
                _eventSubscription = "Subscribed to Windows NotificationChanged";
            }
            catch (Exception ex)
            {
                _subscribed = false;
                _eventSubscription = "Event unavailable (" + ex.GetType().Name + "); polling fallback";
            }
            Record(_eventSubscription);
            _watching = true;
            _pollTimer.Start();
            return true;
        }
        catch (Exception ex)
        {
            _errors++;
            _access = "Failed (" + ex.GetType().Name + ")";
            Record("Listener initialization failed: " + ex.GetType().Name +
                (ex is System.Runtime.InteropServices.COMException ce ? " HRESULT 0x" + ce.HResult.ToString("X8") : ""));
            _watching = false;
            return false;
        }
    }

    private void NotificationChanged(UserNotificationListener sender,
        UserNotificationChangedEventArgs args)
    {
        if (_disposed) return;
        try
        {
            _dispatcher.BeginInvoke(new Action(async () => await PollAsync()));
        }
        catch { }
    }

    private async Task PollAsync()
    {
        if (_disposed || _polling || _listener is null) return;
        _polling = true;
        try
        {
            _polls++;
            if (_listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed)
            {
                _access = "Permission not currently allowed";
                _watching = false;
                _pollTimer.Stop();
                Record("Notification access revoked or unavailable.");
                return;
            }

            var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            if (_disposed) return;

            _visibleNotifications = notifications.Count;
            var currentlyActive = new HashSet<string>();
            foreach (var notification in notifications)
            {
                // All filtering is by app name, not notification content.
                string displayName = "";
                string appId = "";
                try { displayName = notification.AppInfo.DisplayInfo.DisplayName ?? ""; } catch { }
                try { appId = notification.AppInfo.AppUserModelId ?? ""; } catch { }

                if (!MatchesSelectedClient(displayName, appId)) continue;

                _totalSourceMatches++;
                string key = appId + "#" + notification.Id + "@" +
                    notification.CreationTime.ToUnixTimeMilliseconds();
                currentlyActive.Add(key);
                if (!_seen.Add(key)) continue;
                if (!SeenThisProcess.Add(key)) continue;

                _newSourceNotifications++;
                if (!_initialized)
                {
                    // Windows may deliver a startup Mailbird toast *before*
                    // MailSprite's notification listener is ready. Recover
                    // only very recent toasts; older notifications from
                    // earlier sessions are still baseline-ignored.
                    var age = DateTimeOffset.UtcNow - notification.CreationTime;
                    if (!_recoverStartup || age < TimeSpan.Zero || age > StartupCatchup)
                    {
                        _startupOlderIgnored++;
                        Record("Existing " + _clientName + " notification (baseline; ignored).");
                        continue;
                    }

                    _startupRecovered++;
                    Record("Recent " + _clientName + " notification recovered during startup.");
                }
                else
                {
                    Record("NEW " + _clientName + " notification detected by Windows listener.");
                }
                if ((DateTime.UtcNow - _lastAlertUtc).TotalSeconds >= 10)
                {
                    _lastAlertUtc = DateTime.UtcNow;
                    _onMailToast();
                }
            }
            // Do not replay historical notices; bounds memory for long sessions.
            // ID plus time protects against Windows ID recycling.
            if (_seen.Count > 10000)
            {
                _seen.Clear();
                foreach (string key in currentlyActive) _seen.Add(key);
                Record("Old deduplication entries pruned.");
            }
        }
        catch (Exception ex)
        {
            _errors++;
            if (_errors < 6 || _errors % 20 == 0)
            {
                Record("Windows listener read failed: " + ex.GetType().Name +
                    (ex is System.Runtime.InteropServices.COMException ce
                        ? " HRESULT 0x" + ce.HResult.ToString("X8") : ""));
            }
        }
        finally { _polling = false; }
    }

    private bool MatchesSelectedClient(string displayName, string appId)
    {
        if (_monitoredClient == "BlueMail")
        {
            // Notification Logger identifies the Windows toast origin as
            // "BlueMail Email"; compact app ID variants are also accepted.
            // No access to notification subject, sender, or body is needed.
            return displayName.Contains("BlueMail Email", StringComparison.OrdinalIgnoreCase) ||
                displayName.Equals("BlueMail", StringComparison.OrdinalIgnoreCase) ||
                appId.Contains("BlueMail", StringComparison.OrdinalIgnoreCase) ||
                appId.Contains("Blue.Mail", StringComparison.OrdinalIgnoreCase);
        }

        if (_monitoredClient == "WinoMail")
        {
            // Notification Logger shows the Wino app's display name as
            // "Wino Mail". The app ID may use the compact spelling.
            return displayName.Contains("Wino Mail", StringComparison.OrdinalIgnoreCase) ||
                displayName.Contains("WinoMail", StringComparison.OrdinalIgnoreCase) ||
                appId.Contains("WinoMail", StringComparison.OrdinalIgnoreCase) ||
                appId.Contains("Wino.Mail", StringComparison.OrdinalIgnoreCase);
        }

        return displayName.Contains("mailbird", StringComparison.OrdinalIgnoreCase) ||
            appId.Contains("mailbird", StringComparison.OrdinalIgnoreCase);
    }

    private void Record(string line)
    {
        _recent.Enqueue(DateTime.Now.ToString("HH:mm:ss") + " " + line);
        while (_recent.Count > 45) _recent.Dequeue();
    }

    public string GetDiagnosticReport()
    {
        return "MailSprite Test 18 - Windows notification listener\n" +
            "Selected email client: " + _clientName + "\n" +
            "Access status: " + _access + "\n" +
            "App packaging: " + _identity + "\n" +
            "NotificationChanged: " + _eventSubscription + "\n" +
            "Monitoring active: " + _watching + "\n" +
            "Polling rounds: " + _polls + "\n" +
            "Current Windows notifications visible to listener: " + _visibleNotifications + "\n" +
            _clientName + " notifications observed in scans: " + _totalSourceMatches + "\n" +
            "Unique " + _clientName + " notifications since starting: " + _newSourceNotifications + "\n" +
            "Recent startup toasts recovered: " + _startupRecovered + "\n" +
            "Older startup toasts safely ignored: " + _startupOlderIgnored + "\n" +
            "Windows API errors: " + _errors + "\n\n" +
            "Recent events (NO mail content, addresses or subjects):\n" +
            string.Join("\n", _recent);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watching = false;
        _pollTimer.Stop();
        if (_listener is not null && _subscribed)
        {
            try { _listener.NotificationChanged -= NotificationChanged; } catch { }
        }
    }
}
