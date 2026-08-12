using CommunityToolkit.WinUI.Notifications;
using Gomail.Core;
using Windows.Storage;

namespace Gomail_App.Services;

public sealed class WindowsMailNotifier : IAppNotifier
{
    public Task NotifyNewMailAsync(MailConversation conversation, CancellationToken cancellationToken = default)
    {
        var settings = ApplicationData.Current.LocalSettings.Values;
        if (settings.TryGetValue("notificationsEnabled", out var enabled) && enabled is false)
        {
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var sender = string.Join(", ", conversation.Participants.Select(static item => item.DisplayName).Take(3));
        new ToastContentBuilder()
            .AddText(string.IsNullOrWhiteSpace(sender) ? "New mail" : sender)
            .AddText(string.IsNullOrWhiteSpace(conversation.Subject) ? "(no subject)" : conversation.Subject)
            .AddText(conversation.Snippet)
            .Show();
        return Task.CompletedTask;
    }
}
