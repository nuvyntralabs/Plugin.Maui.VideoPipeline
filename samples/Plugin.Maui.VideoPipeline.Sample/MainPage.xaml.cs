using Plugin.Maui.VideoPipeline;

namespace Plugin.Maui.VideoPipeline.Sample;

public partial class MainPage : ContentPage
{
    readonly Label log = new();

    public MainPage()
    {
        InitializeComponent();
        Root.Children.Add(new Button { Text = "From camera", Command = new Command(async () => await Run(true)) });
        Root.Children.Add(new Button { Text = "From gallery", Command = new Command(async () => await Run(false)) });
        Root.Children.Add(log);
    }

    async Task Run(bool camera)
    {
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var builder = camera ? VideoPipeline.FromCamera() : VideoPipeline.FromGallery();
        var result = await builder
            .MaxDuration(TimeSpan.FromSeconds(30))
            .MaxResolution(1280, 720)
            .MaxBytes(12 * 1024 * 1024)
            .ThumbnailAt(TimeSpan.FromSeconds(1))
            .StripMetadata()
            .Encrypt(key)
            .SaveAsync();
        log.Text = result.Succeeded
            ? $"{result.Artifact!.Bytes} bytes encrypted={result.Artifact.Encrypted}\n{result.Artifact.VideoPath}"
            : $"{result.Status} {result.Message}";
    }
}
