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
        DraftsRoot.Loaded += async (_, _) => await ReloadAsync();
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
