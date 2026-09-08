# Changelog

## 1.4

- Fixed WPR command cancellation, timeouts, and status error handling.
- Improved session state persistence and cleanup after interrupted checks.
- Displayed capture warnings and preserved the ETL path when switching languages.
- Disabled WPR health checks while a capture is running.

## 1.3

- Add a wpr.exe check button that verifies the executable, lists available profiles, and runs a short test recording when elevated.
- Cancel a WPR recording left over from a previous session before starting a new capture.
- Bound the trace stop timeout and fall back to cancellation instead of waiting indefinitely.
- Move free-space polling to a background sampler so slow UNC paths can no longer be mistaken for a lost worker connection.
- Mark sessions interrupted by an application exit as interrupted at startup.
- Read worker IPC messages line by line, remove unused profile fields and resources, and make WPR version detection resilient to locked files.

## 1.2

- Add a system-capture mode that starts WPR without launching a target application.
- Adapt target-only controls, stop conditions, and file naming for system captures.
- Improve WPR startup, profile detection, trace finalization, and worker communication reliability.
- Preserve relevant capture settings and WPR version information when switching modes or languages.

## 1.1

- Skip NGEN/PDB generation by default to reduce finalization time and avoid the extra symbol folder.
- Add a per-profile option to keep managed-code symbols when detailed .NET stacks are required.
- Write the primary ETL directly to the selected save directory instead of copying it from the session folder.

## 1.0

- First public release of WPR Helper.
- Start one or several built-in or custom WPR profiles before launching the target application.
- Stop manually, after target exit, after a delay, or at a configured time limit.
- Save ETL files to local or UNC paths and optionally copy the completed trace.
- Show the exact WPR command used for each capture.
- Provide reusable profiles, English and Russian interfaces, and a portable single-file build.
- Reset capture and interface settings to defaults without deleting saved profiles.

## 0.2.0

- Preserve ETL finalization during manual stop, shutdown, and long `wpr -stop` operations.
- Serialize worker IPC events and make invalid late progress events non-fatal.
- Detect target PID reuse and measure duration from the actual WPR start.
- Prefer the Windows Performance Toolkit WPR executable and validate available/custom profiles.
- Check free space on both the WPR backing volume and the selected local destination.
- Apply UI culture consistently and complete Russian/English localization.
- Keep the ETL-folder button beside the saved file and support selecting several WPR profiles with concise descriptions.
- Make the saved-profiles tab compact and remove its unnecessary outer scrollbar.
- Support UNC paths in free-space checks, validation, folder selection, and ETL destinations.
- Show the exact WPR executable and arguments applied to each capture.
- Harden file naming, network-copy verification, diagnostics, tests, and build metadata.

## 0.1.0

- Initial Windows Performance Recorder implementation.
- Start built-in or custom WPR profiles in file or memory mode.
- Launch a selected application immediately after recording starts.
- Stop manually, after target exit, or after a configured duration.
- Finalize ETL through `wpr -stop` and optionally copy it to another directory.
- Reusable capture profiles and Russian/English interface.
