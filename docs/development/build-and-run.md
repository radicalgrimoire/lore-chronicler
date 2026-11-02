# ビルドと起動

[開発環境](../../README.md#開発環境)を用意し、リポジトリのルートからPowerShellで実行します。

```powershell
dotnet build .\src\Chronicler\Chronicler.csproj
dotnet run --project .\src\Chronicler\Chronicler.csproj
```

Windowsでは、リポジトリ内の `script\build-debug.bat` または `script\build-release.bat` を実行して、それぞれDebug／Release構成をビルドできます。バッチファイルは配置場所を基準にプロジェクトを解決するため、どの作業ディレクトリから実行しても構いません。
