# Security and privacy design

- Target programs are started directly with `ProcessStartInfo`; no `cmd.exe`, PowerShell, or shell command is used.
- Wpr-owned arguments use `ArgumentList`, preventing option/path concatenation.
- The normal UI runs as the current user. Only the restricted worker is elevated.
- Named pipes are unique per session and limited to the initiating Windows user and elevated local administrators.
- Profiles and settings do not support stored credentials.
- Filename templates cannot introduce directory separators and reserved Windows names are escaped.
- Network copies use the current Windows identity, write `.partial`, verify length, and preserve the local source on any failure.
- No telemetry is emitted.

ETL files can contain personal or confidential data, including paths, user names, registry content, and command lines. Users must review the displayed final destination and apply their organization's trace-retention rules.
