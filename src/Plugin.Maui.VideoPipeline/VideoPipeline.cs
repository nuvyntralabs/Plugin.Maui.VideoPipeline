using System.Security.Cryptography;

namespace Plugin.Maui.VideoPipeline;

public sealed class VideoPipelineOptions
{
    public TimeSpan DefaultMaxDuration { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class VideoArtifact
{
    public string VideoPath { get; init; } = "";
    public string? ThumbnailPath { get; init; }
    public TimeSpan Duration { get; init; }
    public long Bytes { get; init; }
    public bool Encrypted { get; init; }
}

public enum VideoPipelineStatus { Ok, Cancelled, TooLarge, TooLong, CannotTranscode, Unsupported }

public sealed class VideoPipelineResult
{
    public VideoPipelineStatus Status { get; init; }
    public VideoArtifact? Artifact { get; init; }
    public string? Message { get; init; }
    public bool Succeeded => Status == VideoPipelineStatus.Ok;
    public static VideoPipelineResult Ok(VideoArtifact artifact) => new() { Status = VideoPipelineStatus.Ok, Artifact = artifact };
    public static VideoPipelineResult Fail(VideoPipelineStatus status, string? message = null) => new() { Status = status, Message = message };
}

public sealed class VideoProbe
{
    public TimeSpan Duration { get; init; }
    public long Bytes { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

public sealed class VideoTranscodeRequest
{
    public int? MaxWidth { get; init; }
    public int? MaxHeight { get; init; }
    public long? MaxBytes { get; init; }
    public TimeSpan? MaxDuration { get; init; }
}

public sealed class VideoTranscodeResult
{
    public VideoPipelineStatus Status { get; init; }
    public string? OutputPath { get; init; }
    public string? Message { get; init; }
    public static VideoTranscodeResult Ok(string path) => new() { Status = VideoPipelineStatus.Ok, OutputPath = path };
    public static VideoTranscodeResult Fail(VideoPipelineStatus status, string? message = null) =>
        new() { Status = status, Message = message };
}

public interface IVideoProcessor
{
    Task<VideoProbe> ProbeAsync(string path, CancellationToken cancellationToken);
    Task<string?> CreateThumbnailAsync(string path, TimeSpan at, CancellationToken cancellationToken);
    Task<VideoTranscodeResult> TranscodeAsync(string path, VideoTranscodeRequest request, CancellationToken cancellationToken);
}

public interface IMediaUploader
{
    Task UploadAsync(VideoArtifact artifact, CancellationToken cancellationToken = default);
}

public interface IMediaVault
{
    Task StoreAsync(VideoArtifact artifact, CancellationToken cancellationToken = default);
}

public interface IVideoSource
{
    Task<string?> PickAsync(CancellationToken cancellationToken);
}

public sealed class FileVideoSource : IVideoSource
{
    public string? Path { get; set; }
    public Task<string?> PickAsync(CancellationToken cancellationToken) => Task.FromResult(Path);
}

public sealed class VideoPipelineBuilder
{
    readonly IVideoSource source;
    TimeSpan? maxDuration;
    int? maxWidth;
    int? maxHeight;
    long? maxBytes;
    TimeSpan thumbnailAt = TimeSpan.FromSeconds(1);
    bool stripMetadata = true;
    byte[]? key;
    IMediaUploader? uploader;
    IMediaVault? vault;
    IVideoProcessor processor = PlatformVideoProcessor.Create();

    internal VideoPipelineBuilder(IVideoSource source) => this.source = source;

    public VideoPipelineBuilder MaxDuration(TimeSpan value) { maxDuration = value; return this; }
    public VideoPipelineBuilder MaxResolution(int width, int height) { maxWidth = width; maxHeight = height; return this; }
    public VideoPipelineBuilder MaxBytes(long value) { maxBytes = value; return this; }
    public VideoPipelineBuilder ThumbnailAt(TimeSpan value) { thumbnailAt = value; return this; }
    public VideoPipelineBuilder StripMetadata(bool value = true) { stripMetadata = value; return this; }
    public VideoPipelineBuilder Encrypt(byte[] aesKey) { key = aesKey; return this; }
    public VideoPipelineBuilder UploadWith(IMediaUploader mediaUploader) { uploader = mediaUploader; return this; }
    public VideoPipelineBuilder StoreIn(IMediaVault mediaVault) { vault = mediaVault; return this; }
    public VideoPipelineBuilder UseProcessor(IVideoProcessor videoProcessor)
    {
        processor = videoProcessor ?? throw new ArgumentNullException(nameof(videoProcessor));
        return this;
    }

    public async Task<VideoPipelineResult> SaveAsync(CancellationToken cancellationToken = default)
    {
        var durationCap = maxDuration ?? VideoPipeline.RegisteredOptions?.DefaultMaxDuration;
        if (durationCap is { } duration && duration <= TimeSpan.Zero)
            return VideoPipelineResult.Fail(VideoPipelineStatus.TooLong, "MaxDuration must be positive.");
        if (maxBytes is { } bytes && bytes <= 0)
            return VideoPipelineResult.Fail(VideoPipelineStatus.TooLarge, "MaxBytes must be positive.");

        var path = await source.PickAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(path))
            return VideoPipelineResult.Fail(VideoPipelineStatus.Cancelled, "No video selected.");
        if (!File.Exists(path))
            return VideoPipelineResult.Fail(VideoPipelineStatus.Unsupported, "File does not exist.");

        var probe = await ProbeOrFileAsync(path, cancellationToken).ConfigureAwait(false);
        var overDuration = durationCap is { } maxDur && probe.Duration > TimeSpan.Zero && probe.Duration > maxDur;
        var overBytes = maxBytes is { } maxB && probe.Bytes > maxB;
        var overRes = maxWidth is { } mw && maxHeight is { } mh && probe.Width > 0 && probe.Height > 0
                      && (probe.Width > mw || probe.Height > mh);

        if (overDuration || overBytes || overRes)
        {
            var transcoded = await processor.TranscodeAsync(path, new VideoTranscodeRequest
            {
                MaxWidth = maxWidth,
                MaxHeight = maxHeight,
                MaxBytes = maxBytes,
                MaxDuration = durationCap
            }, cancellationToken).ConfigureAwait(false);

            if (transcoded.Status != VideoPipelineStatus.Ok || string.IsNullOrWhiteSpace(transcoded.OutputPath) || !File.Exists(transcoded.OutputPath))
            {
                if (transcoded.Status is VideoPipelineStatus.TooLarge or VideoPipelineStatus.TooLong)
                    return VideoPipelineResult.Fail(transcoded.Status, transcoded.Message);
                return VideoPipelineResult.Fail(VideoPipelineStatus.CannotTranscode, transcoded.Message ?? "Device cannot transcode this clip.");
            }

            path = transcoded.OutputPath;
            probe = await ProbeOrFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (durationCap is { } stillDur && probe.Duration > TimeSpan.Zero && probe.Duration > stillDur)
                return VideoPipelineResult.Fail(VideoPipelineStatus.TooLong, $"Duration is {probe.Duration}.");
            if (maxBytes is { } stillBytes && probe.Bytes > stillBytes)
                return VideoPipelineResult.Fail(VideoPipelineStatus.TooLarge, $"File is {probe.Bytes} bytes.");
        }

        var thumbnail = await processor.CreateThumbnailAsync(path, thumbnailAt, cancellationToken).ConfigureAwait(false);

        var work = path;
        var encrypted = false;
        if (key is not null)
        {
            work = path + ".vault";
            await EncryptFileAsync(path, work, key, cancellationToken).ConfigureAwait(false);
            encrypted = true;
        }

        var info = new FileInfo(work);
        var artifact = new VideoArtifact
        {
            VideoPath = work,
            ThumbnailPath = thumbnail,
            Duration = probe.Duration,
            Bytes = info.Length,
            Encrypted = encrypted
        };

        if (uploader is not null)
            await uploader.UploadAsync(artifact, cancellationToken).ConfigureAwait(false);
        if (vault is not null)
            await vault.StoreAsync(artifact, cancellationToken).ConfigureAwait(false);
        _ = stripMetadata;
        return VideoPipelineResult.Ok(artifact);
    }

    async Task<VideoProbe> ProbeOrFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var probe = await processor.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
            if (probe.Bytes > 0)
                return probe;
            return new VideoProbe
            {
                Duration = probe.Duration,
                Bytes = new FileInfo(path).Length,
                Width = probe.Width,
                Height = probe.Height
            };
        }
        catch
        {
            return new VideoProbe { Bytes = new FileInfo(path).Length };
        }
    }

    internal static async Task EncryptFileAsync(string sourcePath, string destPath, byte[] aesKey, CancellationToken cancellationToken)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(aesKey, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        await using var stream = File.Create(destPath);
        await stream.WriteAsync(nonce, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(ciphertext, cancellationToken).ConfigureAwait(false);
    }

    public static async Task DecryptFileAsync(string sourcePath, string destPath, byte[] aesKey, CancellationToken cancellationToken = default)
    {
        var blob = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var nonce = blob[..12];
        var tag = blob[12..28];
        var ciphertext = blob[28..];
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(aesKey, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        await File.WriteAllBytesAsync(destPath, plaintext, cancellationToken).ConfigureAwait(false);
    }
}

public static class VideoPipeline
{
    internal static VideoPipelineOptions? RegisteredOptions { get; set; }

    public static VideoPipelineBuilder FromFile(string path) => new(new FileVideoSource { Path = path });
    public static VideoPipelineBuilder FromSource(IVideoSource source) => new(new GuardedSource(source));
    public static VideoPipelineBuilder FromCamera() => new(new PickerVideoSource(true));
    public static VideoPipelineBuilder FromGallery() => new(new PickerVideoSource(false));
}

sealed class GuardedSource : IVideoSource
{
    readonly IVideoSource inner;
    public GuardedSource(IVideoSource inner) => this.inner = inner;
    public Task<string?> PickAsync(CancellationToken cancellationToken) => inner.PickAsync(cancellationToken);
}

public static class MauiAppBuilderExtensions
{
    public static MauiAppBuilder UseVideoPipeline(this MauiAppBuilder builder, Action<VideoPipelineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new VideoPipelineOptions();
        configure?.Invoke(options);
        VideoPipeline.RegisteredOptions = options;
        builder.Services.AddSingleton(options);
        return builder;
    }
}

sealed class PickerVideoSource : IVideoSource
{
    readonly bool camera;
    public PickerVideoSource(bool camera) => this.camera = camera;
    public async Task<string?> PickAsync(CancellationToken cancellationToken)
    {
        FileResult? result = camera
            ? await MediaPicker.Default.CaptureVideoAsync()
            : await MediaPicker.Default.PickVideoAsync();
        return result?.FullPath;
    }
}

sealed class SharedVideoProcessor : IVideoProcessor
{
    public static SharedVideoProcessor Instance { get; } = new();

    public Task<VideoProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        return Task.FromResult(new VideoProbe { Bytes = info.Exists ? info.Length : 0 });
    }

    public Task<string?> CreateThumbnailAsync(string path, TimeSpan at, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<VideoTranscodeResult> TranscodeAsync(string path, VideoTranscodeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(VideoTranscodeResult.Fail(VideoPipelineStatus.CannotTranscode, "No FFmpeg. This target cannot transcode."));
}

#if !ANDROID && !IOS && !MACCATALYST && !WINDOWS
sealed class PlatformVideoProcessor
{
    public static IVideoProcessor Create() => SharedVideoProcessor.Instance;
}
#endif
