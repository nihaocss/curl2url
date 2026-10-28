@echo off
chcp 65001 >nul
title curl2url - 剪贴板 URL 提取
node "%~dp0curl2url.js"
echo.
pause
