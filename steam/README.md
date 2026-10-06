# Shipping Dockable on Steam

Dockable ships as a **self-contained x64 build** — the .NET 10 runtime and WPF are bundled, so players
need nothing pre-installed. Steam delivers the whole publish folder as a depot.

## 1. Build the package

```powershell
pwsh -File steam\build-steam.ps1
```

This runs the `SteamRelease` publish profile and produces:

```
src\Dockable\bin\Publish\Steam\win-x64\Dockable.exe   ← the launch target
```

(Equivalent manual command: `& "C:\Program Files\dotnet\dotnet.exe" publish "src\Dockable\Dockable.csproj" -p:PublishProfile=SteamRelease`.)

The profile is self-contained + ReadyToRun, **not** single-file and **not** trimmed (WPF isn't
trim-safe; a multi-file folder is the most reliable thing to hand to SteamPipe). ~200 MB, ~400 files
— normal for self-contained WPF.

## 2. One-time Steamworks setup (partner site)

1. Get an **App ID** (Steamworks already assigned one when the store page was created).
2. Under **SteamPipe → Depots**, note your **Depot ID** (usually `AppID + 1` for the Windows content depot).
3. Create a **builder account** (a dedicated Steam account with *Edit App Metadata* / *Publish* rights)
   — don't upload with your personal account.
4. Set the app's **Launch Option** to `Dockable.exe` (OS: Windows, no arguments needed).

Then fill the IDs into [`app_build.vdf`](app_build.vdf) and [`depot_build.vdf`](depot_build.vdf)
(replace `YOUR_APP_ID` / `YOUR_DEPOT_ID`).

## 3. Upload

Install **steamcmd** (<https://developer.valvesoftware.com/wiki/SteamCMD>), then from the repo root:

```powershell
steamcmd +login <builder_account> +run_app_build "<repo>\steam\app_build.vdf" +quit
```

Steam Guard will prompt the first time (interactive). On success the build appears under
**Steamworks → your app → SteamPipe → Builds** — select it for a branch (e.g. `default`/`beta`) and
**Publish**.

## Notes / gotchas

- **No Steamworks SDK is integrated** (no overlay/achievements/cloud/DRM wrapper). Steam launches the
  plain exe — which is all that's needed to ship. If you later want the overlay, achievements, or
  Steam's DRM wrapper, that's a separate change (add `steam_api64.dll`, a `steam_appid.txt` for local
  testing, and init calls); ask and I'll wire it.
- **Single instance:** Dockable already uses a named mutex, so Steam's "running" state stays accurate.
- **Auto-update:** any re-upload to the same depot/branch is delivered as a normal Steam update.
- The exe carries product metadata (name **Dockable**, version **1.0.0**, author **Leo**) from
  `Dockable.csproj`; bump `<Version>` there per release and update the `Desc` in `app_build.vdf`.
- `steam/output/` (SteamPipe logs + chunk cache) is gitignored.
