# Builds the Shipping++ mod and deploys it to the COI Mods folder (via the csproj's
# DeployToModsFolder target). Requires COI_ROOT and COI_MODS environment variables.
# -Release also packages <Mod>_<version>.zip for upload. Plain builds never touch the zips,
# so a test build cannot overwrite the archive of an already released version.
param([switch]$Release)
$package = if ($Release) { 'true' } else { 'false' }
& 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe' `
    "$PSScriptRoot\src\ShippingPP.csproj" -p:Configuration=Release -p:PackageRelease=$package -v:minimal -nologo 2>&1 |
    Select-Object -Last 6
exit $LASTEXITCODE
