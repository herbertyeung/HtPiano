# Register this build for .htkeys files for the current Windows user only.
[CmdletBinding()]
param([Parameter(Mandatory)][string] $Executable)

$ErrorActionPreference = 'Stop'
$app = Get-Item -LiteralPath $Executable
if ($app.PSIsContainer -or $app.Name -ne 'HtPiano.exe') { throw 'Expected a built HtPiano.exe.' }
$classes = 'HKCU:\Software\Classes'
$id = 'HtPiano.Performance.1'
foreach ($subkey in @($id, "$id\DefaultIcon", "$id\shell\open\command", '.htkeys', '.htkeys\OpenWithProgids')) {
    if (!(Test-Path -LiteralPath "$classes\$subkey")) { $null = New-Item -Path "$classes\$subkey" -Force }
}
Set-Item -LiteralPath "$classes\$id" -Value 'HtPiano performance'
Set-Item -LiteralPath "$classes\$id\DefaultIcon" -Value ('"{0}",0' -f $app.FullName)
Set-Item -LiteralPath "$classes\$id\shell\open\command" -Value ('"{0}" "%1"' -f $app.FullName)
New-ItemProperty -LiteralPath "$classes\.htkeys\OpenWithProgids" -Name $id -PropertyType None -Value ([byte[]]@()) -Force | Out-Null
if (![Microsoft.Win32.Registry]::ClassesRoot.OpenSubKey('.htkeys').GetValue('')) {
    Set-Item -LiteralPath "$classes\.htkeys" -Value $id
}
Write-Output ('Registered .htkeys -> "{0}" "%1"' -f $app.FullName)
