#Requires -Version 5.1
<#
  OmenSuperHub 前置环境一键安装脚本

  用法：
    1) 将本脚本与 OmenSuperHub.exe 放在同一目录
    2) 双击 install-prerequisites.bat（或右键本文件“使用 PowerShell 运行”）
    3) 脚本先逐项检查，已存在的直接跳过；缺失的再询问你是否安装/下载

  参数：
    -PawnIO      Ask | Yes | No   是否安装 PawnIO 驱动（默认 Ask：交互询问）
    -PresentMon  Ask | Yes | No   是否下载 PresentMon.exe（默认 Ask）
    -Force                         即使已存在 PresentMon.exe 也强制重新下载
    -NonInteractive                不做交互询问（Ask 视为 No，适合自动化）

  示例：
    powershell -ExecutionPolicy Bypass -File install-prerequisites.ps1 -PawnIO Yes -PresentMon No
#>
[CmdletBinding()]
param(
  [ValidateSet('Ask', 'Yes', 'No')][string]$PawnIO = 'Ask',
  [ValidateSet('Ask', 'Yes', 'No')][string]$PresentMon = 'Ask',
  [switch]$Force,
  [switch]$NonInteractive
)

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'   # 关闭进度条，显著加快 Invoke-WebRequest

# 控制台按 UTF-8 输出，避免中文乱码
try { & chcp.com 65001 *> $null } catch { }
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

function Say([string]$Message, [string]$Color) {
  if ([string]::IsNullOrEmpty($Color)) { Write-Host $Message } else { Write-Host $Message -ForegroundColor $Color }
}
function Step([string]$Message) { Say "[*] $Message" 'Cyan' }
function Ok([string]$Message) { Say "[+] $Message" 'Green' }
function Warn([string]$Message) { Say "[!] $Message" 'Yellow' }
function Fail([string]$Message) { Say "[x] $Message" 'Red' }
function Pause-End { if (-not $NonInteractive) { Read-Host "按回车键退出" | Out-Null } }

# 统一的“是否安装”询问；Ask 在非交互模式下视为 No
function Confirm-Install([string]$Name, [string]$Mode) {
  if ($Mode -eq 'Yes') { return $true }
  if ($Mode -eq 'No') { return $false }
  if ($NonInteractive) { return $false }
  return ((Read-Host "  是否现在安装/下载 $Name ？(Y/N)") -match '^(y|Y)')
}

# ---------- 1. 管理员权限（不满足则提权后重启自身） ----------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  $self = if ($PSCommandPath) { $PSCommandPath } else { $MyInvocation.MyCommand.Path }
  Step "当前不是管理员，正在请求提权..."
  try {
    Start-Process -FilePath (Get-Process -Id $PID).Path `
      -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $self + '"')) `
      -Verb RunAs
  } catch {
    Fail "提权失败：$($_.Exception.Message)"
    Pause-End
  }
  exit 1
}

$Root = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$failed = @()

Say ""
Say "==============================================" 'White'
Say " OmenSuperHub 前置环境一键安装" 'White'
Say " 目标目录：$Root" 'White'
Say "==============================================" 'White'
Say ""

# ---------- 2. .NET Framework 4.8（必装，仅检查） ----------
Step "检查 .NET Framework 4.8 ..."
$release = $null
try {
  $release = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction Stop).Release
} catch { }
if ($release -and [int]$release -ge 528040) {
  Ok ".NET Framework 4.8 已安装（Release=$release），跳过"
} else {
  Warn ".NET Framework 4.8 未安装或版本过低，OmenSuperHub 无法运行"
  $failed += '.NET Framework 4.8'
  try { Start-Process "https://dotnet.microsoft.com/download/dotnet-framework/net48" } catch { }
  Warn "已打开下载页面，请安装后重新运行本脚本"
}

# ---------- 3. HP OMEN 官方冲突进程（检查，可选结束） ----------
Step "检查 HP OMEN 官方进程 ..."
$conflicts = @(Get-Process -Name 'OmenCommandCenterBackground', 'OmenCommandCenter' -ErrorAction SilentlyContinue)
if ($conflicts.Count -gt 0) {
  Warn "检测到官方进程运行中（$($conflicts.ProcessName -join ', ')），可能与 OmenSuperHub 冲突"
  if ($NonInteractive) {
    Warn "非交互模式，跳过"
  } else {
    $answer = Read-Host "是否结束这些进程？(Y/N)"
    if ($answer -match '^(y|Y)') {
      $conflicts | Stop-Process -Force -ErrorAction SilentlyContinue
      Ok "已结束冲突进程"
    } else {
      Warn "已跳过，请自行关闭后再使用 OmenSuperHub"
    }
  }
} else {
  Ok "未检测到冲突进程，跳过"
}

