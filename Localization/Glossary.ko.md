# 한국어 glossary

The terms EVE Console's Korean text uses, so the same word reads the same on every screen and
matches the game where the game has a word for it. The words marked "The client's word." are the
Korean EVE client's own, read from its localization tables. Corrections from Korean-speaking
players are welcome — this list is where they belong, so every screen picks them up.

## Style

- Korean as in a desktop app, in standard spacing (띄어쓰기). Labels, column headers, buttons and
  tab names are nouns (`저장`, `새로고침`, `스킬 대기열`) with no full stop.
- Messages, notes and tooltips are complete sentences in the formal polite style: `…합니다`,
  `…되었습니다`, `…하십시오`. Questions `…하시겠습니까?`. A status that is in progress is a noun
  phrase ending in `…`: `불러오는 중…`, `확인 중…`.
- Western punctuation, as Korean uses it: `, . : ! ? ( )`, quotes `"…"`. A label ending in `:`
  keeps `:`. No space before `:`.
- Numbers and counters: no space between a number and a Hangul counter or unit — `5개`, `3일`,
  `10회`, `2명`, `12시간`. A space before a Latin unit or code — `1,000 ISK`, `12 m³`, `30 SP`.
- Particles after a placeholder: the text in `{0}` may end in a vowel or a consonant, so write the
  paired forms as the client does — `{0}은(는)`, `{0}이(가)`, `{0}을(를)`, `{0}과(와)`,
  `{0}(으)로` — or rephrase so no particle follows (`이름: {0}`).
- **Placeholders stay exactly as written**: `{0}`, `{1:N0}`, `{alarm}`. They may move in the
  sentence; none may be dropped or added. A date pattern inside a placeholder is written in the
  Korean order with numeric months — `{0:dd MMM yyyy}` → `{0:yyyy년 M월 d일}`,
  `{0:dd MMM yyyy HH:mm}` → `{0:yyyy년 M월 d일 HH:mm}` — so no English month name appears;
  ISO patterns (`yyyy-MM-dd`) stay.
- Date patterns (`CommonText.Date*`): `M월 d일`, `yyyy년 M월 d일`, `yyyy년 M월`, `HH:mm` as it is,
  12-hour `tt h:mm` (오전/오후 before the time).
- **Unchanged**: EVE Console, EVE, ESI, SDE, SSO, CCP, ISK, m³, SP, ME, TE, zKillboard, Fuzzwork,
  Slack, Discord, GitHub, Tranquility, keyboard keys, file formats, URLs and anything that is
  typed exactly (`yyyy-MM-dd`).
- English written in capitals is a small heading; in Korean it is simply the words.
- Plural families: Korean has one form, `…Other`, with the number as `{0}` and a counter
  (`{0}개`, `{0}명`, `{0}회`, `{0}건`).
- Recurring small phrases: a count of rows is `{0:N0}행`; "+N more" / "and N more" is `외 {0}개`
  (`{0} 외 {1}개` after a name); a date range's labels are `시작일` / `종료일` (From, and Thru or To),
  with a colon where the English has one; version labels are `현재 버전:` / `최신 버전:`; buttons
  that tick or clear a whole list are `모두 선택` / `선택 해제`.
