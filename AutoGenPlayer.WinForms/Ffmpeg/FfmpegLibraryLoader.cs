using FFmpeg.AutoGen;

namespace AutoGen_Player.Ffmpeg;

internal static class FfmpegLibraryLoader
{
    public static void Configure(string? libraryDirectory)
    {
        if (!string.IsNullOrWhiteSpace(libraryDirectory) && Directory.Exists(libraryDirectory))
            ffmpeg.RootPath = libraryDirectory;
    }
}
