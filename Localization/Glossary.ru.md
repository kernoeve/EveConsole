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
- **Counts outside a plural family** are written «label: number»: «Проектов в очереди: {0:N0}»,
  «строк: {0:N0}». A unit after a number box is its abbreviation where it has one («д», «ч»,
  «мин», not «дней»), so the text is right for any number the user types.
- **Units**: ISK, m³ and % as they are. Durations «д», «ч», «мин», «с» without a full stop:
  «2 д 5 ч 30 мин». Large numbers «тыс.», «млн», «млрд». SP is «СП».
- **Dates** put the day first and use the 24-hour clock: `d MMM`, `d MMM yyyy`, `d MMM yyyy, HH:mm`
  (.NET writes the month in the right case by itself). No AM/PM.
- **Wallet journal types** (RefTypeText) are the client's own journal names, word for word, even
  where the client words a type differently from the rest of the app («Обработка» for
  manufacturing). Only plain slips in the client's text are mended: «ё» is written, a Latin
  look-alike letter is written in Cyrillic (the client's list starts «Копирование» and «Снятие ареста
  с имущества» with a Latin K and C), and a wrong ending or a typo is fixed («Вознаграждение …
  размещено», «Выплата по …»).
- **EVE Console is feminine**, as «консоль»: «EVE Console была закрыта», «EVE Console должна…».
- **Unchanged**: EVE Console, EVE, ESI, SDE, SSO, CCP, ISK, m³, ME, TE, zKillboard, Fuzzwork, Slack,
  Discord, GitHub, Tranquility, keyboard keys (Enter, Ctrl+C), file formats (CSV, Markdown), URLs,
  scope names (`esi-fittings.read_fittings.v1`) and anything typed exactly (`yyyy-MM-dd`). Also
  Cloudflare's own names (Worker, Client ID, Secret Key, Callback URL) and the store's mail commands
  (PRICES, ORDER, STATUS…), which buyers type.

## Terms

