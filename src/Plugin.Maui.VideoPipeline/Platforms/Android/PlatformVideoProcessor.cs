#if ANDROID
using Android.Graphics;
using Android.Media;
using Path = System.IO.Path;

namespace Plugin.Maui.VideoPipeline;

sealed class PlatformVideoProcessor : IVideoProcessor
{
    public static IVideoProcessor Create() => new PlatformVideoProcessor();

    public Task<VideoProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(path);
        using var retriever = new MediaMetadataRetriever();
        try
        {
            retriever.SetDataSource(path);
            var durationMs = ParseLong(retriever.ExtractMetadata(MetadataKey.Duration));
            var width = ParseInt(retriever.ExtractMetadata(MetadataKey.VideoWidth));
            var height = ParseInt(retriever.ExtractMetadata(MetadataKey.VideoHeight));
            return Task.FromResult(new VideoProbe
            {
                Bytes = info.Length,
                Duration = durationMs > 0 ? TimeSpan.FromMilliseconds(durationMs) : TimeSpan.Zero,
                Width = width,
                Height = height
            });
        }
        catch
        {
            return Task.FromResult(new VideoProbe { Bytes = info.Exists ? info.Length : 0 });
        }
    }

    public Task<string?> CreateThumbnailAsync(string path, TimeSpan at, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var retriever = new MediaMetadataRetriever();
            retriever.SetDataSource(path);
            var us = (long)Math.Max(0, at.TotalMicroseconds);
            using var bitmap = retriever.GetFrameAtTime(us, Android.Media.Option.ClosestSync);
            if (bitmap is null)
                return Task.FromResult<string?>(null);

            var dest = Path.Combine(Path.GetTempPath(), $"vp-thumb-{Guid.NewGuid():N}.jpg");
            using var stream = File.Create(dest);
            if (Bitmap.CompressFormat.Jpeg is not { } jpeg)
                return Task.FromResult<string?>(null);
            bitmap.Compress(jpeg, 85, stream);
            return Task.FromResult<string?>(dest);
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task<VideoTranscodeResult> TranscodeAsync(string path, VideoTranscodeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var dest = Path.Combine(Path.GetTempPath(), $"vp-xcode-{Guid.NewGuid():N}.mp4");
            if (!AndroidVideoTranscoder.TryTranscode(path, dest, request, cancellationToken))
                return Task.FromResult(VideoTranscodeResult.Fail(VideoPipelineStatus.CannotTranscode, "MediaCodec could not transcode this clip."));
            return Task.FromResult(VideoTranscodeResult.Ok(dest));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(VideoTranscodeResult.Fail(VideoPipelineStatus.CannotTranscode, ex.Message));
        }
    }

    static long ParseLong(string? value) => long.TryParse(value, out var n) ? n : 0;
    static int ParseInt(string? value) => int.TryParse(value, out var n) ? n : 0;
}

