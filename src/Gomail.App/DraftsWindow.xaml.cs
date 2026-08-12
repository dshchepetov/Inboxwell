using Gomail.Core;
using Gomail_App.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace Gomail_App;

public sealed partial class DraftsWindow : Window
{
    private readonly MainPageViewModel viewModel;
    private bool allowClose;

    public event Action<Draft>? DraftOpenRequested;

    public DraftsWindow(MainPageViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DraftsTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(900, 640));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 640;
            presenter.PreferredMinimumHeight = 480;
        }
        AppWindow.Closing += Window_Closing;
        DraftsRoot.Loaded += async (_, _) => { await ReloadAsync(); BeginEntranceAnimation(); };
    }

    private async Task ReloadAsync()
    {
        var drafts = await viewModel.GetDraftsAsync(viewModel.SelectedAccount?.Model?.Id);
        var rows = drafts.Select(static item => new DraftRowItem(item)).ToArray();
        DraftsList.ItemsSource = rows;
        DraftsList.SelectedItem = rows.FirstOrDefault();
        DraftsList.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteButton.IsEnabled = rows.Length > 0;
        RetryButton.IsEnabled = rows.Length > 0;
        OpenButton.IsEnabled = rows.Length > 0;
        DraftCountText.Text = rows.Length == 0 ? "No saved or queued messages" : $"{rows.Length} saved or queued message{(rows.Length == 1 ? string.Empty : "s")}";
    }

    private void BeginEntranceAnimation()
    {
        var storyboard = new Storyboard();
        var opacity = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(200), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var offset = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(240), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(opacity, DraftsRoot); Storyboard.SetTargetProperty(opacity, "Opacity");
        Storyboard.SetTarget(offset, DraftsTransform); Storyboard.SetTargetProperty(offset, "TranslateY");
        storyboard.Children.Add(opacity); storyboard.Children.Add(offset); storyboard.Begin();
    }

    private async Task CloseAnimatedAsync()
    {
        var completion = new TaskCompletionSource();
        var storyboard = new Storyboard();
        var animation = new DoubleAnimation { To = 0, Duration = TimeSpan.FromMilliseconds(120) };
        Storyboard.SetTarget(animation, DraftsRoot); Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation); storyboard.Completed += (_, _) => completion.TrySetResult(); storyboard.Begin();
        await completion.Task; allowClose = true; Close();
    }

    private async void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args) { if (allowClose) return; args.Cancel = true; await CloseAnimatedAsync(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (DraftsList.SelectedItem is DraftRowItem row) DraftOpenRequested?.Invoke(row.Draft);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (DraftsList.SelectedItem is not DraftRowItem row) return;
        await viewModel.DeleteDraftAsync(row.Draft.Id);
        DraftStatus.Text = "Draft deleted";
        await ReloadAsync();
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (DraftsList.SelectedItem is not DraftRowItem row) return;
        DraftStatus.Text = "Retrying…";
        try
        {
            var editable = await viewModel.PrepareDraftForEditingAsync(row.Draft);
            var remaining = await viewModel.QueueDraftForSendAsync(editable);
            DraftStatus.Text = remaining is null ? "Message sent" : remaining.DeliveryState == DraftDeliveryState.Failed ? remaining.LastError ?? "Send failed" : "Message queued";
        }
        catch (Exception exception)
        {
            DraftStatus.Text = exception.Message;
        }
        await ReloadAsync();
    }
}

internal sealed class DraftRowItem
{
    public DraftRowItem(Draft draft)
    {
        Draft = draft;
        Subject = string.IsNullOrWhiteSpace(draft.Subject) ? "(No subject)" : draft.Subject;
        Recipients = draft.To.Count == 0 ? "No recipient yet" : "To: " + string.Join(", ", draft.To.Select(static item => item.DisplayName));
        State = draft.DeliveryState switch { DraftDeliveryState.Queued => "Queued", DraftDeliveryState.Sending => "Sending", DraftDeliveryState.Failed => "Needs attention", _ => "Draft" };
        Error = draft.LastError ?? string.Empty;
        Updated = draft.UpdatedAt.ToLocalTime().ToString("g");
    }
    public Draft Draft { get; }
    public string Subject { get; }
    public string Recipients { get; }
    public string State { get; }
    public string Error { get; }
    public string Updated { get; }
}
