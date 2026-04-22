using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using MdTranslatorViewer.Services;

namespace MdTranslatorViewer;

public partial class App : Application
{
    private const int PrimaryInstanceConnectAttempts = 10;
    private const int PrimaryInstanceConnectTimeoutMilliseconds = 500;
    private static readonly string InstanceMutexName = AppStoragePaths.CreateScopedMutexName(@"Local\MdTranslatorViewer.SingleInstance");
    private static readonly string InstancePipeName = AppStoragePaths.CreateScopedPipeName("MdTranslatorViewer.SingleInstancePipe");
    private static readonly object DiagnosticLogSync = new();

    private Mutex? _instanceMutex;
    private CancellationTokenSource? _ipcCts;
    private Task? _ipcServerTask;
    private bool _ownsInstanceMutex;
    private bool _showingUnhandledException;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += App_OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_OnUnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_OnUnobservedTaskException;

        var startupPath = e.Args.FirstOrDefault();
        if (!EnsurePrimaryInstance(startupPath))
        {
            return;
        }

        try
        {
            base.OnStartup(e);

            var mainWindow = new MainWindow(startupPath);
            MainWindow = mainWindow;
            mainWindow.Show();

            _ipcCts = new CancellationTokenSource();
            _ipcServerTask = RunSingleInstanceServerAsync(_ipcCts.Token);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to start MD Translator Viewer.\n\n{ex.Message}",
                "MD Translator Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Environment.Exit(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= App_OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= TaskScheduler_OnUnobservedTaskException;
        _ipcCts?.Cancel();
        _ipcCts?.Dispose();
        if (_ownsInstanceMutex)
        {
            _instanceMutex?.ReleaseMutex();
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private bool TryAcquirePrimaryInstance()
    {
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
        _ownsInstanceMutex = createdNew;
        return createdNew;
    }

    private bool EnsurePrimaryInstance(string? startupPath)
    {
        if (TryAcquirePrimaryInstance())
        {
            return true;
        }

        if (TrySendOpenRequestToPrimaryInstance(startupPath))
        {
            Environment.Exit(0);
            return false;
        }

        if (TryRecoverPrimaryInstance())
        {
            return true;
        }

        MessageBox.Show(
            "Another MD Translator Viewer process appears to be stuck.\n\nClose the existing process from Task Manager and try again.",
            "MD Translator Viewer",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        Environment.Exit(-1);
        return false;
    }

    private async Task RunSingleInstanceServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    InstancePipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(cancellationToken);

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var requestedPath = (await reader.ReadToEndAsync(cancellationToken)).Trim();

                await Dispatcher.InvokeAsync(async () =>
                {
                    if (MainWindow is not MainWindow window)
                    {
                        return;
                    }

                    window.BringToFront();
                    if (!string.IsNullOrWhiteSpace(requestedPath))
                    {
                        await window.OpenDocumentFromExternalAsync(requestedPath);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
        }
    }

    private bool TryRecoverPrimaryInstance()
    {
        try
        {
            if (_instanceMutex is null)
            {
                return false;
            }

            if (!_instanceMutex.WaitOne(0))
            {
                return false;
            }

            _ownsInstanceMutex = true;
            return true;
        }
        catch (AbandonedMutexException)
        {
            _ownsInstanceMutex = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySendOpenRequestToPrimaryInstance(string? startupPath)
    {
        var payload = string.IsNullOrWhiteSpace(startupPath) ? string.Empty : Path.GetFullPath(startupPath);

        for (var attempt = 0; attempt < PrimaryInstanceConnectAttempts; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".",
                    InstancePipeName,
                    PipeDirection.Out,
                    PipeOptions.CurrentUserOnly);

                client.Connect(PrimaryInstanceConnectTimeoutMilliseconds);

                using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true)
                {
                    AutoFlush = true,
                };

                writer.Write(payload);
                return true;
            }
            catch
            {
                if (attempt == PrimaryInstanceConnectAttempts - 1)
                {
                    break;
                }

                Thread.Sleep(150);
            }
        }

        return false;
    }

    private void App_OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppendDiagnosticLog("DispatcherUnhandledException", e.Exception);
        ShowUnhandledExceptionDialog("processing a UI event", e.Exception);
        e.Handled = true;
    }

    private void CurrentDomain_OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception ??
                        new InvalidOperationException($"Unhandled exception object: {e.ExceptionObject}");
        AppendDiagnosticLog("AppDomain.UnhandledException", exception);

        if (!e.IsTerminating)
        {
            ShowUnhandledExceptionDialog("running background work", exception);
        }
    }

    private void TaskScheduler_OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppendDiagnosticLog("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private void ShowUnhandledExceptionDialog(string action, Exception exception)
    {
        if (_showingUnhandledException)
        {
            return;
        }

        try
        {
            _showingUnhandledException = true;
            var logPath = GetDiagnosticLogPath();
            MessageBox.Show(
                $"MD Translator Viewer hit an unexpected error while {action}.\n\n{exception.Message}\n\nA diagnostic log was written to:\n{logPath}",
                "MD Translator Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
        }
        finally
        {
            _showingUnhandledException = false;
        }
    }

    private static void AppendDiagnosticLog(string source, Exception exception)
    {
        try
        {
            var logPath = GetDiagnosticLogPath();
            var entry = new StringBuilder()
                .Append('[')
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .Append("] ")
                .Append(source)
                .AppendLine()
                .AppendLine(exception.ToString())
                .AppendLine(new string('-', 80))
                .ToString();

            lock (DiagnosticLogSync)
            {
                File.AppendAllText(logPath, entry, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    private static string GetDiagnosticLogPath()
    {
        var logDirectory = AppStoragePaths.StorageRoot;
        Directory.CreateDirectory(logDirectory);
        return Path.Combine(logDirectory, "diagnostics.log");
    }
}
