#Requires -RunAsAdministrator
# Registers ePhone Camera. Needs Release\x64\EPhoneCam.dll built first.
$dll = Join-Path $PSScriptRoot "Release\x64\EPhoneCam.dll"
if (!(Test-Path $dll)) { throw "Build first: msbuild EPhoneCam.vcxproj /p:Configuration=Release /p:Platform=x64. Missing $dll" }
$dllPath = (Resolve-Path $dll).Path
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class CamReg {
  [DllImport("$([System.IO.Path]::GetFileNameWithoutExtension($dllPath))", CallingConvention=CallingConvention.StdCall)]
  public static extern int EPhoneCam_Register();
}
"@ -ErrorAction Stop
# NOTE: DLL must be on PATH or fully qualified LoadLibrary first:
Add-Type -Namespace Win32 -Name K32 -MemberDefinition '[System.Runtime.InteropServices.DllImport("kernel32")] public static extern IntPtr LoadLibrary(string p);'
[Win32.K32]::LoadLibrary($dllPath) | Out-Null
$hr = [CamReg]::EPhoneCam_Register()
if ($hr -ne 0) { throw ("Register failed hr=0x{0:X8}" -f $hr) }
Write-Host "ePhone Camera registered. Check OBS/Zoom device list."
