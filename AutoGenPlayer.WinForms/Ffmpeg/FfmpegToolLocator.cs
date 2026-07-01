namespace AutoGen_Player.Ffmpeg;

internal sealed record FfmpegTools(string FfmpegPath, string FfprobePath, string? LibraryDirectory);

internal static class FfmpegToolLocator
{
    public static FfmpegTools Locate(string? preferredDirectory = null)
    {
        string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        string? projectDirectory = FindProjectDirectory(baseDirectory);

        IEnumerable<string> candidateDirectories = new[]
        {
            preferredDirectory ?? string.Empty,
            Path.Combine(baseDirectory, "Native", "FFmpeg"),
            Path.Combine(baseDirectory, "FFmpeg"),
            projectDirectory is null ? string.Empty : Path.Combine(projectDirectory, "Native", "FFmpeg"),
        }.Where(path => !string.IsNullOrWhiteSpace(path));

        foreach (string directory in candidateDirectories)
        {
            string ffmpegPath = Path.Combine(directory, "ffmpeg.exe");
            string ffprobePath = Path.Combine(directory, "ffprobe.exe");
            if (File.Exists(ffmpegPath) && File.Exists(ffprobePath))
                return new FfmpegTools(ffmpegPath, ffprobePath, directory);
        }

        string? pathFfmpeg = FindOnPath("ffmpeg.exe");
        string? pathFfprobe = FindOnPath("ffprobe.exe");
        if (pathFfmpeg is not null && pathFfprobe is not null)
            return new FfmpegTools(pathFfmpeg, pathFfprobe, Path.GetDirectoryName(pathFfmpeg));

        throw new FileNotFoundException(
            "Could not locate ffmpeg.exe and ffprobe.exe. Add them to a Native\\FFmpeg folder next to the app, configure a custom FFmpeg directory, or install FFmpeg on PATH.");
    }

    private static string? FindProjectDirectory(string startDirectory)
    {
        DirectoryInfo? directory = new(startDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AutoGen_Player.csproj")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    private static string? FindOnPath(string executableName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (string directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            string candidate = Path.Combine(directory.Trim(), executableName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
