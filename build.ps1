param(
    [ValidateSet('Build', 'Test', 'Publish')]
    [string]$Task = 'Build'
)

# 将构建缓存留在项目内，避免要求修改系统或用户级 SDK 配置。
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $projectRoot 'artifacts\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot 'artifacts\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
Push-Location $projectRoot
try {
    # 构建与测试固定使用 global.json 指定的 .NET 8 SDK。
    switch ($Task) {
        'Build' {
            dotnet build GitVault.sln -c Release --nologo
        }
        'Test' {
            dotnet test GitVault.sln -c Release --logger trx --results-directory artifacts/TestResults
        }
        'Publish' {
            # 每次发布使用全新目录，避免把旧包中的内部文档或其他残留文件打包。
            $publishPath = Join-Path $projectRoot ('artifacts\publish\' + [Guid]::NewGuid().ToString('N') + '\GitVault-win-x64')
            dotnet publish src/GitVault.App/GitVault.App.csproj -c Release -r win-x64 --self-contained true -o $publishPath --nologo
            if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
            Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishPath
            Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $publishPath
            # 随可执行文件保留 MVVM 依赖的许可与第三方声明。
            $toolkitPath = Join-Path $env:NUGET_PACKAGES 'communitytoolkit.mvvm\8.4.0'
            Copy-Item -LiteralPath (Join-Path $toolkitPath 'License.md') -Destination (Join-Path $publishPath 'CommunityToolkit.License.md')
            Copy-Item -LiteralPath (Join-Path $toolkitPath 'ThirdPartyNotices.txt') -Destination (Join-Path $publishPath 'CommunityToolkit.ThirdPartyNotices.txt')
            # 使用发布清单中的实际运行时版本，保留自包含运行时的许可声明。
            $runtimeInfo = Get-Content -LiteralPath (Join-Path $publishPath 'GitVault.runtimeconfig.json') -Raw | ConvertFrom-Json
            foreach ($framework in $runtimeInfo.runtimeOptions.includedFrameworks) {
                $packagePath = Join-Path $env:NUGET_PACKAGES ($framework.name.ToLowerInvariant() + '.runtime.win-x64\' + $framework.version)
                if ($framework.name -eq 'Microsoft.NETCore.App') {
                    Copy-Item -LiteralPath (Join-Path $packagePath 'LICENSE.TXT') -Destination (Join-Path $publishPath 'DotNet.LICENSE.txt')
                    Copy-Item -LiteralPath (Join-Path $packagePath 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $publishPath 'DotNet.THIRD-PARTY-NOTICES.txt')
                }
                elseif ($framework.name -eq 'Microsoft.WindowsDesktop.App') {
                    Copy-Item -LiteralPath (Join-Path $packagePath 'LICENSE') -Destination (Join-Path $publishPath 'WindowsDesktop.LICENSE.txt')
                }
            }
            # 仅压缩当前构建的发布目录，不包含源码、测试数据和本机设置。
            Compress-Archive -Path (Join-Path $publishPath '*') -DestinationPath (Join-Path $projectRoot 'artifacts\GitVault-win-x64.zip') -Force
        }
    }
    if ($LASTEXITCODE -ne 0) { throw "dotnet 执行失败：$LASTEXITCODE" }
}
finally {
    Pop-Location
}
