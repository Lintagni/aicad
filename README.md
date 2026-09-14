# AiCad — Draft Assistant for AutoCAD

Describe a drawing in plain language; AiCad plans it, draws it in AutoCAD, then
**measures what it drew and corrects itself**.

The last part is the point. A language model cannot see the drawing it produced,
so AiCad measures the finished geometry — overlapping labels, wires that stop in
empty space, symbols too small to read, parts lying across the title block — and
feeds those faults back as facts. Most are fixed automatically before you see
them.

```
describe  →  plan  →  draw  →  measure  →  correct  →  draw again
```

---

## What you need

| | |
|---|---|
| **AutoCAD** | 2019–2024 (see [Compatibility](#compatibility) — **2025+ does not work yet**) |
| **Windows** | 10 or 11 |
| **.NET** | Framework 4.7+ — already on Windows, nothing to install |
| **An API key** | Google Gemini has a free tier that runs this comfortably |

No Visual Studio, no .NET SDK. The build uses the C# compiler already in Windows
and your own AutoCAD's libraries.

## Getting started

1. Download or clone this repository:

   ```
   git clone https://github.com/<you>/aicad.git
   ```

2. Double-click **`AiCad.cmd`**.

That builds on first run, installs the drawing engine where AutoCAD looks for
plug-ins, and opens the UI in your browser.

3. Click **Settings**, pick a provider, paste an API key, and choose a model.
4. Open AutoCAD.
5. Type what you want:

```
Single line diagram, 250 kVA source, 400 A main MCCB,
four 100 A outgoing ways, IEC symbols
```

The first time you draw after starting AutoCAD, the engine is demand-loaded —
that takes a moment.

### Free to run

The **Gemini free tier** handles this well: the whole request is around 6,000
tokens in and 2,000 out. Click **Reload** beside the model box to see every
model your key can reach. Flash models are the free tier; `gemini-3.6-flash` is
a good default.

If a model is busy the request walks a fallback chain automatically, and the
progress line tells you which model is actually answering.

## Using it

- **2D drawing / 3D solids** — tells the planner which kind of output you want.
- **Add parameters** — pin exact values (standard, scale, layer, text height).
  Blank means "choose sensibly"; a value is treated as fixed.
- **Update current drawing** — replaces what was drawn last instead of adding
  beside it.
- **Pick placement point** — you choose the insertion point in AutoCAD.
- **References** — drop your own DWG/DXF files in `reference/` and AiCad learns
  your conventions from them. See [reference/README.md](reference/README.md).
- **Undo** — removes exactly what the last generation drew, by handle, leaving
  your own work alone.

Each conversation draws into its own drawing, so experiments never land on top
of each other.

## How it works

```
browser UI  ──HTTP──►  AiCadServer.exe  ──COM──►  AutoCAD
                            │                        ▲
                            │  plan (JSON)           │ AiCad.dll
                            └──── %APPDATA%\AiCad ───┘  (the engine)
```

- **`AiCadServer.exe`** — local-only web server on `127.0.0.1:8731`, holds the
  conversation, talks to the model. Every call carries a per-run token.
- **`AiCad.dll`** — the AutoCAD plug-in. Validates and executes plans, and
  reviews the result.
- The two pass plans and results as files in `%APPDATA%\AiCad`.

The model never drives AutoCAD directly. It returns a **JSON drawing plan** made
of validated operations; anything unrecognised fails the whole plan rather than
drawing something wrong.

### The review

After drawing, the result is measured and anything wrong is reported as a fact
with coordinates — then sent straight back for correction:

- labels written on top of each other
- wire ends that stop in empty space
- symbols or text too small to read at the drawing's own scale
- geometry lying across the title block, or outside the border
- 3D parts floating unattached, or buried inside another part
- symbols placed with no tag

Correction is bounded (one pass by default, `autoFixAttempts` in
`aicad.config.json`, maximum 2) and **never accepts a worse drawing** — if the
redraw measures worse by severity, the original is put back.

## Compatibility

**Tested on AutoCAD Electrical 2020.**

| AutoCAD | Works | Why |
|---|---|---|
| 2019 – 2024 | **Expected to work** | .NET Framework based; the build compiles against your own installation's libraries |
| 2025, 2026, 2027 | **No** | AutoCAD moved to .NET 8 at 2025; a .NET Framework plug-in will not `NETLOAD` |
| 2017, 2018 | Unverified | .NET 4.6 era; may work, untested |

AutoCAD **LT does not support .NET plug-ins** and cannot run this.

Vertical flavours (Electrical, Mechanical, MEP) work — they are AutoCAD
underneath. AiCad is domain-neutral: electrical schematics, panel layouts,
mechanical assemblies, enclosures.

> **Porting to 2025+** means retargeting the engine to .NET 8. It is the single
> most valuable contribution anyone could make — see
> [CONTRIBUTING.md](CONTRIBUTING.md).

## Providers

| Provider | Notes |
|---|---|
| **Google Gemini** | Free tier, recommended |
| **Anthropic Claude** | Paid |
| **Groq** | Free, fast, tight per-minute token limits |
| **OpenRouter** | Mixed free and paid |
| **Ollama / local** | Free, offline, needs a capable GPU |
| **OpenAI-compatible** | Any server speaking that API |

Keys are stored in `aicad.config.json` beside the project, which is **git-ignored**.
They are sent only to the provider you choose.

## Project layout

```
src/AiCad/          the AutoCAD plug-in (engine)
  Ops/              primitives and solids
  Generators/       enclosure, DIN rail, busbar, IEC symbols, title block…
  Execution/        validation, execution, the review passes
  Ai/               prompt building and providers
src/AiCadServer/    local web server and conversation
src/AiCadApp/       WinForms host and the COM bridge to AutoCAD
web/                the browser UI
tests/              headless tests — no AutoCAD needed
```

## Building

```powershell
.\build.ps1                                   # auto-detects your AutoCAD
.\build.ps1 -AcadDir "C:\Program Files\Autodesk\AutoCAD 2021"
.\tests\run-tests.ps1                         # headless, no AutoCAD needed
```

Close AutoCAD before rebuilding the engine — a loaded `AiCad.dll` cannot be
replaced. Close the tray app before rebuilding the server.

## Known limitations

- **AutoCAD 2025+ is not supported** (see above).
- Drawings appear when the plan is complete, not progressively. The plan is
  streamed and reported live, but geometry is written in one pass.
- 3D output does not reliably respect stated dimensions — an arm asked for at
  1.8 m may come out larger. There is no dimension check yet.
- Swept and lofted solids in reference drawings are skipped, so imported 3D
  references teach layout but not shape.
- Quality depends heavily on the model. Flash-class models are adequate for 2D
  schematics; 3D assemblies benefit from a stronger one.

## Licence

MIT — see [LICENSE](LICENSE).
