Real Windows-native Claude CLI fixtures, captured 2026-07-18 with claude-cli 2.1.214 via `claude --version`, `claude agents --json --all`, `claude daemon status`, and `claude auth status`.

- `version.json`
- `agents.json`
- `daemon-status.json`
- `auth-status.json` — `email`, `orgId`, and `orgName` are redacted to `<REDACTED>`; do not replace these placeholders with real values.

Re-capture with `scripts/probe-windows.ps1`, which overwrites these same four files with fresh timestamps and CLI version.
