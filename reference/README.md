# Reference drawings

Drop your own **DWG** or **DXF** files in this folder. AiCad reads them once,
converts what it can into examples, and consults the relevant ones whenever it
draws — so the output picks up your layer names, text heights, symbol choices
and tag style instead of generic defaults.

Nothing here is committed. The folder is in `.gitignore` on purpose: these are
usually client drawings, they are often hundreds of megabytes, and they belong
to whoever put them there.

## What gets used

| | |
|---|---|
| **Read** | lines, arcs, circles, polylines, text, mtext, blocks, dimensions |
| **Read (3D)** | solids that are still recognisable primitives — box, cylinder, sphere, cone |
| **Skipped** | swept, lofted and boolean solids — there is no reliable way to turn a finished solid back into the operations that made it, and recording one as a cuboid would teach the wrong thing |

Only the first 1200 entities of each drawing are captured, so a small,
representative drawing teaches more than a huge one.

## Tips

- **Representative beats big.** Three drawings in the work you actually do are
  worth more than thirty unrelated ones.
- **Match the domain.** Architectural plans will not help the assistant draw
  electrical schematics.
- A drawing that fails to convert is recorded and skipped rather than retried
  forever. Edit the file and it will be tried again.
- The **References** window in the app shows what is indexed, waiting or failed,
  and why.

Indexing happens in the background and does not hold up drawing. A large file
takes a few minutes.
