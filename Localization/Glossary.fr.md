# Glossaire français

The terms EVE Console's French text uses, so the same word reads the same on every screen and
matches the game where the game has a word for it. "The client's word" means the French EVE client
uses it (read from the client's own text). Corrections from French-speaking players are welcome —
this list is where they belong, so every screen picks them up.

## Style

- French for a desktop app, addressing the user as **« vous »**, as the French client does. Buttons
  and menu items are infinitives (« Enregistrer », « Ajouter »); tooltips and messages are complete
  sentences with a full stop; labels, buttons and column headers take no full stop.
- **Sentence case** everywhere: only the first word and proper names take a capital (« Ajouter des
  objets depuis un montage », not « Ajouter Des Objets »). English in capitals is a small heading:
  write it in normal case (« Quantité cible »).
- **Non-breaking space (U+00A0)** — the character itself, never `&nbsp;` — before `:` `;` `!` `?`
  and `»`, after `«`, before `%`, and between a number and its unit (`{0} j`, `{0} PC`, `{1} %`),
  as the client writes « Station : … » and « 25 000 PC ». A label ending in `:` keeps it: « Personnage : ».
- Quotation marks « … » (with the non-breaking spaces inside). Apostrophe: the straight `'`, as in
  most of the client.
- **Placeholders stay exactly as written**: `{0}`, `{1:N0}`, `{alarm}`. They may move in the sentence;
  none may be dropped or added. Numbers are formatted by the placeholder, never by hand.
- **Unchanged**: EVE Console, EVE, ESI, SDE, SSO, CCP, ISK, m³, ME, TE, zKillboard, Fuzzwork, Slack,
  Discord, GitHub, Tranquility, file names and formats, URLs and anything typed exactly
  (`yyyy-MM-dd`, scope names). Keyboard keys stay as in English (Enter, Ctrl+C) — AZERTY keyboards
  print « Entrée », « Échap », « Suppr »: a reviewer may prefer those.
- Client abbreviations: SP → **PC** (points de compétence), LP → **PL** (points de loyauté),
  NPC → **PNJ**. A bare « BPO » / « BPC » label stays.
- Dates: day before month, 24-hour clock: `d MMM yyyy`, `d MMM HH:mm`. Durations: `j`, `h`, `min`, `s`.
  Sizes in running text: Mo / Go (not MB / GB); decimals with a comma (« 1,5 Go », « 1,25× »).
- Counted words the code cannot inflect keep the English habit of « (s) »: « {0} ligne(s) »,
  « canal(aux) ». Past participles agree with the noun they describe (« {0} table(s) effacée(s) »).
- Headers are narrow: prefer the shorter of two good words; abbreviate with a full stop (« Qté », « Prod. »).
- Gender and number agree with the noun a placeholder stands for; when it cannot be known, rephrase
  (« Statut : {0} ») rather than guess.

## Terms

| English | Français | Notes |
|---|---|---|
| capsuleer | capsulier | The client's word. |
| character | personnage | The client's word. |
| corporation / corp | corporation | The client's word, for "corp" too (short headers may use « Corpo. » only if needed). |
| alliance | alliance | The client's word. |
| faction | faction | The client's word. |
| NPC corporation | corporation PNJ | PNJ = personnage non joueur, the client's abbreviation. |
| agent (NPC) | agent | The client's word. |
| AI agent / companion (this app's) | assistant IA | « Agent » is kept for the game's NPC agents. |
| standings | réputation(s) | The client's word (« Réputations »). |
| solar system | système solaire / système | The client's word. |
| constellation | constellation | The client's word. |
| region | région | The client's word. |
| station | station | The client's word. |
| structure (Upwell) | structure | The client's word. |
| citadel | citadelle | The client's word. |
| engineering complex | complexe d'ingénierie | The client's word. |
| refinery | raffinerie | The client's word. |
| security status | statut de sécurité | The client's word. |
| high-sec / low-sec / null-sec | haute sécurité / basse sécurité / sécurité nulle | The client's words. |
| wormhole | trou de ver | The client's word. |
| stargate | portail stellaire | The client's word. |
| jump / jumps | saut / sauts | The client's word. "5 jumps" = 5 sauts. |
| jump drive / jump range | propulseur hyperspatial / portée de saut | The client's words. |
| jump clone | clone hyperspatial | The client's word. |
| capital ship | vaisseau capital | The client's word (« Vaisseaux capitaux »). |
| ship | vaisseau | The client's word. |
| hull | coque | A ship's hull. See also the hit-point layer below. |
| fitting | montage | The client's word (fit = montage). |
| module | module | The client's word. |
| rig | optimisation (module d'optimisation) | The client's word. |
| drone | drone | The client's word. |
| charges / ammo | charges / munitions | The client's words. |
| fuel | carburant | The client's word. |
| item | objet | The client's word. |
| type (of item) | type | The client's word. |
| group / category | groupe / catégorie | The client's words. |
| market group | groupe de marché | The client's word. |
| assets | biens | The client's word. |
| hangar | hangar | The client's word. |
| corp hangar | hangar de corporation | The client's word (« Hangar de la corporation »). |
| container | conteneur | The client's word. |
| cargo hold | soute | The client's « Soute de chargement », shortened. |
| volume (of an item, m³) | volume | The client's word. |
| volume (traded) | volume échangé | |
| packaged / assembled | emballé / assemblé | The client's words. |
| blueprint | plan de construction (plan) | The client's word; « plan » alone where space is short. |
| BPO / BPC (in a sentence) | original / copie de plan de construction | The client's « Copie de plan de construction ». A bare "BPO" label stays BPO. |
| runs (of a job or copy) | cycles | The client's « Cycles restants ». "10 runs" = 10 cycles. |
| manufacturing | production | The client's word. |
| reaction | réaction | The client's word. |
| invention | invention | The client's word. |
| copying | copie | The client's word. |
| research | recherche | The client's word. |
| material efficiency / time efficiency | efficacité matérielle / gain de temps | The client's words. ME / TE stay as abbreviations. |
| industry job | travail d'industrie (pl. travaux) | The client's « Travaux » (Industry window). Distinct from task (tâche). |
| job slot | créneau | The client's word (« créneau d'usine »): créneaux de production / de réaction / scientifiques. |
| build cost | coût de construction | The client's word. |
| reprocessing / refining | retraitement / raffinage | The client's words. |
| compression | compression | The client's word. |
| mining | extraction minière | The client's word. |
| ore / ice / moon ore | minerai / glace / minerai lunaire | The client's words (its « ORE » is the corporation). |
| moon mining ledger | registre d'extraction lunaire | The client's « Registre d'extraction ». |
| planetary industry | industrie planétaire | The client's word. |
| market | marché | The client's word. |
| order (market) | ordre | The client's word. |
| buy order / sell order | ordre d'achat / ordre de vente | The client's words. |
| order (a customer's, in the Order Tracker) | commande | Distinct from a market ordre. |
| trade hub | nœud commercial | The client's word (« Nœud commercial de Jita »). |
| price / cost / profit / margin | prix / coût / bénéfice / marge | |
| broker fee / sales tax | frais de courtage / taxe de vente | The client's words. |
| contract | contrat | The client's word. |
| item exchange / courier / auction | troc / courrier / enchère | The client's words (contract types). |
| collateral / reward | caution / récompense | The client's words. |
| accept / reject (a contract) | accepter / refuser | The client's words. |
| wallet | portefeuille | The client's word. |
| journal (wallet) | journal | The client's word. |
| transaction | transaction | The client's word. |
| income / expense | revenus / dépenses | The client's « Revenus ». |
| net worth | valeur nette | The client's word (« Valeur nette totale »). |
| loyalty points / LP store | points de loyauté (PL) / magasin PL | The client's words. |
| skill / skill queue / skill points | compétence / file d'attente de compétences / points de compétence (PC) | The client's words. Training queue = file d'apprentissage. |
| implant | implant | The client's word. |
| killmail | rapport de victime | The client's « Rapport de victimes » (Kill Report). |
| kill / loss | destruction / perte | The client's words (« historique de destructions et de pertes »). |
| victim / attacker / final blow | victime / assaillant / coup de grâce | The client's words. |
| ratting | chasse aux PNJ | The client has no word (its candidate is another sense); players also say « ratting ». |
| mission | mission | The client's word. |
| sovereignty | souveraineté | The client's word. |
| undock / dock | appareiller / amarrer (appareillage / amarrage) | The client's words. |
| online / offline | en ligne / hors ligne; a character: connecté / déconnecté | The client's words. |
| downtime | maintenance | The client's word (« après la maintenance »). |
| notification | notification | The client's word. |
| mail / EVE mail | courrier / messagerie d'EVE (one mail: courrier EVE) | The client's words. |
| channel | canal | The client's word. |
| intel (channel) | renseignements (canal de renseignements) | |
| hauling / haul / trip | transport / transport / trajet | The client's « Transport ». |
| worklist | liste de tâches | |
| Indy Park (this app's) | parc industriel | |
| inventory level / stock / on hand | niveau de stock / stock / disponible | |
| store (this app's shop) | boutique | The client's « magasin » is kept for the LP store and the New Eden Store. |
| buyer | acheteur | The client's word. |
| alarm / alert | alarme / alerte | The client's « Alerte ». |
| scheduler / report | planificateur / rapport | The client's candidate « Signaler » is the verb (report a player). |
| background process | processus d'arrière-plan | |
| polling | collecte (collecter) | The app's periodic ESI requests: « collecte ESI », « données collectées ». |
| token | jeton | |
| scope (ESI) | autorisation | Scope names themselves stay as written (esi-fittings.read_fittings.v1). |
| settings / refresh / save / cancel | paramètres / actualiser / enregistrer / annuler | The client's words. |
| add / remove / delete / edit | ajouter / retirer / supprimer / modifier | The client's words. |
| export / import / filter / search / clear | exporter / importer / filtrer / rechercher / effacer | The client's words. |
| apply / OK / close / copy | appliquer / OK / fermer / copier | The client's words. |
| task (a Worklist row) | tâche | Distinct from job (travail). |
| hull (hit-point layer, beside shield and armour) | structure | bouclier / blindage / structure, as the game shows them; coque stays for a ship's hull. |
| CONCORD | CONCORD | The client's word. |
| starbase / control tower | base stellaire / tour de contrôle | The client's words. |
| reinforced / reinforcement | renforcé / renforcement | The client's words. "Reinforced until" → « Renforcé jusqu'au ». |
| anchoring / unanchoring | ancrage / détachement | The client's words (verbs: ancrer / désamarrer). |
| high power / low power (Upwell) | haute puissance / basse puissance | The client's words. |
| moon extraction / fracture / moon drill | extraction lunaire / fracture / foreuse lunaire | The client's words. |
| war: aggressor / defender / ally | agresseur / défenseur / allié | The client's words. Mutual war: guerre mutuelle; war eligible: apte à la guerre. |
| kill right | permis de détruire | The client's word. |
| bounty / insurance payout | prime / indemnité d'assurance | The client's « Prime », « Assurance ». |
| bill / broker fee / office rental | facture / frais de courtage / location de bureau | The client's words. |
| price types Buy / Sell / Split | Achat / Vente / Médian | Split is the midpoint of the best buy and the best sell (« prix médian »). |
| master wallet / wallet division | portefeuille principal / division du portefeuille | The client's words. |
| transaction tax | taxe de transaction | The client's word (the journal type); sales tax is taxe de vente. |
| contract statuses | en attente / en cours / terminé / refusé / échoué / supprimé / remboursé / expiré | outstanding / in progress / finished / rejected / failed / deleted / reversed / expired; the client's words except reversed (its journal says « Remboursement du contrat »). |
| Active (contract filter) | actifs | Not « en cours », which is In Progress in the same list. |
| loan (contract type) | prêt | The client's word. |
| public / private (availability) | public / privé | The client's words. |
| escrow (market, contract) | dépôt fiduciaire | The client's word. |
| freelance job / project | mission freelance / projet | The client's words (« Missions freelance », « Projets de corporation »). |
| medal | médaille | The client's word. |
| Project Discovery | Project Discovery | The client's word. |
| SKIN / Paragon Hub (cosmetic market) | SKIN / Centre Paragon | The client's words. |
| PLEX / New Eden Store | PLEX / Magasin New Eden | The client's words. |

## The app's own words (settled in wave 1)

Names of screens, tabs and ideas of EVE Console itself, as the navigation and Settings now show
them — other screens that mention them should use the same words.

| English | Français | Notes |
|---|---|---|
| Overview (the app's home) | vue d'ensemble | The client's « Vue d'ensemble »; its in-space overview is « Écran radar ». |
| Item Browser / Structure Browser | explorateur d'objets / explorateur de structures | |
| Inventory Levels / Market Levels | niveaux de stock / niveaux de marché | |
| Universe Map / Jump Planner | carte de l'univers / planificateur de sauts | |
| Industry Jobs / Indy Parks | travaux d'industrie / parcs industriels | |
| Production Calc / calculator | calcul de production / calculateur | |
| Price Overrides (an override) | prix personnalisés (un prix personnalisé) | |
| Industry / Trade Opportunities | opportunités industrielles / commerciales | |
| Market Overview / Item Valuation | aperçu du marché / estimation d'objets | |
| LP Market Values | valeur marchande des PL | |
| standing (= permanent): Standing Buy Orders, Standing Projects, standing instructions | ordres d'achat permanents, projets permanents, instructions permanentes | Not « réputation », which is standings with factions. |
| Order Tracker / Sales Tracker | suivi des commandes / suivi des ventes | |
| Sale Posting / Sale Listing | annonces de vente / liste de vente | |
| Stores / Net Worth / Income & Expense | boutiques / valeur nette / revenus et dépenses | |
| Corp Activity → Projects | activité de la corporation → projets | |
| Killmails (screen and tab) | rapports de victimes | |
| Player / NPC Entities | entités joueurs / entités PNJ | |
| Eve Mail (screen) | messagerie d'EVE | |
| Background Processes / ESI Explorer / Error Log / AI Usage | processus d'arrière-plan / explorateur ESI / journal des erreurs / utilisation de l'IA | |
| Game Log / Chat Log | journal de jeu / journal de discussion (pl. journaux) | |
| Alarms; an armed alarm | alarmes; une alarme armée | |
| Settings tabs | Jetons ESI, Personnages, SDE, Marché, Historique des prix, Industrie, Intervalles (Timers), Collecte (Polling), Journaux de jeu, Journaux de discussion, Données de carte, Rapports de victimes, Top 10 corporation / Résumé, Assistant IA, Assistant, Personnalisation, Voix (TTS), Saisie vocale (STT), Slack, Alertes, Base de données, Conservation des données, Mises à jour, Autres | Other text builds paths from these (« Paramètres → Assistant IA → Saisie vocale (STT) »). |
| auth character | personnage authentifié | |
| endpoint (ESI) | point d'accès | |
| backfill | rattrapage (rattraper) | |
| sighting (intel) | signalement | |
| facility (industry) | installation | The client's word. |
| outbid | surenchéri (la surenchère) | |
| Asset Safety | système de protection des biens | The client's word. |
| Market Orders (Item Browser tab) | ordres du marché | |
| price source methods: Fuzzwork / Region / Player Structure | Fuzzwork / Région / Structure de joueur | |
| UI scale | échelle de l'interface | |
| token (AI model) / prompt | token / prompt | « jeton » stays for ESI and Slack tokens. |
| AI model roles: conversation / data / summaries; falling over / coming back | conversation / données / résumés; bascule / retour | |
| client (one running copy of the app) / build | client / version | |
| worker (the client doing the background work) | le client qui effectue le travail d'arrière-plan; the Windows or systemd one: le service | |
| Dogma | Dogma | CCP's name for the attribute system, kept (the client once says « Dogme »). |
| channel(s) | canal (pl. canaux) | « canal(aux) » when counted in code. |
| alt / main (character) | alt / personnage principal | |
