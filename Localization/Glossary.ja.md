# 日本語 glossary

The terms EVE Console's Japanese text uses, so the same word reads the same on every screen and
matches the game where the game has a word for it. The Japanese words come from the EVE client's
own Japanese text wherever it has one ("The client's word."). Corrections from Japanese-speaking
players are welcome — this list is where they belong, so every screen picks them up.

## Style

- Messages, tooltips and notes: polite です/ます (`保存しました。`, `色を選択します。`). Labels,
  column headers and tab names: nouns (`数量`, `ステータス`). Buttons: a noun or the dictionary form
  of a verb, as the client writes them (`保存`, `追加`, `キャンセル`, `閉じる`, `開く`).
- No full stop after a label, header or button. Sentences end in `。`.
- Full-width punctuation in Japanese text: `、。「」（）！？：`. A label ending in `:` ends in `：`
  (the EVE mail header's From / To / Date labels drop it, to fit their column). An ellipsis stays
  `…`. Quoted words or UI names go in `「」`.
- No space between Japanese and digits, placeholders or Latin words: `30日`, `{0}件`,
  `ESIトークン`, `EVEメール`, `LPストア`. A number keeps its space before a Latin unit, as in English:
  `{0} ISK`, `{0} m³`.
- Counting: `件` for records, orders, results; `個` for items/units; `隻` for ships; `人` for
  characters and people; `回` for runs and repetitions; `日` / `時間` / `分` / `秒` for durations
  (`2日5時間30分`). Plural families have only the Other form, with the number as `{0}`.
  Also `種類` for kinds of item (item types), `社` for corporations, `基` for structures, `か所` for
  places. A count of lines or entries is `件`, not `個`: a contract's item lines are `アイテム{0}件`,
  while `アイテム{0}個` would be units.
- English written in capitals is a small heading; in Japanese it is simply the words.
- **Placeholders stay exactly as written**: `{0}`, `{1:N0}`, `{alarm}`. They may move to where
  Japanese word order puts them (usually earlier in the sentence); none may be dropped or added.
- A date format inside a placeholder is rewritten in Japanese order, keeping the placeholder's
  number: `{2:d MMM HH:mm}` → `{2:M月d日 HH:mm}`, `{0:MMMM yyyy}` → `{0:yyyy年M月}`,
  `{0:ddd d MMM HH:mm}` → `{0:M月d日（ddd） HH:mm}`. A format the user types or reads as a code
  (`yyyy-MM-dd`) stays.
- Where English wraps one sentence around a number box or a pick list, the pieces are reordered so
  the whole line reads as Japanese (`切り替えから最低 [N] 分は元に戻さない`); each piece stays a
  fragment, and a word may move from one piece to the next.
- Text that is spoken aloud (alarms, the assistant's default wording) is written to be heard: short
  sentences, no brackets, `さん` after the person's name.
- **Unchanged**: EVE Console, EVE, ESI, SDE, SSO, CCP, ISK, m³, ME, TE, zKillboard, Fuzzwork, Slack,
  Discord, GitHub, Tranquility, keyboard keys, file formats, URLs and anything that is typed
  exactly (`yyyy-MM-dd`). Also Webhook, the `ly` of light years, and Client ID / Secret Key where
  developers.eveonline.com shows them in English.
- Dates: `yyyy年M月d日`, `M月d日`, times `HH:mm` (24-hour).
- The list separator is `、`.
- Two nouns joined by a slash in a name ("Market / Trade") are joined by `・` (`マーケット・トレード`).
  A path through the app ("Settings → Database") keeps its arrows, in `「」` inside a sentence:
  `「データベース → データベースを縮小」`, with the screens' own Japanese names.
- Station and system names in examples are the client's Japanese names (`ジタ4-4`, `ザ・フォージ`),
  except where the English comment asks for the English name.

## Terms

| English | 日本語 | Notes |
|---|---|---|
| capsuleer | カプセラ | The client's word. |
| character | キャラクター | The client's word. |
| corporation / corp | コーポレーション / コーポ | The client's words. コーポ in compounds and narrow headers (コーポハンガー, as the client). |
| alliance | アライアンス | The client's word. |
| faction | 勢力 | The client's word. |
| NPC corporation | NPCコーポレーション | |
| agent (NPC) | エージェント | The client's word. A locator agent is 位置探知エージェント; the service alone 位置探知. |
| AI agent / companion (this app's) | AIアシスタント | Never エージェント, which is the NPC agent. |
| standings | スタンディング | The client's word. |
| solar system | ソーラーシステム / システム | The client's words. システム alone in headers and where it cannot be misread. |
| constellation | コンステレーション | The client's word. |
| region | リージョン | The client's word. |
| station | ステーション | The client's word. |
| structure (Upwell) | ストラクチャ | The client's word for Upwell structures (ストラクチャブラウザ, アップウェルストラクチャ). The client's 建造物 is the generic "structure" (deployables, buildings) — not this sense. |
| citadel | 城塞 | The client's word. |
| engineering complex | エンジニアリング複合施設 | The client's word. |
| refinery | 精錬所 | The client's word. |
| security status | セキュリティステータス | The client's word. |
| high-sec / low-sec / null-sec | ハイセク / ローセク / ゼロセク | The client's short forms; in full ハイセキュリティ / ローセキュリティ / ゼロセキュリティ. Not ヌルセク. |
| wormhole | ワームホール | The client's word. |
| stargate | スターゲート | The client's word. |
| jump / jumps | ジャンプ | "5 jumps" = 5ジャンプ. The client's word. |
| jump drive / jump range | ジャンプドライブ / ジャンプ距離 | The client's words. |
| jump clone | ジャンプクローン | The client's word. |
| capital ship | 主力艦 | The client's word (キャピタル is only part of item names). |
| ship | 艦船 | The client's word. |
| hull | 船体 | The client's word. |
| fitting | 装備 | The client's word (個人の装備, コーポレーションの装備). A saved fit is also 装備. |
| module | モジュール | The client's word. |
| rig | リグ | The client's word. |
| drone | ドローン | The client's word. |
| charges / ammo | チャージ / 弾薬 | The client's words. |
| fuel | 燃料 | The client's word. |
| item | アイテム | The client's word. |
| type (of item) | タイプ | The client's word. The type of a notification, journal entry, contract or project is タイプ too; 種類 is kept for "kind" (a game-log line's kind, a Worklist task's kind, the database engine) and in labels that already say it (販売の種類, プロジェクトの種類; the client's Contract Type is 契約の種類). |
| group / category | グループ / カテゴリ | The client's words. |
| market group | マーケットグループ | The client's word. |
| assets | 資産 | The client's word. |
| hangar | ハンガー | The client's word. |
| corp hangar | コーポハンガー | The client's word. |
| container | コンテナ | The client's word. |
| cargo hold | カーゴホールド | The client's word. |
| volume (of an item, m³) | 体積 | The client's word. |
| volume (traded) | 取引量 | |
| packaged / assembled | パッケージ済み / 組み立て完了 | The client's words. |
| blueprint | ブループリント | The client's word in the interface (item names use 設計図, from the SDE). |
| BPO / BPC (in a sentence) | オリジナルブループリント / ブループリントコピー | The client's words. A bare "BPO" / "BPC" label stays BPO / BPC. |
| runs (of a job or copy) | 実行回数 | The client's word. "10 runs" = 10回. |
| manufacturing | 製造 | The client's word. |
| reaction | 反応 | The client's word. |
| invention | 発明 | The client's word. |
| copying | コピー | The client's word. |
| research | 研究 | The client's word. |
| material efficiency / time efficiency | 資源効率 / 時間効率 | The client's words. ME / TE stay as abbreviations. |
| industry job | 生産ジョブ | The client's word. A job alone is ジョブ. |
| job slot | ジョブスロット | |
| build cost | 製造コスト | The client's 建築コスト is for building structures. |
| reprocessing / refining | 再処理 / 精錬 | The client's words. The Worklist's "Refine" task is 再処理, the Indy Parks categories say 精錬 — see the last section. |
| compression | 圧縮 | The client's word. |
| mining | 採掘 | The client's word. |
| ore / ice / moon ore | 鉱石 / アイス / 衛星鉱石 | The client's words. |
| moon mining ledger | 衛星採掘台帳 | From the client's 採掘台帳 (mining ledger). |
| planetary industry | 惑星インダストリー | The client's word. PI stays PI where the English says PI. ESI's "Planetary Interaction" is the client's 惑星開発. |
| market | マーケット | The client's word. |
| order (market) | 注文 | The client's word. |
| buy order / sell order | 買い注文 / 売り注文 | The client's words. |
| order (a customer's, in the Order Tracker) | 注文 | |
| trade hub | トレードハブ | The client's word. |
| price / cost / profit / margin | 価格 / コスト / 利益 / 利益率 | 価格 is the client's word; 費用 for fees and expenses. |
| broker fee / sales tax | 仲介手数料 / 物品税 | The client's words (market window). The wallet journal type keeps the client's ブローカー料. |
| contract | 契約 | The client's word. |
| item exchange / courier / auction | アイテム交換 / 輸送 / オークション | The client's words. |
| collateral / reward | 担保 / 報酬 | The client's words. |
| accept / reject (a contract) | 受諾 / 拒否 | The client's words. |
| wallet | ウォレット | The client's word. |
| journal (wallet) | ジャーナル | The client's word. |
| transaction | 取引 | The client's word; the list of them is 取引記録. |
| income / expense | 収入 / 支出 | The client's words. |
| net worth | 純資産 | The client's word. |
| loyalty points / LP store | ロイヤルティポイント / LPストア | The client's words. |
| skill / skill queue / skill points | スキル / スキルキュー / スキルポイント | The client's words. |
| implant | インプラント | The client's word. |
| killmail | キルレポート | The client's word. |
| kill / loss | キル / ロス | |
| victim / attacker / final blow | 犠牲者 / 攻撃者 / 最終攻撃 | The client's words (キルレポート（犠牲者）, 攻撃者, 最終攻撃). |
| ratting | ラッティング | The client's word. |
| mission | ミッション | The client's word. |
| sovereignty | 領有権 | The client's word. |
| undock / dock | 出港 / 入港 | The client's words. Docked = 入港中. |
| online / offline | オンライン / オフライン | The client's words. |
| downtime | ダウンタイム | The client's word. |
| notification | 通知 | The client's word. |
| mail / EVE mail | メール / EVEメール | The client's words. |
| channel | チャンネル | The client's word. |
| intel (channel) | インテル | Players' word; the client has none. |
| hauling / haul / trip | 運搬 / 運搬 / 便 | 運搬 is the client's word. "3 trips" = 3便. |
| worklist | ワークリスト | |
| Indy Park (this app's) | インディパーク | The feature's name, kept short for headers. |
| inventory level / stock / on hand | 在庫レベル / 在庫 / 所持数 | |
| store (this app's shop) | ストア | The client's word. |
| buyer | 買い手 | The client's word. |
| alarm / alert | アラーム / アラート | 警告 is kept for "warning". |
| scheduler / report | スケジューラー / レポート | |
| background process | バックグラウンド処理 | |
| polling | ポーリング | |
| token | トークン | |
| scope (ESI) | スコープ | Not the client's 対象, which is another sense. |
| settings / refresh / save / cancel | 設定 / 更新 / 保存 / キャンセル | The client's words. |
| add / remove / delete / edit | 追加 / 削除 / 削除 / 編集 | The client's words (it uses 削除 for both remove and delete). Where "remove" only takes something off a list, 外す or 除外 may say it better. |
| export / import / filter / search / clear | エクスポート / インポート / フィルター / 検索 / クリア | The client's words. |
| apply / OK / close / copy | 適用 / OK / 閉じる / コピー | The client's words. |
| task (a Worklist row) | タスク | Distinct from job (ジョブ). |
| hull (hit-point layer, beside shield and armour) | 船体 | シールド / アーマー / 船体, as the client shows them. |
| CONCORD | CONCORD | The client's word. |
| starbase / control tower | スターベース / コントロールタワー | The client's words. |
| reinforced / reinforcement | 強化 | The client's word. Reinforced (state) = 強化中; "Reinforced until" = 強化終了 |
| anchoring / unanchoring | 係留 / 係留解除 | The client's words. In progress: 係留中 / 係留解除中; unanchored = 未係留. |
| high power / low power (Upwell) | ハイパワー / ローパワー | The client's words. |
| moon extraction / fracture / moon drill | 衛星抽出 / 粉砕 / 衛星ドリル | The client's words. |
| war: aggressor / defender / ally | 攻撃側 / 防衛側 / 味方 | The client's words. Mutual war 相互布告戦争; war eligible 戦争参加可. |
| kill right | 殺害許可 | The client's word. |
| bounty / insurance payout | 懸賞金 / 保険金 | 懸賞金 is the client's word. |
| bill / broker fee / office rental | 請求書 / 仲介手数料 / オフィスレンタル料 | 請求書 is the client's word. |
| price types Buy / Sell / Split | 買値 / 売値 / 中間値 | Split is the midpoint of the best buy and sell. |
| master wallet / wallet division | マスターウォレット / ウォレット部門 | The client's words. A wallet division by number is 第{0}部門 (the client's 第 2 ウォレット部門); short "Div {0}" 部門{0}. |
| transaction tax | 取引税 | The client's word; the journal type. Sales tax is 物品税. |
| contract statuses | 未締結 / 進行中 / 完了 / 拒否 / 失敗 / 削除済み / 取り消し済み / 期限切れ | outstanding / in progress / finished / rejected / failed / deleted / reversed / expired — the client's words except 取り消し済み (from its 契約の取り消し). Accepted = 請負済み. |
| Active (contract filter) | 有効 | Not 進行中, which is In Progress in the same list. |
| loan (contract type) | ローン | The client's word. |
| public / private (availability) | 公開 / 非公開 | The client's words. |
| escrow (market, contract) | エスクロー | The client's word. |
| freelance job / project | フリーランスジョブ | The client's word. |
| medal | メダル | The client's word in the Medals tab and the journal types (its notices also say 勲章). |
| Project Discovery | プロジェクト・ディスカバリー | The client's word. |
| SKIN / Paragon Hub (cosmetic market) | SKIN / パラゴンハブ | The client's words. |
| PLEX / New Eden Store | PLEX / ニューエデンストア | The client's words. |

## More terms (chosen in wave 1)

| English | 日本語 | Notes |
|---|---|---|
| Overview (this app's home screen) | 概要 | Not オーバービュー, which is the in-space overview. |
| AI agent, short (tab, name, activity) | アシスタント | Short for AIアシスタント: アシスタントの名前, アシスタントの設定. |
| alt (character) | サブキャラクター | |
| price history | 価格推移 | The client's word. |
| materials / minerals | 資源 / 無機物 | The client's words. |
| component / product (of a blueprint) | 部品 / 製品 | 部品 is the client's word. |
| facility (industry) | 施設 | The client's word; 生産施設 for industry facilities. |
| ready (job) / deliver (job) | 準備完了 / 配送 | The client's words; "ready to deliver" = 配送可能. |
| high / medium / low slots | ハイパワースロット / ミディアムスロット / ローパワースロット | The client's words. |
| fighters | 艦載戦闘機 | The client's word. |
| attributes / neural remap / training queue | 属性 / 神経系リマップ / トレーニングキュー | The client's words. |
| title (corp role title) | タイトル | The client's word. |
| pod (capsule, its value) | ポッド | |
| faction warfare / incursion | 国家間戦争 / インカージョン | The client's words (it also says 侵略 for incursions). |
| industry index / system cost index | 産業指数 / システムコスト指数 | The client's words. |
| asset safety | アセットセーフティ | The client's word. |
| corporation projects | コーポレーションプロジェクト | The client's word. Also for "Corp Project(s)" on every screen (Worklist, ESI schedule); never コーポのプロジェクト. |
| standing (buy orders, projects — this app's "always keep") | 常設 | 常設の買い注文, 常設プロジェクト. |
| outbid | 価格で上回られる | |
| backfill (fetch older data) | 過去分の取得 | |
| sighting (intel) | 目撃情報 | |
| Market Orders (tab) | マーケット注文 | The client's word. |
| price source / auth character | 価格ソース / 認証キャラクター | |
| endpoint (ESI) | エンドポイント | |
| asteroid belt / celestials | アステロイドベルト / 天体 | アステロイドベルト is the client's word. The client says セレスチャル for "Celestials" — see the last section. |
| meta group / schematic (PI) / race / certificate | メタグループ / 回路図 / 種族 / 証明書 | The client's words. |
| dogma | ドグマ | The client's word. |
| Top 10 | トップ10 | |
| background service (Windows / systemd) | バックグラウンドサービス | Distinct from バックグラウンド処理 (the work itself). |
| trade / industry opportunities | トレードチャンス / 生産チャンス | |
| Order Tracker / Sales Tracker | 注文管理 / 販売管理 | |
| Item Valuation | アイテム査定 | |
| Market Levels (sell stock kept listed) | 出品レベル | Beside 在庫レベル (Inventory Levels). |

## More terms (chosen in waves 2 and 3)

### Wallet, market and contracts

| English | 日本語 | Notes |
|---|---|---|
| owner | 所有者 | |
| balance (wallet) | 残高 | The Worklist's made-per-used ratio "Balance" is バランス. |
| Market Transactions (tab) | マーケットの取引記録 | The client's word. |
| direction Buy / Sell; B/S | 購入 / 売却; 売買 | The act or a transaction's direction. The price types stay 買値 / 売値. |
| cash flow / net | キャッシュフロー / 純額 | |
| Industry Tax (expense, journal type) | インダストリータックス | The client's word — see the last section. |
| buy escrow / contract collateral / contract value | 買い注文のエスクロー / 契約の担保 / 契約の価値 | |
| time ranges | 過去N日間 / 年初来 / 前年 / 期間を指定 | Last N days / year to date / last year / custom. |
| sort orders | 日付：新しい順 / 金額：高い順 | The pattern for "Date (newest)", "Amount (highest)". |
| issuer / assignee / acceptor | 発行者 / 割り当て先 / 受諾者 | 発行者 and 割り当て先 are the client's words. |
| contractor / Finished (Issuer) / Finished (Contractor) | 契約者 / 完了（発行者） / 完了（契約者） | 契約者 is the client's word. |
| buyout (auction) | 希望落札価格 | The client's word. |
| historical (contracts) | 過去の契約 | |
| From / To (a contract's, a route's) | 出発地 / 目的地 | A date range's From / Thru is 開始 / 終了. |
| wallet journal types | the client's labels | Most exist under different English than ESI's: HyperNet ハイパーノード / ハイパーネットリレー, Paragon Hub SKIN購入 / SKIN販売 / 掲載費 / 販売費 / SKIN取り引き (the client's spelling there). |
| Target / Diff (Levels screens) | 目標 / 差分 | |
| AVAIL (Levels screens) | 出品数 / 在庫 | Market Levels count what is listed (出品数); Inventory Levels what is held (在庫). |
| ST MIN / ST AVG / ST MAX | ST最低 / ST平均 / ST最高 | |
| Default / Scope | 既定 / 対象範囲 | `<Default>` is `<既定>`. |
| Our bid (standing buy order) | 自分の買値 | "Yours: {0}" inside a sentence is 自分の価格：{0}. |

### Industry and the Worklist

| English | 日本語 | Notes |
|---|---|---|
| installer (of a job) | 製造者 | The client's word, used for every activity. |
| job statuses | 進行中 / 一時停止中 / 準備完了 / 配送済み / キャンセル済み / 取消済み | active / paused / ready / delivered / cancelled / reverted — the client's words, with 中 added to its 一時停止 so it reads as a state. |
| job count, any live state | アクティブ{0}件 | Counts running, paused and ready jobs, so not 進行中. |
| success chance / raw materials / intermediates / leftovers | 成功率 / 原料 / 中間製品 / 余剰品 | 原料 is the client's word. |
| shopping list / job cost / facility tax | 買い物リスト / ジョブ費用 / 施設税 | |
| production categories (Indy Parks) | 大型艦 / 中型艦 / 小型艦 / 主力艦 / 主力艦用部品 / 高性能部品 / 化合物反応 / 衛星反応 / 有機化合・気相反応 / ガスの減圧 | |
| formula / datacore / decryptor / decompress | フォーミュラ / データコア / 解読器 / 減圧 | データコア, 解読器 and 減圧 are the client's words. |
| science (slot, job) | 科学 | Used on every screen; the client says サイエンス — see the last section. |
| task states | 実行可能 / ブロック中 / 待機中 / 実行中 | ready / blocked / waiting / running. |
| snooze / manifest / pipeline | スヌーズ / マニフェスト / パイプライン | マニフェスト and パイプライン are the client's words. |
| bottleneck / contention / buffer / cover / surge / verdict / ceiling | ボトルネック / 競合 / バッファー / 在庫日数 / 急増 / 判定 / 上限 | |
| market alt | マーケット用サブキャラクター | |
| Inventory Rules / Sources / stops (of a haul) | 在庫ルール / ソース / 立ち寄り | |
| Final Products chart grain | 日別 / 週別 / 月別 | Grouping on a chart. How often something runs is 毎日 / 毎週 / 毎月. |

### Alarms, Scheduler, notifications and mail

| English | 日本語 | Notes |
|---|---|---|
| check (of an alarm) / fire / armed | チェック項目 / 発動 / 有効 | |
| open alerts / acknowledge | 未処理のアラート / 確認 | |
| WHEN / THEN / stage | 条件 / アクション / ステージ | |
| Continuous / One shot | 継続 / 1回のみ | |
| scheduled alert | 予約アラート | |
| standing project types Deliver Item / Destroy NPC | アイテム配送 / NPC撃墜 | The client's corporation-project types (Deliver Item, Kill NPC). ESI's own types keep their client names (非カプセラの撃破 and the rest). |
| missing / low (standing projects) | 不足 / 残りわずか | 不足しているプロジェクト, 不足・残りわずかのプロジェクト — in the pick list and in the section titles built from it. |
| ship class / freighter / jump freighter / hauler | 艦船クラス / 超大型輸送艦 / ジャンプドライブ搭載型輸送艦 / ハウラー | The client's words. |
| tether / asset safety wrap | テザリング / アセットセーフティラップ | The client's words. |
| mail: sender / recipients / subject / body | 差出人 / 宛先 / 件名 / 本文 | The notifications filter's date "From:" is 開始：. |
| mail folders and states | 受信トレイ / 送信済み / 返信 / 転送 / 既読 / 未読 | A store mail's direction is 送信 / 受信. |
| war HQ / hostile (state) | 戦争HQ / 交戦中 | 戦争HQ is the client's word (it also says 紛争司令部). |
| vulnerability window / entosis | 脆弱性ウィンドウ / エントーシス | The client's words. |
| reagents / moon chunk | 反応試剤 / 岩塊 | The client's words (衛星の岩塊). |
| bills: market fine / alliance maintenance / sovereignty marker | 市場法違反罰則金 / アライアンス維持費 / 領有権マーカー | 市場法違反罰則金 and アライアンス維持費 are the client's words. The journal type for the maintenance fee keeps the client's アライアンスのメンテナンス料. |
| ESI permission "Read X" / "Write X" | Xの読み取り / Xの書き込み | |
| contacts / waypoint / roles (corp) / customs office / jump fatigue | 連絡員 / 経由地点 / 役職 / 税関 / ジャンプ疲労 | The client's words. The AI models' roles are 役割. |
| director / accountant (corp roles) | ディレクター / 会計担当者 | |
| required items / delivered items | 必要なアイテム / 配送されたアイテム | 必要なアイテム is the client's word. |

### Sales, stores and background processes

| English | 日本語 | Notes |
|---|---|---|
| Sale Posting: posting / post block / section / prefix / header / footer | 投稿 / 投稿ブロック / セクション / 接頭辞 / ヘッダー / フッター | |
| static (post type) | 固定テキスト | |
| In Stock / In Build / Reserved | 在庫あり / 製造中 / 予約済み | |
| sale price basis / Contract Cost / price type | 販売価格の基準 / 契約価格 / 価格の種類 | A narrow CONTRACT column over the contract price is 契約価格 as well. |
| order statuses (Order Tracker) | 進行中 / 完了 / キャンセル済み | pending / completed / canceled. The filter's "Active" shows the pending ones, so it is 進行中 too; the Stores tab's counters read the same. |
| delivered (units handed over) | 納品済み | A delivered industry job is 配送済み. |
| Profit % / Margin / not for profit | 利益% / 利益率 / 利益の対象外 | Profit % is over cost, not a margin. 利益% rather than 利益（%）, which does not fit a narrow header. |
| labels (tags) | ラベル | Remove a label = ラベルを外す. |
| store open / open / closed | 開店 / 営業中 / 休業中 | |
| book it / booked / decline / inquiries | 受注 / 受注済み / 拒否 / 問い合わせ | |
| purchase limit / channel ("doorway") / secret / deploy | 購入制限 / 窓口 / シークレット / デプロイ | |
| sweep / idle / queued | 巡回 / アイドル / 待機中 | |
| firehose / live capture / interval poll | ファイアホース / リアルタイム取得 / 定期ポーリング | |
| gap-fill / daily dump / order fulfilment / name cache | 欠落の補完 / 日次ダンプ / 注文の引き当て / 名前キャッシュ | |
| ESI: active orders / mining ledger / current ship | 有効な注文 / 採掘台帳 / 搭乗艦 | 採掘台帳 and 搭乗艦 are the client's words. |
| AI usage: speech out / in, rates, no rate, round trips | 音声出力 / 音声入力, 料金, 料金未設定, 往復 | Input / output 入力 / 出力; the unit of a rate 単位. |
| game log: source / target / weapon / quality / residue / listener | 発生元 / 対象 / 兵器 / 命中の質 / 残留物 / 記録キャラクター | |

### Map, structures and entities

| English | 日本語 | Notes |
|---|---|---|
| waypoints / midpoint / jump through | 経由地点 / 中継地 / 経由地の条件 | 経由地点 is the client's word. |
| Keepstar / Fortizar / supercarrier | キープスター / フォータイザー / 大型艦載機母艦 | The client's words. |
| sovereignty structure / vulnerable / invulnerable | 領有権管理設備 / 攻撃可能 / 攻撃無効 | The client's words. |
| ADM | アクティビティ防衛乗数 | The client's word; ADM stays ADM in narrow places. |
| power / workforce | 電力 / 労働力 | The client's words. |
| faction warfare states | 占領済 / 紛争中 / 紛争なし / 攻撃可能 | The client's words. Owner / occupier 所有者 / 占領者; contested (%) 紛争度; threshold しきい値. |
| incursion states / staging | 定着 / 集結中 / 撤退中 / 中継システム | 中継システム is the client's word (侵略中継システム). Victory points 勝利ポイント. |
| map kills | 艦船キル数 / NPCキル数 / ポッドキル数 | |
| true security | 実際のセキュリティ値 | No client word found. |
| slot bands | ハイ / ミディアム / ロー | The full slot names are in the wave 1 table. |
| unanchored (gone) | 係留解除済み（消滅） | |
| ticker / corp history / member corps / intel reports | 略称 / 雇用履歴 / 所属コーポ / インテル報告 | 略称 and 雇用履歴 are the client's words. |
| Corp Activity: ratting tax / players active / ISK efficiency / donations / donor | ラッティング税 / アクティブなプレイヤー / ISK効率 / 寄付 / 寄付者 | |
| killmail marks | ★ 最終攻撃 / ▲ 最大ダメージ | Final blow / top damage. Slot and hold names are the client's (船舶修理場 for the Ship Hangar, 戦闘機発進用チューブ). |
| Item Browser: appraise / outcome / input materials / required skills | 査定 / 完成物 / 投入資源 / 必須スキル | 完成物, 投入資源 and 必須スキル are the client's words. |
| High / Low (price history) / portion size / flag | 最高値 / 最低値 / バッチサイズ / フラグ | |
| Asset Browser / ISK value (column) | 資産ブラウザ / 金額 | |

## One name for one thing (settled in review)

Where one English word had two Japanese renderings for the same thing, the review picked one; where
the difference is real, the split is recorded here so it stays.

| English | 日本語 | Notes |
|---|---|---|
| location | 場所 / 位置 | 場所 for a place things are at (assets, orders, transactions, a station); 位置 for where a character is (ESI's location, its scopes, the character list). A place whose name is unknown is 場所{0}. |
| Location Name / Location ID | 場所名 / 場所ID | |
| hangar division by number | 部門{0} | The client's 部門 1 (1st Division). A wallet division is 第{0}部門 (see wallet division). |
| locator (agent service) | 位置探知 | The client's word. The wallet journal type keeps the client's エージェントの人物捜索サービス. |
| note | メモ / 備考 | メモ is a note the user writes; 備考 is a remark the app writes (a column explaining a row). |
| Source | 発生元 / 調達元 / 移動元 / 取得元 / 由来 / 検索対象 | Who acted or where it came from (game log, error log) 発生元; where an order's units come from 調達元; a haul's start 移動元; where a fitted module is known from 取得元; where leftovers come from 由来; where an alarm looks 検索対象. |
| Status | ステータス | 状態 only where the English asks to keep it short (a table posted to Slack). |
| Active | 有効 / 進行中 / アクティブ | 有効 for contracts, standing orders and projects in force; 進行中 for a running job and an open customer order; アクティブ for a count of jobs in any live state. |
| Paused | 一時停止中 | As a state, for a skill queue and a job alike (the client's 一時停止, with 中). |
| Cancelled | キャンセル済み / キャンセルしました。 | A state or a count of them キャンセル済み (a bare キャンセル reads as the button); a message saying it happened キャンセルしました。 |
| Completed / Created | 完了 / 完了日 / 完了数; 作成日 / 作成数 | A status, a date (完了日時 with the time), a count. An industry job's "Created" is its start: 開始日時. |
| Delivered | 配送済み / 納品済み / 配送されたアイテム | A job delivered (the client's); units handed to a customer; a notification's list of what was delivered. |
| Daily / Weekly / Monthly | 日別 / 週別 / 月別; 毎日 / 毎週 / 毎月 | Grouping on a chart; how often something runs. |
| update | アップデート / 更新 | The app's own update is アップデート; data and refresh are 更新. Settings' "Update Now" serves both the SDE and the app, so it says 今すぐ更新. |
| Amount | 金額 / 数値 | 金額 for ISK; the game log's amount (damage, units or ISK) is 数値. |
| Fit (a label, a button) / Fit, Not fit (choices) | 装備 / 装備あり, 装備なし | The alarm's label and the Structure Browser's button are 装備; the undock alarm's choices are 装備あり / 装備なし. |

## Short forms for narrow places

Where the app lays a label out narrower than the Japanese, these are the forms that fit, measured in
the app's own fonts. Use them for the same label elsewhere, so it reads the same.

| English | 日本語 | Notes |
|---|---|---|
| Loaded: (above Latest:) | 現在： | Pairs with 最新：. 読み込み済み： does not fit the 70 px label column. |
| From / To / Date (EVE mail header) | 差出人 / 宛先 / 日付 | No colon: the labels share a 36 px column, and 差出人： does not fit. |
| Profit % (column) | 利益% | |
| Took (a call's duration) | 時間 | Beside 時刻 (Time). |
| Fired (how many times) | 発動数 | Beside 最終発動 (Last fired). |
| Final (Inventory Rules tick column) | 最終 | Short for 最終製品, as the English shortens Final Products. |
| (any) (first entry of a filter list) | すべて | The Worklist's filter boxes give their text 34 and 44 px. |

## For a native speaker to check

The open questions from the translators' reports, checked against the current text (what no longer
applied has been dropped), and the short forms this review chose. Grouped by area of the app. Each
line is tagged: **Meaning** — the translator inferred what an ESI name or status means; **Client** —
the draft differs from the client's word, or no client word was found; **Fit** — may be too long for
its space or awkward around the controls; **Style** — wording. Areas and lines run from the most
important down.

### Wallet journal types and finance

- **Meaning** `RefTypeText.FluxTicketSale` / `FluxTicketRepayment` / `FluxTax` / `FluxPayout` "Flux Ticket Sale / Flux Ticket Repayment / Flux Tax / Flux Payout" → ハイパーノード取引 / ハイパーノードの払い戻し / ハイパーネットリレー費用 / ハイパーネットリレーの支払い — "Flux" was taken to be the HyperNet Relay and matched to its labels by meaning and order.
- **Meaning** `RefTypeText.CosmeticMarketSkinSaleBrokerFee` / `CosmeticMarketSkinSaleTax` "Cosmetic Market Skin Sale Broker Fee / … Tax" → パラゴンハブでの掲載費 / パラゴンハブでの販売費 — matched to the client's labels by order and description.
- **Meaning** `RefTypeText.CosmeticMarketComponentItemPurchase` "Cosmetic Market Component Item Purchase" → パラゴンハブでのデザイン要素購入 — assumes a component item is a Paragon Hub design element.
- **Meaning** `RefTypeText.AllignmentBasedGateToll` "Alignment-Based Gate Toll" → 所属勢力に応じたゲート利用料 — inferred from a client message saying the fee depends on faction enlistment.
- **Meaning** `RefTypeText.OperationBonus` / `UnderConstruction` / `MarketProviderTax` / `ItemTraderPayment` / `MilestoneRewardPayment` / `MiningTax` / `GmPlexFeeRefund` / `ProjectPayouts` → 作戦ボーナス / 建設中 / マーケット提供者税 / アイテムトレーダーへの支払い / マイルストーン報酬の支払い / 採掘税 / GM PLEX手数料の払い戻し / プロジェクト報酬の支払い — no client label found; composed from the English.
- **Meaning** `RefTypeText.EssEscrowTransfer` "ESS Transfer" → ESS預託費支払い; `StructureGateJump` → ジャンプブリッジ利用料; `CorporateRewardPayout` "Corp Reward" → コーポレーション報酬の支払い; `AllianceMaintainanceFee` → アライアンスのメンテナンス料 — client labels found under different English; confirm they are the same entries (the bill for the last is アライアンス維持費).
- **Client** `RefTypeText.IndustryJobTax` and `FinanceText.SliceIndustryTax` "Industry Tax" → インダストリータックス — the client's word, but seen once; 生産税 or インダストリー税 may read better.

### Alarms and Scheduler

- **Meaning** `AlarmsText.WhenLabel` "WHEN" → 条件 — one key heads both the alarm's conditions and the Scheduler's schedule; does 条件 say "when" in the Scheduler?
- **Style** `AlarmsText.After` + `SuffixSecondsUndocked` "after [n] seconds undocked" → 経過時間 [n] 秒（出港後） — the order is fixed by tick box, number box and suffix.
- **Style** `AlarmsText.OptionLowerThan` / `OptionNotLowerThan` "Lower than / Not lower than" → 次の値未満 / 次の値以上 — a pick list that sits before a number box.
- **Style** `AlarmsText.IntelSaidReportedAtOther` "{0} hostiles reported in {1}, {2}." → {1}で敵{0}人が報告されました。{2}です。 with `IntelSaidHere` 監視中のシステム内 — spoken aloud; natural when heard?

### Industry and the Worklist

- **Client** Science → 科学 in `CharactersText.ColSci`, `TipScienceSlotsFreeOf`, `SettingsText.Sci`, `TipScienceCopyingResearchInvention` and `WorklistText.Science`, `KindTipScience`, `PoolScience`, `ChipScience`, `ActivityScience`, `SlotsNoCharacterScience`, `SlotsFreeOfTotal`, `WaitingScienceSlotsBusy`, `SummaryAddScienceSlots`, `TipDaysCoveringCopyingAnd` — the client says サイエンス (サイエンスジョブ); change all or none.
- **Client** Refine: `WorklistText.KindRefine`, `SourceRefining` → 再処理, but `IndustryText.CategoryRefineOre` / `CategoryRefineMoonOre` / `CategoryRefineIce` → 通常鉱石の精錬 / 衛星鉱石の精錬 / アイスの精錬 and `AssetsText.FmtWhatOneBatchReturns` 精錬 — one activity in two words; the client calls it 再処理, the glossary allowed both.
- **Client** `IndustryText.Installer` "Installer" → 製造者 — the client's word, but it reads oddly on research, copying and invention jobs.
- **Client** `IndustryText.CategoryBioGasReactions` "Bio and Gas Phase Reactions" → 有機化合・気相反応 — no client term found.
- **Fit** `IndustryText.ActivityReverseEngineering` "Reverse Eng." → リバースエンジニアリング — written out (12 characters) where the English is cut short to fit the column.
- **Fit** Worklist filter boxes: the task-type box gives its text 44 px, the state box 34 px. Cut short: `WorklistText.KindCorpProject` コーポレーションプロジェクト (111 px), `KindAssetSafety` アセットセーフティ (72), `KindSkillQueue` スキルキュー (49), `StateReady` 実行可能 (44), `StateBlocked` ブロック中 (43) — English is cut there too (Asset Safety 67, Blocked 42); a layout matter, listed for the developer.
- **Style** `WorklistText.ColPrio` / `ColFin` / `ColBlkd` / `ColCover` / `ColSeq` / `ColFrees` "prio / fin / blkd / cover / seq / frees" → 優先度 / 最終 / ブロック / 充足 / 順序 / 解放数 — hidden debug columns; lowest priority.

### Across screens

- **Client** Location: 場所 for places (assets, orders, transactions) but 位置 in `CharactersText.ColLocation`, `CommsText.NotifLabelLocation`, `DataText.EndpointLocation`, `OverviewText.ScopeGroupLocation` — the client says 位置 almost everywhere; is the split right?
- **Fit** Shortened to fit their space: `SettingsText.LoadedLabel` 現在：, `CommsText.FromLabel2` / `ToLabel` / `DateLabel` 差出人 / 宛先 / 日付 (no colons), `SalesText.ColProfit2` / `ColProfit4` 利益%, `DataText.ColTook` 時間, `DataText.ColFired` 発動数, `WorklistText.ColFinal` 最終, `WorklistText.FilterAny` すべて — clear enough?

### Settings, the AI assistant and the shell

- **Client** `SettingsText.ImportStageCelestials` "Celestials" → 天体 — the client says セレスチャル.
- **Client** `SettingsText.NotePrivateConversationsAppearNote` keeps 「Private Chat (2)」 — the Japanese client calls these 個人会話チャット（…）; which name do its chat logs use?
- **Client** `SettingsText.SlackNoteToSelf` "Note to Self" → 自分用メモ — should be Slack's own Japanese name for the chat with yourself.
- **Fit** `CommonText.FitBadgePersonal` / `FitBadgeCorp` "P / C" → 個 / 社 — one-character badges on saved fittings; is P / C clearer?
- **Fit** `SettingsText.AtLeast` + `MinutesBetweenOneChangeNote` and `UpFor` + `MinutesWithoutABreakNote` / `MinutesWithoutABreakNote2` → 切り替えから最低 [N] 分は元に戻さない / 途切れずに [N] 分間稼働したら、…に戻す; also `SkillQueueWillBeEmpty` + `Days`, `FilterLowballBuyOrdersBelow` + `OfBuildCost`, `PurchaseAComponentInstead` + `OfBuildValue` — pieces around number boxes, reordered; check on screen.
- **Fit** `SettingsText.MapStatsCoverageRow` → {0,-24} {1,6:N0} 時間枠   {2,4:N0} 日   {3} → {4} — the padding counts characters, so full-width text may not line up.
- **Style** `SettingsText.VoiceTestSentence`, `AgentModelFailoverDefault`, `AgentModelReturnDefault`, `AgentVoiceHandoverDefault`, `AgentVoiceReturnDefault` — spoken defaults, with さん added after {user}; natural when heard?
- **Style** `CharactersText.DurationSeparator` ", " → 、 — Japanese writes 4日2時間3分 with nothing between the parts, but an empty value is refused.
- **Style** `CommonText.DateMonthDayTime12` → M月d日 HH:mm (24-hour, not 12), `DateWeekdayTime` → yyyy年M月d日（ddd） HH:mm, `DateMonthYearShort` → yy年M月.
- **Style** `SettingsText.RemovesAnomaliesCausedByNote` — the percentile explanation (売値＝Nパーセンタイル（安値側）…) is paraphrased.
- **Style** `ShellText.NavPlayerEntities` / `NavNpcEntities` → プレイヤーエンティティ / NPCエンティティ, `NavSalePosting` → 販売の投稿, and `FmtStaticDataExport` keeps "Static Data Export".

### Notifications, mail and the Overview's cards

- **Client** `CommsText.NotifLabelWarHq` "War HQ" → 戦争HQ — the client's word in one message; elsewhere it says 紛争司令部.
- **Style** `CommsText.NotifTitle*` (68 notification type names) → noun phrases (ストラクチャへの攻撃, 宣戦布告) — the client often titles them as sentences.
- **Style** `CommsText.NotifLabel…By` → mostly …者, but `NotifLabelClosedBy` 終了させた人, `NotifLabelCancelledBy` キャンセルした人, `NotifLabelPlacedBy` 懸賞金をかけた人, `NotifLabelRevokedBy` 取り消した人 — one pattern?
- **Style** `CommsText.NotifLabelHostileNow` "Hostile now" → 現在交戦中 — the label of a yes/no field.
- **Style** `OverviewText.BriefCharToCorp` "{0} to {1}" → {0} → {1} (one string for applications and invitations) and `BriefStandingFor` "{0} {1} for {2}" → {0} {1}（{2}） — compact; clear enough?

### Background processes, logs and AI usage

- **Client** `DataText.ColListener` "Listener" → 記録キャラクター — the character whose chat log it is; the client's word is 傍聴者.
- **Fit** `DataText.GameLogFieldSourceAlliance` / `GameLogFieldTargetAlliance` "Source alli / Target alli" → 発生元のアライ / 対象のアライ — shortened for a 16-column pane; is アライ understood?
- **Style** `DataText.ColSource` "Source" → 発生元 — one key heads the Game Log, the Error Log and the contract-items table (公開契約, キャラクターとコーポレーション, …); does 発生元 read right in the last?

### Map, structures and entities

- **Client** `MapText.TipTrueSecurity` / `SecurityTip` "true security" → 実際のセキュリティ値 — no client term found.
- **Fit** `MapText.ColSec` / `AssetsText.ColSec` "Sec" → Sec — kept in Latin letters for 46–70 px columns (the client also writes "sec"); セキュリティ does not fit.
- **Fit** `CorpText.RoleFinalBlow` / `RoleTopDamage` "★ FB / ▲ TD" → ★ 最終攻撃 / ▲ 最大ダメージ — both can show on one attacker line at 9 pt; shorter marks?
- **Style** `MapText.IntelStanding` / `IntelSuperseded` "standing / superseded" → 現行 / 置き換え済み — an intel report still current, or replaced by a later one.
- **Style** `MapText.DetailRegionGateways` "Region gateways" → リージョンの出入口, `NodeStaging` "staging" → 中継, `JumpThroughLabel` "JUMP THROUGH" → 経由地の条件.

### Market, contracts and the Item Browser

- **Fit** `MarketText.Buyout` "Buyout" → 希望落札価格 (the client's word) — may be tight in the 82 px label column of the contract details.
- **Style** `AssetsText.TabRequiredFor` "Required For" → 必要とするもの and `TabDerivedHistory` "Derived History" → 算出値の推移 — tab names; clearer wording?

### Sales and stores

- **Fit** `SalesText.To` / `UnitSOf` / `Per` "to / unit(s) of / per" → 上限 / 個、対象： / 期間： (reads 上限 [n] 個、対象： [アイテムタイプごと] 期間： [m] [日]) and `GiveInStockOrders` + `DaySFromTheOrder` → 在庫ありの注文に予定日を設定する [n] 日後（注文日から） — pieces around fixed controls; check on screen.
- **Style** `SalesText.Reserved` / `ColReserved` "Reserved" → 予約済み — units set aside for orders; 引当済み is the stock-allocation word.