static class AndroidVideoTranscoder
{
    public static bool TryTranscode(string sourcePath, string destPath, VideoTranscodeRequest request, CancellationToken cancellationToken)
    {
        MediaExtractor? extractor = null;
        MediaMuxer? muxer = null;
        try
        {
            extractor = new MediaExtractor();
            extractor.SetDataSource(sourcePath);
            var videoTrack = FindTrack(extractor, "video/");
            if (videoTrack < 0)
                return false;

            extractor.SelectTrack(videoTrack);
            var format = extractor.GetTrackFormat(videoTrack);
            var mime = format.GetString(MediaFormat.KeyMime);
            if (string.IsNullOrWhiteSpace(mime))
                return false;

            var width = format.ContainsKey(MediaFormat.KeyWidth) ? format.GetInteger(MediaFormat.KeyWidth) : 1280;
            var height = format.ContainsKey(MediaFormat.KeyHeight) ? format.GetInteger(MediaFormat.KeyHeight) : 720;
            var outWidth = request.MaxWidth is { } mw && mw > 0 ? Math.Min(width, mw) : width;
            var outHeight = request.MaxHeight is { } mh && mh > 0 ? Math.Min(height, mh) : height;
            if (outWidth % 2 != 0) outWidth--;
            if (outHeight % 2 != 0) outHeight--;
            if (outWidth < 16 || outHeight < 16)
                return false;

            using var decoder = MediaCodec.CreateDecoderByType(mime);
            using var encoder = MediaCodec.CreateEncoderByType(MediaFormat.MimetypeVideoAvc);
            if (decoder is null || encoder is null)
                return false;

            var outFormat = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, outWidth, outHeight);
            outFormat.SetInteger(MediaFormat.KeyBitRate, 1_500_000);
            outFormat.SetInteger(MediaFormat.KeyFrameRate, 30);
            outFormat.SetInteger(MediaFormat.KeyIFrameInterval, 1);
            outFormat.SetInteger(MediaFormat.KeyColorFormat, 0x7F420888); // COLOR_FormatYUV420Flexible

            encoder.Configure(outFormat, null, null, MediaCodecConfigFlags.Encode);
            decoder.Configure(format, null, null, MediaCodecConfigFlags.None);
            encoder.Start();
            decoder.Start();

            muxer = new MediaMuxer(destPath, MuxerOutputType.Mpeg4);
            var muxerStarted = false;
            var videoMuxTrack = -1;
            var eosIn = false;
            var eosOut = false;
            var pendingEncoderEos = false;
            var bufferInfo = new MediaCodec.BufferInfo();
            var maxUs = request.MaxDuration is { } cap && cap > TimeSpan.Zero ? (long)cap.TotalMicroseconds : long.MaxValue;
            const int maxLoops = 50_000;

            for (var loops = 0; !eosOut && loops < maxLoops; loops++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!eosIn)
                {
                    var inIndex = decoder.DequeueInputBuffer(10_000);
                    if (inIndex >= 0)
                    {
                        var input = decoder.GetInputBuffer(inIndex);
                        if (input is null)
                            break;
                        var sample = extractor.ReadSampleData(input, 0);
                        if (sample < 0 || extractor.SampleTime > maxUs)
                        {
                            decoder.QueueInputBuffer(inIndex, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                            eosIn = true;
                        }
                        else
                        {
                            decoder.QueueInputBuffer(inIndex, 0, sample, extractor.SampleTime, 0);
                            extractor.Advance();
                        }
                    }
                }

                var outIndex = decoder.DequeueOutputBuffer(bufferInfo, 10_000);
                if (outIndex >= 0)
                {
                    var decoded = decoder.GetOutputBuffer(outIndex);
                    var encIn = encoder.DequeueInputBuffer(10_000);
                    var isEos = (bufferInfo.Flags & MediaCodecBufferFlags.EndOfStream) != 0;
                    if (decoded is not null && encIn >= 0)
                    {
                        var encBuf = encoder.GetInputBuffer(encIn);
                        if (encBuf is not null)
                        {
                            encBuf.Clear();
                            var copy = Math.Min(decoded.Remaining(), encBuf.Remaining());
                            var temp = new byte[copy];
                            decoded.Get(temp);
                            encBuf.Put(temp);
                            encoder.QueueInputBuffer(encIn, 0, copy, bufferInfo.PresentationTimeUs,
                                isEos ? MediaCodecBufferFlags.EndOfStream : MediaCodecBufferFlags.None);
                        }
                        else if (isEos)
                        {
                            pendingEncoderEos = true;
                        }
                    }
                    else if (isEos)
                    {
                        pendingEncoderEos = true;
                    }
                    decoder.ReleaseOutputBuffer(outIndex, false);
                }

                if (pendingEncoderEos)
                {
                    var encIn = encoder.DequeueInputBuffer(10_000);
                    if (encIn >= 0)
                    {
                        encoder.QueueInputBuffer(encIn, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                        pendingEncoderEos = false;
                    }
                }

                var encOut = encoder.DequeueOutputBuffer(bufferInfo, 10_000);
                if (encOut == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    if (muxerStarted)
                        return false;
                    videoMuxTrack = muxer.AddTrack(encoder.OutputFormat);
                    muxer.Start();
                    muxerStarted = true;
                }
                else if (encOut >= 0)
                {
                    if (!muxerStarted)
                        return false;
                    var encoded = encoder.GetOutputBuffer(encOut);
                    if (encoded is not null && bufferInfo.Size > 0)
                    {
                        encoded.Position(bufferInfo.Offset);
                        encoded.Limit(bufferInfo.Offset + bufferInfo.Size);
                        muxer.WriteSampleData(videoMuxTrack, encoded, bufferInfo);
                    }

                    eosOut = (bufferInfo.Flags & MediaCodecBufferFlags.EndOfStream) != 0;
                    encoder.ReleaseOutputBuffer(encOut, false);
                }
            }

            if (!eosOut)
                return false;

            return muxerStarted && File.Exists(destPath) && new FileInfo(destPath).Length > 0;
        }
        catch
        {
            try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
            return false;
        }
        finally
        {
            try { muxer?.Stop(); } catch { }
            muxer?.Release();
            extractor?.Release();
        }
    }

    static int FindTrack(MediaExtractor extractor, string prefix)
    {
        for (var i = 0; i < extractor.TrackCount; i++)
        {
            var mime = extractor.GetTrackFormat(i).GetString(MediaFormat.KeyMime);
            if (mime is not null && mime.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }
}
#endif
