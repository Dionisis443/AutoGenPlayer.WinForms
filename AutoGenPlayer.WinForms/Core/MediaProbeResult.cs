namespace AutoGen_Player.Core;

internal sealed record MediaProbeResult(
    IReadOnlyList<MediaStreamInfo> AudioStreams,
    MediaVideoInfo VideoInfo);
