# Русский glossary

The terms EVE Console's Russian text uses, so the same word reads the same on every screen and
matches the game where the game has a word for it. The words come from the Russian EVE client
where it has one ("The client's word."). Corrections from Russian-speaking players are welcome —
this list is where they belong, so every screen picks them up.

## Style

- Russian as the game's Russian client writes it: plain, polite, **«вы» in lower case**.
- **Buttons and menu items** are infinitives: «Сохранить», «Добавить предметы». **Window titles and
  tabs** are nouns: «Добавление предмета», «Настройки». **Labels and column headers** are short
  nouns; abbreviate with a full stop only when a header is too narrow («Кол-во», «Произв.»).
- **Status lines** are nouns with «…»: «Загрузка…», «Проверка…». The AI companion speaks for itself
  in the first person: «Думаю…», «Читаю базу данных…».
- **Tooltips and messages** are whole sentences, ending as the English does.
- Quotation marks «…» (inside them „…“). A dash between words is «—» with spaces. The ellipsis is
  one character, «…». The letter «ё» is written.
- A label ending in `:` keeps `:`. English in capitals is a small heading: normal sentence case.
- **Placeholders stay exactly as written**: `{0}`, `{1:N0}`, `{alarm}`. They may move; none may be
  dropped or added. Russian inflects names, and `{0}` cannot be inflected — so rephrase until the
  name stands in the nominative: «Система: {0}», «персонаж {0}», «в системе {0}» (never a case
  ending glued to a placeholder).
- **Plural families**: One (1, 21, 31…), Few (2–4, 22–24…), Many (0, 5–20, 25–30…, and 11–14),
  Other (fractions: the genitive singular, as Few). Each form keeps `{0}`: «{0} предмет»,
  «{0} предмета», «{0} предметов», «{0} предмета».
- **Units**: ISK, m³ and % as they are. Durations «д», «ч», «мин», «с» without a full stop:
  «2 д 5 ч 30 мин». Large numbers «тыс.», «млн», «млрд». SP is «СП».
- **Dates** put the day first and use the 24-hour clock: `d MMM`, `d MMM yyyy`, `d MMM yyyy, HH:mm`
  (.NET writes the month in the right case by itself). No AM/PM.
- **Unchanged**: EVE Console, EVE, ESI, SDE, SSO, CCP, ISK, m³, ME, TE, zKillboard, Fuzzwork, Slack,
  Discord, GitHub, Tranquility, keyboard keys (Enter, Ctrl+C), file formats (CSV, Markdown), URLs,
  scope names (`esi-fittings.read_fittings.v1`) and anything typed exactly (`yyyy-MM-dd`).

## Terms

