using AutoGen_Player.Core;

namespace AutoGen_Player.Audio;

public sealed class AudioLevelSnapshotEventArgs : EventArgs
{
    public AudioLevelSnapshotEventArgs(
        IReadOnlyList<AudioMixSelection> selections,
        IReadOnlyList<StereoLevel> streamLevels,
        StereoLevel mixedLevel)
    {
        Selections = selections;
        StreamLevels = streamLevels;
        MixedLevel = mixedLevel;
    }

    public IReadOnlyList<AudioMixSelection> Selections { get; }
    public IReadOnlyList<StereoLevel> StreamLevels { get; }
    public StereoLevel MixedLevel { get; }
}

public readonly record struct StereoLevel(float Left, float Right)
{
    public float Peak => Math.Max(Left, Right);
}
