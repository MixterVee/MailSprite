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
                if (classSuggestsAlert || smallTopLevel)
                    FoundPossibleAlert();
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
                if (mentionsMailbird) FoundPossibleAlert();
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

            if ((fromMailbird && suggestsAlert) || (fromShell && mentionsMailbird))
                FoundPossibleAlert();
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

    public string GetDiagnosticReport()
    {
        lock (_gate)
        {
            return "MailSprite 0.2 - detection diagnostics\n" +
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
