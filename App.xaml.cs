using System.Windows;

namespace HiddenGPT;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\HiddenGPT.SingleInstance";
    private const string ActivationEventName = @"Local\HiddenGPT.Activate";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationCancellation;

    protected override void OnStartup(StartupEventArgs e)
    {
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _instanceMutex = new Mutex(true, InstanceMutexName, out var isFirstInstance);

        if (!isFirstInstance)
        {
            _activationEvent.Set();
            _activationEvent.Dispose();
            _activationEvent = null;
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var window = new MainWindow(e.Args.Contains("--capture-test", StringComparer.OrdinalIgnoreCase));
        MainWindow = window;
        if (!window.PrepareForFirstShow())
        {
            // Fail closed without showing an unprotected error dialog.
            Shutdown(1);
            return;
        }
        window.Show();

        _activationCancellation = new CancellationTokenSource();
        var cancellation = _activationCancellation;
        var activationEvent = _activationEvent;
        _ = Task.Run(() =>
        {
            var handles = new WaitHandle[] { activationEvent, cancellation.Token.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0)
                Dispatcher.BeginInvoke(window.ShowAndActivateProtected);
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationCancellation?.Cancel();
        _activationCancellation?.Dispose();
        _activationEvent?.Dispose();

        if (_instanceMutex is not null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}
