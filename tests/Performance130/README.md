# Controlled 1.2.3 / HashVista 1.3.0 engine timing

This harness derives from Performance122 and loads one supplied scanner DLL per
process. It does not rebuild either application. It accepts the older three-argument
RunAsync signature and the new optional ScanControl argument, and explicitly selects
the zero-argument Results overload.

The controlled dataset has 3,001 files and 146,505,728 bytes: 3,000 deterministic
4 KiB files plus one 128 MiB file. Every run independently checks every SHA-256,
the complete set of file names, final counts/status, and actual hash-read bytes.
The expected digests are computed during fixture generation independently of the
scanner. No user archive is scanned.

Run one warmup per version followed by three alternating measured runs per version.
Timing covers ScanEngine.RunAsync only, excluding fixture creation, process startup,
database creation, and independent validation. SHA-256 is the sole selected stage.
No pause is requested; the optional ScanControl is null in this engine-only harness.
This does not measure UI rendering, media decoding, cold storage, network drives,
or a multi-terabyte archive. The desktop is a shared development session, not an
isolated performance laboratory.

The actual 28 October 2026 test produced median 3.501723 seconds for 1.2.3 and
3.4642823 seconds for the first HashVista 1.3.0 publish, a difference of -1.07%.
Measured ranges overlap (approximately 3.39–3.53 versus 3.42–3.56 seconds).
This supports comparable performance on this fixture, not a general speedup.
The baseline warmup was 8.45 seconds and is excluded from the measured median.
All eight runs matched every independent digest and read exactly 146,505,728 bytes.

Evidence is `evidence/performance130-summary.json` and `performance130.log`;
generated fixtures and scan databases are under `evidence/performance130-runs`
and must be omitted from public source packages. The summary identifies the
exact DLL hashes and framework used. Later UI-only fixes require separate UI
verification; this timing does not claim to test their rendering behavior.

Build `Performance130.csproj` in Release using the project SDK, then run `run.ps1`
with explicit `-Dotnet`, `-BaselineAssembly`, `-FixedAssembly`, `-Dependencies`,
and a fresh `-RunRoot` outside either app/harness directory. The script refuses
to overwrite an existing run directory or scan database.
