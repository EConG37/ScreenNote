[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ExecutablePath = '',
    [string]$DesktopDirectory = ''
)

$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw '桌面快捷方式仅支持 Windows。' }

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $packagedExecutable = Join-Path $PSScriptRoot 'ScreenNote.exe'
    $workspaceExecutable = Join-Path (Split-Path -Parent $PSScriptRoot) 'output\ScreenNote\ScreenNote.exe'
    $ExecutablePath = if (Test-Path -LiteralPath $packagedExecutable -PathType Leaf) { $packagedExecutable } else { $workspaceExecutable }
}
$resolvedExecutable = [IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $resolvedExecutable -PathType Leaf)) {
    throw ('找不到拾屏程序，请先构建或指定 -ExecutablePath：' + $resolvedExecutable)
}

if ([string]::IsNullOrWhiteSpace($DesktopDirectory)) {
    # SHGetKnownFolderPath follows Windows' actual Desktop folder, including OneDrive redirection.
    if (-not ('ScreenNote.ShortcutSetup.KnownFolders' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace ScreenNote.ShortcutSetup
{
    public static class KnownFolders
    {
        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);

        public static string DesktopDirectory()
        {
            Guid folderId = new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
            IntPtr path = IntPtr.Zero;
            try
            {
                Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out path));
                string result = Marshal.PtrToStringUni(path);
                if (String.IsNullOrEmpty(result)) throw new InvalidOperationException("Windows did not return a Desktop path.");
                return result;
            }
            finally { if (path != IntPtr.Zero) Marshal.FreeCoTaskMem(path); }
        }
    }
}
'@
    }
    $DesktopDirectory = [ScreenNote.ShortcutSetup.KnownFolders]::DesktopDirectory()
}
$resolvedDesktop = [IO.Path]::GetFullPath($DesktopDirectory)
$shortcutPath = Join-Path $resolvedDesktop '拾屏.lnk'
$workingDirectory = Split-Path -Parent $resolvedExecutable
$iconResource = Join-Path $workingDirectory 'assets\app.ico'
$iconLocation = if (Test-Path -LiteralPath $iconResource -PathType Leaf) { $iconResource + ',0' } else { $resolvedExecutable + ',0' }
$shell = $null
$shortcut = $null

try {
    $shell = New-Object -ComObject WScript.Shell
    $alreadyExists = Test-Path -LiteralPath $shortcutPath
    if ($alreadyExists) {
        if (-not (Test-Path -LiteralPath $shortcutPath -PathType Leaf)) { throw ('同名路径不是快捷方式文件，无法覆盖：' + $shortcutPath) }
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $existingTarget = [Environment]::ExpandEnvironmentVariables([string]$shortcut.TargetPath)
        if ([string]::IsNullOrWhiteSpace($existingTarget) -or -not [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($existingTarget), $resolvedExecutable)) {
            throw ('已有「拾屏.lnk」指向其他程序，未覆盖。请改名后重试：' + $shortcutPath)
        }
    }

    if ($PSCmdlet.ShouldProcess($shortcutPath, '创建或更新拾屏桌面快捷方式')) {
        New-Item -ItemType Directory -Path $resolvedDesktop -Force | Out-Null
        if ($null -eq $shortcut) { $shortcut = $shell.CreateShortcut($shortcutPath) }
        $shortcut.TargetPath = $resolvedExecutable
        $shortcut.Arguments = ''
        $shortcut.WorkingDirectory = $workingDirectory
        $shortcut.IconLocation = $iconLocation
        $shortcut.Description = '拾屏 · 快捷截图、标注与 OCR'
        $shortcut.WindowStyle = 1
        $shortcut.Save()
        if (-not (Test-Path -LiteralPath $shortcutPath -PathType Leaf)) { throw '快捷方式未能保存，请检查目标文件夹权限。' }
        [pscustomobject]@{
            ShortcutPath = $shortcutPath
            TargetPath = $resolvedExecutable
            WorkingDirectory = $workingDirectory
            IconLocation = $shortcut.IconLocation
            UpdatedExisting = $alreadyExists
        }
    }
}
finally {
    if ($null -ne $shortcut) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
    if ($null -ne $shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}
