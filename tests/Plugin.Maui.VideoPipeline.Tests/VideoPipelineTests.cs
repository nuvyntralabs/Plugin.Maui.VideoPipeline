using Plugin.Maui.VideoPipeline;

namespace Plugin.Maui.VideoPipeline.Tests;

sealed class RecordingUploader : IMediaUploader
{
    public VideoArtifact? Last { get; private set; }
    public Task UploadAsync(VideoArtifact artifact, CancellationToken cancellationToken = default)
    {
        Last = artifact;
        return Task.CompletedTask;
    }
}

public sealed class VideoPipelineTests : IDisposable
{
    readonly string file = Path.Combine(Path.GetTempPath(), $"vp-{Guid.NewGuid():N}.bin");

    public VideoPipelineTests() => File.WriteAllBytes(file, "video-bytes"u8.ToArray());
    public void Dispose() { try { File.Delete(file); File.Delete(file + ".vault"); } catch { } }

    [Fact]
    public async Task Save_encrypt_round_trip_and_upload()
    {
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var uploader = new RecordingUploader();
        var result = await VideoPipeline.FromFile(file)
            .MaxDuration(TimeSpan.FromSeconds(30))
            .MaxResolution(1280, 720)
            .MaxBytes(8 * 1024)
            .ThumbnailAt(TimeSpan.FromSeconds(1))
            .StripMetadata()
            .Encrypt(key)
            .UploadWith(uploader)
            .SaveAsync();
        Assert.True(result.Succeeded);
        Assert.True(result.Artifact!.Encrypted);
        Assert.Same(result.Artifact, uploader.Last);
        var decrypted = file + ".out";
        await VideoPipelineBuilder.DecryptFileAsync(result.Artifact.VideoPath, decrypted, key);
        Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(decrypted));
        File.Delete(decrypted);
    }

    [Fact]
    public async Task Too_large_and_missing_file_fail()
    {
        var large = await VideoPipeline.FromFile(file).MaxBytes(3).SaveAsync();
        Assert.Equal(VideoPipelineStatus.TooLarge, large.Status);
        var missing = await VideoPipeline.FromFile(file + ".nope").SaveAsync();
        Assert.Equal(VideoPipelineStatus.Unsupported, missing.Status);
        var cancelled = await VideoPipeline.FromSource(new FileVideoSource()).SaveAsync();
        Assert.Equal(VideoPipelineStatus.Cancelled, cancelled.Status);
        var bad = await VideoPipeline.FromFile(file).MaxDuration(TimeSpan.Zero).SaveAsync();
        Assert.Equal(VideoPipelineStatus.TooLong, bad.Status);
    }
}
