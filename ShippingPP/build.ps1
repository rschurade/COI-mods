# Builds the Shipping++ mod and deploys it to the COI Mods folder (via the csproj's
# DeployToModsFolder target). Requires COI_ROOT and COI_MODS environment variables.
& 'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe' `
    "$PSScriptRoot\src\ShippingPP.csproj" -p:Configuration=Release -v:minimal -nologo 2>&1 |
    Select-Object -Last 6
exit $LASTEXITCODE
