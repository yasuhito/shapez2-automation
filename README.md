# shapez2-automation

shapez 2をローカルJSONで操作する専用テストMODとPythonクライアントです。
ゲーム自身の状態、配置、シミュレーション、納品を利用します。

対応する実機検証環境は **1.2.0-rc3 / Steam build 24668948 / Windows Unity + Proton** です。
起動時に対応ゲームDLLのハッシュを照合し、異なるビルドでは動作を拒否します。
他バージョン・Windowsネイティブ・macOSでの動作は未検証です。

## できること

- メニュー状態、ゲームモード一覧、モード選択、固定シードの専用新規ゲーム開始、メニュー復帰。
- 新規開始時の「私はスパゲティ工場を作りません」の同意と続行。
- 建物、島、床、資源、接続仕様、保管数の構造化読み取り。
- 停止・再開・配置取消・配置・自身が作った建物と空の島の削除。
- ゲーム内のPNG撮影。カメラAPIは試験実装で、対応環境でも制御オブジェクトの取得に失敗するため利用できません。
- 同じシードと27部品の操作列から、採掘・搬送・ハブへの実納品を確認するレシピ。

初期有効化後の操作はJSONファイルで行います。OSのマウス・キー入力やフォーカス取得を使わず、
`Application.runInBackground`によりゲームが非フォーカスでも処理します。
実機検証の範囲と限界は[検証記録](docs/verification.md)を参照してください。

## 前提とビルド

- 正規にインストールした対応版のshapez 2。
- .NET SDK 9。実機では9.0.120を使用。
- Python 3。クライアントに追加パッケージは不要です。

**ゲームDLL・アセット・セーブ・第三者MODは同梱しません。**
利用者のインストールを参照してビルドしてください。

```sh
export SPZ2_MANAGED="/path/to/Steam/steamapps/common/shapez 2/shapez 2_Data/Managed"
dotnet build bridge --ignore-failed-sources -p:NuGetAudit=false
python3 -m unittest discover -s client -v
```

環境変数の代わりに`-p:GameManaged="/path/to/Managed"`も指定できます。
出力は`artifacts/mod/`です。`Private=false`の参照により、ゲームDLLを出力へコピーしません。
非推奨ゲームAPIのコンパイル警告が残ります。

## 初期有効化

このMODは**実績を無効にします**。専用テスト用に利用し、通常のプレイ用セーブへ使わないでください。

1. ゲームを終了する。実行中のDLLを上書きするとMonoのメタデータが破損することがあります。
2. Unityの`Application.persistentDataPath`を確認する。
   Protonでは通常、Steamの`steamapps/compatdata/2162800/pfx/drive_c/users/steamuser/AppData/LocalLow/tobspr Games/shapez 2`です。
3. その下に新しい`mods/ShapezAutomationLocal`専用フォルダを作る。
   同名フォルダが存在する場合は内容を確認し、他のファイルを上書きしない。
4. 自作の`ShapezAutomation.dll`と`manifest.json`だけを専用フォルダへコピーする。
   Linuxではこのフォルダと`ipc/requests`・`ipc/responses`を所有者だけがアクセスできる権限にする。
5. ゲームを起動し、MODの確認と実績無効化の確認を読んで続行する。
   この初期設定には手動操作が必要です。その後はJSON操作へ移行します。
6. `ipc/startup.json`と`ping`の実応答を確認する。

MODが開始した新規テストセッションだけを変更できます。既存セーブの読み込みや削除は公開しません。
新規ゲーム確認のAPIを使う場合も、表示内容に同意した上で`accept=true`を指定してください。

## 実行

`IPC`を専用フォルダの`ipc`の絶対パスに設定します。

```sh
export IPC="/path/to/persistentDataPath/mods/ShapezAutomationLocal/ipc"
python3 client/bridge_client.py --ipc "$IPC" ping
python3 client/bridge_client.py --ipc "$IPC" modes
python3 client/bridge_client.py --ipc "$IPC" state
python3 client/factory_check.py --ipc "$IPC" --out local-run
```

`factory_check.py`はメニューから通常モード・シード2162800で新規テストを開始し、
確認画面へ同意して進み、27部品を配置して停止解除後の実納品を待ちます。
全コマンドと応答を出力先へ保存します。ゲームルールや納品判定をPythonで再実装しません。

現在の工場が同じレシピの27部品であり、すべてこのMODが所有する場合だけ、
`--cleanup-current`を追加して自身の配置を片付け、同じ実験を繰り返せます。
想定と異なる配置では自動片付けを拒否します。検証後の工場は稼働状態で残ります。

任意の公開コマンドは`call --command-json`で送れます。

