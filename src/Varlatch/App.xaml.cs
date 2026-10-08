using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Varlatch;

public partial class App : Application
{
    private readonly AppActivationArguments activation;

    public App(AppActivationArguments activation)
    {
        this.activation = activation;
        InitializeComponent();
        UnhandledException += (_, e) => Log.Write("unhandled: " + e.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppController.Shared.Launch(activation);
    }
}
