@echo off
chcp 65001 >nul
title LAN Audio Bridge — 安装 VB-Audio Virtual Cable 驱动

echo ============================================
echo   LAN Audio Bridge — 驱动安装
echo   正在安装 VB-Audio Virtual Cable
echo ============================================
echo.

set "DRIVER=%~dp0..\..\archive\libs\VB-Cable\VBCABLE_Setup_x64.exe"

if not exist "%DRIVER%" (
    echo [错误] 找不到安装包: %DRIVER%
    echo 请从 https://vb-audio.com/Cable/ 手动下载 VBCABLE_Setup_x64.exe
    echo 并存放到 archive\libs\VB-Cable\ 目录
    pause
    exit /b 1
)

echo [1/2] 正在安装 VB-Audio Virtual Cable...
echo 注意：安装过程中会弹出系统对话框，请点击"安装"或"Yes"
start /wait "" "%DRIVER%"

echo [2/2] 验证安装...
timeout /t 2 /nobreak >nul

reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VB-Audio_Virtual_Cable" >nul 2>&1
if %errorlevel% equ 0 (
    echo [成功] VB-Audio Virtual Cable 已安装
    echo 请重启 LAN Audio Bridge 以启用虚拟麦克风模式
) else (
    reg query "HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VB-Audio_Virtual_Cable" >nul 2>&1
    if %errorlevel% equ 0 (
        echo [成功] VB-Audio Virtual Cable 已安装
        echo 请重启 LAN Audio Bridge 以启用虚拟麦克风模式
    ) else (
        echo [警告] 无法确认安装状态，请手动检查系统音频设备中是否有
        echo "CABLE Input" 和 "CABLE Output" 设备
    )
)

echo.
pause
