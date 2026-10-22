namespace TutorArchier.Core.Jobs;

public enum JobStatus
{
    Queued,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>A queued merge. The plan is opaque JSON owned by the engine, so a resumed job runs exactly what was queued.</summary>
/// <param name="CompletedSteps">Steps whose output is on disk; a resume starts at this index.</param>
public sealed record JobRecord(
    long Id,
    string Name,
    string SourceFolder,
    string OutputPath,
    JobStatus Status,
    string PlanJson,
    int CompletedSteps,
    int TotalSteps,
    string? Error,
    DateTimeOffset CreatedAt);

public interface IJobStore
{
    /// <returns>The stored job with its new id.</returns>
    JobRecord Add(JobRecord job);

    void Update(JobRecord job);

    void Delete(long id);

    /// <summary>Oldest first.</summary>
    IReadOnlyList<JobRecord> List();

    /// <summary>Jobs left "Running" by a crash or shutdown go back to the queue.</summary>
    void RequeueInterrupted();
}