- Where the app cuts a label short, use the short form (measured in the app's fonts): the players'
  `하이섹` / `로우섹` / `널섹`, and `시큐` for the Sec column; `발신:` / `수신:` for the mail reading
  pane's 36-px From: / To: labels (보낸 사람 / 받는 사람 everywhere else); `소요` for a call's
  duration (Took) and `발생 수` for how often an alarm fired, in 60-px columns.

## Terms

| English | 한국어 | Notes |
|---|---|---|
| capsuleer | 캡슐리어 | The client's word. |
| character | 캐릭터 | The client's word. |
| corporation / corp | 코퍼레이션 | The client's word, for both; no short form. |
| alliance | 얼라이언스 | The client's word. |
| faction | 팩션 | The client's word. |
| NPC corporation | NPC 코퍼레이션 | |
| agent (NPC) | 에이전트 | The client's word. An NPC corporation's agent division = 분야 (the client's). |
| AI agent / companion (this app's) | AI 도우미 | Never 에이전트, which is the NPC agent. Under the Settings tab AI 도우미, its own sub-tab is just 도우미. |
| standings | 평판 | The client's word. |
| solar system | 항성계 | The client's word. |
| constellation | 성좌 | The client's word. |
| region | 지역 | The client's word. |
| station | 정거장 | The client's word. Home station = 주 정거장 (the client's). |
| structure (Upwell) | 구조물 | The client's word. Upwell = 업웰. |
| citadel | 시타델 | The client's word. |
| engineering complex | 엔지니어링 시설 | The client's word. |
| refinery | 정제소 | The client's word. |
| security status | 시큐리티 상태 | The client's word; 시큐리티 in a short label. The Sec column of the bare figure = 시큐, as players shorten it (시큐리티 does not fit those columns). |
| high-sec / low-sec / null-sec | 하이 시큐리티 / 로우 시큐리티 / 널 시큐리티 | The client's words; 하이섹 / 로우섹 / 널섹 in a narrow header. |
| wormhole | 웜홀 | The client's word. |
| stargate | 스타게이트 | The client's word. |
| jump / jumps | 점프 | The client's word. "5 jumps" = 점프 5회, as the client counts them. |
| jump drive / jump range | 점프 드라이브 / 점프 거리 | The client's words. Light year = 광년. |
| Jump Drive Calibration / Jump Fuel Conservation | 점프 드라이브 최적화 / 점프 연료 관리 | The client's words (skills). JDC stays as players write it. |
| jump bridge / tether | 브릿지 (점프 브릿지) / 테더링 | The client's words. |
| jump clone | 점프 클론 | The client's word. |
| capital ship | 캐피탈 함선 | The client's word. |
| ship | 함선 | The client's word. |
| ship class | 함급 | The client's word. |
| hull | 선체 | The client's word. |
| fitting | 피팅 | The client's word. Saved fittings = 저장된 피팅. To fit a module (a button) = 장착. |
| high / mid / low slots | 하이 / 미드 / 로우 슬롯 | The client's words (미드, not 미디움). Rig / subsystem / service slots = 리그 / 서브시스템 / 서비스 슬롯; a band label in the fitting ring drops 슬롯. |
| module | 모듈 | The client's word. |
| rig | 리그 | The client's word. |
| turret / launcher | 터렛 / 런처 | The client's words. |
| drone | 드론 | The client's word. Fighter = 파이터. |
| charges / ammo | 차지 / 탄약 | The client's words. |
| fuel | 연료 | The client's word. Fuel block = 연료 블록. Isotopes = 동위원소 (the client's). |
| holds and bays | 드론 격납고 / 파이터 격납고 / 파이터 사출관 / 연료실 / 광물 저장고 / 탄약고 | Drone bay / fighter bay / fighter tubes / fuel bay / ore hold / ammo hold; the client's words, like the rest of the killmail holds. |
| item | 아이템 | The client's word. |
| type (of item) | 종류 | The client's word (아이템 종류, 함선 종류). A contract's type is 종류 too (the client's 계약 종류). A type id = 타입 ID. Another kind of thing (notification type, journal entry type, task or project type) = 유형. |
| group / category | 그룹 / 분류 | The client's words. |
| market group | 거래소 그룹 | |
| assets | 자산 | The client's word. |
| hangar | 격납고 | The client's word. Item hangar = 아이템 창고, ship hangar = 함선 격납고, fleet hangar = 함대 공용 창고 (the client's). |
| corp hangar | 코퍼레이션 격납고 | The client's word. |
| hangar division | 구획 | An unnamed one = 제1구획 … 제7구획, the client's default names (`제{0}구획`); beside its name, `{0} (제{1}구획)`. Divisions in general (an ESI scope, the poll of division names) = 부서. |
| container | 컨테이너 | The client's word. |
| cargo hold | 화물실 | The client's word. |
| volume (of an item, m³) | 부피 | The client's word. |
| volume (traded) | 거래량 | The client's word. |
| packaged / assembled | 포장 / 조립 | The client's states are 포장 완료 / 조립 완료; packaged volume = 포장 부피. |
| portion size / base price / mass / capacity | 묶음 크기 / 기본 가격 / 질량 / 적재량 | 질량 and 적재량 are the client's words. |
| blueprint | 블루프린트 | The client's word. |
| BPO / BPC (in a sentence) | 블루프린트 원본 / 블루프린트 복제본 | The client's 원본 / 복제본. A bare "BPO" label stays BPO. |
| reaction formula | 반응식 | The client's word (the Worklist's BPO / 반응식 tab). |
| runs (of a job or copy) | 실행 횟수 | The client's word. "10 runs" = 10회. |
| manufacturing | 제조 | The client's word. |
| reaction | 합성 | The client's word. Composite / biochemical / hybrid reactions = 복합 / 생화학 / 하이브리드 합성 (the client's). |
| invention | 인벤션 | The client's word. Datacore / decryptor = 데이터코어 / 해독기 (the client's). |
| copying | 복제 | The client's word. |
| research | 연구 | The client's word. Science (slots, jobs) = 연구 as well; the science slots cover copying, research and invention. |
| reverse engineering | 역설계 | The client's word. |
| material efficiency / time efficiency | 자원효율성 / 시간효율성 | The client's words. ME / TE stay as abbreviations. |
| industry job | 산업 작업 | The client's 산업 + 작업 (Jobs = 작업, Manufacturing Jobs = 제조 작업). A job alone = 작업. |
| industry job statuses | 진행 중 / 일시정지 / 준비 완료 / 수령 완료 / 취소됨 / 되돌림 | Active / Paused / Ready / Delivered / Cancelled / Reverted. A count of the open ones (active, paused or ready) = 활성 {0}개. |
| job slot | 작업 슬롯 | Manufacturing / reaction / science slots = 제조 / 합성 / 연구 슬롯. |
| build cost | 제조 비용 | The client's 건설 비용 is for building structures, not for making items. |
| raw materials / intermediates / input materials | 원재료 / 중간재 / 투입 자원 | The client's words. |
| reprocessing / refining | 정제 | The client's word. (The wallet journal type reads 재처리 수수료.) |
| compression / decompress | 압축 / 압축해제 | The client's words. |
| mining | 채굴 | The client's word. Mining residue = 손실 (the client's "Residue Probability" is 손실 확률). |
| ore / ice / moon ore | 광물 / 아이스 / 위성 광물 | The client's words (광물 저장고, 아이스 채굴기, 위성 광물). Minerals = 미네랄. |
| moon mining ledger | 위성 채굴 기록 | The client's 채굴 기록. |
| planetary industry | 행성 개발 | The client's word. Planetary colonies = 행성 콜로니. |
| market | 거래소 | The client's word. |
| order (market) | 주문 | The client's word. |
| buy order / sell order | 구매 주문 / 판매 주문 | The client's words. |
| order (a customer's, in the Order Tracker) | 주문 | |
| trade hub | 무역허브 | The client's word. Trade (the app's tools) = 무역. |
| price / cost / profit / margin | 가격 / 비용 / 이익 / 이익률 | The client's 가격, 비용. Unit price = 개당 가격 (the client's). |
| broker fee / sales tax | 중개 수수료 / 판매 수수료 | The client's words. |
| multibuy | 일괄구매 | The client's word. |
| contract | 계약 | The client's word. |
| item exchange / courier / auction | 아이템 교환 / 운송 / 경매 | The client's words. |
| contract parties: issuer / assignee / acceptor | 발행자 / 지정 대상 / 수락인 | 수락인 is the client's word; its issuer is 발행인 or 개설자 (an open question below). The dates are 발행일 / 수락일 / 완료일 / 만료일. |
| offered / requested items (contract) | 제공 / 요구 아이템 | A contract line alone = 제공 / 요구. |
| buyout | 즉시 구매가 | The client's word. |
| collateral / reward | 담보 / 보상 | The client's words; the sums = 담보금 / 보상금. |
| accept / reject (a contract) | 수락 / 거절 | The client's words. |
| wallet | 계좌 | The client's word. |
| balance / owner | 잔고 / 소유자 | The client's words. |
| journal (wallet) | 저널 | The client's word. |
| wallet journal entry types (RefTypeText) | the client's own journal names | Even where the app says it otherwise: 중개인 수수료 (Brokers Fee), 산업 시설 수수료 (industry tax), 재처리 수수료, 거래소 거래기록, LP 스토어. Bounty = 현상금, and so is a ratting payout (bounty_prizes); a player bounty's payout (bounty_prize) = 현상금 상품. |
| transaction | 거래 | The client's word; the wallet's Transactions list = 거래 기록 (the client's), market transactions = 거래소 거래 기록. |
| income / expense | 수익 / 지출 | The client's 수익. |
| net worth | 순자산 | The client's "Total Net Worth" is 총 자산. |
| loyalty points / LP store | 로열티 포인트 / LP 스토어 | The client's words. LP 스토어 with a space, as the client writes it every time (never LP스토어); it is also the journal type's name. An LP offer = 상품. The app's own store is 상점. |
| skill / skill queue / skill points | 스킬 / 스킬 대기열 / 스킬 포인트 | The client's words. Training queue = 훈련 대기열 (the client's). |
| implant | 임플란트 | The client's word. |
| killmail | 킬 메일 | The client's word. Its Kill Report window = 킬 기록. |
| kill / loss | 킬 / 손실 | The client's 킬. Ship kills / pod kills = 함선 킬 / 캡슐 킬. |
| victim / attacker / final blow | 피해자 / 공격자 / 결정타 | The client's words (킬 기록 - 피해자, 결정타). Top damage = 최고 피해량 (the client's). |
| ratting | 랫질 | The client's word. |
| mission | 미션 | The client's word. |
| sovereignty | 소버린티 | The client's word. |
| ADM / industry cost index | 방어 배율 (ADM) / 산업 원가 지수 | The client's words; ADM alone where it is short. |
| vulnerability window / vulnerable / invulnerable | 취약 시간대 / 취약 / 무적 | The client's words. |
| Upwell sovereignty resources | 전력 / 노동력 / 촉매 | Power / workforce / reagents; the client's words. Magmatic gas / superionic ice = 마그마 가스 / 초이온성 아이스. |
| faction warfare states | 점령됨 / 분쟁 중 / 분쟁 없음 / 취약 | The client's words. Owner / occupier = 소유자 / 점령자; victory points = 승리 포인트. |
| incursion states | 침공 개시 / 동원 중 / 후퇴 중 | Established / mobilizing / withdrawing; 침공 개시 and 후퇴 중 are the client's. |
| undock / dock | 도킹 해제 / 도킹 | The client's words. |
| online / offline | 온라인 / 오프라인 | The client's words. |
| downtime | 일일점검 | The client's word. |
| notification | 알림 | The client's word. |
| mail / EVE mail | 메일 / EVE 메일 | The client's words. Inbox / sent = 받은 메일함 / 보낸 메일함; reply / forward = 답장 / 전달; From / To = 보낸 사람 / 받는 사람 (all the client's). In the reading pane's narrow From: / To: labels, 발신: / 수신:. |
| channel | 채널 | The client's word. |
| intel (channel) | 인텔 | As players say it. An intel report = 인텔 보고. |
| hauling / haul / trip | 운반 / 운반 / 운반 횟수 | The client's 운반. "3 trips" = 운반 3회. |
| worklist | 작업 목록 | |
| Indy Park (this app's) | 산업 단지 | |
| inventory level / stock / on hand | 재고 수준 / 재고 / 보유 | |
| store (this app's shop) | 상점 | The client's word. |
| buyer | 구매자 | The client's 의뢰인 is someone who gives a mission or contract. |
| alarm / alert | 알람 / 경보 | The client's 경보. |
| scheduler / report | 스케줄러 / 보고서 | The client's 신고 is reporting a player. |
| background process | 백그라운드 프로세스 | |
| polling | 폴링 | |
| token | 토큰 | |
| scope (ESI) | 권한 범위 | The client's 검색 범위 is a search's range. A scope's name = "<대상> 읽기 / 쓰기" (부서 읽기, 모든 계좌 읽기). |
| corporation roles / titles | 직책 / 직위 | The client's words. Director / accountant = 임원 / 회계사. The AI's model roles (this app's) = 역할. |
| settings / refresh / save / cancel | 설정 / 새로고침 / 저장 / 취소 | The client's words. |
| add / remove / delete / edit | 추가 / 제거 / 삭제 / 수정 | The client's words. |
| export / import / filter / search / clear | 내보내기 / 가져오기 / 필터 / 검색 / 지우기 | The client's words, except clear: its 삭제 would read as delete. |
| apply / OK / close / copy | 적용 / 확인 / 닫기 / 복사 | The client's words. |
| task (a Worklist row) | 작업 항목 | Distinct from job (작업). |
| task (a Scheduler entry) | 예약 작업 | 작업 alone where the Scheduler is plainly the context. |
| hull (hit-point layer, beside shield and armour) | 선체 | 실드 / 장갑 / 선체, as the game shows them; also a ship's hull. |
| CONCORD | CONCORD | The client keeps it in English. |
| starbase / control tower | 스타베이스 / 관제타워 | The client's words. |
| reinforced / reinforcement | 강화 | The client's word. "Reinforced until" → 강화 종료 시각 |
| anchoring / unanchoring | 위치 고정 / 위치 고정 해제 | The client's words (… 중 while it runs). |
| high power / low power (Upwell) | 최대 전력 / 저전력 | The client's words (최대 전력 모드, 저전력 모드). Abandoned = 버려짐. |
| moon extraction / fracture / moon drill | 위성 파편 분리 / 폭파 / 위성 드릴 | The client's words; moon chunk = 위성 파편. |
| war: aggressor / defender / ally | 공격 측 / 수비 측 / 동맹 | The client's words. Mutual war = 협정 전쟁; war eligible = 전쟁 가능 (a status), and the notifications 전쟁 자격 획득 / 상실 (the client's titles); war declared = 전쟁 선포; War HQ = 전쟁 본부. |
| kill right | 사살권 | The client's word. |
| bounty / insurance payout | 현상금 / 보험금 지급 | The client's 현상금, 보험. |
| bill / broker fee / office rental | 청구서 / 중개 수수료 / 사무실 임대료 | The client's words. |
| price types Buy / Sell / Split | 구매가 / 판매가 / 중간가 | Split is the midpoint of the best buy and sell. |
| master wallet / wallet division | 주 계좌 / 계좌 구분 | The client's words. A numbered division = 제{0}계좌 (the client's 제2계좌); Div {0} in a short label = 계좌 {0}. |
| industry tax / transaction tax | 산업세 / 거래세 | The client's words (the journal type industry_job_tax is 산업 시설 수수료); sales tax is 판매 수수료. |
| contract statuses | 미체결 / 진행 중 / 완료 / 거절됨 / 실패 / 삭제됨 / 철회됨 / 만료됨 | outstanding / in progress / finished / rejected / failed / deleted / reversed / expired; the client's words. |
| Active (contract filter) | 활성 | Not 진행 중, which is In Progress in the same list. |
| loan (contract type) | 대여 | The client's word. |
| public / private (availability) | 공개 / 비공개 | The client's words. |
| escrow (market, contract) | 예탁금 | The client's word. |
| freelance job / project | 프리랜서 임무 | The client's word. |
| corporation projects | 코퍼레이션 프로젝트 | The client's word. Its types, the client's words: 함선 격퇴, 함선 파괴, NPC 함선 파괴, 함선 손실, 아이템 제조, 로열티 포인트 모으기, 원격 실드 부스터, 원격 장갑 수리, 잔해 샐비지, 시그니처 스캔, 함선 보험, 팩션 전쟁 컴플렉스 점령 / 방어, 수동 갱신; Deliver Item = 아이템 배송. |
| medal | 훈장 | The client's word. |
| Project Discovery | 프로젝트 디스커버리 | The client's word. |
| SKIN / Paragon Hub (cosmetic market) | SKIN / 파라곤 허브 | The client's words. |
| PLEX / New Eden Store | PLEX / 뉴에덴 스토어 | The client's words. |
| Fortizar / Keepstar | 포티자 / 키프스타 | The client's names. Standup service modules = 스탠드업 제조공장 / 연구실 / 인벤션 연구실 / 정제시설 / 클론 센터 / 거래소 허브 (the client's). |
| ticker / executor corp / home system / militia | 약어 / 대표 코퍼레이션 / 주 항성계 / 밀리샤 | The client's words. Locator (agents) = 위치 확인; clone bay = 클론 시설. |
| staging system / influence / unclaimed | 집결지 항성계 / 영향력 / 미점거 | The client's words. |
| notification subjects | 전문가 시스템 / 얼라이언스 본부 / 거래소 벌금 / 소유권 이전 / 평판 변동 / 자산 보호 포장 | Expert System / alliance capital / market fine / ownership transferred / standing change / asset safety wrap; the client's words. |

