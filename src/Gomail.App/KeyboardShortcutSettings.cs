using Windows.Storage;
using Windows.System;

namespace Gomail_App;

internal enum MailShortcutCommand
{
    NewMessage,
    Search,
    Refresh,
    Reply,
    Forward,
    SendReply,
    MarkUnread,
    MarkRead,
    Archive,
    Delete,
    ToggleStar,
    PreviousConversation,
    NextConversation,
    OpenSettings
}

internal sealed record KeyboardShortcutDefinition(
    MailShortcutCommand Command,
    string Name,
    string Description,
    KeyboardShortcutGesture DefaultGesture);

internal readonly record struct KeyboardShortcutGesture(VirtualKey Key, VirtualKeyModifiers Modifiers)
{
    public override string ToString() => KeyboardShortcutSettings.Format(this);
}

internal static class KeyboardShortcutSettings
{
    private const string SettingPrefix = "keyboardShortcut.";

    public static IReadOnlyList<KeyboardShortcutDefinition> Definitions { get; } =
    [
        new(MailShortcutCommand.NewMessage, "New message", "Open the message composer", Gesture(VirtualKey.N, VirtualKeyModifiers.Control)),
        new(MailShortcutCommand.Search, "Search mail", "Move focus to the search box", Gesture(VirtualKey.F, VirtualKeyModifiers.Control)),
        new(MailShortcutCommand.Refresh, "Sync mail", "Check all visible mailboxes for updates", Gesture(VirtualKey.F5)),
        new(MailShortcutCommand.Reply, "Reply", "Reply to the selected conversation", Gesture(VirtualKey.R, VirtualKeyModifiers.Control)),
        new(MailShortcutCommand.Forward, "Forward", "Forward the selected conversation", Gesture(VirtualKey.F, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift)),
        new(MailShortcutCommand.SendReply, "Send message or reply", "Send from the composer or inline reply", Gesture(VirtualKey.Enter, VirtualKeyModifiers.Control)),
        new(MailShortcutCommand.MarkUnread, "Mark as unread", "Mark the selected conversation as unread", Gesture(VirtualKey.U, VirtualKeyModifiers.Control)),
        new(MailShortcutCommand.MarkRead, "Mark as read", "Mark the selected conversation as read", Gesture(VirtualKey.Q, VirtualKeyModifiers.Control)),
        new(MailShortcutCommand.Archive, "Archive", "Archive the selected conversation", Gesture(VirtualKey.E)),
        new(MailShortcutCommand.Delete, "Delete", "Move the selected conversation to Trash", Gesture(VirtualKey.Delete)),
        new(MailShortcutCommand.ToggleStar, "Star or unstar", "Toggle the star on the selected conversation", Gesture(VirtualKey.S)),
        new(MailShortcutCommand.PreviousConversation, "Previous conversation", "Select the previous conversation in the list", Gesture(VirtualKey.K)),
        new(MailShortcutCommand.NextConversation, "Next conversation", "Select the next conversation in the list", Gesture(VirtualKey.J)),
        new(MailShortcutCommand.OpenSettings, "Open settings", "Open Inboxwell settings", Gesture((VirtualKey)188, VirtualKeyModifiers.Control))
    ];

    public static KeyboardShortcutGesture? Get(MailShortcutCommand command)
    {
        var definition = Definitions.First(item => item.Command == command);
        var values = ApplicationData.Current.LocalSettings.Values;
        if (!values.TryGetValue(SettingPrefix + command, out var stored)) return definition.DefaultGesture;
        if (stored is not string text || string.IsNullOrWhiteSpace(text)) return null;
        return TryParse(text, out var gesture) ? gesture : definition.DefaultGesture;
    }

    public static void Save(IReadOnlyDictionary<MailShortcutCommand, KeyboardShortcutGesture?> shortcuts)
    {
        var values = ApplicationData.Current.LocalSettings.Values;
        foreach (var definition in Definitions)
        {
            shortcuts.TryGetValue(definition.Command, out var gesture);
            values[SettingPrefix + definition.Command] = gesture is { } value ? Serialize(value) : string.Empty;
        }
    }

    public static string Format(KeyboardShortcutGesture gesture)
    {
        var parts = new List<string>(5);
        if (gesture.Modifiers.HasFlag(VirtualKeyModifiers.Control)) parts.Add("Ctrl");
        if (gesture.Modifiers.HasFlag(VirtualKeyModifiers.Shift)) parts.Add("Shift");
        if (gesture.Modifiers.HasFlag(VirtualKeyModifiers.Menu)) parts.Add("Alt");
        if (gesture.Modifiers.HasFlag(VirtualKeyModifiers.Windows)) parts.Add("Win");
        parts.Add(FormatKey(gesture.Key));
        return string.Join(" + ", parts);
    }

    public static bool IsModifierKey(VirtualKey key) => key is
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
        VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static KeyboardShortcutGesture Gesture(VirtualKey key, VirtualKeyModifiers modifiers = VirtualKeyModifiers.None) =>
        new(key, modifiers);

    private static string Serialize(KeyboardShortcutGesture gesture) => $"{(int)gesture.Modifiers}:{(int)gesture.Key}";

    private static bool TryParse(string text, out KeyboardShortcutGesture gesture)
    {
        var parts = text.Split(':', 2);
        if (parts.Length == 2 && int.TryParse(parts[0], out var modifiers) && int.TryParse(parts[1], out var key))
        {
            gesture = new KeyboardShortcutGesture((VirtualKey)key, (VirtualKeyModifiers)modifiers);
            return true;
        }

        gesture = default;
        return false;
    }

    private static string FormatKey(VirtualKey key)
    {
        var value = (int)key;
        if (value is >= 48 and <= 57) return ((char)value).ToString();
        if (value is >= 65 and <= 90) return ((char)value).ToString();
        if (value is >= 112 and <= 135) return $"F{value - 111}";
        return value switch
        {
            8 => "Backspace",
            9 => "Tab",
            13 => "Enter",
            27 => "Esc",
            32 => "Space",
            33 => "Page Up",
            34 => "Page Down",
            35 => "End",
            36 => "Home",
            37 => "Left",
            38 => "Up",
            39 => "Right",
            40 => "Down",
            45 => "Insert",
            46 => "Delete",
            186 => ";",
            187 => "=",
            188 => ",",
            189 => "-",
            190 => ".",
            191 => "/",
            192 => "`",
            219 => "[",
            220 => "\\",
            221 => "]",
            222 => "'",
            _ => key.ToString()
        };
    }
}
