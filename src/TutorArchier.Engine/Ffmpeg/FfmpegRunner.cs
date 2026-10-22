using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using CliWrap;
using Microsoft.Extensions.Logging;

namespace TutorArchier.Engine.Ffmpeg;

public sealed record FfmpegRunOptions
{
    /// <summary>
    /// When set, <c>-progress pipe:1 -nostats</c> is added and stdout is parsed as progress
    /// instead of being captured.
    /// </summary>
    public Action<FfmpegProgress>? OnProgress { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>Run at BelowNormal priority so long encodes don't make the desktop sluggish.</summary>
    public bool LowPriority { get; init; }
}

public sealed record FfmpegResult(int ExitCode, string StdOut, string StdErr, TimeSpan Elapsed)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Thin process wrapper for ffmpeg/ffprobe. Cancellation kills the process.</summary>
public sealed class FfmpegRunner(ILogger<FfmpegRunner> logger)
{
    private const int StdErrTailLines = 400;

    public async Task<FfmpegResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        FfmpegRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new FfmpegRunOptions();

        var args = arguments.ToList();
        var stdout = new StringBuilder();
        var stderr = new TailBuffer(StdErrTailLines);
        PipeTarget stdoutTarget;

        if (options.OnProgress is { } onProgress)
        {
            // Global options go first; ffmpeg warns about trailing ones.
            args.InsertRange(0, ["-progress", "pipe:1", "-nostats"]);
            var parser = new FfmpegProgressParser();
            stdoutTarget = PipeTarget.ToDelegate(line =>
            {
                if (parser.Feed(line) is { } progress)
                    onProgress(progress);
            }, Encoding.UTF8);
        }
        else
        {
            stdoutTarget = PipeTarget.ToStringBuilder(stdout, Encoding.UTF8);
        }

        var command = Cli.Wrap(executable)
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(stdoutTarget)
            .WithStandardErrorPipe(PipeTarget.ToDelegate(stderr.Add, Encoding.UTF8));

        if (options.WorkingDirectory is not null)
            command = command.WithWorkingDirectory(options.WorkingDirectory);

        logger.LogDebug("Running {Executable} {Arguments}", Path.GetFileName(executable), command.Arguments);

        var stopwatch = Stopwatch.StartNew();
        CliWrap.CommandResult result;
        try
        {
            var task = command.ExecuteAsync(cancellationToken);
            AttachToApp(task.ProcessId, options.LowPriority);
            result = await task;
        }
        catch (Win32Exception ex)
        {
            // The executable could not be started at all (missing, blocked, wrong architecture).
            logger.LogWarning(ex, "Could not start {Executable}", executable);
            return new FfmpegResult(-1, "", ex.Message, stopwatch.Elapsed);
        }

        return new FfmpegResult(result.ExitCode, stdout.ToString(), stderr.ToString(), stopwatch.Elapsed);
    }

    /// <summary>
    /// Makes the child die with the app (see <see cref="ChildProcessJob"/>) and optionally lowers its priority.
    /// Quick probes may already have exited by now, which is fine.
    /// </summary>
    private void AttachToApp(int processId, bool lowPriority)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!ChildProcessJob.TryTrack(process, out var error) && error is not null)
                logger.LogWarning("Process {ProcessId} is not tied to the app's lifetime: {Error}", processId, error);
            if (lowPriority)
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            logger.LogDebug(ex, "Process {ProcessId} exited before it could be attached", processId);
        }
    }

    /// <summary>Keeps the last N lines; ffmpeg can be chatty over a multi-hour encode.</summary>
    private sealed class TailBuffer(int capacity)
    {
        private readonly Queue<string> _lines = new(capacity);
        private readonly Lock _lock = new();

        public void Add(string line)
        {
            lock (_lock)
            {
                if (_lines.Count == capacity)
                    _lines.Dequeue();
                _lines.Enqueue(line);
            }
        }

        public override string ToString()
        {
            lock (_lock)
                return string.Join('\n', _lines);
        }
    }
}
