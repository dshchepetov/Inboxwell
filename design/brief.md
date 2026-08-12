# Inboxwell product design brief

Design a polished native Windows 11 desktop email client named Inboxwell. The visual direction should feel as calm, spacious, and crafted as Apple Mail, but it must be an original Windows-native design rather than a clone.

## Visual language

- Windows 11 desktop, 1440x900 primary canvas, with layouts that remain usable at 1280x720.
- Use a restrained warm-neutral palette with one refined blue accent, generous whitespace, subtle separators, 12-16px corner radii, soft Mica-like layered surfaces, and understated shadows.
- Use Segoe UI Variable-like typography and Lucide icons.
- Create both light and dark themes through named design variables.
- Follow an 8px spacing system and accessible contrast.
- Prefer quiet density: information-rich but never cramped.

## Core layout

- Custom integrated title bar.
- Left sidebar: profile/account switcher, Unified Inbox, Starred, Drafts, Sent, Archive, Spam, Trash, and expandable account sections.
- Middle column: virtualized conversation list with sender avatar, sender, subject, snippet, timestamp, unread state, attachment/flag indicators, and selection state.
- Right reading pane: conversation header, compact toolbar, stacked thread messages, collapsed older messages, safe remote-image banner, attachment cards, and reply affordances.
- Columns are resizable; the selected surface should be distinct without heavy borders.

## Required frames

1. Main inbox in light theme with three accounts, populated unified inbox, selected multi-message conversation, remote-image privacy banner, and a subtle offline/sync status.
2. Main inbox in dark theme with a Gmail account and labels visible.
3. First-run onboarding with three large provider choices: Microsoft 365, Gmail, and Other IMAP; include a reassuring local-first privacy note.
4. Add IMAP account form with email, username, password/app password, incoming/outgoing hosts, ports, TLS selectors, and a Test connection action.
5. Separate compose window with To/CC/BCC, subject, formatting toolbar, body, inline signature, attachment chips, From account selector, signature selector, Save draft, and Send.
6. Search mode with query chips, unified results grouped by conversation, and a progressive server-search state for older mail.
7. Settings window on Accounts, showing multiple accounts, sync status, cache range, notification options, and remove account action.
8. Signature editor with multiple signatures per account, rich-text preview, and defaults for new messages and replies.
9. Component and token board containing colors, typography, spacing, icons, buttons, fields, list rows, banners, badges, menus, attachment cards, avatars, empty states, error states, skeletons, and focus/hover/pressed/disabled variants.

## Interaction details to communicate visually

- Closing the main window keeps Inboxwell in the tray.
- New messages can trigger Windows notifications.
- Offline actions are queued and visibly reconciled later.
- Remote images are blocked by default.
- Compose is a separate window so users can keep reading mail.
- Russian and English strings will be supported; ensure controls can accommodate both.

Keep the result implementation-friendly: use reusable components and named variables rather than one-off styling. Organize frames and layers clearly.
