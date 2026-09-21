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

sealed class RecordingVault : IMediaVault
{
    public VideoArtifact? Last { get; private set; }
    public Task StoreAsync(VideoArtifact artifact, CancellationToken cancellationToken = default)
    {
        Last = artifact;
        return Task.CompletedTask;
    }
}

sealed class FakeProcessor : IVideoProcessor
{
    public VideoProbe Probe { get; set; } = new() { Bytes = 11, Duration = TimeSpan.FromSeconds(12), Width = 1920, Height = 1080 };
    public VideoProbe? ProbeAfterTranscode { get; set; }
    public string? ThumbnailPath { get; set; }
    public VideoTranscodeResult Transcode { get; set; } = VideoTranscodeResult.Fail(VideoPipelineStatus.CannotTranscode, "fake");
    public int TranscodeCalls { get; private set; }
    public VideoTranscodeRequest? LastRequest { get; private set; }
    public bool ThrowOnProbe { get; set; }
    public bool ZeroProbeBytes { get; set; }

    public Task<VideoProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowOnProbe)
            throw new InvalidOperationException("probe failed");
        var probe = TranscodeCalls > 0 && ProbeAfterTranscode is { } next ? next : Probe;
        return Task.FromResult(new VideoProbe
        {
            Bytes = ZeroProbeBytes ? 0 : new FileInfo(path).Length,
            Duration = probe.Duration,
            Width = probe.Width,
            Height = probe.Height
        });
    }

    public Task<string?> CreateThumbnailAsync(string path, TimeSpan at, CancellationToken cancellationToken) =>
        Task.FromResult(ThumbnailPath);

    public Task<VideoTranscodeResult> TranscodeAsync(string path, VideoTranscodeRequest request, CancellationToken cancellationToken)
    {
        TranscodeCalls++;
        LastRequest = request;
        return Task.FromResult(Transcode);
    }
}

public sealed class VideoPipelineTests : IDisposable
{
    readonly string file = Path.Combine(Path.GetTempPath(), $"vp-{Guid.NewGuid():N}.bin");

    public VideoPipelineTests()
    {
        File.WriteAllBytes(file, "video-bytes"u8.ToArray());
        VideoPipeline.RegisteredOptions = null;
    }

    public void Dispose()
    {
        VideoPipeline.RegisteredOptions = null;
        try { File.Delete(file); File.Delete(file + ".vault"); } catch { }
    }

