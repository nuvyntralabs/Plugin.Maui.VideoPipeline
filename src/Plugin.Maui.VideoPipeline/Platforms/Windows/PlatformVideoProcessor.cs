#if WINDOWS
namespace Plugin.Maui.VideoPipeline;

sealed class PlatformVideoProcessor : IVideoProcessor
{
    public static IVideoProcessor Create() => new PlatformVideoProcessor();

    public Task<VideoProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        return Task.FromResult(new VideoProbe { Bytes = info.Exists ? info.Length : 0 });
    }

    public Task<string?> CreateThumbnailAsync(string path, TimeSpan at, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<VideoTranscodeResult> TranscodeAsync(string path, VideoTranscodeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(VideoTranscodeResult.Fail(
            VideoPipelineStatus.CannotTranscode,
            "Windows 1.1 is copy + thumbnail floor. Transcode is best-effort and this host cannot encode without FFmpeg."));
}
#endif
