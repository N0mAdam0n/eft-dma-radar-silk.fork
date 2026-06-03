# EFT DMA Radar — Silk.NET Edition (Unity 2022)

A modern DMA (Direct Memory Access) radar overlay for **Escape from Tarkov** (Unity 2022.3.43f1 EFT build), built on [Silk.NET](https://github.com/dotnet/Silk.NET) (Windowing / Input / OpenGL), [ImGui.NET](https://github.com/ImGuiNET/ImGui.NET) panels, and [SkiaSharp](https://github.com/mono/SkiaSharp) 2D rendering. Ships with an embedded ASP.NET Core web radar.


> **Targeting the Unity 6 EFT build?** See the sibling repo [**eft-dma-radar-silk6**](https://github.com/HuiTeab/eft-dma-radar-silk6) — same UI, same web radar, same features; Unity 6000.3.6f1 engine offsets and IL2CPP layout.

The `src-silk/` codebase is an **original work written by [HuiTeab](https://github.com/HuiTeab)**. The only third-party code in this repository is `lib/VmmSharpEx/` — a separately-licensed (AGPL-3.0) wrapper around [MemProcFS](https://github.com/ufrisk/MemProcFS), included unmodified-in-attribution as part of the radar's DMA stack. See [LICENSE](LICENSE) for the full license breakdown.

---

## Repo Layout

```
eft-dma-radar-silk/
├── eft-dma-radar-silk.sln       # Visual Studio solution (VmmSharpEx + src-silk)
├── Directory.Build.props        # Common MSBuild props (net10.0-windows, x64, unsafe)
├── version.json                 # Nerdbank.GitVersioning version source
├── LICENSE                      # BSD Zero Clause License
├── Maps/                        # EFT map SVGs + JSON metadata (Customs, Streets, …)
├── Resources/                   # Embedded font + default item DB
├── lib/
│   └── VmmSharpEx/              # Managed MemProcFS / LeechCore wrapper + native DLLs
└── src-silk/                    # The radar itself (entry: Program.cs → SilkProgram.Main)
```

---

## Requirements

- **DMA hardware** supported by [MemProcFS](https://github.com/ufrisk/MemProcFS) (FPGA card, `usb3380`, etc.)
- **Windows 10 / 11 (x64)** — project targets `net10.0-windows`, `PlatformTarget=x64`
- **[.NET 10 SDK / Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)**
- **Visual Studio 2022 17.12+** (or 2026 Insiders) with the **.NET desktop development** workload
- The native MemProcFS binaries (`vmm.dll`, `leechcore.dll`, `FTD3XX.dll`, …) ship under `lib/VmmSharpEx/native/` and are copied to the build output automatically.

---

## Build & Run

```powershell
git clone https://github.com/HuiTeab/eft-dma-radar-silk.git
cd eft-dma-radar-silk

# Build (Release, x64)
dotnet build eft-dma-radar-silk.sln -c Release

# Run
dotnet run --project src-silk\eft-dma-radar.csproj -c Release
```

Pass `-debug` on the command line (or set `debugLogging=true` in the config) for verbose startup logging.

In Visual Studio: open `eft-dma-radar-silk.sln`, set `eft-dma-radar` as the startup project, press **F5**.

---

## Highlights

**Desktop shell**
- **Icon sidebar** with two tiers — five primary panels (Players · Loot · Aimview · Quests · Settings) plus a compact secondary row (Loot Filters · Killfeed · Hideout · Quest Planner · Player History · Watchlist · Hotkeys) and ESP at the bottom. Every slot is a single click; hotkey hints come from the user's actual binding via `HotkeyManager.GetBindingDisplay`.
- **Top command bar** — pill-style toggles in the same chip language as the bottom status bar: Follow/Free · Battle · Preset · Aim/Loot/Exfils · Restart · More. Right cluster shows current map + FPS.
- **Big-chip status bar** at the bottom: raid state · players (segmented `T/P/S/AI` counts) · vitals · FPS · DMA · map — readable on AnyDesk / TV.
- **Command palette** (`Ctrl+K`) fuzzy-searches every hotkey action and panel, **toast system** for transient feedback, **first-run tour**, and fully configurable hotkeys.

**Presets** (Stealth · Loot Run · PvP · Quests · Custom) bundle 13 toggles each and are intentionally distinct — Stealth = silent extract, Loot Run = max info, PvP = hunter mode, Quests = objectives-only. Drift detection auto-demotes to Custom on manual tweaks.

**Loot Filters panel** — full-width toggle rows, integer steppers (auto-repeat), combo rows, four **Quick View** chips (All Loot · Important+ · Wishlist · Quest), live `visible / total` counter.

**Web radar** (`src-silk/Web/wwwroot/`) — primary buddy view. Works equally on a desktop browser (mouse + keyboard) and on a phone or tablet (touch).
- **Bottom tab bar** (Players · Loot · Layers · Settings) opens slide-up **bottom sheets**; swipe-down or click the close button to dismiss.
- **FAB radial** for the most-used toggles — click to open, click a slice to toggle; on touch, hold to open and release on a slice.
- **Follow-me default** with **double-tap / double-click recenter** on empty map space, scroll-wheel or pinch zoom.
- **Independent web presets** (Spotter · Battle Buddy · Loot Hunter · Quest Helper · Custom) — separate from the desktop host's preset; each buddy picks their own view. Top-center chip cycles them.

**Map rendering**
- SVG-based map layers with height-aware overlays and dimming.
- **Satellite imagery** via assets.tarkov.dev tile pyramid (Customs, Interchange, Reserve, Shoreline, Woods, Ground Zero) — adaptive zoom level matching screen pixels per base-pixel, on-disk tile cache at `%LocalAppData%\eft-dma-radar-silk\tilecache`.
- Independent toggles for desktop and web — the web radar has its own satellite switch that doesn't affect the host's view, and tiles are proxied through `/api/tile/{cacheKey}/{z}/{x}/{y}.png` to bypass CORS for browser clients.

**Config**
- `%AppData%\eft-dma-radar-silk\config.json` — debounced JSON persistence.
- IL2CPP offsets resolved at startup and cached to `il2cpp_offsets.json`; hard-coded fallbacks live in `src-silk/SDK/Offsets.cs`.

---

## Project Details

### `lib/VmmSharpEx`

A managed C# wrapper around [MemProcFS](https://github.com/ufrisk/MemProcFS) (`vmm.dll`) and LeechCore (`leechcore.dll`). Provides a high-level `Vmm` handle (read / write / VFS / process enumeration), a `LeechCore` device wrapper, a scatter API for batched gathers / writes, a memory search engine, a refresh manager, strongly-typed flag enums, a Win32 virtual-key DMA input manager, and a `VmmPointer` abstraction with a rich `VmmException` hierarchy.

- TFM: `net10.0-windows`, `Nullable=enable`, doc-file generated.
- Native bin: `lib/VmmSharpEx/native/` (`vmm.dll`, `leechcore.dll`, `leechcore_driver.dll`, `FTD3XX.dll`, `dbghelp.dll`, `symsrv.dll`, `tinylz4.dll`, `vcruntime140.dll`).
- License: **AGPL-3.0** — original MemProcFS API © Ulf Frisk; `VmmSharpEx` modifications © Lone (Lone DMA), 2025.

### `src-silk`

- AssemblyName: `eft-dma-radar` · RootNamespace: `eft_dma_radar.Silk`
- Entry point: [`SilkProgram.Main`](src-silk/Program.cs)
- Packages: `ImGui.NET 1.91.6.1`, `Silk.NET.Windowing/Input/OpenGL/OpenGL.Extensions.ImGui 2.23.0`, `SkiaSharp 3.119.2`, `Svg.Skia 3.0.3`, `Open.Nat.imerzan 2.2.0` (+ `Microsoft.AspNetCore.App` framework reference for the web radar).

---

## License

The source code in this repository (everything outside `lib/VmmSharpEx/`) is licensed under the **[PolyForm Noncommercial License 1.0.0](LICENSE)** — free to use and modify for personal / non-commercial purposes; commercial use, resale, hosting paid services, or any other revenue-generating use is **not permitted**.

The component under `lib/VmmSharpEx/` is a wrapper around [MemProcFS](https://github.com/ufrisk/MemProcFS) and is licensed separately under **AGPL-3.0** — its original copyright notices are retained in the source files of that directory. Because the compiled radar binary links AGPL-3.0 code, **redistributors of compiled binaries must also satisfy AGPL-3.0 requirements** (source availability, etc.). The PolyForm Noncommercial terms govern this repository's own source code.

If you want to use this project commercially, that means writing a clean replacement for VmmSharpEx (talking to MemProcFS yourself) **and** obtaining a separate commercial license from the copyright holder of this repository.

---

## Credits

- MemProcFS by **Ulf Frisk** (<https://github.com/ufrisk/MemProcFS>) — the DMA stack everything is built on.
- Reference data from [tarkov.dev](https://tarkov.dev/) (see in-app credits).
- Thanks to the broader EFT DMA / MemProcFS community for offsets and reverse-engineering work over the years.
