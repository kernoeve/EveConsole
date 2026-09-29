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

## Terms

| English | 한국어 | Notes |
|---|---|---|
| capsuleer | 캡슐리어 | The client's word. |
| character | 캐릭터 | The client's word. |
| corporation / corp | 코퍼레이션 | The client's word, for both; no short form. |
| alliance | 얼라이언스 | The client's word. |
| faction | 팩션 | The client's word. |
| NPC corporation | NPC 코퍼레이션 | |
| agent (NPC) | 에이전트 | The client's word. |
| AI agent / companion (this app's) | AI 도우미 | Never 에이전트, which is the NPC agent. |
| standings | 평판 | The client's word. |
| solar system | 항성계 | The client's word. |
| constellation | 성좌 | The client's word. |
| region | 지역 | The client's word. |
| station | 정거장 | The client's word. Home station = 주 정거장 (the client's). |
| structure (Upwell) | 구조물 | The client's word. Upwell = 업웰. |
| citadel | 시타델 | The client's word. |
| engineering complex | 엔지니어링 시설 | The client's word. |
| refinery | 정제소 | The client's word. |
| security status | 시큐리티 상태 | The client's word. |
| high-sec / low-sec / null-sec | 하이 시큐리티 / 로우 시큐리티 / 널 시큐리티 | The client's words; 하이섹 / 로우섹 / 널섹 in a narrow header. |
| wormhole | 웜홀 | The client's word. |
| stargate | 스타게이트 | The client's word. |
| jump / jumps | 점프 | The client's word. "5 jumps" = 점프 5회, as the client counts them. |
| jump drive / jump range | 점프 드라이브 / 점프 거리 | The client's words. |
| jump clone | 점프 클론 | The client's word. |
| capital ship | 캐피탈 함선 | The client's word. |
| ship | 함선 | The client's word. |
| hull | 선체 | The client's word. |
| fitting | 피팅 | The client's word. Saved fittings = 저장된 피팅. |
| module | 모듈 | The client's word. |
| rig | 리그 | The client's word. |
| drone | 드론 | The client's word. Fighter = 파이터. |
| charges / ammo | 차지 / 탄약 | The client's words. |
| fuel | 연료 | The client's word. Fuel block = 연료 블록. |
| item | 아이템 | The client's word. |
| type (of item) | 종류 | The client's word (아이템 종류, 함선 종류). A type id = 타입 ID. Another kind of thing (notification type, task type) = 유형. |
| group / category | 그룹 / 분류 | The client's words. |
| market group | 거래소 그룹 | |
| assets | 자산 | The client's word. |
| hangar | 격납고 | The client's word. Item hangar = 아이템 창고, ship hangar = 함선 격납고, fleet hangar = 함대 공용 창고 (the client's). |
| corp hangar | 코퍼레이션 격납고 | The client's word. |
| container | 컨테이너 | The client's word. |
| cargo hold | 화물실 | The client's word. |
| volume (of an item, m³) | 부피 | The client's word. |
| volume (traded) | 거래량 | The client's word. |
| packaged / assembled | 포장 / 조립 | The client's states are 포장 완료 / 조립 완료; packaged volume = 포장 부피. |
| blueprint | 블루프린트 | The client's word. |
| BPO / BPC (in a sentence) | 블루프린트 원본 / 블루프린트 복제본 | The client's 원본 / 복제본. A bare "BPO" label stays BPO. |
| runs (of a job or copy) | 실행 횟수 | The client's word. "10 runs" = 10회. |
| manufacturing | 제조 | The client's word. |
| reaction | 합성 | The client's word. |
| invention | 인벤션 | The client's word. |
| copying | 복제 | The client's word. |
| research | 연구 | The client's word. Science (slots, jobs) = 연구 as well; the science slots cover copying, research and invention. |
| material efficiency / time efficiency | 자원효율성 / 시간효율성 | The client's words. ME / TE stay as abbreviations. |
| industry job | 산업 작업 | The client's 산업 + 작업 (Jobs = 작업, Manufacturing Jobs = 제조 작업). A job alone = 작업. |
| job slot | 작업 슬롯 | Manufacturing / reaction / science slots = 제조 / 합성 / 연구 슬롯. |
| build cost | 제조 비용 | The client's 건설 비용 is for building structures, not for making items. |
| reprocessing / refining | 정제 | The client's word. (The wallet journal type reads 재처리 수수료.) |
| compression | 압축 | The client's word. |
| mining | 채굴 | The client's word. |
| ore / ice / moon ore | 광물 / 아이스 / 위성 광물 | The client's words (광물 저장고, 아이스 채굴기, 위성 광물). Minerals = 미네랄. |
| moon mining ledger | 위성 채굴 기록 | The client's 채굴 기록. |
| planetary industry | 행성 개발 | The client's word. |
| market | 거래소 | The client's word. |
| order (market) | 주문 | The client's word. |
| buy order / sell order | 구매 주문 / 판매 주문 | The client's words. |
| order (a customer's, in the Order Tracker) | 주문 | |
| trade hub | 무역허브 | The client's word. Trade (the app's tools) = 무역. |
| price / cost / profit / margin | 가격 / 비용 / 이익 / 이익률 | The client's 가격, 비용. |
| broker fee / sales tax | 중개 수수료 / 판매 수수료 | The client's words. |
| contract | 계약 | The client's word. |
| item exchange / courier / auction | 아이템 교환 / 운송 / 경매 | The client's words. |
| collateral / reward | 담보 / 보상 | The client's words; the sums = 담보금 / 보상금. |
| accept / reject (a contract) | 수락 / 거절 | The client's words. |
| wallet | 계좌 | The client's word. |
| journal (wallet) | 저널 | The client's word. |
| transaction | 거래 | The client's word; the wallet's Transactions list = 거래 기록 (the client's). |
| income / expense | 수익 / 지출 | The client's 수익. |
| net worth | 순자산 | The client's "Total Net Worth" is 총 자산. |
| loyalty points / LP store | 로열티 포인트 / LP 스토어 | The client's words. |
| skill / skill queue / skill points | 스킬 / 스킬 대기열 / 스킬 포인트 | The client's words. Training queue = 훈련 대기열 (the client's). |
| implant | 임플란트 | The client's word. |
| killmail | 킬 메일 | The client's word. Its Kill Report window = 킬 기록. |
| kill / loss | 킬 / 손실 | The client's 킬. |
| victim / attacker / final blow | 피해자 / 공격자 / 결정타 | The client's words (킬 기록 - 피해자, 결정타). |
| ratting | 랫질 | The client's word. |
| mission | 미션 | The client's word. |
| sovereignty | 소버린티 | The client's word. |
| undock / dock | 도킹 해제 / 도킹 | The client's words. |
| online / offline | 온라인 / 오프라인 | The client's words. |
| downtime | 일일점검 | The client's word. |
| notification | 알림 | The client's word. |
| mail / EVE mail | 메일 / EVE 메일 | The client's words. |
| channel | 채널 | The client's word. |
| intel (channel) | 인텔 | As players say it. |
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
| scope (ESI) | 권한 범위 | The client's 검색 범위 is a search's range. |
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
| war: aggressor / defender / ally | 공격 측 / 수비 측 / 동맹 | The client's words. Mutual war = 협정 전쟁; war eligible = 전쟁 가능; war declared = 전쟁 선포. |
| kill right | 사살권 | The client's word. |
| bounty / insurance payout | 현상금 / 보험금 지급 | The client's 현상금, 보험. |
| bill / broker fee / office rental | 청구서 / 중개 수수료 / 사무실 임대료 | The client's words. |
| price types Buy / Sell / Split | 구매가 / 판매가 / 중간가 | Split is the midpoint of the best buy and sell. |
| master wallet / wallet division | 주 계좌 / 계좌 구분 | The client's words. |
| transaction tax | 거래세 | The client's word (the journal type); sales tax is 판매 수수료. |
| contract statuses | 미체결 / 진행 중 / 완료 / 거절됨 / 실패 / 삭제됨 / 철회됨 / 만료됨 | outstanding / in progress / finished / rejected / failed / deleted / reversed / expired; the client's words. |
| Active (contract filter) | 활성 | Not 진행 중, which is In Progress in the same list. |
| loan (contract type) | 대여 | The client's word. |
| public / private (availability) | 공개 / 비공개 | The client's words. |
| escrow (market, contract) | 예탁금 | The client's word. |
| freelance job / project | 프리랜서 임무 | The client's word. |
| medal | 훈장 | The client's word. |
| Project Discovery | 프로젝트 디스커버리 | The client's word. |
| SKIN / Paragon Hub (cosmetic market) | SKIN / 파라곤 허브 | The client's words. |
| PLEX / New Eden Store | PLEX / 뉴에덴 스토어 | The client's words. |

