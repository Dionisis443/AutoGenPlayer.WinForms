namespace AutoGen_Player.Core;

public sealed record AudioMixSelection(
    int AudioOrdinal,
    int StreamIndex,
    int VolumePercent);
