@echo off
rem Gera o RESET EXPLODE pronto para levar a outro PC (nao precisa instalar o .NET la).
rem Saida: ..\ResetExplode-dist\ResetExplode.exe + LEIA-ME.txt
rem Sempre recompila do zero a partir do codigo atual desta pasta.
rem Para Windows de 32 bits: publicar.bat x86
cd /d "%~dp0"
echo Fechando o ResetExplode se estiver aberto ...
taskkill /f /im ResetExplode.exe >nul 2>&1
echo Gerando ResetExplode.exe a partir do codigo atual ...
rmdir /s /q obj\Release >nul 2>&1
if /i "%~1"=="x86" (set RID=win-x86) else (set RID=win-x64)
dotnet publish -c Release -r %RID% --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o "..\ResetExplode-dist" -nologo -v q
if errorlevel 1 (
    echo.
    echo FALHOU. Veja os erros acima. O .exe antigo NAO foi atualizado.
    pause
    exit /b 1
)
copy /y "LEIA-ME.txt" "..\ResetExplode-dist\LEIA-ME.txt" >nul
echo.
echo Pronto: %~dp0..\ResetExplode-dist
for %%F in ("..\ResetExplode-dist\ResetExplode.exe") do echo Arquivo gerado em %%~tF (confira se e de agora^)
pause
