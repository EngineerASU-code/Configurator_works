# TASK-2026-11-17-OBJECT-TYPE-PREVIEW-COVERAGE — Превью по вариантам Type

## Goal

Устранить пробелы предыдущего ревью превью: подключить выбор иллюстрации к реальному рендеру настроек PLC и покрыть подтверждённые варианты `Type` оборудования отдельными подходящими превью. Не считать ограниченный ручной протокол исчерпывающим списком значений; полноту фактических вариантов нужно подтвердить по доступным данным проекта или явно отметить непроверенные классы.

## Scope

- In scope: PLC configuration card rendering and Type-change flow; preview selection for equipment classes `PLC`, `Valve`, `DSV`, `Motor`, `DI`, `DO`, `AI`, `AO`, `PID`; mapping/reuse of neutral technical preview resources; focused resolver/UI tests where feasible; documentation of the type-value inventory and its evidence.
- Out of scope: DB schema or data changes; writes to a user's database; reading DB backups, credential stores, provider profiles, or secret configuration; changing lookup relationships or other screens; publishing or committing.

## Current behavior

- The existing equipment-card preview resolver distinguishes PLC S7 variants and Valve `КВ` + `Discret`, but other classes currently return one class-level image regardless of `Type`.
- `RenderConfigurationObjectDetailsAsync` renders PLC settings separately from ordinary object cards. Its Type ComboBox currently only enables Save, so the PLC resolver is not called on initial render or when Type changes.
- The available manual record in `docs/configurator/test.md` confirms only selected values: Valve `Discret` and `AS-i`; Motor `Discret IO`; DI `NO`; AI `Default`; AO `SIEMENS peripherie`. It does not enumerate all lookup rows and does not document Type values for DSV, DO, or PID. The earlier example `КВ` + `Discret` is a user-provided requested combination, not a value confirmed by that manual record.

## Requirements

- Populate the existing preview slot when PLC configuration details render, using the current displayed `Type` lookup value; keep the PLC glyph fallback for unknown/unavailable types.
- On PLC Type selection, update the preview immediately and preserve the existing Save-enabled behavior. After successful save/re-render, select the preview from the persisted row value.
- Determine the known unique `Type` and image-defining lookup-value combinations for `Valve`, `DSV`, `Motor`, `DI`, `DO`, `AI`, `AO`, and `PID` from available non-secret source evidence or a read-only application view. Record the evidence and explicitly flag any class for which the complete live lookup set cannot be verified.
- Provide a distinct technical illustration for every confirmed unique combination that changes the equipment kind/appearance; reuse one resource for all records with the same normalized class and displayed dependency values.
- Do not treat unrelated fields such as engineering units or tuning parameters as image-defining dependencies without evidence that they identify equipment type.
- Resolve confirmed values case-insensitively and tolerate common formatting variants without matching a different type accidentally. Unknown or absent combinations must use a neutral class glyph/fallback and must not borrow the illustration of a different type.
- Preserve existing exclusions for `Area`, `Program`, `Status`, `Step`, and `Matrix`, the PLC class-number warning behavior, and existing lookup warnings for actual `Type` problems.
- Keep preview assets shared by type/dependency combination; do not add per-record image storage or DB columns.

## Acceptance criteria

- [ ] Initial rendering of PLC configuration selects the image for its displayed PLC Type when that type is mapped; unknown/missing Type falls back safely.
- [ ] Changing PLC Type changes its preview immediately and enables Save; successful save and re-render preserve the image corresponding to the saved Type.
- [ ] A checked inventory table records, per equipment class, each confirmed distinct Type/dependency combination, its evidence, and the mapped preview resource; unavailable exhaustive lists are marked explicitly rather than presented as complete.
- [ ] Each confirmed image-defining combination has a distinct suitable preview when its appearance differs; equivalent values/aliases reuse the same resource.
- [ ] For documented Valve values `Discret` and `AS-i`, resolution is type-aware; `КВ` + `Discret` is mapped only if the actual field/value combination is confirmed in accessible project data, otherwise it remains explicitly unverified and uses safe fallback.
- [ ] Documented variants `Motor: Discret IO`, `DI: NO`, `AI: Default`, and `AO: SIEMENS peripherie` resolve without affecting unrelated engineering fields.
- [ ] DSV, DO, PID, and any other classes with no complete inventory in accessible non-secret evidence are called out as not fully enumerable; unknown values never select an unrelated type-specific image.
- [ ] Resolver tests cover every checked mapping, case/format aliases, unknown values, classes with exclusions, and shared-resource behavior; tests do not require a live or user database.
- [ ] No database writes, schema changes, credentials, provider settings, or backup files are involved.

## Constraints

- Keep the change limited to the object-card preview feature and its focused tests/documentation.
- Use display values from the lookup editor rather than foreign-key IDs to determine illustrations.
- Preserve Online/Offline behavior and update the preview from the same current editor values shown to the user.
- Do not claim all project Type variants are covered unless the inventory source provides a complete list for each class.

## Test scenarios

- Load a PLC card with each mapped PLC Type; change the Type in its settings ComboBox, verify immediate preview change, save, and reopen the card.
- Exercise every mapped class/type pair through the resolver and verify expected shared asset key.
- Exercise unknown, blank, differently cased, and normalized alias values; verify safe fallback and absence of exceptions or wrong type-specific imagery.
- Verify `Area`, `Program`, `Status`, `Step`, and `Matrix` keep their prior non-equipment presentation.
- Run focused tests and the project Release build. Do not write to a user's database; any manual DB-backed confirmation must be read-only and its source clearly identified.

## Open questions

- The checked-in manual evidence does not expose the full lookup value sets for all classes. Are all values discoverable through the already-open project's read-only UI during this task? If not, exhaustive inventory remains unverified and should be handed back for a safe data export or user-provided lookup list.
- The exact Valve field and displayed value that identify `КВ` in the active project are not documented in the available manual evidence; confirm it from the UI/source metadata before treating the example as a verified combination.
