# Gateway isolation parked handoff

## Decision

As of 2026-09-17, this stack is parked and is not release-ready.

- The Gateway MSIX will not support `--no-isolation` or an isolation on/off
  switch.
- Any plugin UI for this area is informational only. It must not change the
  execution identity or persisted mode.
- The implementation branches are retained as engineering evidence, not as a
  supported or releasable product configuration.

## Preserved stack

The stack is fixed to upstream base
`4a14aeee9b300e361c0faa9e2b5af1381a6b8a00`:

| Layer | Branch | Commit |
|---|---|---|
| 1. Isolation state routing | `chazgo-isolation-state-routing-layer` | `1896e852b05525432ebaa959dadcf834c671cc12` |
| 2. Native Gateway lifecycle | `chazgo-native-gateway-lifecycle` | `45deeca870f28de36d6c163627a43fda5235db82` |
| 3. Mode-aware command routing | `chazgo-mode-aware-command-routing` | `409282db856228c2a5ed61aaa8b4eefc7676d5e9` |
| 4. Transactional isolation commands | `chazgo-gateway-isolation-transitions` | `507d8659d63bfecae3d44db7b7c985baea9291cd` |
| 5. Integration hardening | `chazgo-gateway-isolation-integration` | `51cb853d6998cc99fa6d26b66065d7734dd46aea` |

Each layer is the direct parent of the next layer.

## Completed work and validation

Implementation is complete for the approved five-layer contract, including
persisted mode selection, separate native and isolated ownership, transactional
transitions, rollback evidence, shared lifecycle serialization, diagnostics
exclusions, and x64/ARM64 runtime coverage.

Validation completed at Layer 5:

- Release build: 0 warnings and 0 errors.
- Full managed suite: 736 passed.
- Focused integration suite: 255 passed.
- `scripts\Test-DotNetQuality.ps1`: passed.
- NativeAOT CLI scenarios: 13 passed.
- Explicit x64 and ARM64 launcher and session-host publishes: passed, with no
  companion managed host DLLs.
- All 13 `scripts\Test-*.Tests.ps1` suites: passed.
- One Layer 5 commit above Layer 4, clean worktree, and `git diff --check`
  passed.

## E2E status and last blocker

Packaged clean-machine E2E is **not complete**.

The last product blocker was false recovery-task drift after Task Scheduler
normalized the task principal and logon-trigger SID into an account name.
Layer 2 now translates those account names back to SIDs before comparison and
retains drift for unmapped identities. Signed packages containing that fix were
produced, but they were not installed or rerun through the disposable-VM
matrix.

An earlier x64 `0.1.2449.30016` package reported signature status
`UnknownError`. This was traced to the test harness placing trust in
`CurrentUser` stores. Importing the public certificate into
`LocalMachine\TrustedPeople` resolved the harness issue. E2E then exposed the
scheduler account-name/SID drift fixed in Layer 2; corrected `0.1.2449.30017`
packages were not rerun.

IsolationSession lifecycle testing must run from a true interactive desktop
logon. SSH, PowerShell remoting, SYSTEM, and Session 0 use tokens that the
runtime can reject.

## Final package evidence

Signed x64 and ARM64 packages were produced from exact final source
`51cb853d6998cc99fa6d26b66065d7734dd46aea` at
`artifacts\batch2\51cb853d699\0.1.2449.30017`:

| Architecture | Bytes | SHA-256 |
|---|---:|---|
| x64 | 302427292 | `eb545030604a014c752cf0c26dd5d129ef492992e78ce291071b45dcbbcaa8b2` |
| ARM64 | 295626881 | `b139e72058afe3330abd6aaff4c9530f25cf2a1b29c84e91c182f7679c4de3cf` |

The embedded signer thumbprint is
`14C6E67556046C81D3ED4F0F42D9558BDFCD29DB`. Hashes, signer status, and package
containers were independently verified. The output contains no PFX files or
bundled `node.exe`. These packages were not installed or E2E tested and are not
release-ready.

## Superseded package evidence

The following older local artifacts remain preserved:

| Artifact | Bytes | SHA-256 | Provenance |
|---|---:|---|---|
| `artifacts\local-msix\x64\aad7b721c7c\0.1.2449.30015\OpenClawGateway-x64.msix` | 303371233 | `e9a4afb86e3c00212d6f57436e137efb81e3cfc05a55cf83455eeb669697e1d3` | Unsigned, source `aad7b721c7c4bd10f660a6c7671b621e21199a86` |
| `artifacts\local-msix\x64\0.1.2449.30011\OpenClawGateway-x64.msix` | 303332696 | `91ae2187b232532e4322212d6cdd49caa9957c5944e23a8bfe42ebc3df301539` | Superseded pre-final stack |
| `artifacts\local-msix\arm64\0.1.2449.30012\OpenClawGateway-arm64.msix` | 296537958 | `961a43fa20807a3a9bf6f16b3512ec6102920a8cf95be1bad76f7e281d8145ce` | Superseded pre-final stack |

These packages must not be promoted, signed as final, or used as final-stack
E2E evidence.

## Resume requirements

If the decision changes:

1. Reconfirm the five exact commits and a clean Layer 5 worktree.
2. Remove the unsupported direct-mode switch from the product contract before
   release work resumes. Keep plugin UI informational.
3. Re-run focused, full, quality, NativeAOT, and PowerShell validation.
4. Reverify immutable payload metadata and hashes.
5. Compose a fresh exact-SHA x64 MSIX, validate it completely, then compose and
   validate ARM64. Do not run architecture packaging concurrently because the
   repository shares packaging content and restore assets.
6. Test-sign only the newly validated packages.
7. Run the complete x64 clean-machine matrix in a disposable VM through an
   interactive desktop logon, including reboot and recovery-task validation.
8. Treat ARM64 composition and native compilation as the minimum ARM64 gate
   unless an ARM64 VM is available.

Until those steps are completed and the product decision is revisited, this
stack remains parked, unsupported, and not release-ready.
