# Windows Performance Recorder integration

The application invokes the Windows inbox `wpr.exe` directly through `ProcessStartInfo.ArgumentList`; no command shell is involved.

| Operation | Arguments |
|---|---|
| Start built-in profile | `-start CPU -filemode` |
| Start several profiles | `-start CPU -start DiskIO -start FileIO -filemode` |
| Start custom profile | `-start <path.wprp!profile> -filemode` |
| Save and stop | `-stop <absolute-output.etl>` |
| Emergency cleanup | `-cancel` |

The UI supports selecting several built-in profiles. Each selected profile becomes a separate `-start` pair, matching the native WPR syntax. Additional start arguments entered in the UI are parsed with Windows command-line rules and appended after the generated profiles and optional `-filemode` argument.

WPR start and stop commands normally require elevation. The elevated worker owns the complete recording interval: it starts WPR, signals readiness, waits while the UI launches the target, evaluates stop conditions, and saves the ETL on stop.

Only one system WPR recording session may be active. If another session exists, `wpr -start` returns a non-zero exit code and its diagnostic output is shown to the user. The application does not cancel an unrelated session before attempting its own start.
