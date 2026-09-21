using Plugin.Maui.VideoPipeline;

namespace Plugin.Maui.VideoPipeline.Sample;

sealed class SampleUploader : IMediaUploader
{
    public VideoArtifact? Last { get; private set; }
    public Task UploadAsync(VideoArtifact artifact, CancellationToken cancellationToken = default)
    {
        Last = artifact;
        return Task.CompletedTask;
    }
}

public partial class MainPage : ContentPage
{
    readonly Switch encrypt = new() { IsToggled = true };
    readonly Label log = new() { LineBreakMode = LineBreakMode.WordWrap };
    readonly Image thumb = new() { HeightRequest = 160, Aspect = Aspect.AspectFit, IsVisible = false };
    readonly SampleUploader uploader = new();
    VideoArtifact? last;

    public MainPage()
    {
        InitializeComponent();
        Root.Children.Add(new HorizontalStackLayout
        {
            Spacing = 12,
            Children =
            {
                new Label { Text = "Encrypt AES-256-GCM", VerticalOptions = LayoutOptions.Center },
                encrypt
            }
        });
        Root.Children.Add(new Button { Text = "From camera", Command = new Command(async () => await Run(true)) });
        Root.Children.Add(new Button { Text = "From gallery", Command = new Command(async () => await Run(false)) });
        Root.Children.Add(new Button { Text = "Mock upload last artifact", Command = new Command(async () => await Upload()) });
        Root.Children.Add(thumb);
        Root.Children.Add(log);
    }

    async Task Run(bool camera)
    {
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var builder = camera ? VideoPipeline.FromCamera() : VideoPipeline.FromGallery();
        builder
            .MaxDuration(TimeSpan.FromSeconds(30))
            .MaxResolution(1280, 720)
            .MaxBytes(12 * 1024 * 1024)
            .ThumbnailAt(TimeSpan.FromSeconds(1))
            .StripMetadata();
        if (encrypt.IsToggled)
            builder.Encrypt(key);

        var result = await builder.SaveAsync();
        last = result.Artifact;
        if (result.Succeeded && result.Artifact is { } artifact)
        {
            log.Text =
                $"{artifact.Bytes} bytes duration={artifact.Duration} encrypted={artifact.Encrypted}\n{artifact.VideoPath}";
            if (artifact.ThumbnailPath is { } path && File.Exists(path))
            {
                thumb.Source = ImageSource.FromFile(path);
                thumb.IsVisible = true;
            }
            else
            {
                thumb.IsVisible = false;
                log.Text += "\nno thumbnail (OS could not decode a frame)";
            }
        }
        else
        {
            thumb.IsVisible = false;
            log.Text = result.Status == VideoPipelineStatus.CannotTranscode
                ? $"{result.Status} {result.Message} — no FFmpeg; treat this as a typed result."
                : $"{result.Status} {result.Message}";
        }
    }

    async Task Upload()
    {
        if (last is null)
        {
            log.Text = "No artifact yet.";
            return;
        }

        await uploader.UploadAsync(last);
        log.Text = $"mock uploaded {uploader.Last!.Bytes} bytes encrypted={uploader.Last.Encrypted}";
    }
}
