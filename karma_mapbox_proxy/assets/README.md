# Patch Assets

This folder contains files used by the image patcher and Windows GUI.

`WMM.COF` is the NOAA/NCEI WMM2025 coefficient file used to replace the stock controller magnetic model.

The source repository intentionally does not include generated controller-side binaries:

- `karma-mapbox-proxy`
- `karma-file-browser`
- `karma-button-gate`

Those files should be built and copied into this folder only when preparing a release package. Keeping them out of the source repository avoids committing stale binaries and makes it easier to audit what is source code versus generated output.
