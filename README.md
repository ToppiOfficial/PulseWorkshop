# SrcWorkshop

A **Steam Workshop manager** — Browse, create, and edit your own Workshop
items (title, description, tags, content files, preview image).

Ships configured for **Left 4 Dead 2** (App ID `550`) and **Garry's Mod** (App ID `4000`);
the game list is config-driven, so more games can be added.

This is *only* a Workshop tool — no model viewer, compile, or decompile.

## How it works

SrcWorkshop hooks into your **already-running Steam client** and reuses that session, so it
**never prompts a separate Steam login**. (`SteamAPI_Init` only succeeds for games owned by
the currently logged-in account, with Steam running.)

```
SrcWorkshop.sln
├── SrcWorkshop.App         WPF (net10.0-windows)   UI: game picker, published/drafts/templates lists, item editor
├── SrcWorkshop.Core        classlib (net10.0)      models, game config, pipe client, draft/template stores, services
├── SrcWorkshop.SteamHost   console exe (net10.0)   owns the Steam session for one App ID; named-pipe JSON server
└── SrcWorkshop.SteamBridge C++/CLI (net10.0)       thin managed wrapper over steam_api64.dll (Steamworks SDK)
```

The App talks to `Core`; `Core` launches one `SteamHost` process per active game (App ID is
process-global in the Steamworks SDK, so switching games relaunches the host) and exchanges
JSON over a named pipe. `SteamHost` references the C++/CLI `SteamBridge`, which calls the
native Steamworks API.

## Prerequisites

### To run a published build

- **Windows x64** with the **Steam client running**, and you must own the target game.
- The **[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64)**.
  The published builds are *framework-dependent*, so the runtime must already be installed on the
  machine. (If you build *self-contained* instead, the runtime is bundled and this is not needed -
  see [Publishing](#publishing).)

### To build from source

- **Visual Studio 2026** (v18) with:
  - .NET desktop development workload (WPF)
  - **C++/CLI support** (Desktop development with C++ + the "C++/CLI support" component)
- **.NET 10 SDK**
- The **Steamworks SDK** (see below)

## Getting the Steamworks SDK

The Steamworks SDK and `steam_api64.dll` are **not redistributable** and are **not committed**
to this repo (the `.gitignore` excludes `*.dll` / `*.lib`).

1. Download the Steamworks SDK from <https://partner.steamgames.com/downloads/list> (requires
   a Steamworks account).
2. Copy its contents into `external/steamworks_sdk/` so that the headers resolve as
   `external/steamworks_sdk/public/steam/steam_api.h`.
3. The build copies `external/steamworks_sdk/redistributable_bin/win64/steam_api64.dll` next to
   `SrcWorkshop.SteamHost.exe`.

> **Note:** A `steam_appid.txt` file is used only for local testing and must **not** ship to
> end users. Released builds rely on running alongside the Steam client.

## Building

Open `SrcWorkshop.sln` in Visual Studio 2026 and build the `x64` Debug (or Release)
configuration.

The whole solution must be built with **Visual Studio / MSBuild**, not the `dotnet` CLI: the
C++/CLI `SteamBridge` needs MSBuild (the `dotnet` CLI cannot evaluate `$(VCTargetsPath)`). The
`App` + `Core` projects alone can be built with `dotnet build`, but a full build requires MSBuild.

## Publishing

The repo ships **Folder publish profiles** that drop the App and the SteamHost into a single
shared `publish/` folder, ready to run side by side. The App is published as `SrcWorkshop.exe`,
with `SrcWorkshop.SteamHost.exe` next to it (the App locates the host by that filename).

**Easiest - one command** (builds the solution and publishes both projects into `publish/`):

```powershell
.\build-portable.ps1
```

**Or from Visual Studio:**

1. **Build the solution once** (Release | x64) so the C++/CLI `SteamBridge` is built.
2. Right-click **`SrcWorkshop.SteamHost`** -> **Publish** -> **FolderProfile**.
3. Right-click **`SrcWorkshop.App`** -> **Publish** -> **FolderProfile**.

Both projects publish into `publish/` (the App needs `SrcWorkshop.SteamHost.exe` next to it).
The profiles are **framework-dependent** (no bundled runtime, no single-file), so the target
machine needs the **.NET 10 Desktop Runtime** installed - see [Prerequisites](#to-run-a-published-build).

> To produce a fully portable build that needs no .NET install, set `<SelfContained>true</SelfContained>`
> in both `Properties/PublishProfiles/FolderProfile.pubxml` files. The folder gets larger (the whole
> runtime is bundled), but it runs on any x64 Windows machine with Steam.

## Running

Have **Steam running** and own the target game. Launch `SrcWorkshop.App`, pick a game, and
your published Workshop items load — no extra login prompt.
