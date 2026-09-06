using Module.Mangareader.Features.Downloader.Queue;

namespace Module.Mangareader.Downloader.Tests;

/// <summary>
/// The Download List row offers only the actions that are valid for a job's state.
/// These are projections of the durable state the queue owns — the screen adds no
/// state of its own — and they are what keeps the Action column from stacking
/// overlapping controls.
/// </summary>
public sealed class DownloadListActionValidityTests
{
    [Fact]
    public void EveryJobStateOffersExactlyOnePrimaryAction()
    {
        (DownloadJobState State, bool Pause, bool Resume, bool Choose, bool Open, bool ActNow)[] expected =
        [
            (DownloadJobState.Queued, true, false, false, false, true),
            (DownloadJobState.Resolving, true, false, false, false, true),
            (DownloadJobState.Downloading, true, false, false, false, true),
            (DownloadJobState.Recovering, true, false, false, false, true),
            (DownloadJobState.AwaitingSourceFallback, false, false, true, false, true),
            (DownloadJobState.Decoding, true, false, false, false, true),
            (DownloadJobState.Validating, true, false, false, false, true),
            (DownloadJobState.Publishing, true, false, false, false, true),
            // A pause keeps its action visible but disabled until the bounded
            // command reaches its terminal response.
            (DownloadJobState.Pausing, true, false, false, false, false),
            (DownloadJobState.Paused, false, true, false, false, true),
            (DownloadJobState.Failed, false, true, false, false, true),
            (DownloadJobState.Completed, false, false, false, true, true),
        ];

        // A new job state must be added here deliberately, not silently render
        // every action at once.
        Assert.Equal(expected.Length, Enum.GetValues<DownloadJobState>().Length);

        foreach (var row in expected)
        {
            var job = Job(row.State);

            Assert.Equal(row.Pause, job.CanPause);
            Assert.Equal(row.Resume, job.CanResume);
            Assert.Equal(row.Choose, job.CanChooseFallback);
            Assert.Equal(row.Open, job.CanOpenFolder);
            Assert.Equal(row.ActNow, job.CanActNow);

            Assert.Equal(
                1,
                new[] { job.CanPause, job.CanResume, job.CanChooseFallback, job.CanOpenFolder }
                    .Count(valid => valid));
        }
    }

    [Fact]
    public void TheResumeActionIsLabelledByWhatItDoesToThatState()
    {
        Assert.Equal("Resume", Job(DownloadJobState.Paused).ResumeActionLabel);
        Assert.Equal("Retry", Job(DownloadJobState.Failed).ResumeActionLabel);
    }

    /// <summary>
    /// Awaiting a fallback with no candidate to offer has no valid primary action,
    /// so the row shows only Remove instead of a button that cannot do anything.
    /// </summary>
    [Fact]
    public void AwaitingSourceWithoutCandidatesOffersNoPrimaryAction()
    {
        var job = new DownloadJobRecord
        {
            JobId = "job",
            State = DownloadJobState.AwaitingSourceFallback,
            FallbackCandidates = [],
        };

        Assert.False(job.CanPause);
        Assert.False(job.CanResume);
        Assert.False(job.CanChooseFallback);
        Assert.False(job.CanOpenFolder);
    }

    private static DownloadJobRecord Job(DownloadJobState state) => new()
    {
        JobId = "job",
        State = state,
        PublishedPath = state == DownloadJobState.Completed ? @"C:\library\Title\0001.cbz" : null,
        FallbackCandidates = state == DownloadJobState.AwaitingSourceFallback
            ? [new SourceFallbackCandidate("77", "Scanlation", "c9", "available")]
            : [],
    };
}
