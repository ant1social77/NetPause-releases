# Собирает установщик NetPause: автономная сборка (.NET внутри) + Inno Setup.
# Результат: installer\NetPause-Setup-<версия>.exe
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
[xml]$proj = Get-Content (Join-Path $root 'NetPause.csproj')
$ver = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

$pub = Join-Path $root 'publish'
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
& "C:\Program Files\dotnet\dotnet.exe" publish (Join-Path $root 'NetPause.csproj') -c Release -r win-x64 --self-contained true `
    -p:DebugType=none -o $pub -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'сборка не удалась' }

# в установщик не должно попасть ничего личного
$bad = Get-ChildItem $pub -Recurse -Include 'state.json*', 'secrets.json', 'history.json', 'hosts-today.json', 'tracker-*' -ErrorAction SilentlyContinue
if ($bad) { throw "в сборке личные файлы: $($bad.FullName -join ', ')" }

$iscc = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
& $iscc "/DAppVer=$ver" /Q (Join-Path $root 'setup.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup не собрал установщик' }
Get-Item (Join-Path $root "installer\NetPause-Setup-$ver.exe")
