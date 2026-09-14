# Contributing to AiCad

Pull requests are welcome. This file covers what you need to get building, how
the pieces fit together, and the few rules that matter.

## Before you start

**Never commit `aicad.config.json`.** It holds API keys. It is git-ignored —
keep it that way.

**Never commit drawings.** `reference/` is where users put their own DWG files,
which are usually client work and often hundreds of megabytes. `*.dwg` and
`*.dxf` are ignored repository-wide.

## Getting a development build

```powershell
git clone https://github.com/<you>/aicad.git
cd aicad
.\build.ps1
.\tests\run-tests.ps1
```

You need Windows, an AutoCAD install from 2019–2024, and nothing else — the
build uses `csc.exe` from the .NET Framework already on Windows and links
against your own AutoCAD's `acmgd.dll` / `acdbmgd.dll`.

### The rebuild dance

Two files get locked while things are running:

| Locked by | Blocks | Fix |
|---|---|---|
| AutoCAD | `AiCad.dll` (the engine) | Close AutoCAD |
| The tray app | `AiCadServer.exe` | Close the AiCad tray icon |

After changing engine code you must also **reinstall it**: close AutoCAD, click
**Update engine** in the UI (or run `.\install.ps1`), then reopen AutoCAD. The
engine is demand-loaded, so it only picks up on the first AiCad command.

Changes to `web/` need only a browser refresh — those files are served from disk.

## How it fits together

```
browser UI  ──HTTP──►  AiCadServer.exe  ──COM──►  AutoCAD
                            │                        ▲
                            │  plan (JSON)           │ AiCad.dll
                            └──── %APPDATA%\AiCad ───┘
```

Three processes, two boundaries:

- **The server** owns the conversation, the prompt and the provider call. It
  never touches AutoCAD geometry.
- **The engine** owns everything inside AutoCAD: validating plans, executing
  them, reviewing the result.
- They communicate through JSON files in `%APPDATA%\AiCad` (`inbox/`,
  `capabilities.txt`, progress files), plus COM to nudge AutoCAD.

Sources under `src/AiCad/` are shared by both, so anything there must stay free
of AutoCAD types **unless** it lives in `Ops/`, `Generators/` or `Execution/`.
`build.ps1` lists which shared files the server compiles — add new shared files
to that list or the server build breaks.

## Adding a drawing operation

Operations are the vocabulary the model writes plans in. To add one:

1. Implement `IOp` in `src/AiCad/Ops/` or `src/AiCad/Generators/`.
2. Give it a `Usage` string. **This is documentation the model reads** — it is
   published in the op catalogue and is the only description the model gets.
   Be exact about geometry: a wrong number here produces wrong drawings
   everywhere, silently.
3. Register it in `src/AiCad/Execution/Capabilities.cs`.
4. `Validate` should reject bad input with a clear message; the model gets one
   repair round-trip to fix validation errors before anything is drawn.

Prefer a **generator** over asking the model to compose primitives. Geometry
that a generator builds is correct by construction; geometry composed from
primitives is only as good as the model's arithmetic. Where an op can compute
something itself — a terminal position, a lead wire — let it, rather than asking
the model to.

## Adding a review check

`src/AiCad/Execution/SheetReview.cs` (2D) and `ModelReview.cs` (3D) measure the
finished drawing and report faults. Those reports drive automatic correction, so:

- **A false alarm is expensive.** It sends the next attempt off fixing something
  that was never wrong. Only report faults that are unambiguous.
- **Lead with a number where you can.** Severity is scored from the leading
  integer in a warning, and that is how a correction is judged better or worse.
- **Say what to do.** "36 of 124 wire ends stop in empty space… recompute the
  endpoints from the scaled symbol" is actionable. "Layout is poor" is not.

## Tests

`tests/run-tests.ps1` runs headless — no AutoCAD required. It compiles only the
AutoCAD-free sources, so anything testable should be kept free of AutoCAD types
where that is natural (see `TextFit.cs` and `PlanStream.cs` for the pattern).

Add tests for anything with arithmetic in it. Two real bugs in this codebase —
a character-width estimate and an off-by-one in a streaming parser — were both
silent, and both are now pinned by tests.

## Style

Match the surrounding code. In short: explicit over clever, comments that say
*why* rather than *what*, and no abbreviations in names. Existing comments
explain the reasoning behind non-obvious decisions — that is the standard.

## Good first contributions

| | |
|---|---|
| **AutoCAD 2025+ support** | Retarget the engine to .NET 8. Highest value by far — it unblocks three AutoCAD releases. Needs a second build path; the server and UI are unaffected |
| **Progressive drawing** | Plans are already streamed and ops counted as they arrive (`PlanStream.cs`). Feeding them to AutoCAD in batches would make geometry appear as it is generated |
| **Dimension checking** | Compare the drawing's extents against dimensions stated in the request, so "1.8 m reach" that comes out at 4 m is caught and corrected |
| **More generators** | Fences, conveyors, cable trays, terminal blocks — anything drawn repeatedly is better as a generator than as model arithmetic |
| **Symbol terminals** | `symbol` (AutoCAD Electrical library blocks) has no terminal contract, so the model guesses where wires attach. The blocks carry `X?TERM??` attribute definitions that could be published in the catalogue |
| **More providers** | The provider interface is small — see `src/AiCad/Ai/Providers.cs` |

## Submitting

- One change per pull request.
- Say what you tested. If you could not test something, say so — unverified is
  fine, unverified and presented as verified is not.
- Run `.\tests\run-tests.ps1` and mention the result.
- If you changed engine code, say which AutoCAD version you tested on.
