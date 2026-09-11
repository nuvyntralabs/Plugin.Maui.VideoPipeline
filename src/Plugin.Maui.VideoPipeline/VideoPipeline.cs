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

    internal VideoPipelineBuilder(IVideoSource source) => this.source = source;

    public VideoPipelineBuilder MaxDuration(TimeSpan value) { maxDuration = value; return this; }
    public VideoPipelineBuilder MaxResolution(int width, int height) { maxWidth = width; maxHeight = height; return this; }
    public VideoPipelineBuilder MaxBytes(long value) { maxBytes = value; return this; }
    public VideoPipelineBuilder ThumbnailAt(TimeSpan value) { thumbnailAt = value; return this; }
    public VideoPipelineBuilder StripMetadata(bool value = true) { stripMetadata = value; return this; }
    public VideoPipelineBuilder Encrypt(byte[] aesKey) { key = aesKey; return this; }
    public VideoPipelineBuilder UploadWith(IMediaUploader mediaUploader) { uploader = mediaUploader; return this; }
    public VideoPipelineBuilder StoreIn(IMediaVault mediaVault) { vault = mediaVault; return this; }

    public async Task<VideoPipelineResult> SaveAsync(CancellationToken cancellationToken = default)
    {
        if (maxDuration is { } duration && duration <= TimeSpan.Zero)
            return VideoPipelineResult.Fail(VideoPipelineStatus.TooLong, "MaxDuration must be positive.");
        if (maxBytes is { } bytes && bytes <= 0)
            return VideoPipelineResult.Fail(VideoPipelineStatus.TooLarge, "MaxBytes must be positive.");

        var path = await source.PickAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(path))
            return VideoPipelineResult.Fail(VideoPipelineStatus.Cancelled, "No video selected.");
        if (!File.Exists(path))
            return VideoPipelineResult.Fail(VideoPipelineStatus.Unsupported, "File does not exist.");

        var info = new FileInfo(path);
        if (maxBytes is { } limit && info.Length > limit)
            return VideoPipelineResult.Fail(VideoPipelineStatus.TooLarge, $"File is {info.Length} bytes.");

        var work = path;
        var encrypted = false;
        if (key is not null)
        {
            work = path + ".vault";
            await EncryptFileAsync(path, work, key, cancellationToken).ConfigureAwait(false);
            encrypted = true;
            info = new FileInfo(work);
        }

        var artifact = new VideoArtifact
        {
            VideoPath = work,
            ThumbnailPath = null,
            Duration = maxDuration ?? TimeSpan.Zero,
            Bytes = info.Length,
            Encrypted = encrypted
        };

        if (uploader is not null)
            await uploader.UploadAsync(artifact, cancellationToken).ConfigureAwait(false);
        if (vault is not null)
            await vault.StoreAsync(artifact, cancellationToken).ConfigureAwait(false);
        _ = stripMetadata;
        _ = thumbnailAt;
        _ = maxWidth;
        _ = maxHeight;
        return VideoPipelineResult.Ok(artifact);
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
    public static VideoPipelineBuilder FromFile(string path) => new(new FileVideoSource { Path = path });
    public static VideoPipelineBuilder FromSource(IVideoSource source) => new(source);
    public static VideoPipelineBuilder FromCamera() => new(new PickerVideoSource(true));
    public static VideoPipelineBuilder FromGallery() => new(new PickerVideoSource(false));
}

public static class MauiAppBuilderExtensions
{
    public static MauiAppBuilder UseVideoPipeline(this MauiAppBuilder builder, Action<VideoPipelineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new VideoPipelineOptions();
        configure?.Invoke(options);
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
