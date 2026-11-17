# Manual test and reference materials

This folder preserves the open manual acceptance checklist (`Task.md`), implementation plan, original user observations (`WorkLog.md`), screenshots, UI reference, and legacy GASS reference materials. These remain useful for checking pending WPF/SQL and Program/Step/Matrix compatibility requirements.

The current structure review, cleanup decisions, fresh build/test results, and remaining limitations are in [the preparation report](../docs/project/PROJECT_PREPARATION_2026-11-17.md). The report from 16 November is historical evidence only. Obsolete local build directories and the historical download ZIP were removed with explicit approval. Build a fresh local package using the root README.

`ObjectPreviewAssetsReport_2026-11-17.md` records confirmed preview mappings and unavailable full Type inventories. `GASS/`, `screensots/`, and `NewReference/` are reference evidence, not application build dependencies. Legacy executables and macros are preserved but are not run by build or test commands.

`GASS/SQL_Configuration.ini` is deliberately excluded because the local copy contains a database password. Use `GASS/SQL_Configuration.example.ini` as a template and keep real credentials only in a local, untracked `SQL_Configuration.ini`.
