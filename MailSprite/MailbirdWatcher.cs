using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace MailSprite;

/// <summary>
/// Diagnostics-first Mailbird popup detector. Observes native Windows show/alert
/// events plus UI Automation top-level window-open events. Does not read mail.
/// Mailbird can draw notifications inside an existing window, in which case
/// neither hook is guaranteed to detect them.
/// </summary>
internal sealed class MailbirdWatcher : IDisposable
{
    private const uint EventSystemAlert = 0x0002;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectNameChange = 0x800C;
    private const int ObjIdWindow = 0;
    private const uint WineventOutOfContext = 0x0000;
    private const uint GaRoot = 2;

    private delegate void WinEventCallback(
        IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint eventThread, uint eventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr module, WinEventCallback callback, uint processId,
        uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rectangle);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    private readonly object _gate = new();
    private readonly Action _onAlert;
    private readonly AutomationElement _root;
    private readonly AutomationEventHandler _uiaHandler;
    private readonly WinEventCallback _nativeCallback;
    private readonly List<IntPtr> _hooks = new();
    private readonly Queue<string> _recent = new();
    private readonly Dictionary<string, DateTime> _logThrottle = new();
    private readonly DateTime _start = DateTime.Now;

    private bool _disposed;
    private DateTime _lastMatch = DateTime.MinValue;
    private int _mailbirdEvents, _shellEvents, _uiaEvents, _matches;

