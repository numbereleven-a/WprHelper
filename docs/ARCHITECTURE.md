# Architecture

- `WprHelper.Contracts`: profiles, session records, worker messages, and service interfaces.
- `WprHelper.Core`: capture state machine, validation, filename expansion, and stop-condition evaluation.
- `WprHelper.Infrastructure`: WPR command execution, target launch, elevated named-pipe worker, JSON storage, file transfer, and session orchestration.
- `WprHelper.App`: non-elevated WPF MVVM interface and the `--elevated-worker` entry mode.

The normal UI remains non-elevated. A short-lived elevated copy performs `wpr -start`, monitors stop conditions, and performs `wpr -stop <etl>`. The ETL backing path is allocated directly in the selected local save directory, so the completed trace does not need a second read/copy through the session directory; the session directory stores only metadata and logs. Communication uses a randomly named pipe limited to the current user and administrators.

The selected recorder must exist and be named `wpr.exe`. The application reads its file version as a compatibility check. When target launch is enabled, the target must be a Windows PE executable; system-capture profiles do not require a target path or PID.

WPR creates the final ETL during `-stop`; therefore capture progress does not claim to know the current ETL size. Free-space and duration checks remain available while recording.
