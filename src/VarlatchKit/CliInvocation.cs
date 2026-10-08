namespace VarlatchKit;

/// <summary>The CLI to run and the environment to run it with.</summary>
public sealed record CliInvocation(CliCommand Command, IReadOnlyDictionary<string, string> Environment)
{
    public CliInvocation With(string name, string value)
    {
        var environment = new Dictionary<string, string>(Environment, StringComparer.OrdinalIgnoreCase) { [name] = value };
        return this with { Environment = environment };
    }

    /// <summary>A run of the CLI with <paramref name="arguments"/>, not started yet.</summary>
    public CliProcess Process(IReadOnlyList<string> arguments, Action<string>? onStdout = null)
    {
        var process = new CliProcess(Command.Executable, [.. Command.Prefix, .. arguments], Environment, onStdout);
        if (Unsafe(arguments) is { } argument)
            process.Refuse($"cannot pass \"{argument}\" to {Path.GetFileName(Command.Path)}. Set the CLI path in Settings to the varlatch.cjs it runs.");
        return process;
    }

    /// <summary>Runs the CLI with <paramref name="arguments"/> to the end.</summary>
    public Task<CliResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan? timeout)
    {
        var process = Process(arguments);
        process.Start(timeout);
        return process.WaitAsync();
    }

    /// <summary>
    /// A <c>.cmd</c> CLI runs in <c>cmd.exe</c>, which reads its arguments
    /// again: an argument with characters it treats specially could become a
    /// different command. The app's own arguments never have them; a server
    /// address could, in theory.
    /// </summary>
    private string? Unsafe(IReadOnlyList<string> arguments)
    {
        var extension = Path.GetExtension(Command.Executable).ToLowerInvariant();
        if (extension is not (".cmd" or ".bat")) return null;
        return arguments.FirstOrDefault(a => a.IndexOfAny(['"', '&', '|', '<', '>', '^', '%', '!', '(', ')', ' ']) >= 0);
    }
}