| English | Русский | Notes |
|---|---|---|
| capsuleer | капсулёр | The client's word. |
| character | персонаж | The client's word (it also says «пилот»). |
| corporation / corp | корпорация | The client's word; «корп.» only where a header is too narrow. |
| alliance | альянс | The client's word. |
| faction | фракция | The client's word for the list; it calls the empires «державы», but pirate factions are not державы. |
| NPC corporation | NPC-корпорация | |
| CEO / ticker | президент / короткое название | The client's words; players also say «CEO», «тикер». |
| executor corporation (of an alliance) | руководящая корпорация | The client's word. |
| militia corporation | корпорация ополчения | The client's «ополчение». |
| corporation members | члены корпорации / участники | «Члены корпорации» for the member list (the ESI data), «Участники» for a count or a section heading, as the client. |
| roles (corporation) | полномочия | The client's word. This app's own AI model roles are «роли». |
| home system (a faction's) | столичная система | This app's choice; no client word found. |
| agent (NPC) | агент | The client's word. |
| locator agent / locator services | розыск пилотов | The client's «Услуги розыска пилотов»; a marker beside an agent is «Розыск». The wallet journal type keeps its own name, «Поисковый сервис агента». |
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
| Keepstar, Fortizar, Standup modules | as in English | The client leaves these names in English. |
| service module / service slots | служебный модуль / службы | The client's words («Служебные модули»). |
| security status | статус безопасности | The client's word, for a character and a system, in columns too; «Безоп.» in narrow headers. |
| true security | истинный статус безопасности | |
| high-sec / low-sec / null-sec | хай-сек / лоу-сек / нуль-сек | The client's words. |
| wormhole | червоточина | The client's word; wormhole space is «W-пространство». |
| stargate | звёздные врата | The client's word (plural only: «врата»). |
| celestials | небесные объекты | The client's word. |
| light year | св. г. | The client's abbreviation. |
| jump / jumps | прыжок | 1 прыжок, 2 прыжка, 5 прыжков. The client's word. |
| jump drive / jump range | гипердвигатель / расстояние гиперперехода | The client's words. |
| jump bridge / jump fatigue | гипермост / усталость от гиперпрыжков | The client's words. |
| jump clone | джамп-клон | The client's word. |
| clone bay | медотсек | The client's word. |
| capital ship | корабль большого тоннажа | The client's word; players say «капитал». |
| supercarriers and titans / subcapitals | суперкары и титаны / прочие корабли | The client writes «суперКАРы». |
| ship | корабль | The client's word. |
| shuttle | катер | The client's word. |
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
| assets | имущество | The client's word; the Asset Browser window is «Просмотр имущества». |
| hangar | ангар | The client's word. |
| corp hangar | ангар корпорации | The client's word. |
| division (of a corp hangar) | подразделение | The client's word; «Подразделение {0}» when it has no name. A wallet division is «счёт». |
| container | контейнер | The client's word. |
| stack | стопка | The client's word («Разделить стопку»). |
| cargo hold | грузовой отсек | The client's word. |
| volume (of an item, m³) | объём | The client's word. |
| volume (traded) | объём торгов | |
| packaged / assembled | в упаковке / собран | The client's words («Упаковано», «Собран»). |
| blueprint | чертёж | The client's word. |
| BPO / BPC (in a sentence) | оригинал чертежа / копия чертежа | A bare "BPO" / "BPC" label stays as it is. |
| runs (of a job or copy) | прогоны | "10 runs" = 10 прогонов. The client's word. |
| manufacturing | производство | The client's word. |
| reaction | реакция | The client's word. |
| reaction formula | формула | The client's word. |
| invention | модернизация | The client's word (the Industry window). Players also say «инвент». |
| copying | копирование | The client's word. |
| research | исследование | The client's word. ME / TE research: «исследование ME / TE». |
| material efficiency / time efficiency | экономия материалов / экономия времени | The client's words. ME / TE stay as abbreviations. |
| datacore / decryptor | инфоблок / файл данных | The client's words. |
| intermediate material / raw materials | полуфабрикат / сырьё | The client's word for the first. |
| gas decompression | декомпрессия | The client's word. |
| industry job | промышленный проект | «проект» for short; «производственный проект» for manufacturing. The client's word. |
| activity (of an industry job) | вид работ | In every column, filter and label («Вид работ», «Все виды работ»). The client's Industry window says «Задачи» or «Занятие». |
| product (of a job or blueprint) | продукция | The client's word. |
| job statuses | идёт / приостановлен / готов / доставлен / отменён / свёрнут | active / paused / ready / delivered / cancelled / reverted; «свёрнут» is the client's word. A finished job's time left also reads «Готов». |
| installer (of a job) | оператор | The client's word. |
| job slot | слот | «производственные слоты», «слоты реакций», «научные слоты». |
| cost index | индекс стоимости проектов | The client's word. |
| build cost | стоимость производства | «Стоим. произв.» in a narrow column, «Себест.» (себестоимость) in the narrowest; see Short forms. |
| catch-all facility | объект по умолчанию | This app's. |
| shopping list / leftover | список покупок / остаток | This app's. |
| reprocessing / refining | переработка | The client's word. |
| compression | сжатие | The client's word. |
| mining | добыча | The client's word (the Mining skill itself is «Бурение»). |
| ore / ice / moon ore | руда / лёд / руда со спутников | The client's words. A moon is «спутник» in the client. |
| moon mining ledger | журнал учёта добычи со спутника | The client's word. |
| planetary industry | планетарная промышленность | The client's word; players say «планетарка». |
| market | рынок | The client's window is «Торговая система»; «рынок» / «рыночный» in text and headers. |
| order (market) | ордер | The client's word. |
| buy order / sell order | ордер на покупку / ордер на продажу | The client's words. |
| outstanding orders / contracts | открытые ордера / размещённые контракты | |
| order (a customer's, in the Order Tracker) | заказ | |
| trade hub | торговый узел | The client's word. |
| price / cost / profit / margin | цена / стоимость / прибыль / маржа | |
| price sources: market / build / contract / reprocessed | Рынок / Производство / Контракт / Переработка | As chart lines and as the choices of what a price or a profit is based on. |
| broker fee / sales tax | гонорар брокера / налог с продаж | The client's words (its wallet journal types). |
| contract | контракт | The client's word. |
| item exchange / courier / auction | обмен предметами / курьерский контракт / аукцион | The client's words. |
| issuer / assignee / acceptor (contract) | создатель / получатель / принявший | The client's words for the first two. |
| buyout | цена выкупа | The client's word. |
| collateral / reward | залог / вознаграждение | The client's words; «награда» is kept for bounties. |
| accept / reject (a contract) | принять / отклонить | The client's words. |
| wallet | кошелёк | The client's word. |
| journal (wallet) | журнал | «журнал кошелька». |
| transaction | сделка | As the client's «рыночная сделка», «налог на сделку». |
| income / expense | доход / расход | The client's words. |
| net / cashflow | сальдо / денежный поток | Income & Expense; the Monthly Summary's Net section is «Итог». |
| net worth | капитал | The client's character sheet says «Общая оценка», which reads vague out of context. |
| loyalty points / LP store | наградные баллы (LP) / магазин наград | The client's words (its station service and journal type; its Neocom window is «Наградной отдел»); «LP» where short. |
| required items (an LP offer's) | Требуется | The column header over an LP offer's items, on both LP tabs. The client's LP store column says «Необходимые предметы», which does not fit the Item Browser's column. |
| skill / skill queue / skill points | навык / план освоения навыков / очки навыков (СП) | The client's words; «план навыков» where short. |
| implant | имплантат | The client's word. |
| expert system | экспертная система | The client's word. |
| killmail | отчёт о бое | The client's word (its "Kill Report"). |
| kill / loss | уничтожение / потеря | A kill as a record (a ship destroyed, one killmail): «уничтожение». A character's kills against its losses: «победы / потери», the client's words. |
| victim / attacker / final blow | жертва / нападающий / решающий удар | The client's words. |
| ratting | охота на пиратов | The client's word. |
| industry tax / ratting tax | промышленный налог / налог с охоты на пиратов | The client's word for the first; «Пром. налог», «Налог с охоты» in narrow columns and chart legends. |
| mission | задание | The client's word. |
| sovereignty | право владения | The client's word; players say «суверенитет». |
| ADM | ADM | The client's long form is «коэффициент защиты планетной системы (ADM)». |
| IHub / ESS | ЦУПС / СНС | The client's abbreviations. |
| power / workforce / reagent (sovereignty) | энергия / рабочая сила / реагент | The client's words. |
| vulnerability window | окно уязвимости | The client's word. |
| war HQ / entosis | штаб / энтоз | The client's words. |
| incursion states | мобилизация / подготовка / отступление | mobilizing / established / withdrawing, the client's words (its incursion window); the staging system is «место сбора флота». |
| victory points | очки за победу | The client's word. |
| undock / dock | выход из дока / вход в док | Verbs «выйти из дока» / «войти в док». The client's words. |
| tethered | пришвартован | The client's word. |
| online / offline | онлайн / офлайн | The client's word, as a short state of a character or the server. In a sentence: a character is «в игре», the server «работает / не работает». A module or service: включён / выключен. |
| downtime | перезагрузка сервера | The client's word; players say «даунтайм». |
| notification | уведомление | The client's word. |
| mail / EVE mail | почта / почта EVE | One message is «письмо». The client's word. |
| mail folders | Входящие / Отправлено / Корпоративные / Общеальянсовые | The client's words; Compose is «Новое письмо». A mail's sender and recipient: «От:», «Кому:». |
| corporation application | заявка | «Заявка принята / отклонена / отозвана», the client's words. |
| channel | канал | The client's word. |
| intel (channel) | разведка | «канал разведки»; players say «интел». Intel reports (Corp Activity): «донесения разведки». |
| hauling / haul / trip | перевозка / перевозка / рейс | The client's word («Перевозки»). |
| worklist | список задач | This app's. |
| Indy Park (this app's) | промпарк | This app's: the user's industry sites. |
| inventory level / stock / on hand | уровень запасов / запас / в наличии | |
| store (this app's shop) | магазин | |
| buyer | покупатель | |
| alarm / alert | тревога / оповещение | An alarm is armed («взведена») and goes off («срабатывает»). |
| alarm check / stage / cooldown | проверка / этап / пауза | Repeat choices Continuous / One shot: «Постоянно» / «Однократно». |
| acknowledge / mute / wake-up call | подтвердить / заглушить / будильник | Alarms. |
| placeholders / text-to-speech | подстановки / синтез речи | |
| scheduler / report | планировщик / отчёт | |
| background process | фоновый процесс | |
| polling | опрос | «опрос ESI»; an interval poll is «опрос по интервалу». |
| call / sweep / firehose / gap-fill / idle (background work) | запрос / обход / поток / заполнение пробелов / ожидание | Background Processes; the name cache is «кэш имён», AI rates «тарифы». |
| token | токен | |
| scope (ESI) | область доступа | The scope's own name stays as it is. Scope descriptions start with a verbal noun: «Чтение …», «Изменение …», «Управление …». |
| scope (of a group, rule or limit) | охват | Not an ESI scope. |
| settings / refresh / save / cancel | настройки / обновить / сохранить / отменить | The client's words. |
| add / remove / delete / edit | добавить / убрать / удалить / изменить | The client says «Удалить» for remove too, and «Редактировать» for edit: «убрать» where something only leaves a list, «изменить» for button width. |
| export / import / filter / search / clear | экспорт / импорт / фильтр / поиск / очистить | The client's words. |
| apply / OK / close / copy | применить / OK / закрыть / копировать | The client's words. |
| pager buttons | ⏮ В начало / ◀ Назад / Вперёд ▶ / В конец ⏭ | On every pager. |
| a date range | С / По | «С:» / «По:» with a colon; «От:» is only a mail's sender. |
| a place with no known name | Место {0} | «Место» is also the Location column; «Местоположение» for a field label or where a character is. |
| "+N more" (after a shortened list) | ещё {0} | |
| "Type to search…" | Введите текст для поиска… | |
| "Asking ESI…" | Запрос к ESI… | |
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
| price types Buy / Sell / Split | Покупка / Продажа / Середина | Split is the midpoint of the best buy and sell. In running text: «цены «Середина»». |
| master wallet / wallet division | главный счёт / счёт | The client's words. |
| transaction tax | налог на сделку | The journal type; sales tax is «налог с продаж». The client's word. |
| contract statuses | размещён / выполняется / завершён / отклонён / провален / удалён / аннулирован / истёк | outstanding / in progress / finished / rejected / failed / deleted / reversed / expired |
| Active (contract filter) | Действующие | The client's word. Not «Выполняется», which is In Progress in the same list. Historical: «Архивные». |
| loan (contract type) | ссуда | The client's word. |
| public / private (availability) | общедоступный / частный | The client's words. |
| escrow (market, contract) | депозит | The client's word («Депозит по контракту»). Buy escrow: «депозит на покупку». |
| freelance job / project | внешний заказ | The client's word. |
| medal | медаль | The client's word. |
| Project Discovery | Проект «Дискавери» | The client's word. |
| SKIN / Paragon Hub (cosmetic market) | окраска / Штаб «Парагона» | The client's words: «лицензия на окраску», «Покупка окраски в штабе «Парагона»»; a structure's paint is «окраска сооружения». |
| HyperNet Relay / HyperNode / HyperNet offer | гиперсетевое реле / гиперузел / предложение гиперсети | The client's words. |
| PLEX / New Eden Store | плекс (PLEX) / Игровой магазин | The client's words. |
| corporation project (ESI) | проект корпорации | The client's word. Always «проект корпорации», never a bare «проект», which is an industry job. This app's standing projects: «постоянные проекты». Project types follow the client's own mix of verbs and nouns («Доставить предмет», «Уничтожение NPC», «Повредить корабль»); faction warfare there is «МВ», the client's abbreviation. |
| donations / donor / contributors / contributed | пожертвования / жертвователь / участники / вклад | Corp Activity. |
| deliver (an industry job) | доставить | The client's word: «готов к доставке». |
| alt / main (character) | твинк / основной персонаж | Players' words. A market alt: «рыночный твинк». |
| outbid | перебить (цену) | «его цену перебили». |
| order book | стакан | Traders' word. |
| Asset Safety | система безопасности активов | The client's word, in full where there is room (a source, a tooltip, a heading). The Worklist's task kind is «Безоп. активов», the client's own short «безопасность активов» abbreviated, to fit its Type filter. |
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
| collection / derived history | коллекция / расчётная история | Item Browser and Inventory Levels. |
| map overlay / legend | слой / условные обозначения | The client's word for the second. |
| Sale Posting: label / post block / thread / section / price list | метка / блок сообщения / ветка / раздел / прайс-лист | A post's header and footer: «шапка» / «подвал» (in a section: «заголовок раздела»). What a sale price is based on: «основа цены продажи». |
| not for profit | некоммерческие | Sale Posting and the Sales Tracker. |
| Stores: shop front / usage message / deploy / secret / account | витрина / справка / развернуть (развёртывание) / секрет / учётная запись | Cloudflare's own names stay in English (see Style). |
| Worklist analysis | узкие места / дефицит предметов / конкуренция / пул / конвейер / буфер / покрытие / всплеск / потолок / вывод / совет | bottlenecks / item contention / contention (of prints) / pool / pipeline / buffer / days of cover / surge / ceiling / verdict / suggestion. This app's. |
| Worklist actions | отложить / груз | snooze / manifest. |
| Overview (this app's home) | Обзор | This app's screen, not EVE's in-space overview. |
| Item Browser / Structure Browser | Просмотр предметов / Просмотр сооружений | This app's screens; the second is the client's word. "Open in Item Browser" → «Открыть в разделе «Просмотр предметов»». |
| Player Entities / NPC Entities | Справочник игроков / Справочник NPC | This app's screens. "Open in NPC Entities" → «Открыть в справочнике NPC». |
| Price Overrides | Ручные цены | This app's screen. |
| Inventory Levels / Market Levels | Уровни запасов / Запасы на рынке | This app's screens. |
| Order Tracker / Sales Tracker / Sale Posting | Учёт заказов / Учёт продаж / Объявления о продаже | This app's screens; one posting is «объявление о продаже». |
| Industry Opportunities / Trade Opportunities | Возможности производства / Торговые возможности | This app's screens. |
| Net Worth / Income & Expense | Капитал / Доходы и расходы | This app's screens. |
| Corp Activity (its Projects tab) | Активность корпорации (Проекты) | This app's screen. |
| Background Processes (its Price History tab) | Фоновые процессы (История цен) | This app's screen. |
| Market Orders (Item Browser tab) | Рыночные ордера | This app's tab, named in Settings. |
| the other screens | Карта вселенной, Планировщик прыжков, Промышленные проекты, Промпарки, Расчёт производства, Обзор рынка, Оценка предметов, Рыночная ценность LP, Постоянные ордера на покупку, Магазины, Кошелёк, Отчёты о боях, Почта EVE, Уведомления, Обозреватель ESI, Журнал ошибок, Использование ИИ, Игровой журнал, Журнал чата | This app's navigation names (ShellText.Nav…): Universe Map, Jump Planner, Industry Jobs, Indy Parks, Production Calc, Market Overview, Item Valuation, LP Market Values, Standing Buy Orders, Stores, Wallet, Killmails, Eve Mail, Notifications, ESI Explorer, Error Log, AI Usage, Game Log, Chat Log. |
| Worklist tabs | Потребности станций / Потребности в предметах / Конечная продукция / Правила запасов / Уровни станций | This app's. |

## Short forms for narrow columns

Use the full wording wherever it fits; these are for headers and labels the app measured too narrow
(widths in the app's own fonts). A header may drop what its column's values make obvious. Keep one
short form per thing: «Стоим.» for стоимость, «произв.» for производства, «Рын.» for рыночная,
«Кол-во» for количество, «вручн.» for an override set by hand.

| English | Full | Short | Notes |
|---|---|---|---|
| build cost / build value | стоимость производства | Стоим. произв. · Себест. | «Себест.» where even «Стоим. произв.» does not fit. Not «Произв.», which reads as a quantity beside «Выпуск» or «В произв.». |
| market value | рыночная стоимость | Рын. стоимость · Рынок | «Рынок» among price-source columns, beside «Контракт». |
| total (a column of sums) | всего | Всего | «Итого» only on a totals row. |
| quantity / count | количество | Кол-во | |
| produced (a quantity) | произведено | Выпуск | |
| job cost (Production Calculator) | стоимость проекта | За проекты | |
| profit / profit % | прибыль / прибыль, % | Приб. / Приб. % | |
| sale price | цена продажи | Цена прод. | |
| min / avg / max price at the screen's station | минимальная / средняя / максимальная цена | Мин. цена / Сред. цена / Макс. цена | Market Levels. |
| in stock / in build / reserved, and their hand-set overrides | в наличии / в производстве / резерв | В наличии / В произв. / Резерв · Налич. вручн. / Пр-во вручн. / Резерв вручн. | Sale Posting. |
| min quantity (a market order's) | мин. объём (client) | Мин. кол. | The client's «Мин. объём» where it fits. |
| trigger below / fill up to (%) | срабатывать ниже / пополнять до (%) | Если ниже (%) / Поднять до (%) | Inventory rules. |
| accepts surplus | принимает излишки | Приём излишков | |
| enabled (Да / Нет) | включена | Вкл. | |
| average / calls / offers / took | средняя / запросы / предложения / длительность | Ср. · Сред. / Запр. / Предлож. / Время | AI Usage & Cost, Background Processes. |
| last fired | последнее срабатывание | Посл. срабатывание | |
| success chance | вероятность успеха (client) | Шанс успеха | |
| reward (a contract's) | вознаграждение | Вознагр. | |
| sender type | тип отправителя | Тип отпр. | «Отправитель» is the sender's own column. |
| current / latest (a version or build) | текущая / последняя | Текущая: / Новая: | The update dialogs. |
| why (a glyph column) | причина | Зачем | |
| task state Blocked | заблокировано | Заблок. | The Worklist's State filter. |
| task kind Asset Safety | система безопасности активов | Безоп. активов | The Worklist's Type filter. |
| Jump Fuel Conservation (skill) | Топливоснабжение гипердвигателей (client) | Топливоснабжение гипердв. | |

## For a native speaker to check

Grouped by area; the areas whose doubts matter most come first. The tag says what kind of doubt it
is: **meaning** (the translator inferred what the English means), **client** (the draft differs from,
or follows an odd, client wording), **fit** (may be too long for its space), **style**.

### Wallet journal types (RefTypeText)

- `RefTypeText.AgentsPreward` — "Agents Pre-reward" — «Предварительное вознаграждение агента» — **meaning**: no client text (its list has only the placeholder «Агенты_preward»).
- `RefTypeText.AllignmentBasedGateToll` — "Alignment-Based Gate Toll" — «Сбор за врата с учётом союза с фракцией» — **meaning**: "alignment" read as a pilot's standing with a faction; the client has only «Сбор за врата».
- `RefTypeText.MarketProviderTax` — "Market Provider Tax" — «Налог оператора рынка» — **meaning**: no client text.
- `RefTypeText.SecurityProcessingFee` — "Security Processing Fee" — «Плата за обработку (безопасность)» — **meaning**: no client text.
- `RefTypeText.OperationBonus` — "Operation Bonus" — «Бонус за операцию» — **meaning**: no client text.
- `RefTypeText.IndustrySecurityTax`, `MarketSecurityTax`, `AgentMissionSecurityTax` — "… Security Tax" — «Налог на защиту промышленных проектов / рыночных сделок / наград за задания агентов» — **meaning**: patterned on the client's «Налог на защиту наград за головы NPC»; no client text for these three.
- `RefTypeText.EssEscrowTransfer` — "ESS Transfer" — «Депозитный платёж СНС» — **meaning**: the client's name for "ESS Escrow Payment", assumed to be this type.
- `RefTypeText.FluxPayout`, `FluxTax`, `FluxTicketRepayment`, `FluxTicketSale` — "Flux …" — «Выплата по реализованному предложению гиперсети», «Счёт за использование гиперсетевого реле», «Возврат гиперузлов за просроченное предложение гиперсети», «Операция с гиперузлом» — **client**: the client's HyperNet Relay journal names, paired by meaning (as the German, Spanish, Japanese, Korean and Chinese drafts pair them); the client's payout name has «про», written here as «по».
- `RefTypeText.ResearchingMaterialProductivity`, `ResearchingTimeProductivity` — "Researching Material / Time Productivity" — «Повышение материалоэффективности производства», «Повышение скорости производства» — **client**: the client's journal names (its English says "Efficiency"), not the glossary's «исследование экономии материалов / времени».
- `RefTypeText.Manufacturing`, `ResearchingTechnology`, `Inheritance` — «Обработка», «Технология исследования», «Премия» — **client**: the client's journal names, kept word for word although they read oddly beside the rest of the app.
- `RefTypeText.ContractRewardDeposited`, `ContractRewardDepositedCorp` — "Contract Reward Deposited (Corp)" — «Вознаграждение за контракт размещено», «… (корп.)» — **client**: the client names both «Вознаграждение за контракт размещен (корп.)»; the gender and the personal type's «(корп.)» are corrected here.

### Market

- `MarketText.ModeSellToBuyOrder`, `ModeUndercutSellOrder` — "Buy Sell → Sell to Buy Order" / "… → Undercut Sell Order" — «Купить → продать в ордер на покупку», «Купить → перебить ордер на продажу» — **meaning**: «Купить» drops "buy from sell orders".
- `MarketText.ColStMin`, `ColStAvg`, `ColStMax` — "ST MIN / AVG / MAX" — «Мин. цена», «Сред. цена», «Макс. цена» — **style**: shortened to fit; "station" is left to the screen. Do they read as the station's prices beside «Рын. цена»?

### Settings

- `SettingsText.ImportStageStationOperations` — "Station operations" — «Виды станций» — **meaning**: read as the kinds of NPC station and the services each has.
- `SettingsText.ImportStageIndustryModifiers` — "Industry Modifiers" — «Влияние на промышленность» — **meaning**: worded to avoid «модификатор», which is the rig.
- `SettingsText.ImportStageRaces` — "Races" — «Расы» — **client**: the client says «Национальности».
- `SettingsText.NotePrivateConversationsAppearNote` — "…appear as "Private Chat (2)"…" — keeps «Private Chat (2)» — **client**: the Russian client calls these channels «Приватный чат»; the note should show what a Russian client's channel list shows.
- `SettingsText.SlackNoteToSelf` — "Note to Self" — «Заметки для себя» — **client**: use Slack's own Russian name for the self-DM if it has one.
- `SettingsText.Days` — "days" — «д» (after a number box: «Удалять записи старше [30] д») — **style**: chosen so any number reads right; confirm it does not read clipped.

### Corporation (Corp Activity, entity viewer, killmails)

- `CorpText.FactHomeSystem` — "Home system" (a faction's) — «Столичная система» — **meaning**: no client word found.
- `CorpText.FactLocator` — "Locator" — «Розыск пилотов» (value Да / Нет) — **client**: from the client's «Услуги розыска пилотов».
- `CorpText.FactCeo`, `FactTicker` — "CEO", "Ticker" — «Президент», «Короткое название» — **client**: the client's words; players say «CEO», «тикер».
- `CorpText.SlotShipHold` — "Ship Hold" (a hold for ships, killmail section) — «Отсек для кораблей» — **client**: the client's name for the ship maintenance bay; its inventory flag "Ship Hold" reads «Грузовой отсек».
- `CorpText.RoleFinalBlow`, `RoleTopDamage` — "★ FB", "▲ TD" — «★ Добил», «▲ Макс. урон» — **style**.

### Notifications and mail (CommsText)

- `CommsText.NotifLabelProject` — "Project" — «Название» — **meaning**: one label serves corporation projects and freelance jobs, so it names neither.
- `CommsText.ColRead`, `Read` — "Read" — «Статус» (values «Прочитано» / «Не прочитано») — **style**.

### Map

- `MapText.IncursionEstablished` — "established" — «подготовка» — **client**: the client's word for this incursion state (was «закрепление»); it reads like "preparation", which fits the phase if not the English.
- `MapText.FittingBandRigs` (and the other `FittingBand…`) — "RIGS" — «Модификаторы» — **fit**: a 9 px label on the fitting ring.
- `MapText.DockSupers` — "Supers & titans — Keepstar only" — «Суперкары и титаны — только Keepstar» — **style**: the client writes «суперКАРы».

### Industry

- `IndustryText.Activity` (and `MapText.ColActivity`, `IndustryText.ActivityLabel`) — "Activity" — «Вид работ» — **client**: the client's Industry window says «Задачи» or «Занятие».
- `IndustryText.ActivityReverseEngineering` — "Reverse Eng." — «Инж. анализ» — **fit**: the client's «Инженерный анализ», shortened for the column.
- `IndustryText.RigCat…` — "reactions" etc. — «реакций» etc. — **style**: genitive, to fit «{0}: нет модификаторов для {1}»; right only in that sentence.

### Finance

- `FinanceText.TabMarketTransactions` — "Market Transactions" — «Рыночные сделки» — **client**: the client says «Операции в торговой системе».
- `FinanceText.ColBuySell`, `BuySellLabel` — "B/S" — «Сделка», «Сделка:» — **style**: the header over «Покупка» / «Продажа» (the column is being widened to fit them).

### Whole app and navigation (ShellText, CommonText)

- `ShellText.NavNetWorth` — "Net Worth" — «Капитал» — **client**: the client's character sheet says «Общая оценка».
- (terms) market «рынок», invention «модернизация», industry job «проект», job slot «слот» — **client**: the client's window is «Торговая система»; players also say «инвент». Confirm what players expect.
- `ShellText.NavPlayerEntities`, `NavNpcEntities`, `NavPriceOverrides`, `NavMarketLevels`, `NavIndyParks` — «Справочник игроков», «Справочник NPC», «Ручные цены», «Запасы на рынке», «Промпарки» — **style**: names made up for this app's screens.
- `ShellText.AlarmsArmed…`, `CommonText.AlarmAction…` — alarm «тревога», armed «взведена», alert «оповещение» — **style**: does «тревога» sound natural for a rule the user sets up?
- `CommonText.DateMonthYearShort` — "MMM yy" — «сент. 26» — **style**: may read as a day of the month; `MM.yy` is the alternative.

### Sale Posting, Order Tracker, Stores (SalesText)

- `SalesText.ColStockOvr`, `ColBuildOvr`, `ColRsrvOvr` — "STOCK OVR" etc. — «Налич. вручн.», «Пр-во вручн.», «Резерв вручн.» — **style**: short forms that fit 70 px; do they read clearly as the hand-set overrides?
- `SalesText.ColBuildCost2`, `IndustryText.ColBuildValue` — "BUILD COST", "Build Value" — «Себест.» — **style**: «себестоимость» where even «Стоим. произв.» does not fit; `IndustryText.ColJobCost` "Job Cost" is «За проекты» beside «Стоим. мат.».
- `SalesText.LimitScopeType`, `LimitScopeGroup`, `LimitScopeStore` — "item type" etc. — «тип предмета», «группу предметов», «весь магазин» — **style**: accusative, to read in «до [N] ед. на [группу предметов] за [N] [д]»; they look inflected alone in the drop-down.
- `SalesText.ShowNotForProfit`, `MenuMarkAsNotForProfit`, `ExcludedShown` — "not for profit" — «некоммерческие» — **style**.

### Background processes and logs (DataText)

- `DataText.ColFired` — "Fired" (how many times an alarm went off) — «Сраб.» — **fit**: shortened for a 60 px column; is the abbreviation clear?

### Overview

- `OverviewText.BriefVersus`, `BriefJoins` — "{0} vs {1}", "{0} joins {1}" — «{0} против {1}», «{0} на стороне {1}» — **style**: a name after a preposition, left undeclined (EVE names are Latin script).
- `OverviewText.SboOutbid…` — "{0} is outbid" — «у {0} перебита цена» — **style**.

### Alarms

- `AlarmsText.IntelIgnoreNvLabel` — "Ignore no visual" — «Пропускать NV (нет визуала)» — **style**.
- `AlarmsText.ScheduleYearlyAt` — "{0} {1} each year at {2}" — «Ежегодно: {0}, {1}-е число, в {2}» — **style**: worded so the month list can stay in the nominative (no «1 января»).
- `AlarmsText.Adrift…`, `UndockSaid…`, `IntelSaid…` — the spoken lines — **style**: rephrased so a hull name stays in the nominative («Корабль персонажа {1} ({2})…», «{0}: выход из дока…», «Корабли: {0}.»).

### Worklist

- `WorklistText.BlueprintVerdictSteady` — "Steady" (a lasting shortage of prints) — «Постоянно» — **style**: beside the trend «Стабильно» for the same English.
- (terms) «узкие места», «дефицит предметов», «конкуренция», «буфер», «покрытие», «всплеск», «потолок» — **style**: this app's analysis words; do they read naturally?
- `WorklistText.RankTopPercent` — "top {0:N0}%" — «верхние {0:N0}%» — **style**.
- `WorklistText.ColWhy`, `StateBlocked`, `ColFinal`, `FillUpTo` — "Why", "Blocked", "Final", "Fill up to (%)" — «Зачем», «Заблок.», «Конеч.», «Поднять до (%)» — **style**: short forms for 35–77 px spaces; do they read naturally?

### Characters and Assets

- `CharactersText.ColLocation` — "Location" — «Место» — **style**: could be read as "rank" in that grid.
- `AssetsText.AssetsSummaryUnitsStacksPlaces`, `StatusItemsAcrossCategories` — "{0} in {1} across {2}", "{0} across {1}" — «{0} — {1}, {2}», «{0}, {1}» — **style**: reshaped because "in" and "across" would need other cases.