## The app's own names

| English | 한국어 | Notes |
|---|---|---|
| Overview (the app's home page) | 개요 | Not 오버뷰, which is the game's in-space overview. Market Overview = 거래소 개요. |
| Item Browser / Structure Browser | 아이템 브라우저 / 구조물 브라우저 | |
| Universe Map | 우주 지도 | The client's word for its star map. The breadcrumb's first step = 우주. |
| Jump Planner | 점프 플래너 | |
| Production Calc | 생산 계산기 | Leftovers = 잉여분; shopping list = 구매 목록; the catch-all facility = 기본 시설. |
| Price Overrides | 가격 수동 지정 | |
| Industry / Trade Opportunities | 산업 기회 / 무역 기회 | |
| Market Levels | 거래소 재고 수준 | Sell-order stock kept on a market. Its AVAIL column = 판매 중; Inventory Levels' AVAIL = 가용. |
| Standing Buy Orders | 상시 구매 주문 | Their states: 활성 / 가격 밀림 / 누락. |
| Order Tracker / Sales Tracker / Sale Posting | 주문 추적 / 판매 추적 / 판매 게시 | |
| Item Valuation | 아이템 가치 평가 | Appraise = 평가. Its paste box reads item names in the client's language, so its examples use the client's names (트리타늄 22222). The alarm editor still matches English names only, so its examples stay English. |
| LP Market Values | LP 거래소 시세 | |
| Stores (the app's web and mail stores) | 상점 | |
| Corp Activity / Income & Expense | 코퍼레이션 활동 / 수익 및 지출 | ISK efficiency = ISK 효율; percentage points = %p. |
| Background Processes / ESI Explorer / Error Log / AI Usage | 백그라운드 프로세스 / ESI 탐색기 / 오류 로그 / AI 사용량 | ESI Data Explorer = ESI 데이터 탐색기. |
| Game Log / Chat Log | 게임 로그 / 채팅 로그 | |
| Player / NPC Entities | 플레이어 개체 / NPC 개체 | Characters, corporations and alliances. |
| standing projects | 상시 프로젝트 | This app's own rules that a corp project should exist. |
| attributes (a character's) | 능력치 | The client's word: 지능 / 기억력 / 카리스마 / 지각력 / 정신력. |
| neural remap | 신경망 재배열 | The client's word; bonus remaps = 보너스 재배열. |
| client (one running copy of the app) | 클라이언트 | |
| build (a version of the app) | 빌드 | |
| Settings tabs | ESI 토큰, 캐릭터, SDE, 거래소, 가격 히스토리, 산업, 타이머, 폴링, 게임 로그, 채팅 로그, 지도 데이터, 킬 메일, "코퍼레이션 상위 10 / 요약", AI 도우미 (sub-tabs 도우미, 개인 설정, 음성 (TTS), 음성 입력 (STT), Slack), 경보, 데이터베이스, 데이터 보존, 업데이트, 기타 | Quote them exactly when a message names a tab. |
| themes Dark / Light | 어둡게 / 밝게 | As Windows names its app modes; Blue (dark) = 파랑 (어둡게). |
| Worklist tabs | 작업 목록, 정거장 필요량, 아이템 필요량, 병목, 요약, 슬롯, 아이템 경합, BPO / 반응식, 운반, 최종 생산품, 설정, 출처, 거래소, 재고 규칙, 정거장 재고 수준, 기타 | Sources (출처) are the generators the tasks come from. |
| Worklist task kinds / states | 구매, 운반, 정제, 압축해제, 산업 작업, 자산 보호, 스킬 대기열, 코퍼레이션 프로젝트 / 준비됨, 막힘, 대기 중, 진행 중 | 준비됨 is a task that can start now, not an industry job's 준비 완료. |
| Worklist planning words | 비축분 / 슬롯 풀 / 원본 / 상한 / 보유 일수 / 수급 비율 / 급증 / 판정 / 제안 / 적재 목록 / 경유지 / 계획기 / 일시 숨김 | Buffer / slot pool / original (print) / ceiling / cover / balance / surge / verdict / suggestion / manifest / stops / planner / snooze. A market alt = 거래소 부캐릭터. |
| Alarms and Scheduler | 검사 / 조건 / 실행 / 동작 / 발생 / 쿨다운 / 단계 / 자리 표시자 | Check / WHEN / THEN / action / fire / cooldown / stage / placeholder. Continuous / one shot = 계속 유지 / 1회만; wake-up call = 깨우기 알림; spoken prices count in 만 / 억 ISK. |
| Sale Posting words | 게시물 / 게시 블록 / 섹션 / 머리글 / 바닥글 / 고정 텍스트 / 접두어 | Posting / post block / section / header / footer / static text / prefix. In Stock / In Build / Reserved = 재고 / 제조 중 / 예약됨; the override columns = … 수동. |
| an order's source (Order Tracker, Stores, Overview) | 조달: 재고 / 생산 중 / 계약됨 / 미조달 | Stock / In production / Contracted / Unsourced. Book an order = 접수; units delivered = 전달됨; not for profit = 비영리; purchase limit = 구매 한도; price list = 가격표. |
| store setup | 메일함 / 배포 / 동기화 / 비밀 키 / 콜백 URL | Mailbox / deploy / sync / secret / callback URL. Client ID and Secret Key stay in English, as the EVE developer site shows them. |
| Background Processes words | 수집 / 대기 중 / 실시간 수집 / 전체 스트림 / 주기적 폴링 / 공백 채우기 / 이름 캐시 / 엔드포인트 | Sweep / idle / live capture / firehose / interval poll / gap-fill / name cache / endpoint. |
| AI Usage words | 요금 / 제공자 / 단위 | Rates / provider / units (what the counts are counted in). |
| Overview words | 내 주문가 / 잔여 / 해제 / 활성 경보 | Our bid / Rem. / dismiss / open alerts. |
| Finance words | 현금 흐름 / 순손익 / 로그 눈금 / 올해 / 작년 / 구분 | Cashflow / net / log scale / year to date / prior year / B/S (a 50-px column; the filter says 구매/판매). |

## Other words settled in wave 1

| English | 한국어 | Notes |
|---|---|---|
| download | 다운로드 | The client's word (업데이트 다운로드 중). |
| restart | 다시 시작 | |
| log in / log out | 로그인 / 로그아웃 | The client's 로그아웃. |
| alt / main (character) | 부캐릭터 / 주 캐릭터 | |
| Asset Safety | 자산 보호 | The client's word. |
| outbid (market order) | 가격 밀림 | In a sentence: 더 높은 가격의 주문에 밀림. The client's 상회입찰 is for auctions. |
| deliver (a finished industry job) | 수령 | "ready to deliver" = 수령할 수 있음. |
| New Eden | 뉴에덴 | The client's word. |
| incursion / faction warfare | 인커젼 / 팩션 전쟁 | The client's words. |
| price history | 가격 히스토리 | The client's word. |
| repackaged (volume) | 재포장 | The client's word. |
| item attributes / Dogma | 속성 / 도그마 | The client's 도그마; a character's attributes are 능력치. |
| races (Amarr, Caldari…) | 국가 | The client's word. |
| certificates / schematics (PI) | 자격증 / 설계도 | The client's words. |
| asteroid belt / celestials | 소행성 벨트 / 천체 | The client's words. |
| Upwell, SKIN, PLEX | 업웰, SKIN, PLEX | The client's forms. |
| intel sighting | 목격 보고 | |
| webhook / workspace / thread (Slack) | 웹후크 / 워크스페이스 / 스레드 | |
| scope (ESI, in a sentence) | 권한 범위 | Scope as a filter's reach (zKillboard import) = 범위. |
| backfill | 과거 데이터 채우기 | |
| purge (data retention) | 삭제 | |
| Yes / No | 예 / 아니오 | The client's words. |

## One English word, two Korean words

Where the English repeats a word for different things, Korean keeps them apart. Use the word for
the meaning, not for the English.

| English | 한국어 | Which is which |
|---|---|---|
| Active | 활성 / 진행 중 | 활성: a contract filter, a standing buy order's state, a project's status, the count of open industry jobs. 진행 중: a running job, a running incursion, an open order in the Sales filter. |
| Ready | 준비 완료 / 준비됨 | 준비 완료: a finished job waiting to be delivered, and the app when idle. 준비됨: a Worklist task that can start now. |
| Running | 진행 중 / 실행 중 | A job or task / a background service. |
| Delivered | 수령 완료 / 전달됨 / 배송됨 | An industry job's status / the units an order's buyer has received / items delivered to a structure (notifications). |
| Detail(s) | 세부 정보 / 세부 내역 | Information about one thing (a tab, a column, a notification's text) / an itemised breakdown (Detail tabs of AI Usage, Sales Tracker, Corp Activity; the posting block). |
| Note | 비고 / 메모 | The app's own remark on a row (a warning, a reason) / a note the user types. |
| Source | 조달 / 출처 / 주체 / 위치 | Where an order's or a material's supply comes from / where a record, a leftover or a task came from / who acted in a game-log line / the Worklist's location filter. |
| From / To | 시작일 / 종료일, 출발 / 도착, 발행자, 보낸 사람 / 받는 사람, 위치 / 도착지 | A date range / a jump / a contract's issuer (the column) / mail / a contract's location and a courier's destination. |
| Expires | 만료일 / 만료 | A column or field that shows a date / one that shows the time left, and a notification's label. |
| Enabled / Disabled | 활성화 / 비활성화됨 / 사용 안 함 | A switch or its Yes/No column / its off state / a choice meaning "no key". |
| Division | 제{0}구획 / 제{0}계좌 / 부서 / 분야 | An unnamed hangar division / a wallet division / divisions in general / an NPC corporation's agent division. |
| In space | 비행 중 / 우주 공간 | A character undocked (the client's 비행 중) / a thing left in space in a system. |
| Missing | 부족 / 누락 | Short of material / a standing buy order with no live order. |
| Store | 상점 / 스토어 | The app's own store / an NPC corporation's LP store (LP 스토어). |
| Daily / Weekly / Monthly | 일별 / 주별 / 월별, 매일 / 매주 / 매월 | Grouping on a chart / how often something runs. |
| Buy / Sell | 구매 / 판매, 구매가 / 판매가 | A direction or an action / a price type. |
| Capacity | 적재량 / 용량 | What an item holds (the client's) / the job slots in a pool. |
| Balance | 잔고 / 수급 비율 | A wallet's / the Worklist's made-per-used ratio. |
| Amount | 금액 / 수치 | ISK / a game-log line's figure (damage, units, ISK). |
| Attributes | 속성 / 능력치 | An item's / a character's. |
| Creator | 생성자 / 설립자 | Who set up a project (the client's Creator) / an alliance's founder. |
| Copy | 복사 / 복제 | To the clipboard / blueprint copying. |
| Roles | 직책 / 역할 | Corporation roles / the AI's model roles. |
| Units | 수량 / 단위 | A quantity / what a count is counted in. |
| Output | 산출물 / 출력 | What reprocessing produces / an AI model's output. |
| Medium | 중형 / 중간 | A size class / a voice's quality tier. |

## For a native speaker to check

The open questions, grouped by area. The areas with the weightiest doubts come first, and within
an area the order is: meaning guessed, then a draft that differs from the client's word, then fit,
then style. The tag at the start of each line says which.

### Wallet journal types (RefTypeText)

- [meaning] `RefTypeText.MarketProviderTax` — "Market Provider Tax" → 안전무역위원회 추가 수수료 — taken to be the client's "SCC surcharge" (matched by id); confirm it is that charge.
- [meaning] `RefTypeText.MilestoneRewardPayment` — "Milestone Reward Payment" → 중간 목표 보상 지급 — no client name found; the translator's own wording.
- [client] `RefTypeText.OperationBonus` — "Operation Bonus" → 작전 보너스 — the client names this type 미션 보상 (a tutorial mission reward), which would read the same as MissionReward.
- [client] `RefTypeText.AllignmentBasedGateToll` — "Alignment-Based Gate Toll" → 팩션 소속 기반 게이트 요금 — the client says just 게이트 요금.
- [client] `RefTypeText.MarketSecurityTax`, `NpcBountySecurityTax`, `IndustrySecurityTax`, `AgentMissionSecurityTax` — "… Security Tax" → 거래소 / NPC 현상금 / 산업 / 에이전트 미션 보안 수수료 — the client's journal names are 엑조디엄 거래소 / 현상금 / 산업 / 미션 에이전트 수수료.
- [client] `RefTypeText.CosmeticMarketSkinSaleBrokerFee`, `CosmeticMarketSkinSaleTax` — → 파라곤 허브 SKIN 판매 중개 수수료 / 파라곤 허브 SKIN 판매세 — the client's journal names are 파라곤 허브 등록 서비스 비용 (or 거래 등록 비용) and 파라곤 허브 거래 비용.
- [style] `RefTypeText.BountyPrize` — "Bounty Prizes" (ESI bounty_prize) → 현상금 상품 — the client's name, set in review so it no longer reads the same as bounty_prizes (현상금); does it read well?
- [style] `RefTypeText.AllianceMaintainanceFee` → 얼라이언스 정비 비용, `ProjectDiscoveryTax` → 프로젝트 디스커버리 사납금 — the client's own wording, which reads oddly; keep or reword?

### Corporation: projects, Corp Activity, killmails

- [meaning] `CorpText.ConfigKilledBy` — "Killed By" (the losses that count are to these) → 격파한 상대 — check the sense.
- [client] `CorpText.DeliverItem`, `AlarmsText.ProjectTypeDeliverItem`, `AlarmsText.NoDeliverProjects`, `CorpText.ProjectsTitleDeliver…` — "Deliver item" → 아이템 납품 — the game's corp-project type is 아이템 배송. Likewise `CorpText.DestroyNpc`, `AlarmsText.ProjectTypeDestroyNpc` NPC 파괴 against the game's NPC 함선 파괴. Change each family together.
- [client] `CorpText.ProjectTypeMineMaterial` — "Mine Material" → 자원 채굴 — the client's "Mine Ore" is 광물 채굴.
- [client] `CorpText.RoleTopDamage` — "▲ TD" → ▲ 최고 피해 — the client says 최고 피해량; keep it short.
- [style] `CorpText.SummaryHeader`, `SummaryHeaderCorp`, `Top10ExportHeader`, `Top10ExportHeaderCorp` — "{0} {1}" (month, year) → {1}년 {0} — reads 2026년 9월 on screen.

### Alarms and Scheduler

- [meaning] `AlarmsText.CheckUndockedTooLongNote` — "…still in space — the thirty seconds after a jump" → "…점프 직후의 30초가 그런 경우입니다" — check the sense.
- [fit] `AlarmsText.After`, `AdriftStageLabel`, `SuffixSecondsUndocked` — a label, a number box and a suffix → "{0}단계: 경과 [180] 초 (도킹 해제 후)"; `OptionLowerThan` / `OptionNotLowerThan` → 다음 값 미만 / 다음 값 이상 before the box — the English order is fixed; check in the editor.

### Market and contracts

- [meaning] `MarketText.From` — "From" → 위치 — a contract's start location, and also the source station in Trade Opportunities, where it pairs with 도착지 and 출발지 would read better (one key for both).
- [client] `MarketText.Issuer`, `ColFrom`, `ContractStatusFinishedIssuer` — issuer → 발행자 — the client says 발행인 (contract From) or 개설자 (Issuer).

### Saved fittings

- [meaning] `CommonText.FitBadgePersonal` / `FitBadgeCorp` — one-letter badges "P" / "C" (a character's / a corporation's fitting) → 개 / 코 — understood? P / C instead?

### Across the app

- [client] Names kept in English in examples: `SettingsText.HintEGJita44`, `RestrictPricesToANote`, `EsiRegionFetchesAllNote`, `ABackgroundJobRefreshesNote`, `AgentHelpUserGuidance` (Jita 4-4, The Forge; the client says 지타, 포지), and the typed-name notes `AlarmsText.UndockShipsNote`, `AdriftShipsNote`, `MarketItemNote`, `IntelSystemsNote`, `MarketMarketNote`, `WorklistText.HintJitaAmarr` — the alarms match English names only, so these stay English; should the notes say 영문 이름?
- [style] Spoken lines: `SettingsText.VoiceTestSentence`, `AgentModelFailoverDefault`, `AgentModelReturnDefault`, `AgentVoiceHandoverDefault`, `AgentVoiceReturnDefault` — said aloud by the voices; do they sound natural?

### Notifications and mail

- [client] `CommsText.NotifTitleCloneRevokedMsg2` — "Clone revoked" → 클론 해지됨; `NotifTitleStructurePaintPurchased` — "Structure paint purchased" → 구조물 도색 구매 — no client wording found.
- [fit] `CommsText.FromLabel2` / `ToLabel` — "From:" / "To:" in the mail reading pane → 발신: / 수신: — shortened in review: the label column is 36 px, and 보낸 사람: / 받는 사람: need 51 (보낸이: / 받는이: 37). Natural in a mail header?
- [style] `CommsText.MailQuoteHeader` — "{0} wrote on {1}:" → "{1}, {0}님이 작성:" — goes into outgoing mail.
- [style] `CommsText.SortTypeAz` — "Type (A → Z)" → 유형 (오름차순).

### Worklist

- [client] `WorklistText.Installed` — "Installed" (originals busy in a job) → 작업 중 — the client's word is 설치됨.
- [style] `WorklistText.Blocking` — "Blocking" (how many tasks this holds up) → 막는 항목 — may read as "the items that block"; 막힌 작업 수?

### Map, Jump Planner, Structure Browser

- [client] `MapText.IncursionMobilizing` — "mobilizing" → 동원 중 — no client word; `IncursionEstablished` 침공 개시 rests on a single client entry.
- [fit] `MapText.NodeVictoryPoints` — "{0:N0}/{1:N0} VP" → 승리 포인트 {0:N0}/{1:N0} — the label moved in front of the numbers.
- [style] `MapText.AlternativeDetail` — "in {1} ly · out {2} ly" → 진입 {1}광년 · 진출 {2}광년.
- [style] `MapText.IntelNoVisual` (NV), `MapText.RangePerJump` (JDC) — player abbreviations kept in English.

### Industry

- [client] `IndustryText.CategoryMoonReactions` → 위성 합성, `RigCatGasReactions` → 가스 합성 — not found in the client, which has 복합 / 생화학 / 하이브리드 합성.
- [style] `IndustryText.CompletedBy` — "Completed By" → 완료자.

### Item Browser and assets

- [client] `AssetsText.RangeJumpsOther` — "{0} Jumps" (a buy order's range) → {0} 점프 — the glossary counts jumps as 점프 {0}회, but this is a range.
- [fit] `AssetsText.ColSec`, `CorpText.ColSec`, `MapText.ColSec` — "Sec" → 시큐 — set in review: the app cut 시큐리티 short (44 px of text in 23 px of room in the Item Browser). 시큐 as players say it, or 보안 (the same width)?
- [style] `AssetsText.TabRequiredFor` — "Required For" → 다음을 위해 필요 — the client's own tab name, odd as a tab.

### Settings, title bar, shared words

- [client] `ShellText.NavNetWorth`, `FinanceText.NetWorthLabel` — "Net Worth" → 순자산 — the client's "Total Net Worth" is 총 자산.
- [client] `SettingsText.NotePrivateConversationsAppearNote` — keeps "Private Chat (2)" in English — the client calls these 개인 채팅; what are the Korean client's log channels named?
- [client] `SettingsText.SlackNoteToSelf` — "Note to Self" → 나에게 보내는 메모 — Slack's own Korean name is unverified.
- [fit] `SettingsText.FilterLowballBuyOrdersBelow` + `OfBuildCost`, `PurchaseAComponentInstead` + `OfBuildValue`, `AtLeast` / `UpFor` + the minute notes, `SkillQueueWillBeEmpty` + `Days`, `BackfillTheLastDaysLabel` — sentences split around an input box, some reworded as "다음 …:" — check in the window.
- [fit] `FinanceText.ColBuySell` — "B/S" → 구분 — a 50-px column; the filter says 구매/판매.
- [style] `ShellText.NavOverview` → 개요 (or 대시보드?), `ShellText.NavWorklist` → 작업 목록 (may blur with 작업, an industry job).
- [style] `ShellText.NavPlayerEntities` / `NavNpcEntities`, `CharactersText.EntityNumbered` — "Entities" → 개체 — no established game word (the client's Entity is 객체).
- [style] `ShellText.ThemeLight` / `ThemeDark` and the tinted themes — 밝게 / 어둡게 (or 라이트 / 다크?).
- [style] `ShellText.FmtStaticDataExport` — "Static Data Export: {0}" → "게임 데이터 (SDE): {0}".
- [style] `SettingsText.ImportStageRaces` — "Races" → 국가 — the client's word, literally "nations".

### Sales, stores, sale posting

- [fit] `SalesText.To`, `UnitSOf`, `Per` — pieces of one row around boxes → "최대 [5] 개 — [아이템 종류별] — 기간 [1] [개월]" — does it read as one limit?
- [fit] `SalesText.Every` + `MinutesABuyerWaitsNote` → "간격 [N] 분. …", `GiveInStockOrders` + `DaySFromTheOrder` → "… [N] 일 후 (주문일 기준)" — word order forced by the layout.
- [fit] `SalesText.ColStockOvr`, `ColBuildOvr`, `ColRsrvOvr` — "STOCK OVR" … → 재고 수동 / 제조 수동 / 예약 수동 — shortened for narrow headers; clear enough?
- [style] `SalesText.MenuRestoreToProfit` — "Restore to profit" → 비영리 표시 해제.

### Background Processes, logs, AI Usage

- [fit] `DataText.ColFired` — "Fired" → 발생 수, `DataText.ColTook` — "Took" → 소요 — shortened in review from 발생 횟수 / 소요 시간, which the app cut short (37 px of room); clear enough, or 횟수 / 시간?
- [style] `DataText.GameLogFieldSource`, `…SourceShip`, `…SourceCorp`, `…SourceAlliance` — "Source …" (who acted) → 주체 … — the grid column above reads 출처 (`DataText.ColSource`, shared with other screens).

### Overview

- [style] `OverviewText.BriefBy` "by {0}" → {0}에 의해; `BriefStandingFor` "{0} {1} for {2}" → {0} {1} ({2}); `BriefDeliveredFor` "{0} for {1}" → {1}에게 {0}; `BriefCharToCorp` "{0} to {1}" → {0} → {1} — card fragments reworded because the dim English linking words do not carry over; check on a card.
