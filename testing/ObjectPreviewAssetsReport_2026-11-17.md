# Object preview inventory and assets — 2026-11-17

## Available Type evidence

This table records only values present in available manual evidence or explicitly supplied by the user. It is not a complete inventory of the connected project's lookup tables.

| Class | Confirmed or reported Type/dependency values | Preview mapping | Evidence / limit |
|---|---|---|---|
| PLC | `S7-1500`, `S7300`, `S400` are requested variants; diagnostic log reports three rows in `dbo.PLC_Types` but omits their labels. | `plc-s7-1500.png`, `plc-s7-300.png`, `plc-s400.png` | Mapping follows the task examples. Actual lookup labels and full table contents were not captured. |
| Valve | `Discret`, `AS-i` in manual test notes; user-reported combination `MS_Type=КВ` + `Type=Discret` is not independently confirmed in project data. | `valve-discret.png`, `valve-asi.png`; unverified `КВ` combination safely falls back to `valve-discret.png`. | The manual values are observations, not a full lookup list. The live `КВ` row was not independently read, so its dedicated illustration is not selected. |
| DSV | No Type value found in manual notes. | `dsv.png` class fallback | Type set remains unknown. |
| Motor | `Discret IO`. | `motor-discret-io.png` | `docs/configurator/test.md`, M3-29. Other values not listed. |
| DI | `NO`. | `di-no.png` | `docs/configurator/test.md`, M3-30. Other values not listed. |
| DO | No Type value found in manual notes. | `do.png` class fallback | Type set remains unknown. |
| AI | `Default`. | `ai-default.png` | `docs/configurator/test.md`, M3-32. Units are intentionally not used as an image key. |
| AO | `SIEMENS peripherie`. | `ao-siemens-peripherie.png` | `docs/configurator/test.md`, M3-33. Units are intentionally not used as an image key. |
| PID | No Type value found in manual notes. | `pid.png` class fallback | Type set remains unknown. |

The read-only UI inventory attempt reached the project table list but did not retrieve lookup rows before the application window became unavailable. No database write was performed. Full unique-value coverage remains unverified for every class.

## Assets

The following neutral technical mini-illustrations are shared by type/dependency mapping; none is stored per object row:

- PLC: `plc-s7-300.png`, `plc-s400.png`, `plc-s7-1500.png`.
- Valve: `valve-discret.png`, `valve-asi.png`. The unused generated KV variant was removed with explicit approval; add a dedicated illustration only after its field/value combination is confirmed from project data.
- Motor / DI / AI / AO: `motor-discret-io.png`, `di-no.png`, `ai-default.png`, `ao-siemens-peripherie.png`.
- Generic class fallbacks: `valve.png`, `dsv.png`, `motor.png`, `di.png`, `do.png`, `ai.png`, `ao.png`, `pid.png`.

Variant files were generated at 180×180 with transparent backgrounds. The existing resource wildcard in `Configurator.csproj` includes PNG assets under `Images/ObjectPreviews`.
