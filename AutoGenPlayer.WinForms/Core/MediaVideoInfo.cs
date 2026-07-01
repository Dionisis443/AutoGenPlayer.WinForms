namespace AutoGen_Player.Core;

public sealed record MediaVideoInfo(
    double FrameRate,
    TimeSpan Duration,
    int FrameCount,
    TimeSpan StartTime,
    int Width = 0,
    int Height = 0,
    double DisplayAspectRatio = 0)
{
    public static MediaVideoInfo Empty { get; } = new(0, TimeSpan.Zero, 0, TimeSpan.Zero);
}
