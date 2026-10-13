using TutorArchier.Engine.Merge;

namespace TutorArchier.Tests.Merge;

public sealed class QualitySearchTests
{
    [Theory]
    [InlineData(27, 27)] // 19..27 pass
    [InlineData(35, 35)] // everything passes
    [InlineData(18, 19)] // nothing passes: the best quality is used anyway
    public async Task Finds_the_highest_value_that_still_passes_in_few_trials(int lastPassing, int expected)
    {
        var trials = 0;

        var found = await QualitySearch.HighestPassing(19, 35, q =>
        {
            trials++;
            return Task.FromResult(q <= lastPassing);
        });

        Assert.Equal(expected, found);
        Assert.InRange(trials, 1, 5); // 17 candidates: each trial costs several encodes plus VMAF runs
    }

    [Theory]
    [InlineData(new[] { 96.0, 95.5, 94.5, 95.0 }, 95.25)] // close together: the average decides
    [InlineData(new[] { 99.0, 99.0, 99.0, 90.0 }, 92.0)] // slides can't outvote the one soft camera clip
    [InlineData(new[] { 97.0 }, 97.0)]
    public void Course_score_is_the_average_held_down_by_the_worst_clip(double[] scores, double expected) =>
        Assert.Equal(expected, QualitySearch.Aggregate(scores), 3);
}
