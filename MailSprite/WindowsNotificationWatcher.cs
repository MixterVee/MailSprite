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
    private readonly Action _onMailbirdToast;
    private readonly DispatcherTimer _pollTimer;
    private readonly HashSet<string> _seen = new();
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
    private int _totalMailbirdMatches;
    private int _newMailbird;
    private int _errors;
    private string _access = "Not yet requested";
    private string _identity = "Not yet checked";
    private string _eventSubscription = "Not attempted";

    public WindowsNotificationWatcher(Dispatcher dispatcher, Action onMailbirdToast)
    {
        _dispatcher = dispatcher;
        _onMailbirdToast = onMailbirdToast;
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(650)
        };
        _pollTimer.Tick += async (_, _) => await PollAsync();
        Record("Watcher constructed. User permission required.");
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

                if (!displayName.Contains("mailbird", StringComparison.OrdinalIgnoreCase) &&
                    !appId.Contains("mailbird", StringComparison.OrdinalIgnoreCase))
                    continue;

                _totalMailbirdMatches++;
                string key = appId + "#" + notification.Id + "@" +
                    notification.CreationTime.ToUnixTimeMilliseconds();
                currentlyActive.Add(key);
                if (!_seen.Add(key)) continue;

                _newMailbird++;
                if (!_initialized)
                {
                    Record("Existing Mailbird notification detected (baseline; ignored).");
                    continue;
                }

                Record("NEW Mailbird notification detected by Windows listener.");
                if ((DateTime.UtcNow - _lastAlertUtc).TotalSeconds >= 10)
                {
                    _lastAlertUtc = DateTime.UtcNow;
                    _onMailbirdToast();
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

    private void Record(string line)
    {
        _recent.Enqueue(DateTime.Now.ToString("HH:mm:ss") + " " + line);
        while (_recent.Count > 45) _recent.Dequeue();
    }

    public string GetDiagnosticReport()
    {
        return "MailSprite Test 5 - Windows notification listener\n" +
            "Access status: " + _access + "\n" +
            "App packaging: " + _identity + "\n" +
            "NotificationChanged: " + _eventSubscription + "\n" +
            "Monitoring active: " + _watching + "\n" +
            "Polling rounds: " + _polls + "\n" +
            "Current Windows notifications visible to listener: " + _visibleNotifications + "\n" +
            "Mailbird notifications observed in scans: " + _totalMailbirdMatches + "\n" +
            "Unique Mailbird notifications since starting: " + _newMailbird + "\n" +
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
