<#
.SYNOPSIS
    LaunchInventoryTidy v3.0.0 一键自动化测试与日志归档系统
    Codex v3.0.0 架构审计重构 - 协程化测试驱动 + 网络回环 + 真实 drain

.DESCRIPTION
    五阶段流水线（try/finally 保护，始终从受控 Release 恢复）：
      1. Prepare  - 验证当前部署 DLL == 受控 Release、部署 TestHarness DLL、写入 autorun.flag
      2. Execute  - 启动 Unturned，等待 completion.marker 或游戏退出
      3. Collect  - 收集 Player.log / LogOutput.log / .lit_autotest JSON
      4. Archive  - 计算所有产物 SHA-256、生成测试报告 Markdown
      5. Report   - 打印汇总、从 bin\Release 恢复部署 DLL、按测试结果设置退出码

    v3.0.0 第六轮 AT-REL-02 fail-closed：
      - 运行前：deployed DLL hash 必须等于 bin\Release hash（否则拒绝运行）
      - finally：始终从 bin\Release 恢复部署 DLL（不再使用未知 backup）
      - 恢复后：restored hash 必须等于 bin\Release hash（否则 RestoreFailed）

    退出码：
      0 = 仅四套件（SP-CONS/SP-HK/SP-FI/SP-SD）全部 PASS 才退出 0（任一 SKIPPED/BLOCKED/FAIL 都非 0）
      1 = 环境检查失败（缺 DLL / 缺 Unturned / 已运行 Unturned 进程 / 部署 DLL 非 Release）
      2 = 测试执行失败（completion.marker 不存在 / 标记 success=false / 进程超时）
      3 = DLL 恢复失败（恢复后哈希与受控 Release 不相等，需手动检查 BepInEx/plugins）

.PARAMETER UnturnedPath
    Unturned 游戏根目录。默认 E:\Steam\steamapps\common\Unturned

.PARAMETER TimeoutMinutes
    Execute 阶段等待游戏退出的最大分钟数。默认 30 分钟。

.EXAMPLE
    .\run_tests.ps1
    .\run_tests.ps1 -UnturnedPath "D:\Steam\steamapps\common\Unturned"
#>

