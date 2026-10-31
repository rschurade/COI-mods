# Working on these mods on another machine

## Development notes

`dev-notes/` holds the working notes Claude Code keeps for this repo (one fact per file,
indexed by `dev-notes/MEMORY.md`): per-mod status and release history, hard-won game
internals, and workflow rules. They were written on the main PC, so absolute paths in them
(`D:\projects\privat\mods\coi\...`, `E:\SteamLibrary\...`) refer to that machine.

To give Claude Code on another machine the same context, copy the files into its memory
folder for the directory you work in:

```
%USERPROFILE%\.claude\projects\<working-dir-slug>\memory\
```

`<working-dir-slug>` is the working directory with `:` and `\` replaced by `-`, e.g.
`C:\dev\coi` becomes `C--dev-coi`. Start Claude Code in that directory once so the folder
exists, then copy `dev-notes/*.md` into its `memory\` subfolder.

When the notes change on one machine, copy them back here and commit, so both machines
stay in sync.

## Claude Code transcript retention

Claude Code deletes conversation transcripts older than 30 days by default. Set this in
`%USERPROFILE%\.claude\settings.json` on every machine to keep them:

```json
"cleanupPeriodDays": 100000
```

## Build prerequisites

- **Captain of Industry** installed (Steam).
- **Visual Studio 2022 Community** with MSBuild: the `build.ps1` scripts call
  `C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe`
  directly. `dotnet build` does not work for these projects (the manifest version
  generator uses `CodeTaskFactory`). Adjust the path in `build.ps1` if VS is installed
  elsewhere.
- Environment variables (set them yourself, e.g. in System Properties):
  - `COI_ROOT` = the game folder, e.g. `C:\Program Files (x86)\Steam\steamapps\common\Captain of Industry`
  - `COI_MODS` = `%APPDATA%\Captain of Industry\Mods`
- **PowerShell 7** (`pwsh`), used by the release-zip step.
- Optional, for reading game code: `dotnet tool install --global ilspycmd`, then e.g.
  `ilspycmd "<COI_ROOT>\Captain of Industry_Data\Managed\Mafi.Core.dll" -o <dir> -p`.
  The decompiled game code is not kept in this repo.

## Building

- `ModName\build.ps1` builds the mod and deploys it into `COI_MODS`.
- `ModName\build.ps1 -Release` additionally writes `COI_MODS\ModName_<version>.zip` for
  upload. Use it only for the final build of a release, after the version bump in
  `src\manifest.json`.
