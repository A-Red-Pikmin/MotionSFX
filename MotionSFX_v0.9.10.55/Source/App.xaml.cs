using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MotionSFX;

public partial class App : Application
{
    internal const string VersionText = "0.9.10.55";

    private Mutex _instanceMutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _instanceMutex = new Mutex(true, @"Local\MotionSFX.Alpha.Native", out _ownsMutex);
            if (!_ownsMutex)
            {
                MessageBox.Show("MotionSFX is already running.", "MotionSFX",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            AppLog.StartSession(VersionText);
            try { SetCurrentProcessExplicitAppUserModelID("MotionSFX.DesktopApp"); } catch { }

            DispatcherUnhandledException += OnDispatcherException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                AppLog.Write("ERROR", "Unobserved task exception: " + args.Exception);
                args.SetObserved();
            };

            var main = new MainWindow();
            MainWindow = main;
            main.Show();
        }
        catch (Exception ex)
        {
            AppLog.Write("FATAL", ex.ToString());
            MessageBox.Show("MotionSFX could not start.\n\n" + ex.Message +
                "\n\nLog: " + AppLog.LogPath, "MotionSFX startup error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch { }
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write("ERROR", "Unhandled UI exception: " + e.Exception);
        MessageBox.Show("An unexpected application error occurred. Details were written to the MotionSFX log.\n\n" +
            e.Exception.Message, "MotionSFX error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        AppLog.Write("FATAL", "Unhandled process exception: " + e.ExceptionObject);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}

internal static class AppLog
{
    private static readonly object Gate = new();
    private static readonly Queue<string> Lines = new();
    private static TextBox _view;
    private static string _currentLogPath;

    public static string DataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MotionSFX");
    public static string LogsRoot => Path.Combine(DataRoot, "Logs");
    public static string LogPath
    {
        get
        {
            lock (Gate)
                return _currentLogPath ?? Path.Combine(LogsRoot, "MotionSFX-SessionLog-startup.log");
        }
    }

    public static void StartSession(string version)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(LogsRoot);
            DateTime started = DateTime.Now;
            string timestamp = started.ToString("yyyy-MM-dd_HH-mm-ss-fff", System.Globalization.CultureInfo.InvariantCulture);
            string candidate = Path.Combine(LogsRoot, $"MotionSFX-SessionLog_{timestamp}.log");
            int suffix = 1;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(LogsRoot, $"MotionSFX-SessionLog_{timestamp}_{suffix++}.log");
            }

            string firstLine = $"[{started:yyyy-MM-dd HH:mm:ss.fff}] [INFO] LAUNCHED VERSION: MotionSFX Alpha {version}";
            // Create a new per-launch file and write the version before any other log entry.
            File.WriteAllText(candidate, firstLine + Environment.NewLine, new System.Text.UTF8Encoding(false));
            _currentLogPath = candidate;
            Lines.Clear();
            Lines.Enqueue(firstLine);
        }
    }

    public static void Attach(TextBox view)
    {
        _view = view;
        lock (Gate)
        {
            foreach (string line in Lines)
                _view.AppendText(line + Environment.NewLine);
        }
        _view.ScrollToEnd();
    }

    public static void Write(string level, string message)
    {
        string clean = (message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {clean}";
        lock (Gate)
        {
            if (string.IsNullOrEmpty(_currentLogPath))
            {
                try { StartSession(App.VersionText); }
                catch { }
            }

            Lines.Enqueue(line);
            while (Lines.Count > 500) Lines.Dequeue();
            try
            {
                Directory.CreateDirectory(LogsRoot);
                if (!string.IsNullOrEmpty(_currentLogPath))
                    File.AppendAllText(_currentLogPath, line + Environment.NewLine, new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        if (_view != null)
        {
            try
            {
                _view.AppendText(line + Environment.NewLine);
                if (_view.Text.Length > 60000)
                    _view.Text = _view.Text[^45000..];
                _view.ScrollToEnd();
            }
            catch { }
        }
    }
}
