using System.Diagnostics;
using System.Windows.Automation;

namespace MailSprite;

/// <summary>
/// Experimental hook: observes opened UI Automation windows and looks for
/// Mailbird-originated notification popups. NOT an inbox or toast-history API.
/// Not all Mailbird versions expose their popups as UIA windows.
/// </summary>
internal sealed class MailbirdWatcher : IDisposable
{
    private readonly AutomationEventHandler _handler;
    private readonly AutomationElement _root;
    private bool _disposed;
    private DateTime _lastMatch = DateTime.MinValue;
    private readonly Action _onAlert;

    public MailbirdWatcher(Action onAlert)
    {
        _onAlert = onAlert;
        _root = AutomationElement.RootElement;
        _handler = OnWindowOpened;
        Automation.AddAutomationEventHandler(
            WindowPattern.WindowOpenedEvent,
            _root,
            TreeScope.Children,
            _handler);
    }

    private void OnWindowOpened(object sender, AutomationEventArgs args)
    {
        if (_disposed || args.EventId != WindowPattern.WindowOpenedEvent) return;
        try
        {
            if (sender is not AutomationElement element) return;
            var name = element.Current.Name ?? "";
            var className = element.Current.ClassName ?? "";
            var pid = element.Current.ProcessId;
            var process = Process.GetProcessById(pid).ProcessName;
            bool fromMailbird = process.Contains("mailbird", StringComparison.OrdinalIgnoreCase);
            string summary = name;

            // Top-level Windows notifications may be hosted by Windows rather than
            // Mailbird. Examine only a handful of direct child accessibility names.
            var children = element.FindAll(TreeScope.Children, Condition.TrueCondition);
            for (int i = 0; i < Math.Min(children.Count, 12); i++)
            {
                summary += " " + (children[i].Current.Name ?? "");
            }

            bool mentionsMailbird = summary.Contains("mailbird", StringComparison.OrdinalIgnoreCase);
            bool looksLikePopup =
                className.Contains("toast", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("notification", StringComparison.OrdinalIgnoreCase) ||
                summary.Contains("new mail", StringComparison.OrdinalIgnoreCase) ||
                summary.Contains("new message", StringComparison.OrdinalIgnoreCase) ||
                summary.Contains("you have mail", StringComparison.OrdinalIgnoreCase) ||
                summary.Contains("unread message", StringComparison.OrdinalIgnoreCase);

            // Never fire merely because the main Mailbird window opened.
            if (!(fromMailbird || mentionsMailbird) || !looksLikePopup) return;
            if ((DateTime.UtcNow - _lastMatch).TotalSeconds < 12) return;
            _lastMatch = DateTime.UtcNow;
            _onAlert();
        }
        catch (Exception)
        {
            // Windows UI Automation elements can vanish while being inspected.
            // Fail quietly; a missing notification must not crash MailSprite.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Automation.RemoveAutomationEventHandler(WindowPattern.WindowOpenedEvent, _root, _handler); }
        catch { /* Windows may be shutting down. */ }
    }
}
