# Bounded shell verification

`Verify-Shell.ps1` launches one supplied Windows published artifact for a bounded
manual inspection interval. It creates a marked OS-temp root, passes its
`app-root` child as `--proof-root <absolute directory>`, records the exact
executable, PID, and creation time, and stops only that verified process before
removing the owned root. Process or window existence does not establish GUI
success; record the visible result during the interval.

Add `-VerifySecondLaunch` to start a second copy with the same proof root and
arguments while the primary process is held. The wrapper records the second
process identity, exit code, and forwarding result, and stops it by that
identity if it exceeds `-SecondLaunchTimeoutSeconds`.

Example for a Windows published artifact:

```powershell
pwsh -NoProfile -File .\scripts\Verify-Shell.ps1 `
  -ArtifactPath .\artifacts\publish\win-x64\Glideslope.App.exe `
  -DurationSeconds 45 -VerifySecondLaunch -ArtifactArgument '--autostart'
```

The Linux package, startup-registration, and tray lifecycle proof
implementations are retained in `src/Glideslope.Shell.Specs`. Run the shell
specification executable with `dotnet run --project src/Glideslope.Shell.Specs` to exercise the deterministic startup and lifecycle
contracts. Install and launch the built `.deb` in a real graphical Linux
session to verify package paths, visible fallback, tray registration, and
Show/Exit interaction. The removed remote-shell wrappers and their host-specific
SSH/session recipes are not part of this public repository. A headless proof or
successful process launch does not establish visible desktop behavior.

All application proofs must use a dedicated `--proof-root <absolute directory>`
that the application validates and uses for settings, data, runtime, cache,
startup registration, and instance namespace. A wrapper must not claim profile
isolation if the artifact ignores that contract. Keep the primary process alive
for the complete inspection interval; an early exit fails the proof even when
its exit code is zero. Use an expected-clean-exit option only for a separately
documented intentional tray-exit exercise.
