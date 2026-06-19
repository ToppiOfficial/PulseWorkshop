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

Open `SrcWorkshop.sln` in Visual Studio 2026 and build the `x64` Debug configuration, or:

```sh
dotnet build SrcWorkshop.sln -c Debug
```

(The C++/CLI `SteamBridge` project builds with MSBuild via Visual Studio; the `dotnet` CLI
builds the managed projects.)

## Running

Have **Steam running** and own the target game. Launch `SrcWorkshop.App`, pick a game, and
your published Workshop items load — no extra login prompt.