    public MailbirdWatcher(Action onAlert)
    {
        _onAlert = onAlert;
        _root = AutomationElement.RootElement;
        _uiaHandler = OnWindowOpened;
        _nativeCallback = OnNativeEvent; // Retain callback delegate so GC cannot collect it.

        try
        {
            Automation.AddAutomationEventHandler(
                WindowPattern.WindowOpenedEvent, _root, TreeScope.Children, _uiaHandler);
            AddHook(EventSystemAlert);
            AddHook(EventObjectShow);
            AddHook(EventObjectNameChange);
            Record("Hooks started", "monitoring Mailbird and Windows notification hosts");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void AddHook(uint eventCode)
    {
        var hook = SetWinEventHook(eventCode, eventCode, IntPtr.Zero,
            _nativeCallback, 0, 0, WineventOutOfContext);
        if (hook == IntPtr.Zero)
            throw new InvalidOperationException("Windows event hook failed: " + eventCode);
        _hooks.Add(hook);
    }

    private static string ProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch { return "<exited>"; }
    }

    private static bool IsMailbird(string process) =>
        process.Contains("mailbird", StringComparison.OrdinalIgnoreCase);

    private static bool IsShell(string process) =>
        process.Contains("shellexperiencehost", StringComparison.OrdinalIgnoreCase) ||
        process.Contains("explorer", StringComparison.OrdinalIgnoreCase);

    private void OnNativeEvent(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (_disposed || hwnd == IntPtr.Zero || idObject != ObjIdWindow) return;
        try
        {
            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return;
            string process = ProcessName((int)pid);
            bool fromMailbird = IsMailbird(process);
            bool fromShell = IsShell(process);
            if (!fromMailbird && !fromShell) return;

            string category = eventType switch
            {
                EventSystemAlert => "system alert",
                EventObjectShow => "window show",
                _ => "name change"
            };

            var classBuffer = new StringBuilder(260);
            GetClassName(hwnd, classBuffer, classBuffer.Capacity);
            string className = classBuffer.ToString();
            bool classSuggestsAlert =
                className.Contains("toast", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("notification", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("alert", StringComparison.OrdinalIgnoreCase);

            if (fromMailbird)
            {
                lock (_gate) _mailbirdEvents++;
                bool smallTopLevel = false;
                if (eventType == EventObjectShow &&
                    GetAncestor(hwnd, GaRoot) == hwnd && GetWindowRect(hwnd, out Rect r))
                {
                    int width = r.Right - r.Left;
                    int height = r.Bottom - r.Top;
                    smallTopLevel = width >= 175 && width <= 850 &&
                                    height >= 55 && height <= 390;
                }

                Record(category, "Mailbird class=" + ScrubClass(className) +
                    (smallTopLevel ? " small top-level window" : ""));
                // Diagnostic only: a small Mailbird window can be settings,
                // account switching, splash screens, or any normal UI popup.
                // Never infer a new email from window size or class alone.
                // Confirmed email detection will require a dedicated source.
            }
            else if (eventType == EventObjectShow || eventType == EventSystemAlert)
            {
                lock (_gate) _shellEvents++;
                // Windows owns its toast windows. Check only accessible labels
                // for the literal app name, never retain email or sender text.
                bool mentionsMailbird = false;
                if (classSuggestsAlert || eventType == EventSystemAlert)
                    mentionsMailbird = AccessibleNamesMentionMailbird(hwnd);

                Record(category, "Windows shell class=" + ScrubClass(className) +
                    (mentionsMailbird ? " Mailbird-label detected" : ""));
                // Even shell-hosted popups with Mailbird branding can be
                // regular menus, taskbar previews or app status windows.
                // Keep as diagnostic evidence, never fire automatically.
            }
        }
        catch
        {
            // Accessibility elements may disappear as notifications animate away.
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    private static bool AccessibleNamesMentionMailbird(IntPtr hwnd)
    {
        try
        {
            var element = AutomationElement.FromHandle(hwnd);
            if ((element.Current.Name ?? "").Contains("mailbird", StringComparison.OrdinalIgnoreCase))
                return true;
            var children = element.FindAll(TreeScope.Children, Condition.TrueCondition);
            for (int i = 0; i < Math.Min(children.Count, 18); i++)
                if ((children[i].Current.Name ?? "").Contains("mailbird", StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        catch { }
        return false;
    }

    private void OnWindowOpened(object sender, AutomationEventArgs args)
    {
        if (_disposed) return;
        try
        {
            if (sender is not AutomationElement element) return;
            string process = ProcessName(element.Current.ProcessId);
            string className = element.Current.ClassName ?? "";
            string name = element.Current.Name ?? "";
            bool fromMailbird = IsMailbird(process);
            bool fromShell = IsShell(process);
            if (!fromMailbird && !fromShell) return;

            lock (_gate) _uiaEvents++;
            bool mentionsMailbird = name.Contains("mailbird", StringComparison.OrdinalIgnoreCase);
            if (!mentionsMailbird && fromShell)
            {
                var children = element.FindAll(TreeScope.Children, Condition.TrueCondition);
                for (int i = 0; i < Math.Min(children.Count, 18); i++)
                    mentionsMailbird |= (children[i].Current.Name ?? "")
                        .Contains("mailbird", StringComparison.OrdinalIgnoreCase);
            }

            bool suggestsAlert = name.Contains("new mail", StringComparison.OrdinalIgnoreCase)
                || name.Contains("new message", StringComparison.OrdinalIgnoreCase)
                || className.Contains("toast", StringComparison.OrdinalIgnoreCase)
                || className.Contains("notification", StringComparison.OrdinalIgnoreCase);

            Record("UIA window open", (fromMailbird ? "Mailbird" : "Windows shell") +
                " class=" + ScrubClass(className) +
                (mentionsMailbird ? " Mailbird-label detected" : ""));

            // Window-open events alone do not prove a new message arrived.
            // We intentionally do not fire an animation here.
        }
        catch { }
    }

    private static string ScrubClass(string cls)
    {
        // Class names are not message content; bound length for easy sharing.
        if (cls.Length > 70) return cls[..70];
        return cls.Replace("\r", "").Replace("\n", "");
    }

    private void FoundPossibleAlert()
    {
        bool fire;
        lock (_gate)
        {
            fire = !_disposed && (DateTime.UtcNow - _lastMatch).TotalSeconds >= 12;
            if (fire)
            {
                _lastMatch = DateTime.UtcNow;
                _matches++;
            }
        }
        if (fire)
        {
            Record("POSSIBLE EMAIL ALERT", "animation requested");
            _onAlert();
        }
    }

    private void Record(string eventName, string description)
    {
        lock (_gate)
        {
            if (_disposed) return;
            string key = eventName + ":" + description;
            var now = DateTime.UtcNow;
            if (_logThrottle.TryGetValue(key, out var last) &&
                (now - last).TotalSeconds < 2) return;
            _logThrottle[key] = now;
            if (_logThrottle.Count > 200) _logThrottle.Clear();

            _recent.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " +
                eventName + " — " + description);
            while (_recent.Count > 55) _recent.Dequeue();
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
            return "MailSprite 0.3 - detection diagnostics\n" +
                "Started: " + _start.ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                "Mailbird native window events: " + _mailbirdEvents + "\n" +
                "Windows shell candidate events: " + _shellEvents + "\n" +
                "UI Automation window opens: " + _uiaEvents + "\n" +
                "Possible alerts detected: " + _matches + "\n\n" +
                "Recent events (window class names only; NO emails, subjects, or senders):\n" +
                string.Join("\n", _recent);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var hook in _hooks) if (hook != IntPtr.Zero) UnhookWinEvent(hook);
        _hooks.Clear();
        try { Automation.RemoveAutomationEventHandler(WindowPattern.WindowOpenedEvent, _root, _uiaHandler); }
        catch { }
    }
}
