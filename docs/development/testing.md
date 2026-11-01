# テスト

リポジトリのルートからPowerShellで実行します。

```powershell
dotnet test .\tests\Chronicler.Tests\Chronicler.Tests.csproj -c Release
```

自動テストは一時フォルダーとログイン・リポジトリ操作の代替実装を使用し、SDKによるオフラインのリビジョン同期も検証します。実際のサーバーやEntra環境へのログイン、実際のcloneやサーバーからの取得は含みません。
