using WhisperNotes.Core.Diarization;

namespace WhisperNotes.Core.Tests.Diarization;

public sealed class SpeakerClusteringTests
{
    [Fact]
    public void Cluster_ReusesOriginalSpeaker_WhenFirstVoiceReturns()
    {
        float[][] embeddings =
        [
            [1f, 0f, 0f],
            [0f, 1f, 0f],
            [0.9998f, 0.02f, 0f],
        ];

        int[] speakers = SpeakerClustering.Cluster(
            embeddings,
            weights: [1d, 1d, 1d],
            mergeThreshold: 0.2,
            maxSpeakers: 8);

        Assert.Equal([0, 1, 0], speakers);
    }

    [Fact]
    public void Cluster_AssignsNewAnonymousLabel_WhenVoiceDoesNotMatch()
    {
        float[][] embeddings =
        [
            [1f, 0f, 0f],
            [0f, 1f, 0f],
            [0f, 0f, 1f],
        ];

        int[] speakers = SpeakerClustering.Cluster(
            embeddings,
            weights: [1d, 1d, 1d],
            mergeThreshold: 0.2,
            maxSpeakers: 8);

        Assert.Equal([0, 1, 2], speakers);
    }

    [Fact]
    public void Timeline_DoesNotInventLabelFromInvalidNearestTurn()
    {
        SpeakerTimeline timeline = new(
            [new SpeakerTurn(TimeSpan.Zero, TimeSpan.FromSeconds(1), Speaker: 5)],
            speakerCount: 1);

        Assert.Null(timeline.Label(TimeSpan.FromSeconds(1.1), TimeSpan.FromSeconds(1.2)));
    }
}
