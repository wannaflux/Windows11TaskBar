param([switch]$Installer)
$ErrorActionPreference='Stop'
Push-Location $PSScriptRoot
try {
    dotnet publish Win11Taskbar.App/Win11Taskbar.App.csproj -c Release -r win-x64 --self-contained true -o Portable -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    if ($Installer) {
        $compiler=Get-Command ISCC.exe -ErrorAction SilentlyContinue
        $compilerPath=if($compiler){$compiler.Source}else{"${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"}
        if (-not(Test-Path -LiteralPath $compilerPath)) { throw 'Install Inno Setup 6 and add ISCC.exe to PATH.' }
        & $compilerPath Installer/Win11Taskbar.iss
        if($LASTEXITCODE -ne 0){throw 'Installer compilation failed'}
    }
} finally { Pop-Location }
