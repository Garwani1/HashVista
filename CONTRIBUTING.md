# Contributing

Please describe the concrete problem, the expected behavior, and the Windows version. Use a small synthetic example to reproduce bugs. Do not attach personal scan databases, real filenames or paths, drive identifiers, or media containing personal metadata. A shortened error message and an invented filename are usually sufficient.

## Project scope

Keep this an independent file inventory, hash, and media inspection tool. Comparison, duplicate detection, similarity grouping, moving files, deleting source files, and online file submission are outside scope. Do not copy proprietary program code or assets. Preserve attribution and licenses when adapting open-source work.

## Correctness

- Read source files without changing them. Keep all failed or skipped stages explicit.
- Calculate the standard digest for empty files. Do not put error text in a hash field.
- Never label a partial sample, decoded-media fingerprint, or reused result as a freshly computed full-file hash.
- Record algorithm and profile versions, computation time, and reuse validation.
- Keep CSV export independent of visible grid rows and avoid loading the complete export into memory.
- Cover cancellation, changed files, overlapping roots, link loops, Unicode paths, CSV quoting, and reopen/resume behavior when changes affect them.

## Performance

Use bounded buffers, queues, and database batches. Keep UI updates throttled and result lists paged or virtualized. Measure time to first persisted result, bytes actually read, total elapsed time, and peak working set. Compare equivalent algorithms and media settings on the same fixture. State cache conditions and run repeated alternating measurements before claiming a speed improvement.

## Local checks

Use the pinned SDK in `global.json` and locked application restore. Follow the core build commands in README.md. CoreIntegration creates disposable fixtures and reports under `tests/CoreIntegration/runs` and `evidence`; these are excluded from version control and public staging. Media checks require optional tools and separate fixtures; a core test run does not establish media support.

For a change, report the tests actually run and any limits. A successful build or uploaded artifact alone is not proof that the GUI, media decoder, or CSV workflow works. Keep changes small enough to review and explain changes in behavior rather than listing implementation steps.
