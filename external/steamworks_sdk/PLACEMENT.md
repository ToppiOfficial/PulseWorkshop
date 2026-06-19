# Steamworks SDK goes here

This folder is intentionally (almost) empty — the Steamworks SDK is **not redistributable** and
is **not committed** to the repo.

Download the SDK from <https://partner.steamgames.com/downloads/list> and extract its `sdk`
contents here so the following paths resolve:

```
external/steamworks_sdk/
├── public/
│   └── steam/
│       └── steam_api.h            <- headers (used by SrcWorkshop.SteamBridge)
└── redistributable_bin/
    └── win64/
        ├── steam_api64.lib        <- linked by SrcWorkshop.SteamBridge
        └── steam_api64.dll        <- copied next to SrcWorkshop.SteamHost.exe at build
```

The build reads this location via the `SteamSdkDir` MSBuild property
(defined in `SrcWorkshop.SteamBridge.vcxproj`); override it if you keep the SDK elsewhere.
