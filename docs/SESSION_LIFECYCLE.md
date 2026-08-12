# Session lifecycle

The normal state path is:

`Idle → Validating → Preparing → WaitingForElevation → StartingWpr → WaitingForWpr → LaunchingTarget → Capturing → StopRequested → StoppingWpr → Finalizing → Completed`

`Copying` is inserted after finalization when a destination directory is configured. Failures transition to `Failed`; non-fatal copy problems produce `CompletedWithWarnings`.

Cancellation while capturing requests a graceful stop. The elevated worker still runs `wpr -stop <session.etl>` so recorded data is finalized. `wpr -cancel` is used only as cleanup if finalization itself fails.
