@echo off
chcp 65001 >nul
node "%~dp0curl2url.js" %*