```sh
python3 client/bridge_client.py --ipc "$IPC" call --command-json '{"op":"select_mode","mode":"RegularGameMode"}'
python3 client/bridge_client.py --ipc "$IPC" call --command-json '{"op":"start_new","seed":2162800,"test_only":true}'
```

開始応答は遷移の受付です。`global_state`で`InGame`かつ`transition_pending=false`を確認し、
`assignment_confirmation=true`なら現在の`save_id`を取得して次を実行します。

```json
{"op":"accept_assignment","save_id":"CURRENT_SAVE_ID","accept":true}
```

確認画面が閉じるまで`assignment_confirmation=false`を待ちます。
`InGame`だけでは確認画面が閉じたことを意味しません。

## コマンド

| 操作 | コマンドと主要引数 |
|---|---|
| 接続・状態 | `ping`、`global_state`、`state` |
| モード・開始 | `modes`、`select_mode(mode)`、`start_new(seed, test_only=true)` |
| 新規確認 | `accept_assignment(save_id, accept=true)` |
| 構成・床・資源 | `catalog`、`floor`、`resources(radius=0..8)` |
| 停止・取消 | `pause(save_id, paused)`、`cancel(save_id)` |
| 建物配置 | `place(save_id, test_only=true, kind, x, y, z, rotation=0..3)` |
| 島配置 | `place_island(save_id, test_only=true, definition, x, y, z, rotation)` |
| 自身の配置だけ削除 | `delete_owned(save_id, x, y, z)`、`delete_owned_island(...)` |
| メニュー復帰 | `main_menu(save_id)`。所有物の片付けが必要 |
| カメラ | `camera(save_id, x, y, zoom)`。試験実装。現在は制御オブジェクトの取得に失敗し、利用できません |
| 撮影 | `screenshot(save_id, name)`。専用フォルダの`screenshots`へPNGを保存 |

建物の`kind`は`belt`、`miner`、`sender`、`receiver`、`left`、`right`に限定します。
島は`Layout_ShapeMiner`、`Foundation_1x1`、`Layout_SpaceBeltNode`に限定します。
座標・回転・解禁・床・切欠き・採掘資源等を確認しますが、通常の配置処理全体を完全に再現した汎用APIではありません。

## 安全な操作と通信

通信は専用フォルダの番号付きJSONファイルです。ネットワーク待受、認証トークン、OSサービスはありません。
クライアントは同時に1個だけ使います。要求と応答は一意の番号で保存し、既存の応答を上書きしません。
**変更要求がタイムアウトしたら結果不明です。元の応答とstateを確認し、別IDで自動再試行しないでください。**

ゲームの構造変更は`PlayerActionManager.TryScheduleAction`に渡し、
`FinalizeLogicUpdate`後の操作キューで実行します。UnityのUpdateから直接Mapを変更すると
並列シミュレーションと競合するため、その初期実装は検証で不合格になりました。

変更には現在の`save_id`が必要です。異なるセーブ、占有、床外、所有外削除等は拒否します。
削除対象はこのプロセスでMOD自身が作った正確な建物ID・島IDだけです。
所有記録はメモリ上にあるため、配置を片付ける前にゲームやMODを再ロードしないでください。
任意の反射呼び出し・任意パスへの書き込みは公開しません。

## 検証と限界

- 4件のクライアント単体テストは模擬バックエンドで配置復元・占有拒否・変化検出・停止状態復元を検証します。通信と実機動作の証拠とは別です。
- CIはPythonの構文・単体テストとメタデータユーティリティのビルドを確認します。
  正規のゲームDLLが必要なMODビルドと実機テストはCIに含めません。
- 実機では固定シードと27部品の操作列を複数回実行し、各配置段階の建物ハッシュと実納品を確認しました。
- `layout_hash`は建物の座標・種類・回転だけを含みます。島、設定、在庫、乱数、シミュレーション全体のハッシュではありません。
- **完全なシミュレーションの決定性、固定tick送り、完全なsnapshot/restoreは未実装・未証明です。**
- `SimulationTime_G`は診断値であり、値を書き換えることを時間送りとは扱いません。
- 他の操作者が同時にゲームへ入力する場合の再現性は保証しません。

実機の生ログ・セーブ識別子・画像・絶対パス入りの証跡は公開リポジトリに含めません。
公開用に要約した結果だけを`docs/verification.md`へ記載します。
第三者コード・参照については[NOTICE.md](NOTICE.md)を参照してください。

## ライセンス

独自のソースと文書は[MIT License](LICENSE)です。ゲーム本体・ゲームDLL・アセット・セーブ・第三者ソフトウェアは対象外で、それぞれの権利とライセンスに従います。[NOTICE.md](NOTICE.md)も参照してください。
