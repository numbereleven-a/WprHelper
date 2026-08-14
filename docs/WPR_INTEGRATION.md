# Windows Performance Recorder integration

The application invokes the Windows inbox `wpr.exe` directly through `ProcessStartInfo.ArgumentList`; no command shell is involved.

| Operation | Arguments |
|---|---|
| Start built-in profile | `-start CPU -filemode` |
| Start several profiles | `-start CPU -start DiskIO -start FileIO -filemode` |
| Start custom profile | `-start <path.wprp!profile> -filemode` |
| Save and stop | `-stop <absolute-output.etl> -skipPdbGen` by default; the option can be disabled for detailed managed-code symbols |
| Emergency cleanup | `-cancel` |

The UI supports selecting several built-in profiles. Each selected profile becomes a separate `-start` pair, matching the native WPR syntax. Additional start arguments entered in the UI are parsed with Windows command-line rules and appended after the generated profiles and optional `-filemode` argument.

WPR start and stop commands normally require elevation. The elevated worker owns the complete recording interval: it starts WPR, signals readiness, waits while the UI launches the target, evaluates stop conditions, and saves the ETL on stop.

Target launch is optional. In system-capture mode the worker moves directly from WPR readiness to recording, does not create or track a target PID, and stops manually, at the configured maximum duration, or when the free-space reserve is reached.

The primary ETL path is selected before the capture starts and is passed directly to `wpr -stop` in the configured save directory. The session directory is used for metadata and logs, not as an intermediate ETL location. After finalization, the application briefly retries the file check to tolerate delayed visibility on virtualized or redirected file systems.

By default, WPR Helper passes `-skipPdbGen` to `wpr -stop`. This keeps the ETL event data intact and avoids the extra `capture.etl.NGENPDB` symbol folder, but managed .NET stacks may be less detailed in WPA. Clear **Skip NGEN/PDB symbol generation** when those symbols are needed.

Only one system WPR recording session may be active. If another session exists, `wpr -start` returns a non-zero exit code and its diagnostic output is shown to the user. The application does not cancel an unrelated session before attempting its own start.
