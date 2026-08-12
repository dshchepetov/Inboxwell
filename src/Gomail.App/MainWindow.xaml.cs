using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.Storage;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Gomail_App;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    private bool allowClose;
    private bool isHiding;

    public IRelayCommand ShowWindowCommand { get; }
    public IRelayCommand ExitCommand { get; }

    public MainWindow()
    {
        ShowWindowCommand = new RelayCommand(ShowWindow);
        ExitCommand = new RelayCommand(ExitApplication);
        InitializeComponent();

        WindowRoot.RequestedTheme = (ApplicationData.Current.LocalSettings.Values["themePreference"] as string) switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 900;
            presenter.PreferredMinimumHeight = 620;
        }
        RestoreWindowBounds();
        AppWindow.Closing += OnWindowClosing;
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPositionChange || args.DidSizeChange) SaveWindowBounds();
        };

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }

    private async void ShowWindow()
    {
        this.Show();
        Activate();
        WindowRoot.Opacity = 0;
        await AnimateWindowOpacityAsync(1, 170);
    }

    private async void ExitApplication()
    {
        allowClose = true;
        SaveWindowBounds();
        await AnimateWindowOpacityAsync(0, 110);
        TrayIcon.Dispose();
        Close();
    }

    private async void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        SaveWindowBounds();
        var settings = ApplicationData.Current.LocalSettings.Values;
        var closeToTray = !settings.TryGetValue("closeToTray", out var value) || value is true;
        if (!allowClose && closeToTray)
        {
            args.Cancel = true;
            if (isHiding) return;
            isHiding = true;
            await AnimateWindowOpacityAsync(0, 120);
            this.Hide();
            WindowRoot.Opacity = 1;
            isHiding = false;
        }
        else
        {
            TrayIcon.Dispose();
        }
    }

    private Task AnimateWindowOpacityAsync(double to, int milliseconds)
    {
        var completion = new TaskCompletionSource();
        var storyboard = new Storyboard();
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            EasingFunction = new CubicEase { EasingMode = to > WindowRoot.Opacity ? EasingMode.EaseOut : EasingMode.EaseIn }
        };
        Storyboard.SetTarget(animation, WindowRoot);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) => completion.TrySetResult();
        storyboard.Begin();
        return completion.Task;
    }

    private void RestoreWindowBounds()
    {
        var settings = ApplicationData.Current.LocalSettings.Values;
        var width = ReadInt(settings, "windowWidth", 1440);
        var height = ReadInt(settings, "windowHeight", 900);
        width = Math.Clamp(width, 900, 3840);
        height = Math.Clamp(height, 620, 2160);
        if (settings.TryGetValue("windowX", out var xValue) && xValue is int x &&
            settings.TryGetValue("windowY", out var yValue) && yValue is int y &&
            x > -width + 100 && y > -height + 100)
        {
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        else
        {
            AppWindow.Resize(new SizeInt32(width, height));
        }
    }

    private void SaveWindowBounds()
    {
        if (AppWindow.Size.Width < 300 || AppWindow.Size.Height < 300) return;
        var settings = ApplicationData.Current.LocalSettings.Values;
        settings["windowX"] = AppWindow.Position.X;
        settings["windowY"] = AppWindow.Position.Y;
        settings["windowWidth"] = AppWindow.Size.Width;
        settings["windowHeight"] = AppWindow.Size.Height;
    }

    private static int ReadInt(IDictionary<string, object> settings, string key, int fallback) =>
        settings.TryGetValue(key, out var value) && value is int number ? number : fallback;
}