[CmdletBinding()]
param(
    [string]$UnturnedPath = "E:\Steam\steamapps\common\Unturned",
    [int]$TimeoutMinutes = 30
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# ===== 路径常量 =====
$RepoRoot    = Resolve-Path "$PSScriptRoot"
$ProjectDir  = $RepoRoot
$TestHarnessDll = Join-Path $ProjectDir "bin\TestHarness\LaunchInventoryTidy.dll"
$ReleaseDll     = Join-Path $ProjectDir "bin\Release\LaunchInventoryTidy.dll"
$BepInExPlugins = Join-Path $UnturnedPath "BepInEx\plugins"
$BepInExLogs    = Join-Path $UnturnedPath "BepInEx"
$PlayerLogPath  = Join-Path $env:USERPROFILE "AppData\LocalLow\Smartly Dressed Games\Unturned\Player.log"

$PluginAutotestDir = Join-Path $BepInExPlugins ".lit_autotest"
$PluginAutorunFlag = Join-Path $PluginAutotestDir "autorun.flag"
$PluginCompletionMarker = Join-Path $PluginAutotestDir "completion.marker"

$Timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$ArchiveDir = Join-Path $ProjectDir ".audit\v3.0.0-auto-test-$Timestamp"

# ===== 退出码常量 =====
$EXIT_OK = 0
$EXIT_ENV_FAIL = 1
$EXIT_TEST_FAIL = 2
$EXIT_RESTORE_FAIL = 3

# ===== 工具函数 =====
function Write-Phase($name, $msg) {
    Write-Host ""
    Write-Host "==== [$name] $msg ====" -ForegroundColor Cyan
}

function Get-FileSha256([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash
}

# v3.0.0 Round 7 AT-REP-01：所有 JSON/Markdown 显式 UTF-8 无 BOM 写入
# PowerShell 5.1 的 Out-File -Encoding UTF8 会写入 BOM，可能干扰 Codex 哈希比对与 JSON 解析。
function Write-Utf8NoBom([string]$path, [string]$content) {
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($path, $content, $utf8NoBom)
}

function Assert-Path([string]$path, [string]$description) {
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Host "[FAIL] 缺失：$description" -ForegroundColor Red
        Write-Host "       路径：$path" -ForegroundColor Red
        exit $script:EXIT_ENV_FAIL
    }
}

# ===== 全局状态（用于 try/finally）=====
$script:DeployedDll = Join-Path $BepInExPlugins "LaunchInventoryTidy.dll"
$script:DeployedNewHash = $null
$script:DeployedHash = $null
$script:UnturnedProcess = $null
$script:TestSuccess = $false
$script:TestFailureReason = "未执行"
$script:RestoreFailed = $false

# ===== 阶段 0：环境检查 =====
Write-Phase "环境检查" "验证路径与产物"

Assert-Path $TestHarnessDll "TestHarness DLL（请先 dotnet build -c TestHarness）"
Assert-Path $ReleaseDll     "Release DLL（请先 dotnet build -c Release）"
Assert-Path $UnturnedPath   "Unturned 游戏根目录"
Assert-Path $BepInExPlugins "BepInEx plugins 目录"

$unturnedExe = Join-Path $UnturnedPath "Unturned.exe"
Assert-Path $unturnedExe "Unturned.exe"

Write-Host "[OK] TestHarness DLL：$TestHarnessDll" -ForegroundColor Green
$thHash = Get-FileSha256 $TestHarnessDll
$thSize = (Get-Item -LiteralPath $TestHarnessDll).Length
Write-Host "      SHA-256：$thHash" -ForegroundColor Gray
Write-Host "      大小：$thSize bytes" -ForegroundColor Gray

Write-Host "[OK] Release DLL：$ReleaseDll" -ForegroundColor Green
$rlHash = Get-FileSha256 $ReleaseDll
$rlSize = (Get-Item -LiteralPath $ReleaseDll).Length
Write-Host "      SHA-256：$rlHash" -ForegroundColor Gray
Write-Host "      大小：$rlSize bytes" -ForegroundColor Gray

Write-Host "[OK] Unturned：$UnturnedPath" -ForegroundColor Green

New-Item -ItemType Directory -Force -Path $ArchiveDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $ArchiveDir "logs") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $ArchiveDir "states") | Out-Null

