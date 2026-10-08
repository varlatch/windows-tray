using Microsoft.Windows.AppLifecycle;
using VarlatchKit;

namespace Varlatch;

/// <summary>What a launch of <c>Varlatch.exe</c> asks for.</summary>
internal abstract record Command
{
    /// <summary>
    /// No arguments: started by hand. <paramref name="AtLogin"/>: started at
    /// login, or by Windows for a click on a notification, which arrives on
    /// its own; either way the panel stays closed.
    /// </summary>
    public sealed record Launch(bool AtLogin) : Command;

    /// <summary><c>--open</c>: the panel.</summary>
    public sealed record Open : Command;

    /// <summary><c>--settings</c></summary>
    public sealed record OpenSettings : Command;

    /// <summary><c>--refresh</c>: read the CLI and the sessions again, as after installing the CLI.</summary>
    public sealed record Refresh : Command;

    /// <summary><c>--sign-in &lt;server&gt;</c>: a browser sign-in, shown in the panel.</summary>
    public sealed record SignInTo(string Server) : Command;

    /// <summary><c>--quit</c>: for the installer, before it replaces the app.</summary>
    public sealed record Quit : Command;

    /// <summary><c>--unregister</c>: for the uninstaller: no more opening at login or notifications.</summary>
    public sealed record Unregister : Command;

    /// <summary><c>--debug &lt;name&gt;</c>, with debug hooks on.</summary>
    public sealed record Debug(string Name) : Command;
}

internal static class Commands
{
    public static Command FromActivation(AppActivationArguments activation)
    {
        // For an app that is not packaged, the arguments are its command line.
        var line = activation.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
            ? launch.Arguments
            : Environment.CommandLine;
        return Parse(Arguments(line));
    }

    /// <summary>The arguments of a command line, without the program.</summary>
    public static string[] Arguments(string commandLine)
    {
        var parts = Native.SplitCommandLine(commandLine);
        return parts.Length > 0 && parts[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? parts[1..] : parts;
    }

    public static Command Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return new Command.Launch(AtLogin: false);
        return args[0] switch
        {
            LaunchAtLogin.Argument or ToastActivator.Argument or "-Embedding" => new Command.Launch(AtLogin: true),
            "--open" => new Command.Open(),
            "--settings" => new Command.OpenSettings(),
            "--refresh" => new Command.Refresh(),
            "--sign-in" when args.Count > 1 => new Command.SignInTo(args[1]),
            "--quit" => new Command.Quit(),
            "--unregister" => new Command.Unregister(),
            "--debug" when args.Count > 1 => new Command.Debug(args[1]),
            _ => new Command.Launch(AtLogin: false),
        };
    }
}
