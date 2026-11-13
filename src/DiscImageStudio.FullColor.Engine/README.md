# BDColorLab engine for Disc Image Studio

This directory contains the file-only numerical backend for the native Full Color
workspace. It is adapted from the user-provided `BDColorLab_64099_v0.1.zip`.
The original `bdcolor` numerical modules retain their GPL-3.0-only licensing;
their BD scrambler, geometry, LDC/BIS position and 17PP routines were ported from
DiscImageStudio v1.5.0. See `PROVENANCE.md` and `LICENSE`.

`worker.py` accepts one JSON request file and emits newline-delimited JSON
progress. It provides preview, calibration and full-image export operations.
It never accesses optical drives. GUI code and parameters remain in the WPF app.
The first preview generates the spectrum bank in the chosen work directory;
no generated binary bank is committed. Full color on physical media is unverified.

Dependencies are installed into a separate environment with
`tools/setup-fullcolor.ps1`; see `docs/FULL_COLOR.md` for build and usage.
