# Chronicler

Chroniclerは、Lore向けのWindowsデスクトップクライアントです。

現在は開発初期段階です。複数のLoreサーバー情報の保存と、公式SDKによるブラウザーログインに対応しています。起動時にサーバーへ接続したり、ブラウザーを開いたりすることはありません。リポジトリの一覧表示やファイル操作はまだできません。

## 開発環境

- Windows x64
- .NET 10 SDK
- Lore C# SDK `LoreVcs` 0.12.0（NuGetから復元）

Lore SDKのネイティブライブラリを復元できるように、プロジェクトのランタイム識別子は `win-x64` に設定しています。初回ビルドにはNuGetへのアクセスが必要です。ローカルの `lore-dotnet` リポジトリはビルドに必要ありません。

## ビルドと起動

リポジトリのルートからPowerShellで実行します。

```powershell
dotnet build .\src\Chronicler\Chronicler.csproj
dotnet run --project .\src\Chronicler\Chronicler.csproj
```

## サーバーの登録とログイン

1. 「新規サーバー」を選び、表示名とサーバーURL（例: `lore://server.example.com:41337`）を入力して保存します。URLにはリポジトリのパスや認証情報を含めません。
2. 保存済みサーバーを選択し、「ログイン」を押します。
3. 既定のブラウザーでログインを完了し、Chroniclerに戻ります。認証コードが表示された場合はブラウザーで確認または入力します。ブラウザーを開けなかった場合は再試行できます。

接続には、そのサーバーおよび認証環境と互換性のあるLore SDKと、信頼されたTLS証明書が必要です。環境により公開証明書をWindowsの信頼ストアに登録し、起動するPowerShellで `SSL_CERT_FILE` を指定する必要があります。証明書の信頼設定は管理者の手順に従い、証明書検証を無効にしないでください。

サーバーの表示名とURLは `%LOCALAPPDATA%\Chronicler\servers.json` に保存されます。認証情報はこのファイルには保存せず、Lore SDKに管理を任せます。サーバー設定の削除はLoreのログアウトや認証情報の削除にはなりません。トークンの期限切れ後は、認証方式によって再度ブラウザーログインが必要です。

## テスト

```powershell
dotnet test .\tests\Chronicler.Tests\Chronicler.Tests.csproj -c Release
```

自動テストは設定ファイル用の一時フォルダーとログインの代替実装を使用します。実際のサーバーやEntra環境へのログインは含みません。

今後の方針は[開発ノート](docs/development-notes.md)を参照してください。