| English | Русский | Notes |
|---|---|---|
| capsuleer | капсулёр | The client's word. |
| character | персонаж | The client's word (it also says «пилот»). |
| corporation / corp | корпорация | The client's word; «корп.» only where a header is too narrow. |
| alliance | альянс | The client's word. |
| faction | фракция | The client's word for the list; it calls the empires «державы», but pirate factions are not державы. |
| NPC corporation | NPC-корпорация | |
| agent (NPC) | агент | The client's word. |
| AI agent / companion (this app's) | ИИ-помощник | «помощник» for short; «агент» stays for NPC agents. |
| standings | отношения | The client's word. |
| solar system | звёздная система | «система» for short. The client's word (it also says «планетная система»). |
| constellation | созвездие | The client's word. |
| region | сектор | The client's word. |
| station | станция | The client's word. |
| structure (Upwell) | сооружение | The client's word; the Structure Browser is «Просмотр сооружений». |
| citadel | цитадель | The client's word. |
| engineering complex | промышленный комплекс | The client's word. |
| refinery | перерабатывающий комплекс | The client's word for the Upwell class. |
| security status | статус безопасности | The client's word, for a character and a system; «Безоп.» in narrow headers. |
| high-sec / low-sec / null-sec | хай-сек / лоу-сек / нуль-сек | The client's words. |
| wormhole | червоточина | The client's word; wormhole space is «W-пространство». |
| stargate | звёздные врата | The client's word (plural only: «врата»). |
| jump / jumps | прыжок | 1 прыжок, 2 прыжка, 5 прыжков. The client's word. |
| jump drive / jump range | гипердвигатель / расстояние гиперперехода | The client's words. |
| jump clone | джамп-клон | The client's word. |
| capital ship | корабль большого тоннажа | The client's word; players say «капитал». |
| ship | корабль | The client's word. |
| hull | корпус | Also the hit-point layer (see below). |
| fitting | оснастка (a saved fit); оснащение (fitting a ship) | The client's words («Сохранённые оснастки»). High / mid / low slots: разъёмы большой / средней / малой мощности. |
| module | модуль | The client's word. |
| rig | модификатор | The client's word; rig slots «разъёмы для модификаторов». |
| drone | дрон | The client's word. |
| charges / ammo | заряды / боеприпасы | The client's words. |
| fuel | топливо | The client's word. |
| item | предмет | The client's word. |
| type (of item) | тип | The client's word. |
| group / category | группа / категория | The client's words. |
| market group | группа рынка | |
| assets | имущество | The client's word. |
| hangar | ангар | The client's word. |
| corp hangar | ангар корпорации | The client's word. |
| container | контейнер | The client's word. |
| cargo hold | грузовой отсек | The client's word. |
| volume (of an item, m³) | объём | The client's word. |
| volume (traded) | объём торгов | |
| packaged / assembled | в упаковке / собран | The client's words («Упаковано», «Собран»). |
| blueprint | чертёж | The client's word. |
| BPO / BPC (in a sentence) | оригинал чертежа / копия чертежа | A bare "BPO" / "BPC" label stays as it is. |
| runs (of a job or copy) | прогоны | "10 runs" = 10 прогонов. The client's word. |
| manufacturing | производство | The client's word. |
| reaction | реакция | The client's word. |
| invention | модернизация | The client's word (the Industry window). Players also say «инвент». |
| copying | копирование | The client's word. |
| research | исследование | The client's word. |
| material efficiency / time efficiency | экономия материалов / экономия времени | The client's words. ME / TE stay as abbreviations. |
| industry job | промышленный проект | «проект» for short; «производственный проект» for manufacturing. The client's word. |
| job slot | слот | «производственные слоты», «слоты реакций», «научные слоты». |
| build cost | стоимость производства | |
| reprocessing / refining | переработка | The client's word. |
| compression | сжатие | The client's word. |
| mining | добыча | The client's word (the Mining skill itself is «Бурение»). |
| ore / ice / moon ore | руда / лёд / руда со спутников | The client's words. A moon is «спутник» in the client. |
| moon mining ledger | журнал учёта добычи со спутника | The client's word. |
| planetary industry | планетарная промышленность | The client's word; players say «планетарка». |
| market | рынок | The client's window is «Торговая система»; «рынок» / «рыночный» in text and headers. |
| order (market) | ордер | The client's word. |
| buy order / sell order | ордер на покупку / ордер на продажу | The client's words. |
| order (a customer's, in the Order Tracker) | заказ | |
| trade hub | торговый узел | The client's word. |
| price / cost / profit / margin | цена / стоимость / прибыль / маржа | |
| broker fee / sales tax | гонорар брокера / налог с продаж | The client's words (its wallet journal types). |
| contract | контракт | The client's word. |
| item exchange / courier / auction | обмен предметами / курьерский контракт / аукцион | The client's words. |
| collateral / reward | залог / вознаграждение | The client's words; «награда» is kept for bounties. |
| accept / reject (a contract) | принять / отклонить | The client's words. |
| wallet | кошелёк | The client's word. |
| journal (wallet) | журнал | «журнал кошелька». |
| transaction | сделка | As the client's «рыночная сделка», «налог на сделку». |
| income / expense | доход / расход | The client's words. |
| net worth | капитал | The client's character sheet says «Общая оценка», which reads vague out of context. |
| loyalty points / LP store | наградные баллы (LP) / магазин наград | The client's words; «LP» where short. |
| skill / skill queue / skill points | навык / план освоения навыков / очки навыков (СП) | The client's words; «план навыков» where short. |
| implant | имплантат | The client's word. |
| killmail | отчёт о бое | The client's word (its "Kill Report"). |
| kill / loss | уничтожение / потеря | A kill as a record (a ship destroyed, one killmail): «уничтожение». A character's kills against its losses: «победы / потери», the client's words. |
| victim / attacker / final blow | жертва / нападающий / решающий удар | The client's words. |
| ratting | охота на пиратов | The client's word. |
| mission | задание | The client's word. |
| sovereignty | право владения | The client's word; players say «суверенитет». |
| undock / dock | выход из дока / вход в док | Verbs «выйти из дока» / «войти в док». The client's words. |
| online / offline | онлайн / офлайн | The client's word, as a short state of a character or the server. In a sentence: a character is «в игре», the server «работает / не работает». A module or service: включён / выключен. |
| downtime | перезагрузка сервера | The client's word; players say «даунтайм». |
| notification | уведомление | The client's word. |
| mail / EVE mail | почта / почта EVE | One message is «письмо». The client's word. |
| channel | канал | The client's word. |
| intel (channel) | разведка | «канал разведки»; players say «интел». |
| hauling / haul / trip | перевозка / перевозка / рейс | The client's word («Перевозки»). |
| worklist | список задач | This app's. |
| Indy Park (this app's) | промпарк | This app's: the user's industry sites. |
| inventory level / stock / on hand | уровень запасов / запас / в наличии | |
| store (this app's shop) | магазин | |
| buyer | покупатель | |
| alarm / alert | тревога / оповещение | An alarm is armed («взведена») and goes off («срабатывает»). |
| scheduler / report | планировщик / отчёт | |
| background process | фоновый процесс | |
| polling | опрос | «опрос ESI». |
| token | токен | |
| scope (ESI) | область доступа | The scope's own name stays as it is. |
| settings / refresh / save / cancel | настройки / обновить / сохранить / отменить | The client's words. |
| add / remove / delete / edit | добавить / убрать / удалить / изменить | The client says «Удалить» for remove too, and «Редактировать» for edit: «убрать» where something only leaves a list, «изменить» for button width. |
| export / import / filter / search / clear | экспорт / импорт / фильтр / поиск / очистить | The client's words. |
| apply / OK / close / copy | применить / OK / закрыть / копировать | The client's words. |
| task (a Worklist row) | задача | Distinct from job (проект). |
| hull (hit-point layer, beside shield and armour) | корпус | щиты / броня / корпус, as the client shows them. |
| CONCORD | КОНКОРД | The client's word. |
| starbase / control tower | звёздная база (ПОС) / башня управления | The client's words. |
| reinforced / reinforcement | укреплённый режим | "Reinforced until" → «Укреплённый режим до». The client's word. |
| anchoring / unanchoring | постановка на якорь / снятие с якоря | The client's words. |
| high power / low power (Upwell) | высокая мощность / малая мощность | «в режиме высокой / малой мощности». The client's words. |
| moon extraction / fracture / moon drill | извлечение породы / раскол / буровой модуль | The client's words. |
| war: aggressor / defender / ally | нападающая сторона / защищающаяся сторона / союзник | mutual war «взаимная война»; war eligible «может участвовать в войнах». The client's words. |
| kill right | разрешение на ликвидацию | The client's word. |
| bounty / insurance payout | награда за голову / страховая выплата | The client's words. |
| bill / broker fee / office rental | счёт / гонорар брокера / аренда офиса | The client's words. |
| price types Buy / Sell / Split | Покупка / Продажа / Середина | Split is the midpoint of the best buy and sell. |
| master wallet / wallet division | главный счёт / счёт | The client's words. |
| transaction tax | налог на сделку | The journal type; sales tax is «налог с продаж». The client's word. |
| contract statuses | размещён / выполняется / завершён / отклонён / провален / удалён / аннулирован / истёк | outstanding / in progress / finished / rejected / failed / deleted / reversed / expired |
| Active (contract filter) | Действующие | The client's word. Not «Выполняется», which is In Progress in the same list. |
| loan (contract type) | ссуда | The client's word. |
| public / private (availability) | общедоступный / частный | The client's words. |
| escrow (market, contract) | депозит | The client's word («Депозит по контракту»). |
| freelance job / project | внешний заказ | The client's word. |
| medal | медаль | The client's word. |
| Project Discovery | Проект «Дискавери» | The client's word. |
| SKIN / Paragon Hub (cosmetic market) | SKIN / Штаб «Парагона» | The client's words. |
| PLEX / New Eden Store | плекс (PLEX) / Игровой магазин | The client's words. |
| corporation project (ESI) | проект корпорации | The client's word. Always «проект корпорации», never a bare «проект», which is an industry job. This app's standing projects: «постоянные проекты». |
| deliver (an industry job) | доставить | The client's word: «готов к доставке». |
| alt / main (character) | твинк / основной персонаж | Players' words. |
| outbid | перебить (цену) | «его цену перебили». |
| order book | стакан | Traders' word. |
| Asset Safety | система безопасности активов | The client's word. |
| facility (where jobs run) | объект | «промышленные объекты». |
| faction warfare / incursions | межгосударственные войны / вторжения | The client's words. |
| New Eden | Новый Эдем | The client's word. |
| sighting (intel) | разведданные | Counted: «сообщений разведки: {0}». |
| home station | станция прописки | The client's word. |
| attributes / remap / bonus remap | характеристики / перераспределение / доп. сеанс перераспределения | The client's words. |
| titles (corporation) | должности | The client's word. |
| high / mid / low slots | разъёмы большой / средней / малой мощности | The client's words. |
| pod | капсула | The client's word. |
| SP | СП | The client's abbreviation of «очки навыков». |
| push-to-talk | режим рации | |
| webhook / workspace (Slack) | веб-хук / рабочее пространство | |
| backfill | дозагрузка | |
| purge (old data) | очистка; «удалять … старше» | |
| endpoint (ESI) | конечная точка | |
| client (one running copy of this app) / build / release | клиент / сборка / выпуск | |
| background service / systemd unit | фоновая служба / юнит | |
| keyring | связка ключей | |
| Overview (this app's home) | Обзор | This app's screen, not EVE's in-space overview. |
| Item Browser / Structure Browser | Просмотр предметов / Просмотр сооружений | This app's screens; the second is the client's word. "Open in Item Browser" → «Открыть в разделе «Просмотр предметов»». |
| Player Entities / NPC Entities | Справочник игроков / Справочник NPC | This app's screens. "Open in NPC Entities" → «Открыть в справочнике NPC». |
| Price Overrides | Ручные цены | This app's screen. |
| Inventory Levels / Market Levels | Уровни запасов / Запасы на рынке | This app's screens. |
| Order Tracker / Sales Tracker / Sale Posting | Учёт заказов / Учёт продаж / Объявления о продаже | This app's screens. |
| Industry Opportunities / Trade Opportunities | Возможности производства / Торговые возможности | This app's screens. |
| Net Worth / Income & Expense | Капитал / Доходы и расходы | This app's screens. |
| Corp Activity (its Projects tab) | Активность корпорации (Проекты) | This app's screen. |
| Background Processes (its Price History tab) | Фоновые процессы (История цен) | This app's screen. |
| Market Orders (Item Browser tab) | Рыночные ордера | This app's tab, named in Settings. |
