# Isolated updater smoke test

Run from the repository root on Windows with Python 3.11+ and the .NET 10 SDK:

```text
python scripts/verify_update.py
```

The script publishes two tiny test hosts in a unique temporary directory. Both link the production updater sources directly; neither includes the cleaner or opens Devin data. A local HTTP handler supplies the synthetic newer executable and checksum without making network requests. .NET restore may contact configured NuGet feeds.

The success case exercises download validation, the real helper process, parent authentication, ready/commit handoff, replacement, restart, window startup confirmation, and cleanup. The second case makes the new test host exit before startup confirmation and verifies restoration of the original executable. Only the error dialog owned by that case's authenticated helper process is dismissed.

Update staging uses one uniquely named child of the normal updates directory per case so that the real cross-process ownership checks remain active. Only those recorded directories are cleaned. Test processes are tracked by executable path and process creation time before any termination. Build outputs, test hosts, and temporary directories are removed after the run. Failed cleanup is reported rather than treated as a passing test.

Options: `--case success` or `--case rollback` selects one case; `--version 1.3.0` overrides the target test version (default: the application project version); `--timeout 90` sets each process test's deadline in seconds. This exercises updater process behavior with synthetic hosts, not the production main window or a live GitHub download.
