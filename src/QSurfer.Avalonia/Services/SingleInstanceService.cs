using System.Threading;
using QSurfer.Core.Services;

namespace QSurfer.Avalonia.Services;

internal sealed class SingleInstanceService : IDisposable
{
    private Mutex? _mutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationWait;

    public event Action? ActivationRequested;

    public bool TryAcquire()
    {
        if (!OperatingSystem.IsWindows())
        {
            // The activation event uses Windows named kernel objects. Linux may
            // run separate QSurfer processes until a desktop-native activation
            // channel is introduced.
            return true;
        }

        try
        {
            var mutex = new Mutex(true, MutexName, out var createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                SignalExistingInstance();
                return false;
            }

            _mutex = mutex;
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName, out _);
            _activationWait = ThreadPool.RegisterWaitForSingleObject(
                _activationEvent,
                (_, _) => ActivationRequested?.Invoke(),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _activationWait?.Unregister(null);
        _activationWait = null;
        _activationEvent?.Dispose();
        _activationEvent = null;
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }
        finally
        {
            _mutex?.Dispose();
            _mutex = null;
        }
    }

    private static string MutexName => RuntimeMode.IsDemo
        ? @"Global\QSurfer.Demo.SingleInstance"
        : @"Global\QSurfer.SingleInstance";

    private static string ActivationEventName => RuntimeMode.IsDemo
        ? @"Global\QSurfer.Demo.ActivateInstance"
        : @"Global\QSurfer.ActivateInstance";

    private static void SignalExistingInstance()
    {
        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
            activationEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
