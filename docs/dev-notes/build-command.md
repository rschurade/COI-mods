---
name: build-command
description: Exact MSBuild command/flags to build & deploy the ElevationPP COI mod — do not alter it
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 568e6649-c510-41f3-aba1-69f2ce6d94e6
  modified: 2026-08-05T18:35:55.635Z
---

Build the Elevation++ mod with this EXACT PowerShell tool command (preapproved via
the allow rule "PowerShell(*ElevationPP\\build.ps1*)" in user settings — mid-string
wildcards are required; prefix-anchored PowerShell rules do not match because
Claude Code re-serializes the parsed command before matching):

```
D:\projects\privat\mods\coi\mods\ElevationPP\build.ps1
```

(Moved 2026-08-08: both mods now live in the `mods` monorepo — see [[git-workflow]].
The mid-string wildcard in the allow rule still matches the new path. The old
standalone checkout at D:\projects\privat\mods\coi\ElevationPP is superseded.)

The script wraps MSBuild + deploy; it needs the COI_ROOT/COI_MODS Windows user env
vars, which the user set THEMSELVES on 2026-08-02. Fallback if the script or vars
are missing (self-contained, also preapproved):

```
$env:COI_ROOT = 'E:\SteamLibrary\steamapps\common\Captain of Industry'; $env:COI_MODS = "$env:APPDATA\Captain of Industry\Mods"; & 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe' 'D:\projects\privat\mods\coi\mods\ElevationPP\src\ElevationPP.csproj' -p:Configuration=Release -v:minimal -nologo 2>&1 | Select-Object -Last 6
```

NEVER set environment variables for the user via setx or registry edits — the user
had their PATH destroyed by an automated setx once and explicitly wants to manage
env vars themselves (2026-08-02).

**Why:** dotnet build fails on this project (CodeTaskFactory in the manifest version generator); only full MSBuild works. The csproj's DeployToModsFolder target auto-copies the dll/manifest/changelog/thumbnail into `%APPDATA%\Captain of Industry\Mods\ElevationPP` and zips `ElevationPP_<version>.zip`.

**How to apply:** reuse it verbatim every build; don't switch the cwd/csproj path or MSBuild invocation. Note the project root is `D:\projects\privat\mods\coi\ElevationPP\src` (there is a stray `src\ElevationPP.csproj` glob hit at the repo root — ignore it).

RELEASE ZIPS (since 2026-10-29, commit `b7fc21c`): a plain `build.ps1` builds + deploys but does NOT write `<Mod>_<version>.zip` any more; only `build.ps1 -Release` packages it (csproj Exec Condition PackageRelease=true; same in all mod build scripts incl. the private MissingModsPP). Use `-Release` ONLY for the final release build, after the manifest version bump. **Why:** test builds before a version bump used to silently overwrite the already-released version's zip (ElevationPP 0.9.12, ExcavatorPP 0.1.2, ShippingPP 0.6.5 during the 0.8.8 port); the user called that "less than ideal and shouldn't happen".
