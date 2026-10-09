#Requires -RunAsAdministrator
# Dev install for EPhoneMic.sys. Release needs EV + attestation (see README).
param([string]$Inf = (Join-Path $PSScriptRoot "EPhoneMic.inf"))
Write-Host "testsigning must be ON for unsigned dev driver:"
bcdedit /set testsigning on
Write-Host "Installing $Inf ..."
pnputil /add-driver $Inf /install
Write-Host "Reboot if device not visible. Then Sound settings -> Input -> ePhone Mic."
Write-Host "Feed it from cs app: Engine/EPhoneMic.cs writes 48kHz mono via \\\\.\\EPhoneMic."
