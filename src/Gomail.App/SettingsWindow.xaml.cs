using Gomail.Core;
using Gomail_App.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using System.Reflection;
using System.Globalization;
using Windows.ApplicationModel;
using Windows.Graphics;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;

namespace Gomail_App;

public sealed partial class SettingsWindow : Window
{
    private readonly MainPageViewModel viewModel;
    private readonly string initialPage;
    private readonly IDictionary<string, object> localSettings = ApplicationData.Current.LocalSettings.Values;
    private readonly Dictionary<MailShortcutCommand, KeyboardShortcutGesture?> pendingShortcuts = new();
    private readonly Dictionary<MailShortcutCommand, TextBox> shortcutBoxes = new();
    private bool loadingSignature;
    private bool updatingSignatureFontSize;

    public event EventHandler? SettingsChanged;
    public event EventHandler? AddAccountRequested;

    public SettingsWindow(MainPageViewModel viewModel, string initialPage = "general")
    {
        this.viewModel = viewModel;
        this.initialPage = initialPage;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(SettingsTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1120, 760));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 720;
            presenter.PreferredMinimumHeight = 600;
        }
        SettingsRoot.Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        NotificationsToggle.IsOn = !localSettings.TryGetValue("notificationsEnabled", out var notifications) || notifications is true;
        CloseToTrayToggle.IsOn = !localSettings.TryGetValue("closeToTray", out var tray) || tray is true;
        BlockRemoteImagesToggle.IsOn = localSettings.TryGetValue("blockRemoteImages", out var blockImages) && blockImages is true;
        SelectByTag(ReadingPanePicker, localSettings["readingPanePosition"] as string ?? "right");
        SelectByTag(ThemePicker, localSettings["themePreference"] as string ?? "system");
        LoadShortcutRows();
        LoadAboutInformation();
        CompactNavigation.SelectedIndex = 0;
        var navigationItem = SettingsNav.Items.OfType<ListViewItem>().FirstOrDefault(item => item.Tag as string == initialPage);
        if (navigationItem is not null) SettingsNav.SelectedItem = navigationItem;
        var compactItem = CompactNavigation.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag as string == initialPage);
        if (compactItem is not null) CompactNavigation.SelectedItem = compactItem;
        ShowPage(initialPage);
        RefreshAccounts();
        await LoadSignatureAccountsAsync();
    }

    private void SettingsNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsNav.SelectedItem is ListViewItem { Tag: string tag }) ShowPage(tag);
    }

    private void CompactNavigation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CompactNavigation.SelectedItem is ComboBoxItem { Tag: string tag }) ShowPage(tag);
    }

    private void ShowPage(string tag)
    {
        if (GeneralPage is null || AccountsPage is null || SignaturesPage is null || ShortcutsPage is null || IntegrationsPage is null || PrivacyPage is null || AboutPage is null) return;
        GeneralPage.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        AccountsPage.Visibility = tag == "accounts" ? Visibility.Visible : Visibility.Collapsed;
        SignaturesPage.Visibility = tag == "signatures" ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPage.Visibility = tag == "shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        IntegrationsPage.Visibility = tag == "integrations" ? Visibility.Visible : Visibility.Collapsed;
        PrivacyPage.Visibility = tag == "privacy" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadAboutInformation()
    {
        try
        {
            var identity = Package.Current.Id;
            var version = identity.Version;
            var packageVersion = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            var displayVersion = version.Revision == 0
                ? $"{version.Major}.{version.Minor}.{version.Build}"
                : packageVersion;
            AboutVersionText.Text = $"Version {displayVersion}";
            AboutPackageVersionText.Text = packageVersion;
            AboutArchitectureText.Text = identity.Architecture.ToString();
        }
        catch
        {
            var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
            AboutVersionText.Text = $"Version {assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
            AboutPackageVersionText.Text = assemblyVersion.ToString();
            AboutArchitectureText.Text = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        }
    }

    private void RefreshAccounts()
    {
        var selectedId = (AccountsList.SelectedItem as AccountItem)?.Model?.Id;
        var items = viewModel.Accounts.Where(static item => item.Model is not null).ToArray();
        AccountsList.ItemsSource = items;
        AccountsList.SelectedItem = items.FirstOrDefault(item => item.Model?.Id == selectedId) ?? items.FirstOrDefault();
    }

    private async void RenameAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is not AccountItem { Model: { } account } selected) return;

        var nameBox = new TextBox
        {
            Header = "Mailbox name",
            Text = selected.DisplayName,
            MaxLength = 80,
            TextAlignment = TextAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Style = (Style)Application.Current.Resources["GomailFieldStyle"]
        };
        var content = new StackPanel { Spacing = 10, MinWidth = 420 };
        content.Children.Add(new TextBlock
        {
            Text = "This name is shown only inside Inboxwell. It does not change the sender name recipients see.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.68
        });
        content.Children.Add(nameBox);
        content.Children.Add(new TextBlock
        {
            Text = $"Account: {account.Email}",
            FontSize = 11,
            Opacity = 0.55
        });

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = "Rename mailbox",
            Content = content,
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Use automatic name",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return;

        await viewModel.RenameAccountAsync(account, result == ContentDialogResult.Secondary ? null : nameBox.Text);
        SettingsStatus.Text = result == ContentDialogResult.Secondary ? "Mailbox name reset" : "Mailbox renamed";
        RefreshAccounts();
        await LoadSignatureAccountsAsync();
    }

    private async void ReconnectAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is not AccountItem { Model: { } account }) return;
        SettingsStatus.Text = $"Reconnecting {account.Email}…";
        try
        {
            await viewModel.ReconnectAccountAsync(account);
            SettingsStatus.Text = "Account connected and synchronized";
            RefreshAccounts();
        }
        catch (Exception exception)
        {
            SettingsStatus.Text = "Could not reconnect: " + exception.Message;
        }
    }

    private async void ToggleAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is not AccountItem { Model: { } account }) return;
        await viewModel.SetAccountEnabledAsync(account, !account.IsEnabled);
        SettingsStatus.Text = account.IsEnabled ? "Account disabled" : "Account enabled";
        RefreshAccounts();
    }

    private async void RemoveAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsList.SelectedItem is not AccountItem { Model: { } account }) return;
        var confirmation = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = "Remove this account?",
            Content = $"{account.Email} and its local cached mail will be removed from Inboxwell. Messages on the server are not deleted.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
        await viewModel.DeleteAccountAsync(account.Id);
        SettingsStatus.Text = "Account removed";
        RefreshAccounts();
        await LoadSignatureAccountsAsync();
    }

    private void AddAccount_Click(object sender, RoutedEventArgs e) => AddAccountRequested?.Invoke(this, EventArgs.Empty);

    private async Task LoadSignatureAccountsAsync()
    {
        var accounts = viewModel.Accounts.Where(static item => item.Model is not null).ToArray();
        SignatureAccountPicker.ItemsSource = accounts;
        SignatureAccountPicker.SelectedItem = accounts.FirstOrDefault();
        await LoadSignaturesAsync();
    }

    private async void SignatureAccountPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => await LoadSignaturesAsync();

    private async Task LoadSignaturesAsync()
    {
        if (SignatureAccountPicker.SelectedItem is not AccountItem { Model: { } account }) return;
        loadingSignature = true;
        var choices = new List<SignatureChoice> { new("New signature", null) };
        choices.AddRange((await viewModel.GetSignaturesAsync(account.Id)).Select(item => new SignatureChoice(item.Name, item)));
        SignaturePicker.ItemsSource = choices;
        SignaturePicker.DisplayMemberPath = nameof(SignatureChoice.Name);
        SignaturePicker.SelectedIndex = 0;
        ClearSignatureEditor();
        loadingSignature = false;
    }

    private void SignaturePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingSignature) return;
        if (SignaturePicker.SelectedItem is SignatureChoice { Signature: { } signature })
        {
            SignatureNameBox.Text = signature.Name;
            if (!string.IsNullOrWhiteSpace(signature.Rtf))
                SignatureBodyEditor.Document.SetText(TextSetOptions.FormatRtf, signature.Rtf);
            else
                SignatureBodyEditor.Document.SetText(TextSetOptions.None, signature.PlainText);
            DefaultNewCheck.IsChecked = signature.IsDefaultForNew;
            DefaultReplyCheck.IsChecked = signature.IsDefaultForReplies;
        }
        else
        {
            ClearSignatureEditor();
        }
    }

    private void ClearSignatureEditor()
    {
        SignatureNameBox.Text = "Personal";
        SignatureBodyEditor.Document.SetText(TextSetOptions.None, string.Empty);
        DefaultNewCheck.IsChecked = true;
        DefaultReplyCheck.IsChecked = true;
    }

    private async void SaveSignature_Click(object sender, RoutedEventArgs e)
    {
        var content = RichTextEditorUtilities.Capture(SignatureBodyEditor);
        if (SignatureAccountPicker.SelectedItem is not AccountItem { Model: { } account } || string.IsNullOrWhiteSpace(content.PlainText))
        {
            SettingsStatus.Text = "Choose a mailbox and enter the signature text";
            return;
        }
        var existing = (SignaturePicker.SelectedItem as SignatureChoice)?.Signature;
        await viewModel.SaveSignatureAsync(new Signature
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            AccountId = account.Id,
            Name = string.IsNullOrWhiteSpace(SignatureNameBox.Text) ? "Signature" : SignatureNameBox.Text.Trim(),
            PlainText = content.PlainText,
            Html = content.Html,
            Rtf = content.Rtf,
            IsDefaultForNew = DefaultNewCheck.IsChecked == true,
            IsDefaultForReplies = DefaultReplyCheck.IsChecked == true
        });
        SettingsStatus.Text = "Signature saved";
        await LoadSignaturesAsync();
    }

    private void SignatureBold_Click(object sender, RoutedEventArgs e)
    {
        var format = SignatureBodyEditor.Document.Selection.CharacterFormat;
        format.Bold = format.Bold == FormatEffect.On ? FormatEffect.Off : FormatEffect.On;
        SignatureBodyEditor.Focus(FocusState.Programmatic);
    }

    private void SignatureItalic_Click(object sender, RoutedEventArgs e)
    {
        var format = SignatureBodyEditor.Document.Selection.CharacterFormat;
        format.Italic = format.Italic == FormatEffect.On ? FormatEffect.Off : FormatEffect.On;
        SignatureBodyEditor.Focus(FocusState.Programmatic);
    }

    private void SignatureUnderline_Click(object sender, RoutedEventArgs e)
    {
        var format = SignatureBodyEditor.Document.Selection.CharacterFormat;
        format.Underline = format.Underline == UnderlineType.None ? UnderlineType.Single : UnderlineType.None;
        SignatureBodyEditor.Focus(FocusState.Programmatic);
    }

    private void SignatureBullet_Click(object sender, RoutedEventArgs e)
    {
        SignatureBodyEditor.Document.Selection.SetText(TextSetOptions.None, "• ");
        SignatureBodyEditor.Focus(FocusState.Programmatic);
    }

    private void SignatureFontSizePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingSignatureFontSize || SignatureBodyEditor is null || !TryParseFontSize(SignatureFontSizePicker.SelectedItem, out var size)) return;
        SignatureBodyEditor.Document.Selection.CharacterFormat.Size = (float)size;
        SignatureBodyEditor.Focus(FocusState.Programmatic);
    }

    private void SignatureBodyEditor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (updatingSignatureFontSize || SignatureFontSizePicker is null) return;
        var size = SignatureBodyEditor.Document.Selection.CharacterFormat.Size;
        var choice = SignatureFontSizePicker.Items.OfType<string>()
            .FirstOrDefault(item => TryParseFontSize(item, out var candidate) && Math.Abs(candidate - size) < 0.1);
        if (choice is null || Equals(SignatureFontSizePicker.SelectedItem, choice)) return;
        updatingSignatureFontSize = true;
        SignatureFontSizePicker.SelectedItem = choice;
        updatingSignatureFontSize = false;
    }

    private static bool TryParseFontSize(object? item, out double size)
    {
        var value = item as string;
        var number = value?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return double.TryParse(number, NumberStyles.Number, CultureInfo.InvariantCulture, out size);
    }

    private void SignatureUndo_Click(object sender, RoutedEventArgs e)
    {
        if (SignatureBodyEditor.Document.CanUndo()) SignatureBodyEditor.Document.Undo();
        SignatureBodyEditor.Focus(FocusState.Programmatic);
    }

    private void SignatureRedo_Click(object sender, RoutedEventArgs e)
    {
        if (SignatureBodyEditor.Document.CanRedo()) SignatureBodyEditor.Document.Redo();
        SignatureBodyEditor.Focus(FocusState.Programmatic);
    }

    private async void DeleteSignature_Click(object sender, RoutedEventArgs e)
    {
        if (SignaturePicker.SelectedItem is not SignatureChoice { Signature: { } signature }) return;
        await viewModel.DeleteSignatureAsync(signature.Id);
        SettingsStatus.Text = "Signature deleted";
        await LoadSignaturesAsync();
    }

    private void LoadShortcutRows()
    {
        pendingShortcuts.Clear();
        shortcutBoxes.Clear();
        ShortcutRowsHost.Children.Clear();

        for (var index = 0; index < KeyboardShortcutSettings.Definitions.Count; index++)
        {
            var definition = KeyboardShortcutSettings.Definitions[index];
            var gesture = KeyboardShortcutSettings.Get(definition.Command);
            pendingShortcuts[definition.Command] = gesture;

            var row = new Grid
            {
                Padding = new Thickness(0, 11, 0, 11),
                ColumnSpacing = 12
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var details = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            details.Children.Add(new TextBlock { Text = definition.Name, FontWeight = FontWeights.SemiBold });
            details.Children.Add(new TextBlock
            {
                Text = definition.Description,
                FontSize = 11,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap
            });

            var shortcutBox = new TextBox
            {
                Text = FormatShortcut(gesture),
                Tag = definition.Command,
                IsReadOnly = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["GomailFieldStyle"]
            };
            shortcutBox.KeyDown += ShortcutBox_KeyDown;
            shortcutBox.GotFocus += (_, _) => shortcutBox.SelectAll();
            Grid.SetColumn(shortcutBox, 1);

            var resetButton = new Button
            {
                Content = "Reset",
                Tag = definition.Command,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)Application.Current.Resources["GomailSecondaryButtonStyle"]
            };
            resetButton.Click += ResetShortcut_Click;
            Grid.SetColumn(resetButton, 2);

            row.Children.Add(details);
            row.Children.Add(shortcutBox);
            row.Children.Add(resetButton);
            ShortcutRowsHost.Children.Add(row);
            shortcutBoxes[definition.Command] = shortcutBox;

            if (index < KeyboardShortcutSettings.Definitions.Count - 1)
            {
                ShortcutRowsHost.Children.Add(new Border
                {
                    Height = 1,
                    Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["GomailBorderBrush"]
                });
            }
        }
    }

    private void ShortcutBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: MailShortcutCommand command } box) return;
        e.Handled = true;

        if (e.Key == VirtualKey.Back)
        {
            pendingShortcuts[command] = null;
            box.Text = FormatShortcut(null);
            SettingsStatus.Text = "Shortcut turned off";
            return;
        }

        if (KeyboardShortcutSettings.IsModifierKey(e.Key))
        {
            SettingsStatus.Text = "Press a letter, number, function key, or navigation key too";
            return;
        }

        var gesture = new KeyboardShortcutGesture(e.Key, CurrentModifiers());
        var conflict = pendingShortcuts.FirstOrDefault(item => item.Key != command && item.Value == gesture);
        if (!conflict.Equals(default(KeyValuePair<MailShortcutCommand, KeyboardShortcutGesture?>)))
        {
            var name = KeyboardShortcutSettings.Definitions.First(item => item.Command == conflict.Key).Name;
            SettingsStatus.Text = $"{gesture} is already assigned to {name}";
            return;
        }

        pendingShortcuts[command] = gesture;
        box.Text = gesture.ToString();
        box.SelectAll();
        SettingsStatus.Text = "New shortcut ready to save";
    }

    private void ResetShortcut_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not MailShortcutCommand command) return;
        var definition = KeyboardShortcutSettings.Definitions.First(item => item.Command == command);
        var conflict = pendingShortcuts.FirstOrDefault(item => item.Key != command && item.Value == definition.DefaultGesture);
        if (!conflict.Equals(default(KeyValuePair<MailShortcutCommand, KeyboardShortcutGesture?>)))
        {
            var name = KeyboardShortcutSettings.Definitions.First(item => item.Command == conflict.Key).Name;
            SettingsStatus.Text = $"{definition.DefaultGesture} is already assigned to {name}";
            return;
        }
        pendingShortcuts[command] = definition.DefaultGesture;
        shortcutBoxes[command].Text = definition.DefaultGesture.ToString();
        SettingsStatus.Text = "Default shortcut restored";
    }

    private void ResetShortcuts_Click(object sender, RoutedEventArgs e)
    {
        foreach (var definition in KeyboardShortcutSettings.Definitions)
        {
            pendingShortcuts[definition.Command] = definition.DefaultGesture;
            shortcutBoxes[definition.Command].Text = definition.DefaultGesture.ToString();
        }
        SettingsStatus.Text = "Default shortcuts restored and ready to save";
    }

    private static VirtualKeyModifiers CurrentModifiers()
    {
        var modifiers = VirtualKeyModifiers.None;
        if (IsKeyDown(VirtualKey.Control)) modifiers |= VirtualKeyModifiers.Control;
        if (IsKeyDown(VirtualKey.Shift)) modifiers |= VirtualKeyModifiers.Shift;
        if (IsKeyDown(VirtualKey.Menu)) modifiers |= VirtualKeyModifiers.Menu;
        if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows)) modifiers |= VirtualKeyModifiers.Windows;
        return modifiers;
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static string FormatShortcut(KeyboardShortcutGesture? gesture) => gesture is { } value ? value.ToString() : "Not assigned";

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var duplicate = pendingShortcuts
            .Where(static item => item.Value.HasValue)
            .GroupBy(static item => item.Value!.Value)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            SettingsStatus.Text = $"{duplicate.Key} is assigned more than once";
            return;
        }

        localSettings["notificationsEnabled"] = NotificationsToggle.IsOn;
        localSettings["closeToTray"] = CloseToTrayToggle.IsOn;
        localSettings["blockRemoteImages"] = BlockRemoteImagesToggle.IsOn;
        localSettings["readingPanePosition"] = SelectedTag(ReadingPanePicker, "right");
        localSettings["themePreference"] = SelectedTag(ThemePicker, "system");
        KeyboardShortcutSettings.Save(pendingShortcuts);
        ApplyTheme(SelectedTag(ThemePicker, "system"));
        SettingsStatus.Text = "Settings saved";
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        await Task.Delay(220);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void OpenSourceCode_Click(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/dshchepetov/Inboxwell"));

    private static string SelectedTag(ComboBox picker, string fallback) => (picker.SelectedItem as ComboBoxItem)?.Tag as string ?? fallback;

    private static void SelectByTag(ComboBox picker, string tag)
    {
        picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag as string == tag) ?? picker.Items[0];
    }

    private void ApplyTheme(string theme)
    {
        var requested = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        SettingsRoot.RequestedTheme = requested;
        if (App.Window.Content is FrameworkElement mainRoot) mainRoot.RequestedTheme = requested;
    }
}
