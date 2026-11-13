# DiscColorStudio adapter

Source: https://github.com/Neo-626/DiscColorStudio
Pinned commit: ef44b13694e9873fc47ea9e0040ec5af502995ae (v0.8.5).
License: GPL-3.0, retained in LICENSE and third-party license files.

The original measured angular LUT and its compressed data are unchanged. They were
measured on Verbatim 95058 DVD-R 16x at 4x, not Verbatim 43533 or Blu-ray.
The demonstration media under docs/media are omitted. User artwork is never bundled.

DIS modifications: add a JSON/NDJSON worker entry and DisWorker.cs, isolate build
artifact names, and allow the worker to choose DVD encoding parallelism from the
available logical CPU count. The normal upstream GUI retains its default scheduler.
The worker never opens a recorder. Physical writing remains in the DIS application.
