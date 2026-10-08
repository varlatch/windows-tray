using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using VarlatchKit;

namespace Varlatch;

/// <summary>
/// One Varlatch per user: a second launch (from Start, a notification, the
/// installer, or the CLI installer's <c>--sign-in</c>) hands its arguments to
/// the one running and exits.
/// </summary>
public static class Program
{
    private const string InstanceKey = "Varlatch.Windows.Main";

    [STAThread]
    private static int Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!main.IsCurrent)
        {
            Redirect(main, activation);
            return 0;
        }

        // Nothing running to ask.
        switch (Commands.FromActivation(activation))
        {
            case Command.Quit:
                return 0;
            case Command.Unregister:
                new LaunchAtLogin(new CurrentUserRegistry(), Environment.ProcessPath!).Set(false);
                Notifier.Uninstall();
                return 0;
        }

        main.Activated += (_, args) => AppController.Shared.Activated(args);
        Application.Start(start =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App(activation);
        });
        return 0;
    }

    /// <summary>
    /// Hands the activation to the running instance, waiting on another
    /// thread while this one keeps pumping messages, as Windows App SDK asks.
    /// </summary>
    private static void Redirect(AppInstance target, AppActivationArguments activation)
    {
        // So the running instance may bring its panel to the front.
        Native.AllowSetForegroundWindow(target.ProcessId);
        var done = Native.CreateEvent(IntPtr.Zero, true, false, null);
        _ = Task.Run(() =>
        {
            try
            {
                target.RedirectActivationToAsync(activation).AsTask().Wait();
            }
            catch (Exception e)
            {
                Log.Write("cannot reach the running Varlatch: " + e.Message);
            }
            Native.SetEvent(done);
        });
        Native.CoWaitForMultipleObjects(0, 0xFFFFFFFF, 1, [done], out _);
        Native.CloseHandle(done);
    }
}