    [Fact]
    public async Task Save_encrypt_round_trip_and_upload()
    {
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var uploader = new RecordingUploader();
        var vault = new RecordingVault();
        var result = await VideoPipeline.FromFile(file)
            .MaxDuration(TimeSpan.FromSeconds(30))
            .MaxResolution(1280, 720)
            .MaxBytes(8 * 1024)
            .ThumbnailAt(TimeSpan.FromSeconds(1))
            .StripMetadata()
            .Encrypt(key)
            .UploadWith(uploader)
            .StoreIn(vault)
            .SaveAsync();
        Assert.True(result.Succeeded);
        Assert.True(result.Artifact!.Encrypted);
        Assert.Same(result.Artifact, uploader.Last);
        Assert.Same(result.Artifact, vault.Last);
        var decrypted = file + ".out";
        await VideoPipelineBuilder.DecryptFileAsync(result.Artifact.VideoPath, decrypted, key);
        Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(decrypted));
        File.Delete(decrypted);
    }

    [Fact]
    public async Task Over_budget_without_transcode_is_cannot_transcode()
    {
        var large = await VideoPipeline.FromFile(file).MaxBytes(3).SaveAsync();
        Assert.Equal(VideoPipelineStatus.CannotTranscode, large.Status);
        var missing = await VideoPipeline.FromFile(file + ".nope").SaveAsync();
        Assert.Equal(VideoPipelineStatus.Unsupported, missing.Status);
        var cancelled = await VideoPipeline.FromSource(new FileVideoSource()).SaveAsync();
        Assert.Equal(VideoPipelineStatus.Cancelled, cancelled.Status);
        var bad = await VideoPipeline.FromFile(file).MaxDuration(TimeSpan.Zero).SaveAsync();
        Assert.Equal(VideoPipelineStatus.TooLong, bad.Status);
        var badBytes = await VideoPipeline.FromFile(file).MaxBytes(0).SaveAsync();
        Assert.Equal(VideoPipelineStatus.TooLarge, badBytes.Status);
    }

    [Fact]
    public async Task Processor_writes_thumbnail_and_transcodes()
    {
        var smaller = Path.Combine(Path.GetTempPath(), $"vp-small-{Guid.NewGuid():N}.bin");
        var thumb = Path.Combine(Path.GetTempPath(), $"vp-thumb-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(smaller, "ok"u8.ToArray());
        File.WriteAllBytes(thumb, [1, 2, 3]);
        try
        {
            var processor = new FakeProcessor
            {
                Probe = new VideoProbe { Duration = TimeSpan.FromSeconds(20), Width = 1920, Height = 1080 },
                ThumbnailPath = thumb,
                Transcode = VideoTranscodeResult.Ok(smaller)
            };
            var result = await VideoPipeline.FromFile(file)
                .MaxBytes(3)
                .MaxResolution(1280, 720)
                .UseProcessor(processor)
                .SaveAsync();
            Assert.True(result.Succeeded);
            Assert.Equal(1, processor.TranscodeCalls);
            Assert.Equal(thumb, result.Artifact!.ThumbnailPath);
            Assert.Equal(smaller, result.Artifact.VideoPath);
            Assert.Equal(3, processor.LastRequest!.MaxBytes);
            Assert.Equal(1280, processor.LastRequest.MaxWidth);
        }
        finally
        {
            try { File.Delete(smaller); File.Delete(thumb); } catch { }
        }
    }

    [Fact]
    public async Task Over_duration_or_resolution_triggers_transcode()
    {
        var smaller = Path.Combine(Path.GetTempPath(), $"vp-dur-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(smaller, "ok"u8.ToArray());
        try
        {
            var processor = new FakeProcessor
            {
                Probe = new VideoProbe { Duration = TimeSpan.FromSeconds(40), Width = 1920, Height = 1080 },
                ProbeAfterTranscode = new VideoProbe { Duration = TimeSpan.FromSeconds(8), Width = 640, Height = 360 },
                Transcode = VideoTranscodeResult.Ok(smaller)
            };
            var duration = await VideoPipeline.FromFile(file)
                .MaxDuration(TimeSpan.FromSeconds(10))
                .UseProcessor(processor)
                .SaveAsync();
            Assert.True(duration.Succeeded);
            Assert.Equal(1, processor.TranscodeCalls);
            Assert.Equal(TimeSpan.FromSeconds(10), processor.LastRequest!.MaxDuration);

            processor = new FakeProcessor
            {
                Probe = new VideoProbe { Duration = TimeSpan.FromSeconds(5), Width = 3840, Height = 2160 },
                Transcode = VideoTranscodeResult.Ok(smaller)
            };
            var resolution = await VideoPipeline.FromFile(file)
                .MaxResolution(1280, 720)
                .UseProcessor(processor)
                .SaveAsync();
            Assert.True(resolution.Succeeded);
            Assert.Equal(1, processor.TranscodeCalls);
        }
        finally
        {
            try { File.Delete(smaller); } catch { }
        }
    }

    [Fact]
    public async Task Under_budget_skips_transcode()
    {
        var processor = new FakeProcessor
        {
            Probe = new VideoProbe { Duration = TimeSpan.FromSeconds(8), Width = 640, Height = 360 }
        };
        var result = await VideoPipeline.FromFile(file)
            .MaxDuration(TimeSpan.FromSeconds(30))
            .MaxBytes(8 * 1024)
            .MaxResolution(1280, 720)
            .UseProcessor(processor)
            .SaveAsync();
        Assert.True(result.Succeeded);
        Assert.Equal(0, processor.TranscodeCalls);
        Assert.Equal(file, result.Artifact!.VideoPath);
        Assert.False(result.Artifact.Encrypted);
    }

    [Fact]
    public async Task Transcode_that_stays_over_budget_is_too_large()
    {
        var processor = new FakeProcessor
        {
            Probe = new VideoProbe { Duration = TimeSpan.FromSeconds(8), Width = 640, Height = 360 },
            Transcode = VideoTranscodeResult.Ok(file)
        };
        var result = await VideoPipeline.FromFile(file).MaxBytes(3).UseProcessor(processor).SaveAsync();
        Assert.Equal(VideoPipelineStatus.TooLarge, result.Status);
    }

    [Fact]
    public async Task Transcode_that_stays_too_long_is_too_long()
    {
        var processor = new FakeProcessor
        {
            Probe = new VideoProbe { Duration = TimeSpan.FromSeconds(40), Width = 640, Height = 360 },
            Transcode = VideoTranscodeResult.Ok(file)
        };
        var result = await VideoPipeline.FromFile(file)
            .MaxDuration(TimeSpan.FromSeconds(10))
            .UseProcessor(processor)
            .SaveAsync();
        Assert.Equal(VideoPipelineStatus.TooLong, result.Status);
    }

    [Fact]
    public async Task Processor_typed_too_large_or_too_long_is_kept()
    {
        var large = new FakeProcessor
        {
            Probe = new VideoProbe { Duration = TimeSpan.FromSeconds(8), Width = 1920, Height = 1080 },
            Transcode = VideoTranscodeResult.Fail(VideoPipelineStatus.TooLarge, "still huge")
        };
        Assert.Equal(VideoPipelineStatus.TooLarge,
            (await VideoPipeline.FromFile(file).MaxBytes(3).UseProcessor(large).SaveAsync()).Status);

        var longClip = new FakeProcessor
        {
            Probe = new VideoProbe { Duration = TimeSpan.FromSeconds(90), Width = 640, Height = 360 },
            Transcode = VideoTranscodeResult.Fail(VideoPipelineStatus.TooLong, "still long")
        };
        Assert.Equal(VideoPipelineStatus.TooLong,
            (await VideoPipeline.FromFile(file).MaxDuration(TimeSpan.FromSeconds(10)).UseProcessor(longClip).SaveAsync()).Status);
    }

    [Fact]
    public async Task Missing_transcode_output_is_cannot_transcode()
    {
        var processor = new FakeProcessor
        {
            Probe = new VideoProbe { Duration = TimeSpan.FromSeconds(8), Width = 1920, Height = 1080 },
            Transcode = VideoTranscodeResult.Ok(file + ".missing")
        };
        var result = await VideoPipeline.FromFile(file).MaxBytes(3).UseProcessor(processor).SaveAsync();
        Assert.Equal(VideoPipelineStatus.CannotTranscode, result.Status);
    }

    [Fact]
    public async Task Shared_processor_cannot_transcode_or_thumbnail()
    {
        var probe = await SharedVideoProcessor.Instance.ProbeAsync(file, CancellationToken.None);
        Assert.Equal(11, probe.Bytes);
        Assert.Null(await SharedVideoProcessor.Instance.CreateThumbnailAsync(file, TimeSpan.FromSeconds(1), CancellationToken.None));
        var transcode = await SharedVideoProcessor.Instance.TranscodeAsync(file, new VideoTranscodeRequest { MaxBytes = 3 }, CancellationToken.None);
        Assert.Equal(VideoPipelineStatus.CannotTranscode, transcode.Status);
    }

    [Fact]
    public async Task Probe_fallback_uses_file_length()
    {
        var throwing = new FakeProcessor { ThrowOnProbe = true };
        var result = await VideoPipeline.FromFile(file).UseProcessor(throwing).SaveAsync();
        Assert.True(result.Succeeded);
        Assert.Equal(11, result.Artifact!.Bytes);

        var zero = new FakeProcessor { ZeroProbeBytes = true, Probe = new VideoProbe { Duration = TimeSpan.FromSeconds(4) } };
        var filled = await VideoPipeline.FromFile(file).UseProcessor(zero).SaveAsync();
        Assert.True(filled.Succeeded);
        Assert.Equal(11, filled.Artifact!.Bytes);
    }

    [Fact]
    public async Task Registered_default_duration_caps_overlong_clips()
    {
        VideoPipeline.RegisteredOptions = new VideoPipelineOptions { DefaultMaxDuration = TimeSpan.FromSeconds(10) };
        var processor = new FakeProcessor
        {
            Probe = new VideoProbe { Duration = TimeSpan.FromSeconds(40), Width = 640, Height = 360 },
            Transcode = VideoTranscodeResult.Fail(VideoPipelineStatus.CannotTranscode, "no encoder")
        };
        var result = await VideoPipeline.FromFile(file).UseProcessor(processor).SaveAsync();
        Assert.Equal(VideoPipelineStatus.CannotTranscode, result.Status);
        Assert.Equal(1, processor.TranscodeCalls);
        Assert.Equal(TimeSpan.FromSeconds(10), processor.LastRequest!.MaxDuration);
    }

    [Fact]
    public void UseProcessor_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => VideoPipeline.FromFile(file).UseProcessor(null!));
    }

    [Fact]
    public async Task Decrypt_rejects_wrong_key()
    {
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var other = Enumerable.Range(2, 32).Select(i => (byte)i).ToArray();
        var vault = file + ".vault";
        await VideoPipelineBuilder.EncryptFileAsync(file, vault, key, CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(() => VideoPipelineBuilder.DecryptFileAsync(vault, file + ".bad", other));
    }
}