## The app's own names

| English | 한국어 | Notes |
|---|---|---|
| Overview (the app's home page) | 개요 | Not 오버뷰, which is the game's in-space overview. |
| Item Browser / Structure Browser | 아이템 브라우저 / 구조물 브라우저 | |
| Universe Map | 우주 지도 | The client's word for its star map. |
| Jump Planner | 점프 플래너 | |
| Production Calc | 생산 계산기 | |
| Price Overrides | 가격 수동 지정 | |
| Industry / Trade Opportunities | 산업 기회 / 무역 기회 | |
| Market Levels | 거래소 재고 수준 | Sell-order stock kept on a market. |
| Standing Buy Orders | 상시 구매 주문 | |
| Order Tracker / Sales Tracker / Sale Posting | 주문 추적 / 판매 추적 / 판매 게시 | |
| Player / NPC Entities | 플레이어 개체 / NPC 개체 | Characters, corporations and alliances. |
| attributes (a character's) | 능력치 | The client's word: 지능 / 기억력 / 카리스마 / 지각력 / 정신력. |
| neural remap | 신경망 재배열 | The client's word; bonus remaps = 보너스 재배열. |
| client (one running copy of the app) | 클라이언트 | |
| build (a version of the app) | 빌드 | |
| Settings tabs | ESI 토큰, 캐릭터, SDE, 거래소, 가격 히스토리, 산업, 타이머, 폴링, 게임 로그, 채팅 로그, 지도 데이터, 킬 메일, "코퍼레이션 상위 10 / 요약", AI 도우미 (sub-tabs 도우미, 개인 설정, 음성 (TTS), 음성 입력 (STT), Slack), 경보, 데이터베이스, 데이터 보존, 업데이트, 기타 | Quote them exactly when a message names a tab. |
| themes Dark / Light | 어둡게 / 밝게 | As Windows names its app modes; Blue (dark) = 파랑 (어둡게). |

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
