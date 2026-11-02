@echo off
setlocal
pushd "%~dp0.."
dotnet build ".\src\Chronicler\Chronicler.csproj" --configuration Debug
set "BUILD_RESULT=%ERRORLEVEL%"
popd
echo.
if not "%BUILD_RESULT%"=="0" echo Debug build failed with exit code %BUILD_RESULT%.
pause
exit /b %BUILD_RESULT%
