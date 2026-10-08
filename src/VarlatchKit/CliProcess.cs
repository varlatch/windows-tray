using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace VarlatchKit;

/// <summary>How a CLI command ended.</summary>
public sealed record CliResult(int ExitCode, string Stdout = "", string Stderr = "")
{
    /// <summary>Ended by the app: a cancel, the app quitting, or the timeout.</summary>
    public bool Signaled { get; init; }

    /// <summary>Stopped because it ran past its timeout.</summary>
    public bool TimedOut { get; init; }

    /// <summary>The process could not start at all.</summary>
    public string? LaunchError { get; init; }

    public bool Succeeded => ExitCode == 0 && !Signaled && LaunchError is null;

    /// <summary>
    /// The last lines of stderr (or stdout when stderr is empty) that are not
    /// indented, for a short error message, as the other Varlatch apps show them.
    /// </summary>
    public string LastErrorLines(int count = 2)
    {
        if (LaunchError is not null) return LaunchError;
        if (TimedOut) return "the CLI did not finish in time";
        var source = string.IsNullOrWhiteSpace(Stderr) ? Stdout : Stderr;
        var lines = source.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith(' ') && l.Trim().Length > 0)
            .ToList();
        return string.Join(" ", lines.TakeLast(count));
    }
}

/// <summary>
/// One run of the CLI: separate stdout and stderr, stdin closed, no console
/// window, an optional timeout, and a way to stop it with everything it
/// started. Never blocks the caller; <see cref="WaitAsync"/> completes once
/// the process has exited and its output is read.
/// </summary>
/// <remarks>
/// Every process runs in the app's job object, which ends them all when the
/// app does, even when it crashes: a sign-in the app started must not
/// outlive it.
/// </remarks>
public sealed class CliProcess
{
    public string Executable { get; }
    public IReadOnlyList<string> Arguments { get; }

    private readonly Process process = new();
    private readonly Action<string>? onStdout;
    private readonly object gate = new();
    private readonly StringBuilder stdout = new();
    private readonly StringBuilder stderr = new();
    private readonly TaskCompletionSource<CliResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool started;
    private bool terminated;
    private bool timedOut;
    private string? refusal;

    /// <param name="onStdout">
    /// Gets everything printed to stdout so far, each time more arrives, on a
    /// background thread: for the browser sign-in's address.
    /// </param>
    public CliProcess(string executable, IReadOnlyList<string> arguments,
                      IReadOnlyDictionary<string, string> environment, Action<string>? onStdout = null)
    {
        Executable = executable;
        Arguments = arguments;
        this.onStdout = onStdout;
        var info = process.StartInfo;
        info.FileName = executable;
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = new UTF8Encoding(false);
        info.StandardErrorEncoding = new UTF8Encoding(false);
        info.Environment.Clear();
        foreach (var (name, value) in environment) info.Environment[name] = value;
    }

    public int ProcessId => started ? process.Id : 0;

    public bool IsRunning => !completion.Task.IsCompleted;

    /// <summary>Starts the process. One that cannot start finishes at once with <see cref="CliResult.LaunchError"/> set.</summary>
    public void Start(TimeSpan? timeout = null)
    {
        if (refusal is not null)
        {
            completion.TrySetResult(new CliResult(127) { LaunchError = refusal });
            return;
        }
        try
        {
            process.Start();
            started = true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            completion.TrySetResult(new CliResult(127) { LaunchError = $"cannot run {Executable}: {e.Message}" });
            return;
        }
        JobObject.Shared.Add(process);
        process.StandardInput.Close();
        var outTask = Read(process.StandardOutput, isStdout: true);
        var errTask = Read(process.StandardError, isStdout: false);
        _ = Finish(outTask, errTask);
        if (timeout is { } limit)
        {
            _ = Task.Delay(limit).ContinueWith(_ =>
            {
                if (!IsRunning) return;
                lock (gate) timedOut = true;
                Terminate();
            }, TaskScheduler.Default);
        }
    }

    /// <summary>Makes <see cref="Start"/> finish at once with <paramref name="message"/> as its launch error.</summary>
    internal void Refuse(string message) => refusal = message;

    /// <summary>Ends the process and everything it started.</summary>
    public void Terminate()
    {
        if (!started || !IsRunning) return;
        lock (gate) terminated = true;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    /// <summary>Completes once the process has exited.</summary>
    public Task<CliResult> WaitAsync() => completion.Task;

    private async Task Read(StreamReader reader, bool isStdout)
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var n = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (n == 0) break;
                string? snapshot = null;
                lock (gate)
                {
                    (isStdout ? stdout : stderr).Append(buffer, 0, n);
                    if (isStdout && onStdout is not null) snapshot = stdout.ToString();
                }
                if (snapshot is not null) onStdout!(snapshot);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The pipe broke: the process is gone.
        }
    }

    private async Task Finish(Task outTask, Task errTask)
    {
        await process.WaitForExitAsync().ConfigureAwait(false);
        // A child that outlives the CLI could keep the pipes open; do not wait
        // for it longer than a moment.
        await Task.WhenAny(Task.WhenAll(outTask, errTask), Task.Delay(1000)).ConfigureAwait(false);
        CliResult result;
        lock (gate)
        {
            result = new CliResult(process.ExitCode, Normalize(stdout), Normalize(stderr))
            {
                Signaled = terminated,
                TimedOut = timedOut,
            };
        }
        process.Dispose();
        completion.TrySetResult(result);
    }

    /// <summary>The CLI's own line endings, whatever the platform wrote.</summary>
    private static string Normalize(StringBuilder text) => text.ToString().Replace("\r\n", "\n");

    /// <summary>Runs a command to the end.</summary>
    public static Task<CliResult> RunAsync(string executable, IReadOnlyList<string> arguments,
                                           IReadOnlyDictionary<string, string> environment, TimeSpan? timeout)
    {
        var process = new CliProcess(executable, arguments, environment);
        process.Start(timeout);
        return process.WaitAsync();
    }
}
