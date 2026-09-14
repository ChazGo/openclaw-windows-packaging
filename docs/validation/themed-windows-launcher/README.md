# Themed Windows Launcher validation

This proof was generated from packaging implementation commit
`5ef558a28f182fd002478db89b8844c8ac4747fc` against the merged generic theme
forwarding in `openclaw/openclaw` commit
`f65ecca89667b8a55d9f88d76c487f0a0ab11da8`.

The four screenshots cover the default Claw dark and light themes plus
distinctive imported custom dark and light themes. Each run uses the same
authenticated Gateway process and iframe. The result metadata verifies:

- `Windows Launcher` in the Control UI sidebar, tab, and page heading.
- `Gateway Isolation` as the launcher status row.
- The CLI command and Copy control remain present.
- Exact semantic color, font, and radius forwarding.
- One authenticated iframe request with no navigation or reload during live
  theme changes.
- `sandbox="allow-scripts"` without `allow-same-origin`.
- Read-only HTTP behavior and fail-closed launcher mode handling.

`runtime-results.json` contains sanitized environment details, source commits,
artifact hashes, route checks, frame checks, and semantic token results. This
is expanded-layout browser proof, not installed MSIX proof.