# ---------- 4. PawnIO 驱动（可选） ----------
Step "检查 PawnIO 驱动 ..."
$pawnInstalled = $false
& sc.exe query PawnIO *> $null
if ($LASTEXITCODE -eq 0) { $pawnInstalled = $true }
if (-not $pawnInstalled -and (Get-Service -Name 'PawnIO' -ErrorAction SilentlyContinue)) { $pawnInstalled = $true }

if ($pawnInstalled) {
  Ok "PawnIO 驱动已安装，跳过"
} else {
  Warn "未检测到 PawnIO 驱动（CPU 温度等硬件读取需要它）"
  if (Confirm-Install 'PawnIO 驱动' $PawnIO) {
    if (Get-Command winget -ErrorAction SilentlyContinue) {
      & winget install --id namazso.PawnIO -e --accept-source-agreements --accept-package-agreements
      if ($LASTEXITCODE -eq 0) {
        Ok "PawnIO 驱动安装完成"
      } else {
        Warn "winget 安装返回码 $LASTEXITCODE，可手动安装"
        $failed += 'PawnIO 驱动'
        try { Start-Process "https://github.com/namazso/PawnIO/releases" } catch { }
      }
    } else {
      Warn "未找到 winget，改为打开下载页面"
      $failed += 'PawnIO 驱动'
      try { Start-Process "https://github.com/namazso/PawnIO/releases" } catch { }
    }
  } else {
    Warn "已跳过 PawnIO 驱动，稍后可重新运行本脚本安装"
  }
}

# ---------- 5. PresentMon.exe（可选） ----------
Step "检查 PresentMon.exe ..."
$pmPath = Join-Path $Root 'PresentMon.exe'
if ((Test-Path $pmPath) -and -not $Force) {
  Ok "PresentMon.exe 已存在，跳过"
} else {
  if (Confirm-Install 'PresentMon.exe（FPS 精确模式）' $PresentMon) {
    Warn "正在从 GitHub 获取免安装 x64 版本 ..."
    $temp = "$pmPath.tmp"
    try {
      [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

      # 不走 api.github.com：未认证请求限流 60 次/小时，极易返回 403（已禁止）。
      # 改为跟随 releases/latest 跳转拿到版本号，再拼免安装单文件的下载地址。
      $latest = Invoke-WebRequest -Uri 'https://github.com/GameTechDev/PresentMon/releases/latest' -TimeoutSec 30 -UseBasicParsing
      $finalUri = $latest.BaseResponse.ResponseUri.AbsoluteUri
      if ($finalUri -match '/tag/([^/?#]+)') {
        $tag = $Matches[1]
      } else {
        throw "无法解析最新版本号（$finalUri）"
      }
      $fileName = 'PresentMon-' + $tag.TrimStart('v') + '-x64.exe'
      $downloadUrl = "https://github.com/GameTechDev/PresentMon/releases/download/$tag/$fileName"

      Invoke-WebRequest -Uri $downloadUrl -OutFile $temp -TimeoutSec 300 -UseBasicParsing
      if (-not (Test-Path $temp) -or (Get-Item $temp).Length -le 0) { throw '下载文件为空' }
      Move-Item -Path $temp -Destination $pmPath -Force
      Ok "PresentMon 下载完成：$fileName"
    } catch {
      Fail "PresentMon 下载失败：$($_.Exception.Message)"
      $failed += 'PresentMon.exe'
      Write-Host "  可手动下载后放到：$Root" -ForegroundColor Yellow
      try { Start-Process "https://github.com/GameTechDev/PresentMon/releases/latest" } catch { }
    } finally {
      if (Test-Path $temp) { Remove-Item $temp -Force -ErrorAction SilentlyContinue }
    }
  } else {
    Warn "已跳过 PresentMon.exe，FPS 的精确模式将不可用"
  }
}

# ---------- 结果汇总 ----------
Say ""
if ($failed.Count -eq 0) {
  Ok "检查完成，必需与已选资源均已就绪。"
} else {
  Warn "以下项目未完成，请手动处理：$($failed -join '、')"
}
Say ""
Pause-End

if ($failed.Count -eq 0) { exit 0 } else { exit 1 }