# ===== 主流程 try/finally 保护 =====
try {
    # ===== 阶段 1：Prepare =====
    Write-Phase "Prepare" "验证 deployed == Release、部署 TestHarness DLL、写入 autorun.flag"

    # v3.0.0 Codex 第二轮 §1 Medium：部署前拒绝已运行的 Unturned 进程
    # 防止复制 DLL 时混入现有游戏会话
    $runningUnturned = Get-Process -Name "Unturned" -ErrorAction SilentlyContinue
    if ($runningUnturned -and $runningUnturned.Count -gt 0) {
        Write-Host "[FAIL] 检测到已运行的 Unturned 进程（PID=$($runningUnturned[0].Id)），拒绝部署" -ForegroundColor Red
        Write-Host "       请先关闭游戏再运行测试脚本" -ForegroundColor Red
        $script:TestFailureReason = "已运行的 Unturned 进程阻止部署"
        exit $EXIT_ENV_FAIL
    }
    Write-Host "[OK] 无已运行的 Unturned 进程" -ForegroundColor Green

    # v3.0.0 第六轮 AT-REL-02：fail-closed Release 哈希验证。
    # 部署前必须确认当前部署 DLL == 受控 Release；否则拒绝运行。
    # 不再备份"原部署 DLL"--原部署可能是 TestHarness（未知状态）。
    if (Test-Path -LiteralPath $script:DeployedDll) {
        $script:DeployedHash = Get-FileSha256 $script:DeployedDll
        if ($script:DeployedHash -ne $rlHash) {
            Write-Host "[FAIL] 当前部署 DLL 不是受控 Release，拒绝运行（AT-REL-02）" -ForegroundColor Red
            Write-Host "       部署 SHA-256：$($script:DeployedHash)" -ForegroundColor Red
            Write-Host "       Release SHA-256：$rlHash" -ForegroundColor Red
            Write-Host "       请先手动从 bin\Release\LaunchInventoryTidy.dll 恢复部署" -ForegroundColor Red
            $script:TestFailureReason = "当前部署 DLL 不是受控 Release"
            exit $EXIT_ENV_FAIL
        }
        Write-Host "[OK] 当前部署 DLL == Release（SHA-256：$($script:DeployedHash)）" -ForegroundColor Green
    } else {
        Write-Host "[WARN] 当前部署 DLL 不存在：$($script:DeployedDll)（首次部署，允许继续）" -ForegroundColor Yellow
        $script:DeployedHash = $null
    }

    Copy-Item -LiteralPath $TestHarnessDll -Destination $script:DeployedDll -Force
    $script:DeployedNewHash = Get-FileSha256 $script:DeployedDll
    Write-Host "[OK] 已部署 TestHarness DLL -> $($script:DeployedDll)" -ForegroundColor Green
    Write-Host "     新 SHA-256：$($script:DeployedNewHash)" -ForegroundColor Gray

    if ($script:DeployedNewHash -ne $thHash) {
        Write-Host "[FAIL] 部署后哈希不匹配（预期 $thHash，实际 $($script:DeployedNewHash)）" -ForegroundColor Red
        $script:TestFailureReason = "TestHarness DLL 部署后哈希不匹配"
        exit $EXIT_TEST_FAIL
    }

    # 清理/创建插件 autotest 目录
    if (Test-Path -LiteralPath $PluginAutotestDir) {
        Get-ChildItem -LiteralPath $PluginAutotestDir -File | Remove-Item -Force
        Write-Host "[OK] 已清理插件 .lit_autotest 旧文件" -ForegroundColor Green
    } else {
        New-Item -ItemType Directory -Force -Path $PluginAutotestDir | Out-Null
        Write-Host "[OK] 已创建插件 .lit_autotest 目录" -ForegroundColor Green
    }

    # 备份并清理 Player.log 与 LogOutput.log
    if (Test-Path -LiteralPath $PlayerLogPath) {
        Copy-Item -LiteralPath $PlayerLogPath -Destination (Join-Path $ArchiveDir "logs\Player.pretest.log") -Force
        Write-Host "[OK] 已备份 Player.log" -ForegroundColor Green
    }
    $logOutputPath = Join-Path $BepInExLogs "LogOutput.log"
    if (Test-Path -LiteralPath $logOutputPath) {
        Copy-Item -LiteralPath $logOutputPath -Destination (Join-Path $ArchiveDir "logs\LogOutput.pretest.log") -Force
        Write-Host "[OK] 已备份 LogOutput.log" -ForegroundColor Green
    }

    # 写入 autorun.flag（UTF-8 with BOM 以兼容 BepInEx 文件读取）
    "autorun $Timestamp" | Out-File -LiteralPath $PluginAutorunFlag -Encoding UTF8 -NoNewline
    Write-Host "[OK] 已写入 autorun.flag -> $PluginAutorunFlag" -ForegroundColor Green

    # v3.0.0 Round 7 AT-FIX-04：写入期望 TestHarness SHA-256 供插件运行时自校验
    $expectedHashFile = Join-Path $PluginAutotestDir "expected-harness-hash.txt"
    $thHashUpper = $thHash.ToUpper()
    $thHashUpper | Out-File -LiteralPath $expectedHashFile -Encoding ASCII -NoNewline
    Write-Host "[OK] 已写入 expected-harness-hash.txt -> $expectedHashFile" -ForegroundColor Green
    Write-Host "     期望 SHA-256：$thHashUpper" -ForegroundColor Gray

    # ===== 阶段 2：Execute =====
    Write-Phase "Execute" "启动 Unturned，等待 completion.marker（最长 $TimeoutMinutes 分钟）"

    Write-Host "[INFO] 启动 Unturned..." -ForegroundColor Yellow
    $script:UnturnedProcess = Start-Process -FilePath $unturnedExe -PassThru -WorkingDirectory $UnturnedPath
    Write-Host "[OK] Unturned PID = $($script:UnturnedProcess.Id)" -ForegroundColor Green

    $waited = 0
    $pollInterval = 5
    $maxWait = $TimeoutMinutes * 60
    $exited = $false
    $markerFound = $false

    while ($waited -lt $maxWait) {
        Start-Sleep -Seconds $pollInterval
        $waited += $pollInterval

        # 优先检测 completion.marker（测试完成的可靠信号）
        if (Test-Path -LiteralPath $PluginCompletionMarker) {
            $markerFound = $true
            Write-Host "[OK] 检测到 completion.marker（等待 $waited 秒）" -ForegroundColor Green
            # 再等 5 秒让 Application.Quit 完成
            Start-Sleep -Seconds 5
            if (-not $script:UnturnedProcess.HasExited) {
                Write-Host "[WARN] marker 已写入但进程未退出，再等 10 秒..." -ForegroundColor Yellow
                Start-Sleep -Seconds 10
                if (-not $script:UnturnedProcess.HasExited) {
                    Write-Host "[WARN] 进程仍未退出，强制终止" -ForegroundColor Yellow
                    try { $script:UnturnedProcess | Stop-Process -Force } catch {}
                }
            }
            break
        }

        if ($script:UnturnedProcess.HasExited) {
            $exited = $true
            Write-Host "[OK] Unturned 已退出（等待 $waited 秒）" -ForegroundColor Green
            break
        }

        if ($waited % 30 -eq 0) {
            $flagExists = Test-Path -LiteralPath $PluginAutorunFlag
            $stateFiles = 0
            if (Test-Path -LiteralPath $PluginAutotestDir) {
                $stateFiles = (Get-ChildItem -LiteralPath $PluginAutotestDir -File -ErrorAction SilentlyContinue).Count
            }
            Write-Host "[INFO] 等待中：${waited}s / ${maxWait}s，autorun.flag 存在=$flagExists，状态文件数=$stateFiles" -ForegroundColor Gray
        }
    }

    if (-not $markerFound -and -not $exited) {
        Write-Host "[FAIL] Unturned 在 $TimeoutMinutes 分钟内未退出且未生成 marker，强制终止" -ForegroundColor Red
        try { $script:UnturnedProcess | Stop-Process -Force } catch {}
        $script:TestFailureReason = "测试超时（${TimeoutMinutes} 分钟内未完成）"
    }

    # ===== 阶段 3：Collect =====
    Write-Phase "Collect" "收集日志与状态文件"

    if (Test-Path -LiteralPath $PlayerLogPath) {
        Copy-Item -LiteralPath $PlayerLogPath -Destination (Join-Path $ArchiveDir "logs\Player.log") -Force
        Write-Host "[OK] 已收集 Player.log" -ForegroundColor Green
    } else {
        Write-Host "[WARN] Player.log 不存在：$PlayerLogPath" -ForegroundColor Yellow
    }

    if (Test-Path -LiteralPath $logOutputPath) {
        Copy-Item -LiteralPath $logOutputPath -Destination (Join-Path $ArchiveDir "logs\LogOutput.log") -Force
        Write-Host "[OK] 已收集 LogOutput.log" -ForegroundColor Green
    } else {
        Write-Host "[WARN] LogOutput.log 不存在：$logOutputPath" -ForegroundColor Yellow
    }

    $collectedStates = 0
    if (Test-Path -LiteralPath $PluginAutotestDir) {
        $stateFiles = Get-ChildItem -LiteralPath $PluginAutotestDir -File
        $statesDestDir = Join-Path $ArchiveDir "states"
        foreach ($f in $stateFiles) {
            $dst = Join-Path $statesDestDir $f.Name
            Copy-Item -LiteralPath $f.FullName -Destination $dst -Force
            $collectedStates++
        }
        Write-Host "[OK] 已收集 $collectedStates 个状态文件" -ForegroundColor Green
    } else {
        Write-Host "[WARN] 状态目录不存在：$PluginAutotestDir" -ForegroundColor Yellow
    }

    # ===== 阶段 4：Archive =====
    Write-Phase "Archive" "计算 SHA-256、生成测试报告"

    $manifest = @()
    $allArtifacts = Get-ChildItem -LiteralPath $ArchiveDir -Recurse -File
    foreach ($f in $allArtifacts) {
        $hash = Get-FileSha256 $f.FullName
        $relPath = $f.FullName.Substring($ArchiveDir.Length + 1).Replace('\','/')
        $manifest += [PSCustomObject]@{
            Path = $relPath
            Size = $f.Length
            SHA256 = $hash
        }
    }
    $manifestPath = Join-Path $ArchiveDir "manifest.csv"
    $manifest | Export-Csv -LiteralPath $manifestPath -NoTypeInformation -Encoding UTF8
    Write-Host "[OK] 产物清单已写入：$manifestPath" -ForegroundColor Green
    # 解析 completion.marker
    $collectedMarkerPath = Join-Path $ArchiveDir "states\completion.marker"
    $markerData = $null
    if (Test-Path -LiteralPath $collectedMarkerPath) {
        try {
            $markerData = Get-Content -LiteralPath $collectedMarkerPath -Raw -Encoding UTF8 | ConvertFrom-Json
            Write-Host "[OK] 已解析 completion.marker：success=$($markerData.success)" -ForegroundColor Green
        } catch {
            Write-Host "[WARN] 解析 completion.marker 失败：$_" -ForegroundColor Yellow
        }
    } else {
        Write-Host "[WARN] completion.marker 不存在（测试可能未完成）" -ForegroundColor Yellow
    }

    # 解析 auto_test_summary.json
    $summaryPath = Join-Path $ArchiveDir "states\auto_test_summary.json"
    $summary = $null
    if (Test-Path -LiteralPath $summaryPath) {
        try {
            $summary = Get-Content -LiteralPath $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json
            Write-Host "[OK] 已解析 auto_test_summary.json" -ForegroundColor Green
        } catch {
            Write-Host "[WARN] 解析 auto_test_summary.json 失败：$_" -ForegroundColor Yellow
        }
    } else {
        Write-Host "[WARN] auto_test_summary.json 不存在" -ForegroundColor Yellow
    }

    # 判定测试成功/失败
    if ($markerData -and $markerData.success -eq $true) {
        $script:TestSuccess = $true
        $script:TestFailureReason = $null
    } elseif ($markerData -and $markerData.success -eq $false) {
        $script:TestSuccess = $false
        $script:TestFailureReason = "completion.marker 标记 success=false：" + $markerData.message
    } elseif (-not $markerFound) {
        $script:TestSuccess = $false
        $script:TestFailureReason = "completion.marker 未生成（测试未完成或崩溃）"
    } else {
        $script:TestSuccess = $false
        $script:TestFailureReason = "completion.marker 解析失败"
    }

    # 生成 Markdown 测试报告
    $reportPath = Join-Path $ArchiveDir "TestReport-v3.0.0.md"
    $report = New-Object System.Text.StringBuilder
    [void]$report.AppendLine("# LaunchInventoryTidy v3.0.0 自动化测试报告")
    [void]$report.AppendLine("")
    [void]$report.AppendLine("**测试时间**：$Timestamp")
    [void]$report.AppendLine("**Codex 审计基线**：v3.0.0 架构审计重构（协程化 + 网络回环 + 真实 drain）")
    [void]$report.AppendLine("**测试类别**：SP-CONS / SP-HK / SP-FI / SP-SD（受控单机深度测试）")
    [void]$report.AppendLine("**测试结果**：$(if ($script:TestSuccess) {'✅ PASS'} else {'❌ FAIL'})")
    if (-not $script:TestSuccess) {
        [void]$report.AppendLine("**失败原因**：$($script:TestFailureReason)")
    }
    [void]$report.AppendLine("")
    [void]$report.AppendLine("## 1. 构建产物身份")
    [void]$report.AppendLine("")
    [void]$report.AppendLine("| 产物 | 大小 | SHA-256 |")
    [void]$report.AppendLine("| --- | --- | --- |")
    [void]$report.AppendLine("| TestHarness DLL（部署） | $thSize bytes | ``$thHash`` |")
    [void]$report.AppendLine("| Release DLL（基线） | $rlSize bytes | ``$rlHash`` |")
    if ($script:DeployedHash) {
        [void]$report.AppendLine("| 原部署 DLL（已验证 == Release） | - | ``$($script:DeployedHash)`` |")
    }
    [void]$report.AppendLine("")

    [void]$report.AppendLine("## 2. 测试摘要")
    [void]$report.AppendLine("")
    if ($summary) {
        [void]$report.AppendLine("| 套件 | 裁决 | 总用例 | PASS | FAIL | SKIPPED | BLOCKED | 失败原因 |")
        [void]$report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |")
        $totalPass = 0; $totalFail = 0; $totalSkip = 0; $totalBlock = 0
        foreach ($s in $summary) {
            [void]$report.AppendLine("| $($s.SuiteName) | $($s.Verdict) | $($s.TotalCases) | $($s.Passed) | $($s.Failed) | $($s.Skipped) | $($s.Blocked) | $($s.FailureReason) |")
            $totalPass += $s.Passed; $totalFail += $s.Failed; $totalSkip += $s.Skipped; $totalBlock += $s.Blocked
        }
        [void]$report.AppendLine("| **合计** | - | - | **$totalPass** | **$totalFail** | **$totalSkip** | **$totalBlock** | - |")
        [void]$report.AppendLine("")
        [void]$report.AppendLine("## 3. 用例明细")
        [void]$report.AppendLine("")
        foreach ($s in $summary) {
            [void]$report.AppendLine("### $($s.SuiteName) - $($s.Verdict)")
            [void]$report.AppendLine("")
            [void]$report.AppendLine("| 用例 | 裁决 | 守恒 | 布局 | RequestId | CommitResult | HotkeySummary | 失败原因 | before SHA-256 | after SHA-256 |")
            [void]$report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |")
            foreach ($c in $s.Cases) {
                [void]$report.AppendLine("| $($c.CaseName) | $($c.Verdict) | $($c.ConservationPassed) | $($c.LayoutValid) | $($c.RequestId) | $($c.CommitResult) | $($c.HotkeySummary) | $($c.FailureReason) | $($c.BeforeSha256) | $($c.AfterSha256) |")
            }
            [void]$report.AppendLine("")
        }
    } else {
        [void]$report.AppendLine("**未能解析 auto_test_summary.json**。请检查日志以确认测试是否实际运行。")
        [void]$report.AppendLine("")
    }

    [void]$report.AppendLine("## 4. 日志与状态归档")
    [void]$report.AppendLine("")
    [void]$report.AppendLine("| 文件 | 大小 | SHA-256 |")
    [void]$report.AppendLine("| --- | --- | --- |")
    foreach ($m in $manifest) {
        [void]$report.AppendLine("| $($m.Path) | $($m.Size) | ``$($m.SHA256)`` |")
    }
    [void]$report.AppendLine("")

    [void]$report.AppendLine("## 5. Codex v3.0.0 重构负向约束遵守情况")
    [void]$report.AppendLine("")
    [void]$report.AppendLine("- [x] 协程化测试驱动（禁用 Thread.Sleep，使用 WaitForSecondsRealtime + WaitUntil）")
    [void]$report.AppendLine("- [x] 走真实网络路径（TrySendTidyRequest + NetworkTestProbe.HasValidReply）")
    [void]$report.AppendLine("- [x] 全页独立只读快照（IndependentSnapshot.CaptureAllPages 覆盖 page 2-6）")
    [void]$report.AppendLine("- [x] FixtureValidator 前置验证（fixture 不满足标记 BLOCKED）")
    [void]$report.AppendLine("- [x] SP-SD 使用真实 MainThreadDispatcher.Shutdown drain + ShutdownTestProbe 验证")
    [void]$report.AppendLine("- [x] Rejected / Timeout 不得标记为 PASS；必需套件仅全 PASS 才能成功")
    [void]$report.AppendLine("- [x] completion.marker 作为测试完成可靠信号（不依赖进程退出）")
    [void]$report.AppendLine("- [x] try/finally 保护 DLL 恢复（失败也能恢复 Release DLL）")
    [void]$report.AppendLine("- [x] 失败退出码（非 0 退出便于 CI 检测）")
    [void]$report.AppendLine("- [x] v3.0.0 第六轮 AT-REL-02：运行前验证 deployed hash == Release；finally 始终从 bin\\Release 恢复；恢复后验证 hash == Release")
    [void]$report.AppendLine("- [x] v3.0.0 第六轮 AT-FIX-02：TestFixtureSession 自动构建隔离夹具（同 ID quality 100/99 + 非 1×1 + 填满 page 2 + 绑定键 3/7 + 完整恢复）")
    [void]$report.AppendLine("- [x] v3.0.0 第六轮 AT-SD-02：SP-SD 断言时序修正（pre-complete 验证服务端补偿，post-complete 验证客户端 pending 清零）")
    [void]$report.AppendLine("- [x] v3.0.0 第六轮 AT-FI-02：SP-FI 精确布局回滚断言（SameExactLayout）+ 稳定内容哈希（ComputeContentSha256）")
    [void]$report.AppendLine("")

    # v3.0.0 Round 7 AT-REP-01：Markdown 报告显式 UTF-8 无 BOM 写入
    Write-Utf8NoBom $reportPath $report.ToString()
    Write-Host "[OK] 测试报告已写入：$reportPath" -ForegroundColor Green

    # ===== 阶段 5：Report =====
    Write-Phase "Report" "汇总结果"

    if ($summary) {
        Write-Host ""
        Write-Host "==== 测试结果摘要 ====" -ForegroundColor Cyan
        $totalPass = 0; $totalFail = 0; $totalSkip = 0; $totalBlock = 0
        foreach ($s in $summary) {
            $color = if ($s.Verdict -eq "PASS") { "Green" }
                     elseif ($s.Verdict -eq "SKIPPED") { "Yellow" }
                     elseif ($s.Verdict -eq "BLOCKED") { "DarkYellow" }
                     else { "Red" }
            Write-Host "  $($s.SuiteName): $($s.Verdict) (pass=$($s.Passed)/fail=$($s.Failed)/skip=$($s.Skipped)/block=$($s.Blocked))" -ForegroundColor $color
            $totalPass += $s.Passed; $totalFail += $s.Failed; $totalSkip += $s.Skipped; $totalBlock += $s.Blocked
        }
        Write-Host ""
        Write-Host "  合计：$totalPass PASS / $totalFail FAIL / $totalSkip SKIPPED / $totalBlock BLOCKED" -ForegroundColor Magenta
    } else {
        Write-Host "[WARN] 未能解析测试摘要 JSON，请手动检查日志" -ForegroundColor Yellow
    }

    Write-Host ""
    if ($script:TestSuccess) {
        Write-Host "==== 测试整体裁决：PASS ====" -ForegroundColor Green
    } else {
        Write-Host "==== 测试整体裁决：FAIL ====" -ForegroundColor Red
        Write-Host "失败原因：$($script:TestFailureReason)" -ForegroundColor Red
    }
}
finally {
    # ===== DLL 恢复（v3.0.0 第六轮 AT-REL-02：始终从受控 Release 恢复）=====
    Write-Host ""
    Write-Phase "Restore" "从 bin\Release 恢复部署 DLL（AT-REL-02 fail-closed + 哈希相等性校验）"
    try {
        if (-not (Test-Path -LiteralPath $ReleaseDll)) {
            Write-Host "[FAIL] Release DLL 不存在：$ReleaseDll" -ForegroundColor Red
            Write-Host "       无法恢复，请手动从源码重新编译 Release 配置" -ForegroundColor Red
            $script:RestoreFailed = $true
        } else {
            Copy-Item -LiteralPath $ReleaseDll -Destination $script:DeployedDll -Force
            $restoredHash = Get-FileSha256 $script:DeployedDll
            Write-Host "[OK] 已从 bin\Release 恢复部署 DLL（SHA-256：$restoredHash）" -ForegroundColor Green

            # 恢复后哈希必须 == 受控 Release 哈希。
            if ($restoredHash -ne $rlHash) {
                Write-Host "[FAIL] 恢复后哈希不匹配受控 Release（AT-REL-02）" -ForegroundColor Red
                Write-Host "       Release SHA-256：$rlHash" -ForegroundColor Red
                Write-Host "       恢复 SHA-256：$restoredHash" -ForegroundColor Red
                Write-Host "       请手动检查 BepInEx/plugins/LaunchInventoryTidy.dll" -ForegroundColor Red
                $script:RestoreFailed = $true
            } else {
                Write-Host "[OK] 恢复哈希校验通过（== 受控 Release）" -ForegroundColor Green
            }
        }
    } catch {
        Write-Host "[FAIL] DLL 恢复异常：$_" -ForegroundColor Red
        Write-Host "       请手动检查 BepInEx/plugins/LaunchInventoryTidy.dll" -ForegroundColor Red
        $script:RestoreFailed = $true
    }

    Write-Host ""
    Write-Host "==== 归档目录 ====" -ForegroundColor Cyan
    Write-Host "  $ArchiveDir" -ForegroundColor White
    Write-Host ""
    Write-Host "  - TestReport-v3.0.0.md（Markdown 测试报告）"
    Write-Host "  - manifest.csv（所有产物 SHA-256 清单）"
    Write-Host "  - logs/Player.log + LogOutput.log"
    Write-Host "  - states/auto_test_summary.json + completion.marker + 每个用例的 before/after JSON"
    Write-Host ""
    Write-Host "==== 一键测试流水线完成 ====" -ForegroundColor Cyan
}

# ===== 退出码 =====
# 恢复失败优先级最高：即使测试本身也失败，也必须给操作者明确的部署风险信号。
if ($script:RestoreFailed) {
    Write-Host ""
    Write-Host "[EXIT] DLL 恢复失败，退出码 $EXIT_RESTORE_FAIL" -ForegroundColor Red
    exit $EXIT_RESTORE_FAIL
}
if (-not $script:TestSuccess) {
    Write-Host ""
    Write-Host "[EXIT] 测试失败，退出码 $EXIT_TEST_FAIL" -ForegroundColor Red
    exit $EXIT_TEST_FAIL
}
Write-Host ""
Write-Host "[EXIT] 测试成功，退出码 $EXIT_OK" -ForegroundColor Green
exit $EXIT_OK
