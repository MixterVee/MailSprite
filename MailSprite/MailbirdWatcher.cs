using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace MailSprite;

/// <summary>
/// Test 4: Conservative Windows popup watcher. Does not read Gmail/Outlook or
/// infer mail from the tray badge. Tracks *new* small lower-right Mailbird/
/// Mailbird-labelled shell windows, rather than any mailbird window-open event.
/// </summary>
internal sealed class MailbirdWatcher : IDisposable
{
    private const int GwOwner = 4;
    private const uint EventObjectShow = 0x8002;
    private const uint EventSystemAlert = 0x0002;
    private const uint WinEventOutOfContext = 0;

    private delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr hwnd,
        int objectId, int childId, uint threadId, uint time);
    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr state);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder buffer, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int capacity);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out WinRect bounds);
    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr module, WinEventCallback callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRect { public int Left, Top, Right, Bottom; }

    private readonly object _gate = new();
    private readonly Action _onAlert;
    private readonly DispatcherTimer _pollTimer;
    private readonly WinEventCallback _winEventCallback;
    private readonly List<IntPtr> _hooks = new();
    private readonly Dictionary<IntPtr, Candidate> _seen = new();
    private readonly Queue<string> _recent = new();
    private readonly DateTime _started = DateTime.Now;
    private readonly DateTime _warmUntil = DateTime.UtcNow.AddSeconds(4);

    private bool _disposed;
    private bool _primed;
    private bool _mailbirdWasPresent;
    private DateTime _lastAlert = DateTime.MinValue;
    private int _polls, _candidates, _alerts, _mailbirdWindows, _shellEvents;

    private sealed class Candidate
    {
        public DateTime FirstSeenUtc { get; init; }
        public bool AlreadyReported { get; set; }
        public bool WasPreexisting { get; init; }
    }

    public MailbirdWatcher(Action onAlert)
    {
        _onAlert = onAlert;
        _winEventCallback = OnWinEvent;
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        _pollTimer.Tick += (_, _) => PollWindows();
        try
        {
            AddHook(EventObjectShow);
            AddHook(EventSystemAlert);
            // Baseline all currently visible windows. Never trigger on startup.
            PollWindows();
            _primed = true;
            _pollTimer.Start();
            Record("Test 4 activated: waiting for actual newly appearing tray popups");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void AddHook(uint type)
    {
        var hook = SetWinEventHook(type, type, IntPtr.Zero, _winEventCallback,
            0, 0, WinEventOutOfContext);
        if (hook == IntPtr.Zero) throw new InvalidOperationException(
            "Unable to register Windows event hook " + type);
        _hooks.Add(hook);
    }

    private void OnWinEvent(IntPtr hook, uint type, IntPtr hwnd,
        int objectId, int childId, uint threadId, uint time)
    {
        if (_disposed || hwnd == IntPtr.Zero || objectId != 0) return;
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return;
            string process = GetProcessName(pid);
            if (!IsMailbird(process) && !IsShellHost(process)) return;
            lock (_gate)
            {
                if (IsShellHost(process)) _shellEvents++;
            }
            // Deliberately never fire an alert from this event. A Windows
            // show event cannot distinguish mail from application UI.
        }
        catch { }
    }

    private static string GetProcessName(uint pid)
    {
        try { return Process.GetProcessById((int)pid).ProcessName; }
        catch { return ""; }
    }

    private static bool IsMailbird(string process) =>
        process.Contains("mailbird", StringComparison.OrdinalIgnoreCase);

    private static bool IsShellHost(string process) =>
        process.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
        process.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
        process.Equals("NotificationUx", StringComparison.OrdinalIgnoreCase);

    private static string WindowClass(IntPtr hwnd)
    {
        var buffer = new StringBuilder(160);
        GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string WindowTitle(IntPtr hwnd)
    {
        var buffer = new StringBuilder(500);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static bool LooksLikeInboxOrMainWindow(string title)
    {
        // Exclude app windows that are obviously not an incoming-mail toast.
        return title.Contains("Settings", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Compose", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Inbox", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("Mailbird", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Preferences", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NearNotificationCorner(WinRect rect)
    {
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width < 165 || width > 800 || height < 65 || height > 415)
            return false;

        // Multi-monitor aware: taskbar/tray may be on another monitor.
        foreach (var screen in Forms.Screen.AllScreens)
        {
            var area = screen.WorkingArea;
            if (rect.Right >= area.Right - 210 &&
                rect.Right <= area.Right + 45 &&
                rect.Bottom >= area.Bottom - 245 &&
                rect.Bottom <= area.Bottom + 45 &&
                rect.Left >= area.Left - 30 &&
                rect.Top >= area.Top - 30)
                return true;
        }
        return false;
    }

    private static bool MailbirdNamedToast(IntPtr hwnd)
    {
        // Windows can own notifications. Limit scanning to the already-filtered
        // small lower-right window and discard all text after checking app name.
        try
        {
            var el = AutomationElement.FromHandle(hwnd);
            if ((el.Current.Name ?? "").Contains("Mailbird", StringComparison.OrdinalIgnoreCase))
                return true;
            var children = el.FindAll(TreeScope.Children, Condition.TrueCondition);
            for (int i = 0; i < Math.Min(children.Count, 20); i++)
            {
                if ((children[i].Current.Name ?? "").Contains("Mailbird",
                    StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }

    private static bool IsValidCandidate(IntPtr hwnd, out string provenance)
    {
        provenance = "";
        if (!IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var rect) ||
            !NearNotificationCorner(rect)) return false;

        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return false;
        string process = GetProcessName(pid);
        string cls = WindowClass(hwnd);

        if (IsMailbird(process))
        {
            var title = WindowTitle(hwnd);
            if (LooksLikeInboxOrMainWindow(title)) return false;

            // Explicitly filter main application windows (and their app-like
            // siblings). A secondary popup may be owned by the main window.
            bool hasOwner = GetWindow(hwnd, GwOwner) != IntPtr.Zero;
            bool likelyPopupClass = cls.Contains("popup", StringComparison.OrdinalIgnoreCase) ||
                cls.Contains("toast", StringComparison.OrdinalIgnoreCase) ||
                cls.Contains("notify", StringComparison.OrdinalIgnoreCase) ||
                cls.Contains("tooltip", StringComparison.OrdinalIgnoreCase);
            if (!hasOwner && !likelyPopupClass) return false;
            provenance = "Mailbird secondary popup, class=" + cls[..Math.Min(cls.Length, 65)];
            return true;
        }

        if (IsShellHost(process))
        {
            bool likelyShellToast = cls.Contains("toast", StringComparison.OrdinalIgnoreCase) ||
                cls.Contains("notification", StringComparison.OrdinalIgnoreCase) ||
                cls.Contains("DesktopWindowContentBridge", StringComparison.OrdinalIgnoreCase);
            if (!likelyShellToast || !MailbirdNamedToast(hwnd)) return false;
            provenance = "Windows-hosted Mailbird-labelled popup, class=" +
                         cls[..Math.Min(cls.Length, 65)];
            return true;
        }
        return false;
    }

    private void PollWindows()
    {
        if (_disposed) return;
        _polls++;
        var now = DateTime.UtcNow;
        var visible = new HashSet<IntPtr>();
        bool sawMailbird = false;

        EnumWindows((hwnd, _) =>
        {
            if (_disposed) return false;
            try
            {
                if (!IsWindowVisible(hwnd)) return true;
                GetWindowThreadProcessId(hwnd, out uint pid);
                string process = GetProcessName(pid);
                if (IsMailbird(process))
                {
                    sawMailbird = true;
                    _mailbirdWindows++;
                }
                if (!IsMailbird(process) && !IsShellHost(process)) return true;

                if (!IsValidCandidate(hwnd, out string provenance)) return true;
                visible.Add(hwnd);
                if (!_seen.TryGetValue(hwnd, out var entry))
                {
                    entry = new Candidate
                    {
                        FirstSeenUtc = now,
                        WasPreexisting = !_primed || now < _warmUntil
                    };
                    _seen[hwnd] = entry;
                    _candidates++;
                    Record("Observed " + provenance +
                           (entry.WasPreexisting ? " (baseline/initializing)" : ""));
                }

                // Toast must be visible for at least one extra scan and arrive
                // AFTER the initial baseline. Cooldown handles bursty emails.
                if (!entry.AlreadyReported && !entry.WasPreexisting &&
                    (now - entry.FirstSeenUtc).TotalMilliseconds >= 550 &&
                    (now - _lastAlert).TotalSeconds >= 12)
                {
                    entry.AlreadyReported = true;
                    _lastAlert = now;
                    _alerts++;
                    Record("NEW MAIL-LIKE POPUP", provenance + " -> sprite");
                    _onAlert();
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        if (sawMailbird && !_mailbirdWasPresent) Record("Mailbird process was observed");
        _mailbirdWasPresent = sawMailbird;

        // Remove vanished candidates. A new pop-up at a reused HWND can then
        // be treated as a fresh event, but not a persistent background window.
        foreach (var oldHandle in _seen.Keys.Where(handle => !visible.Contains(handle)).ToList())
            _seen.Remove(oldHandle);
    }

    private void Record(string what)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _recent.Enqueue(DateTime.Now.ToString("HH:mm:ss") + " " + what);
            while (_recent.Count > 50) _recent.Dequeue();
        }
    }

    /// <summary>
    /// On-demand, READ-ONLY check of the Windows taskbar accessibility tree.
    /// We do not record element labels (which could contain account names).
    /// The blue unread number drawn inside a tray icon may not be exposed
    /// through UI Automation at all. This is a probe, not an alert trigger.
    /// </summary>
    public static string ProbeTaskbarBadge()
    {
        int nodes = 0, mailbirdMatches = 0, numericLabels = 0;
        var counts = new List<int>();
        var errors = new List<string>();
        try
        {
            var root = AutomationElement.RootElement;
            var windows = root.FindAll(TreeScope.Children, Condition.TrueCondition);
            for (int i = 0; i < windows.Count; i++)
            {
                var window = windows[i];
                string classname;
                try { classname = window.Current.ClassName ?? ""; }
                catch { continue; }
                if (!classname.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase)
                    && !classname.Equals("NotifyIconOverflowWindow", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Only search taskbar / overflow, never arbitrary app windows.
                var descendants = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                for (int j = 0; j < Math.Min(descendants.Count, 1800); j++)
                {
                    nodes++;
                    string label;
                    try { label = descendants[j].Current.Name ?? ""; }
                    catch { continue; }
                    if (!label.Contains("mailbird", StringComparison.OrdinalIgnoreCase)) continue;
                    mailbirdMatches++;

                    var match = System.Text.RegularExpressions.Regex.Match(label,
                        @"(?i)\b(?<num>\d{1,5})\s*(?:unread|new)\s*(?:e-?mails?|messages?)\b");
                    if (!match.Success)
                        match = System.Text.RegularExpressions.Regex.Match(label,
                            @"(?i)\b(?:unread|new)\s*(?:e-?mails?|messages?)\s*[:(-]?\s*(?<num>\d{1,5})\b");
                    if (match.Success && int.TryParse(match.Groups["num"].Value, out int number))
                    {
                        numericLabels++;
                        counts.Add(number);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add(ex.GetType().Name);
        }

        string result =
            "MailSprite 0.3 taskbar unread badge probe\n" +
            "Taskbar accessibility elements checked: " + nodes + "\n" +
            "Elements identifying themselves as Mailbird: " + mailbirdMatches + "\n" +
            "Labels including a parseable unread number: " + numericLabels + "\n" +
            "Unread counts found in accessibility labels: " +
            (counts.Count == 0 ? "none" : string.Join(", ", counts)) + "\n";
        if (errors.Count > 0)
            result += "Probe error types: " + string.Join(", ", errors) + "\n";
        result += "\nNo email text or sender/account labels are collected. " +
                  "If no number was found, the number may only be painted as an icon.";
        return result;
    }


    public string GetDiagnosticReport()
    {
        lock (_gate)
        {
            return "MailSprite 0.4 - Mailbird tray popup detector\n" +
                "Started: " + _started.ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                "Polling rounds: " + _polls + "\n" +
                "Mailbird windows encountered during polling: " + _mailbirdWindows + "\n" +
                "Windows shell show/alert events (diagnostic only): " + _shellEvents + "\n" +
                "Small lower-right candidate windows seen: " + _candidates + "\n" +
                "Notifications triggering an animation: " + _alerts + "\n\n" +
                "Recent (NO email text, subject, or sender):\n" +
                string.Join("\n", _recent) + "\n\n" +
                "If candidates stay at zero when mail arrives, Mailbird may not " +
                "show a separate popup, or Windows may expose it differently.";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Stop();
        foreach (var hook in _hooks) if (hook != IntPtr.Zero) UnhookWinEvent(hook);
        _hooks.Clear();
        _seen.Clear();
    }
}
