namespace Plugin.Maui.VideoPipeline.Sample;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new NavigationPage(new MainPage()));
}
