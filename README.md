# Arcanum

A free, open-source tabletop card game client built with Godot 4 (C#) for Windows, Linux, macOS, Android and iOS.
Rules automation in the style of digital card game clients (automatic phase passing, auto-pay, automatic combat damage)
with a clean, modern board.

## Content modules
Arcanum is a rules engine and client; it ships no card content. Card data sources, card ability scripts,
formats and sample decks come from a content module installed at runtime (see `docs/card-scripts.md`).

## Status
Early development. See [docs/roadmap.md](docs/roadmap.md) for milestones and what comes next.

## Layout
| Path | What |
|------|------|
| `src/Arcanum.Engine` | Rules engine. Pure .NET 8, no Godot dependency, deterministic. |
| `src/Arcanum.Cards` | Generic cards for tests and the offline demo. |
| `src/Arcanum.Data` | Content modules, card data import, card scripts, deck lists. |
| `docs/` | Card script format and other documentation. |
| `tests/` | xUnit tests (`dotnet test`). |
| project root | Godot project (`project.godot`, `Arcanum.csproj`). |

## Building
- .NET SDK 8
- Godot 4.6 **.NET (mono)** build
- `dotnet test` runs the engine tests; open the folder in Godot .NET to run the client.

## License
Code is licensed under the **GNU Affero General Public License v3.0 or later** (see `LICENSE`).
Anyone distributing modified versions, or running them as a network service, must make their source available
under the same license. Plugins that run inside Arcanum are considered part of the combined work.

## Disclaimer
Arcanum is unofficial, non-commercial fan software. It is not affiliated with, endorsed or sponsored by any game
publisher. Card names, rules text and card images belong to their respective owners; this project does not
distribute card images and is not sold. Card data and images are fetched from public sources at runtime.
