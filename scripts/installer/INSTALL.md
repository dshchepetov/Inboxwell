# Install Inboxwell

This development build supports Windows 11 x64, build 26100 or newer.

1. Extract the ZIP into its own folder.
2. Right-click `Install-Inboxwell.ps1` and choose **Run with PowerShell**.
3. Approve UAC. Administrator access is used to trust this build's local certificate for the computer.

The installer verifies that the MSIX signature matches the included certificate, skips Windows App Runtime installation when a compatible version is already present, and then installs or upgrades Inboxwell.

The included certificate is self-signed and is only suitable for local development builds from a source you trust. Public distribution should use a trusted code-signing certificate or Microsoft Store signing.
