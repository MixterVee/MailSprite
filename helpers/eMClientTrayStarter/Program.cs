using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows.Forms;

namespace EMClientTrayStarter;

internal static class Program
{
    private const string AppName = "eM Client Tray Starter";
    private const string StartupValue = "EMClientTrayStarter";
    private const string ExecutableName = "eMClientTrayStarter.exe";
    private const uint WmSysCommand = 0x0112;
    private const int ScMinimize = 0xF020;
    private const int GwOwner = 4;
    private const int SwMinimize = 6;
    private const int MinWidth = 390;
    private const int MinHeight = 220;

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "eMClientTrayStarter");

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Contains("--run", StringComparer.OrdinalIgnoreCase))
            {
                RunStartupHelper();
                return 0;
            }

            if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
            {
                Uninstall();
                return 0;
            }

            Install();
            return 0;
        }
        catch (Exception ex)
        {
            Log("Error: " + ex.GetType().Name + ": " + ex.Message);
            MessageBox.Show(
                "Could not set up eM Client Tray Starter.\n\n" + ex.Message,
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void Install()
    {
        DialogResult answer = MessageBox.Show(
            "Install a small startup helper?\n\n" +
            "When you sign in to Windows, it will wait for eM Client to open " +
            "and then request Minimize to tray. It will not close eM Client.\n\n" +
            "This adds an entry to your Windows user startup settings. " +
            "Administrator access is not required.\n\n" +
            "Make sure eM Client's own 'Run on system startup' and " +
            "'Minimize application to tray' options are enabled.\n\n" +
            "Install now?",
            AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);

        if (answer != DialogResult.Yes) return;

        string target = Path.Combine(Folder, ExecutableName);
        Directory.CreateDirectory(Folder);

        string source = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot find the helper executable.");

        if (!Path.GetFullPath(source).Equals(Path.GetFullPath(target),
                StringComparison.OrdinalIgnoreCase))
        {
            // Startup child normally finishes within 2 minutes. If an older
            // helper is still running, the update may need to be retried.
            File.Copy(source, target, overwrite: true);
        }

        using (RegistryKey run = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run")
            ?? throw new InvalidOperationException("Cannot open Windows startup settings."))
        {
            run.SetValue(StartupValue, "\"" + target + "\" --run",
                RegistryValueKind.String);
        }

        Log("Installed per-user startup entry.");

        // Demonstrate on the currently open eM Client; helper can wait for
        // eM Client to start if it is not running yet.
        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            Arguments = "--run",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Folder
        });

        MessageBox.Show(
            "Installed successfully.\n\n" +
            "The helper will start automatically when you sign in to Windows, " +
            "wait for eM Client to appear, and minimize it once to its tray icon.\n\n" +
            "In eM Client → Menu → Settings → General, leave these ON:\n" +
            "• Run on system startup\n" +
            "• Minimize application to tray\n\n" +
            "No WindowsApps files were changed.\n\n" +
            "To remove this helper, run the included Uninstall.cmd.",
            AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void RunStartupHelper()
    {
        using var single = new Mutex(initiallyOwned: true,
            @"Local\EMClientTrayStarterWorker", out bool first);
        if (!first) return;

        Log("Startup worker entered.");
        var deadline = DateTime.UtcNow.AddMinutes(3);

        // eM Client's Store installation launches MailClient.exe.
        // No version-specific WindowsApps directory is used.
        while (DateTime.UtcNow < deadline)
        {
            IntPtr hwnd = LocateMainWindow();
            if (hwnd == IntPtr.Zero)
            {
                Thread.Sleep(700);
                continue;
            }

            // Wait for splash/opening transitions to finish, then re-evaluate.
            Thread.Sleep(1800);
            if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd))
            {
                Log("Main window already hidden/minimized.");
                return;
            }

            if (!IsMailClientWindow(hwnd))
            {
                Thread.Sleep(700);
                continue;
            }

            // Sending a regular minimize command gives eM Client the chance
            // to honor its own "Minimize application to tray" preference.
            Log("Main eM Client window found; sending minimize command.");
            PostMessage(hwnd, WmSysCommand, (IntPtr)ScMinimize, IntPtr.Zero);
            Thread.Sleep(1100);

            if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd))
            {
                Log("Window minimized/hidden successfully.");
                return;
            }

            // Fallback remains a MINIMIZE, not close or hide. We never use
            // WM_CLOSE because that could terminate the email client.
            ShowWindowAsync(hwnd, SwMinimize);
            Thread.Sleep(900);
            Log(!IsWindowVisible(hwnd) || IsIconic(hwnd)
                ? "Window minimized via fallback."
                : "Minimize requested; client may override external commands.");
            return;
        }

        Log("No visible eM Client main window appeared within three minutes.");
    }

    private static IntPtr LocateMainWindow()
    {
        var ids = new HashSet<uint>();
        foreach (Process process in Process.GetProcessesByName("MailClient"))
        {
            try { ids.Add((uint)process.Id); }
            catch { /* Exiting process. */ }
            finally { process.Dispose(); }
        }

        if (ids.Count == 0) return IntPtr.Zero;

        IntPtr best = IntPtr.Zero;
        long largestArea = 0;

        // Packaged apps may have multiple MailClient processes. Enumerating
        // top-level windows is more reliable than a fixed executable path.
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!ids.Contains(pid) || GetWindow(hwnd, GwOwner) != IntPtr.Zero)
                return true;
            if (!GetWindowRect(hwnd, out Rect rect)) return true;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width < MinWidth || height < MinHeight) return true;

            long area = (long)width * height;
            if (area > largestArea)
            {
                largestArea = area;
                best = hwnd;
            }
            return true;
        }, IntPtr.Zero);

        return best;
    }

    private static bool IsMailClientWindow(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using Process process = Process.GetProcessById((int)pid);
            return process.ProcessName.Equals("MailClient",
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void Uninstall()
    {
        using (RegistryKey? run = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
        {
            run?.DeleteValue(StartupValue, throwOnMissingValue: false);
        }

        Log("Per-user startup entry removed.");
        MessageBox.Show(
            "Automatic startup has been disabled.\n\n" +
            "You can now delete this folder if desired:\n" + Folder +
            "\n\nNothing in eM Client itself was changed.",
            AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.AppendAllText(Path.Combine(Folder, "starter.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message +
                Environment.NewLine);
        }
        catch { }
    }

    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback,
        IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd,
        out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint message,
        IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hwnd, int command);
}
