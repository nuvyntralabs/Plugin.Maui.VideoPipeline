#if IOS || MACCATALYST
using AVFoundation;
using CoreMedia;
using Foundation;
using UIKit;

namespace Plugin.Maui.VideoPipeline;

sealed class PlatformVideoProcessor : IVideoProcessor
{
    public static IVideoProcessor Create() => new PlatformVideoProcessor();

    public Task<VideoProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        using var asset = AVAsset.FromUrl(NSUrl.FromFilename(path));
        if (asset is null)
            return Task.FromResult(new VideoProbe { Bytes = info.Exists ? info.Length : 0 });

        var duration = TimeSpan.FromSeconds(asset.Duration.Seconds);
        var mediaType = AVMediaTypes.Video.GetConstant() ?? "vide";
        var track = asset.TracksWithMediaType(mediaType).FirstOrDefault();
        var size = track?.NaturalSize ?? default;
        return Task.FromResult(new VideoProbe
        {
            Bytes = info.Length,
            Duration = duration.TotalSeconds > 0 ? duration : TimeSpan.Zero,
            Width = (int)size.Width,
            Height = (int)size.Height
        });
    }

    public Task<string?> CreateThumbnailAsync(string path, TimeSpan at, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var asset = AVAsset.FromUrl(NSUrl.FromFilename(path));
            if (asset is null)
                return Task.FromResult<string?>(null);

            using var generator = new AVAssetImageGenerator(asset) { AppliesPreferredTrackTransform = true };
            var time = CMTime.FromSeconds(Math.Max(0, at.TotalSeconds), 600);
            var image = generator.CopyCGImageAtTime(time, out _, out var error);
            if (image is null || error is not null)
                return Task.FromResult<string?>(null);

            using var ui = UIImage.FromImage(image);
            var dest = Path.Combine(Path.GetTempPath(), $"vp-thumb-{Guid.NewGuid():N}.jpg");
            var data = ui.AsJPEG(0.85f);
            data?.Save(dest, true);
            return Task.FromResult<string?>(dest);
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    public async Task<VideoTranscodeResult> TranscodeAsync(string path, VideoTranscodeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var asset = AVAsset.FromUrl(NSUrl.FromFilename(path));
            if (asset is null)
                return VideoTranscodeResult.Fail(VideoPipelineStatus.CannotTranscode, "AVAsset could not open the file.");

            var preset = request.MaxWidth is <= 640 || request.MaxHeight is <= 480
                ? AVAssetExportSessionPreset.Preset640x480
                : AVAssetExportSessionPreset.Preset1280x720;
            var presetName = preset.GetConstant() ?? "AVAssetExportPreset1280x720";
            var session = new AVAssetExportSession(asset, presetName);
            if (session is null)
                return VideoTranscodeResult.Fail(VideoPipelineStatus.CannotTranscode, "AVAssetExportSession preset is unavailable.");

            var dest = Path.Combine(Path.GetTempPath(), $"vp-xcode-{Guid.NewGuid():N}.mp4");
            session.OutputUrl = NSUrl.FromFilename(dest);
            session.OutputFileType = AVFileTypes.Mpeg4.GetConstant() ?? "public.mpeg-4";
            session.ShouldOptimizeForNetworkUse = true;
            if (request.MaxDuration is { } cap && cap > TimeSpan.Zero)
            {
                var end = CMTime.FromSeconds(Math.Min(asset.Duration.Seconds, cap.TotalSeconds), 600);
                session.TimeRange = new CMTimeRange { Start = CMTime.Zero, Duration = end };
            }

            await session.ExportTaskAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (session.Status != AVAssetExportSessionStatus.Completed || !File.Exists(dest))
                return VideoTranscodeResult.Fail(VideoPipelineStatus.CannotTranscode, session.Status.ToString());
            return VideoTranscodeResult.Ok(dest);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return VideoTranscodeResult.Fail(VideoPipelineStatus.CannotTranscode, ex.Message);
        }
    }
}
#endif
