using Microsoft.Extensions.Logging;
using Plugin.Maui.VideoPipeline;

namespace Plugin.Maui.VideoPipeline.Sample;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.Services.AddSingleton<MainPage>();
        builder.UseMauiApp<App>()
            .UseVideoPipeline(o => o.DefaultMaxDuration = TimeSpan.FromSeconds(30));
#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
