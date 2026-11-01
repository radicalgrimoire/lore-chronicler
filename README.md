# Chronicler

Chroniclerは、Lore向けのWindowsデスクトップクライアントです。

現在は開発初期段階です。ブラウザーログイン、リポジトリ一覧の取得、clone、プロジェクトの切り替え、ローカルファイルの表示、ステージ・アンステージ、ローカルコミットとプッシュに対応しています。プロジェクトごとにサーバー接続先を記録し、異なるサーバーのプロジェクトを同じ一覧に表示します。起動時にサーバーへ接続したり、ブラウザーを開いたりすることはありません。

## 開発環境

- Windows x64
- .NET 10 SDK
- Lore C# SDK `LoreVcs` 0.12.0（NuGetから復元）

Lore SDKのネイティブライブラリを復元できるように、プロジェクトのランタイム識別子は `win-x64` に設定しています。初回ビルドにはNuGetへのアクセスが必要です。ローカルの `lore-dotnet` リポジトリはビルドに必要ありません。

## ドキュメント

- [ビルドと起動](docs/development/build-and-run.md)
- [ログイン](docs/guide/login.md)
- [プロジェクトの作成](docs/guide/projects.md)
- [テスト](docs/development/testing.md)
- [開発ノート](docs/development/development-notes.md)
