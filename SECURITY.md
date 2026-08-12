# Security policy

## Reporting a vulnerability

Please do not open a public issue for a vulnerability that could expose messages, credentials, OAuth tokens, encryption keys or local files.

Use the repository's private GitHub Security Advisory reporting flow. Include the affected version, a concise reproduction, expected impact and any suggested mitigation. Please remove real mailbox addresses, messages, tokens and secrets from all evidence.

## Supported versions

Until the first public release, security fixes are applied to the latest commit on `main`. Locally built packages and development certificates are trusted at the builder's or installer's discretion.

## Security boundaries

Inboxwell stores mailbox content in a SQLCipher database, credentials and OAuth tokens in Windows Credential Manager, and cached attachments in AES-GCM containers. A compromised Windows account or process with equivalent user privileges remains outside the application's protection boundary.
