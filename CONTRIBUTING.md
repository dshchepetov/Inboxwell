# Contributing to Inboxwell

Thank you for helping make Inboxwell better.

## Before opening a pull request

1. Open or reference an issue for changes that materially affect behavior or architecture.
2. Keep provider-specific behavior behind the existing provider contracts.
3. Do not commit mailbox data, OAuth credentials, certificates, tokens or diagnostic reports.
4. Preserve upgrade compatibility for package identity, Credential Manager entries and the encrypted database unless a reviewed migration is included.
5. Match the existing responsive WinUI visual language and update the Pen.dev source for material interface changes.

## Verification

```powershell
dotnet restore Inboxwell.slnx
dotnet test tests/Gomail.Tests/Gomail.Tests.csproj -c Release
dotnet build Inboxwell.slnx -c Release
```

For UI changes, also verify compact and wide layouts, both reading-pane positions, light and dark themes, keyboard navigation and pointer hover/pressed states.

## Pull requests

Keep commits focused and explain the user-facing result, compatibility impact and verification performed. New behavior should include tests where practical.
