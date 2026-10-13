using Microsoft.Extensions.Logging;
using TutorArchier.Core;
using TutorArchier.Core.Jobs;
using TutorArchier.Engine.Hardware;

namespace TutorArchier.Engine.Merge;

public sealed record JobProgress(long JobId, MergeProgress Progress);

/// <summary>
/// Runs merge jobs one at a time, oldest first. State lives in <see cref="IJobStore"/> after every step,
/// so a job interrupted by a pause, a crash or closing the app continues from its last finished step.
/// Events are raised on the worker thread.
/// </summary>
public sealed class JobScheduler : IDisposable
{
    private readonly IJobStore _store;
    private readonly MergeRunner _runner;
    private readonly HardwareService _hardware;
    private readonly ILogger<JobScheduler> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _lock = new();
    private (long Id, CancellationTokenSource Cancellation, JobStatus StopAs)? _running;
    private Task? _loop;

    public JobScheduler(IJobStore store, MergeRunner runner, HardwareService hardware, ILogger<JobScheduler> logger)
    {
        _store = store;
        _runner = runner;
        _hardware = hardware;
        _logger = logger;
        // The queue waits for ffmpeg; finding or installing it lets queued jobs start.
        _hardware.Changed += (_, _) => Wake();
    }

    /// <summary>Raised when a job is added, removed or changes status.</summary>
    public event EventHandler? Changed;

    public event EventHandler<JobProgress>? ProgressChanged;

    public JobProgress? CurrentProgress { get; private set; }

    public IReadOnlyList<JobRecord> Jobs => _store.List();

    /// <summary>Starts the worker. Jobs cut off by the last shutdown are queued again and resume first.</summary>
    public void Start()
    {
        if (_loop is not null)
            return;
        _store.RequeueInterrupted();
        _loop = Task.Run(RunLoopAsync);
    }

    public JobRecord Enqueue(MergePlan plan)
    {
        var job = _store.Add(new JobRecord(
            0, plan.Title, plan.SourceFolder, plan.OutputPath, JobStatus.Queued, plan.ToJson(),
            CompletedSteps: 0, plan.StepCount, Error: null, DateTimeOffset.Now));
        OnChanged();
        Wake();
        return job;
    }

    public void Pause(long id) => Stop(id, JobStatus.Paused);

    public void Cancel(long id) => Stop(id, JobStatus.Cancelled);

    /// <summary>Paused or failed jobs go back to the queue and pick up at their last finished step.</summary>
    public void Resume(long id)
    {
        if (Find(id) is { Status: JobStatus.Paused or JobStatus.Failed } job)
        {
            _store.Update(job with { Status = JobStatus.Queued, Error = null });
            OnChanged();
            Wake();
        }
    }

    /// <summary>Forgets a job that isn't running. Unfinished work files are deleted; a finished video is kept.</summary>
    public void Remove(long id)
    {
        if (Find(id) is not { } job || job.Status == JobStatus.Running)
            return;
        if (job.Status != JobStatus.Completed)
            AppPaths.TryDeleteDirectory(MergePlan.FromJson(job.PlanJson).WorkDirectory);
        _store.Delete(id);
        OnChanged();
    }

    private void Stop(long id, JobStatus stopAs)
    {
        lock (_lock)
        {
            if (_running is { } running && running.Id == id)
            {
                _running = running with { StopAs = stopAs };
                running.Cancellation.Cancel();
                return;
            }
        }

        if (Find(id) is { Status: JobStatus.Queued or JobStatus.Paused or JobStatus.Failed } job)
        {
            if (stopAs == JobStatus.Cancelled)
                AppPaths.TryDeleteDirectory(MergePlan.FromJson(job.PlanJson).WorkDirectory);
            _store.Update(job with { Status = stopAs });
            OnChanged();
        }
    }

    private async Task RunLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            var ffmpeg = _hardware.Ffmpeg;
            var next = ffmpeg is null ? null : _store.List().FirstOrDefault(j => j.Status == JobStatus.Queued);
            if (next is null)
            {
                try
                {
                    await _wake.WaitAsync(_shutdown.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }

            await RunJobAsync(next, ffmpeg!);
        }
    }

    private async Task RunJobAsync(JobRecord job, Core.Hardware.FfmpegInfo ffmpeg)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        lock (_lock)
            _running = (job.Id, cancellation, JobStatus.Paused);

        job = job with { Status = JobStatus.Running, Error = null };
        _store.Update(job);
        OnChanged();

        try
        {
            var plan = MergePlan.FromJson(job.PlanJson);
            await _runner.RunAsync(
                ffmpeg,
                plan,
                progress =>
                {
                    CurrentProgress = new JobProgress(job.Id, progress);
                    ProgressChanged?.Invoke(this, CurrentProgress);
                },
                completed =>
                {
                    job = job with { CompletedSteps = completed };
                    _store.Update(job);
                },
                cancellation.Token);
            job = job with { Status = JobStatus.Completed };
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // App closing: stay resumable without the user having to press anything.
            job = job with { Status = JobStatus.Queued };
        }
        catch (OperationCanceledException)
        {
            JobStatus stopAs;
            lock (_lock)
                stopAs = _running?.StopAs ?? JobStatus.Paused;
            if (stopAs == JobStatus.Cancelled)
                AppPaths.TryDeleteDirectory(MergePlan.FromJson(job.PlanJson).WorkDirectory);
            job = job with { Status = stopAs };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Merge job {Id} failed", job.Id);
            job = job with { Status = JobStatus.Failed, Error = ex.Message };
        }
        finally
        {
            lock (_lock)
                _running = null;
            CurrentProgress = null;
            _store.Update(job);
            OnChanged();
        }
    }

    private JobRecord? Find(long id) => _store.List().FirstOrDefault(j => j.Id == id);

    private void Wake()
    {
        if (_wake.CurrentCount > 0)
            return;
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Another caller woke it first.
        }
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            // Give a running ffmpeg a moment to die and the job a chance to record its state.
            _loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }
        _shutdown.Dispose();
        _wake.Dispose();
    }
}
