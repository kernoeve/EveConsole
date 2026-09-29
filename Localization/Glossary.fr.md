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
  The short forms already chosen for columns and labels the app cuts short are listed under
  "Short forms" below: reuse them, and measure a new one before shortening it further.
- Gender and number agree with the noun a placeholder stands for; when it cannot be known, rephrase
  (« Statut : {0} ») rather than guess.
- **Status and filter words agree with the noun they stand for**, shown or not: an order (commande)
  is « Terminée », « Annulée »; a job (travail) « Terminé »; a filter over orders offers « Actives »,
  « Terminées », « Toutes »; a count of items agrees with « objets » (« Terminés », « Annulés »).
  So the same English word may rightly read two ways on two screens.
- **A number in place of an unknown name**: « Compétence n° {0} », « Groupe n° {0} » — « n° » then
  U+00A0 — never « #{0} ».
- **Wallet journal types** (`RefTypeText`) are the French client's own journal names, word for word,
  even where they differ from the terms below (« Recherche efficacité de matériaux », « Frais de
  maintenance - Alliance »). Where the client has none, build one on its pattern (« … - Agent »,
  « … - Contrat », « … du centre Paragon »). ESI's flux_* types are the HyperNet Relay.
- ESI scope names read « Lire les … » / « Modifier les … ».
- When the code builds a line from pieces (a row of controls, a tooltip that adds a value), a piece
  may carry its own preposition or article (« de chaque type d'objet », « de production »,
  « les réactions »): read the key's comment before shortening one.

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
| volume (traded) | volume échangé | « Vol. échangé » in a narrow column. |
| packaged / assembled | emballé / assemblé | The client's words. "Packaged only" = « emballés uniquement ». |
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
| skill / skill queue / skill points | compétence / file d'attente de compétences / points de compétence (PC) | The client's words. Training queue = file d'apprentissage, also the client's short name for the skill queue (the Worklist's task kind). A narrow column says « File compét. ». |
| implant | implant | The client's word. |
| killmail | rapport de victime | The client's « Rapport de victimes » (Kill Report). |
| kill / loss | destruction / perte | The client's words (« historique de destructions et de pertes »). |
| victim / attacker / final blow | victime / assaillant / coup de grâce | The client's words. Short headers: « CG » (coup de grâce), « TD » (the client's « Top dégâts »). |
| ratting | chasse aux PNJ | The client has no word (its candidate is another sense); players also say « ratting ». |
| mission | mission | The client's word. |
| sovereignty | souveraineté | The client's word. |
| undock / dock | appareiller / amarrer (appareillage / amarrage) | The client's words. |
| online / offline | en ligne / hors ligne; a character: connecté / déconnecté | The client's words. |
| downtime | maintenance | The client's word (« après la maintenance »). |
| notification | notification | The client's word. |
| mail / EVE mail | courrier / messagerie d'EVE (one mail: courrier EVE) | The client's words. |
| mail folders / compose / sender | Boîte aux lettres, Envoyés / rédiger / expéditeur | The client's « Boîte aux lettres » (Inbox) and « Expéditeur ». |
| channel | canal | The client's word. |
| intel (channel) | renseignements (canal de renseignements) | |
| hauling / haul / trip / stop / pickup / manifest | transport / transport / trajet / arrêt / enlèvement / manifeste | The client's « Transport ». |
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
| export / import / filter / search / clear | exporter / importer / filtrer / rechercher / effacer | The client's words. The noun is « import » (« Échec de l'import : {0} »). |
| apply / OK / close / copy | appliquer / OK / fermer / copier | The client's words. |
| task (a Worklist row) | tâche | Distinct from job (travail). |
| hull (hit-point layer, beside shield and armour) | structure | bouclier / blindage / structure, as the game shows them; coque stays for a ship's hull. |
| CONCORD | CONCORD | The client's word. |
| starbase / control tower | base stellaire / tour de contrôle | The client's words. |
| reinforced / reinforcement | renforcé / renforcement | The client's words. "Reinforced until" → « Renforcé jusqu'au ». |
| anchoring / unanchoring | ancrage / détachement | The client's words (verbs: ancrer / désamarrer; unanchored: désamarré(e)). |
| high power / low power (Upwell) | haute puissance / basse puissance | The client's words. |
| moon extraction / fracture / moon drill | extraction lunaire / fracture / foreuse lunaire | The client's words. |
| war: aggressor / defender / ally | agresseur / défenseur / allié | The client's words. Mutual war: guerre mutuelle; war eligible: apte à la guerre. |
| war HQ | QG de guerre | |
| kill right | permis de détruire | The client's word. |
| bounty / insurance payout | prime / indemnité d'assurance | The client's « Prime », « Assurance ». |
| bill / broker fee / office rental | facture / frais de courtage / location de bureau | The client's words. |
| price types Buy / Sell / Split | Achat / Vente / Médian | Split is the midpoint of the best buy and the best sell (« prix médian », « prix médians »). |
| master wallet / wallet division | portefeuille principal / division du portefeuille | The client's words. |
| transaction tax | taxe de transaction | The client's word (the journal type); sales tax is taxe de vente. |
| industry tax | taxe industrielle | The client's word. A narrow Corp Activity column shortens it to « Taxe industrie ». |
| contract statuses | en attente / en cours / terminé / refusé / échoué / supprimé / remboursé / expiré | outstanding / in progress / finished / rejected / failed / deleted / reversed / expired; the client's words except reversed (its journal says « Remboursement du contrat »). |
| Active (contract filter) | actifs | Not « en cours », which is In Progress in the same list. |
| contract issuer / assignee / acceptor | émetteur / assigné à / accepté par | The Historical filter is « Clos »; its acceptor filter « Tous les acceptants ». |
| buyout (auction) | rachat | The client's word. |
| loan (contract type) | prêt | The client's word. |
| public / private (availability) | public / privé | The client's words. |
| escrow (market, contract) | dépôt fiduciaire | The client's word. |
| freelance job / project | mission freelance / projet | The client's words (« Missions freelance », « Projets de corporation »). |
| medal | médaille | The client's word. |
| Project Discovery | Project Discovery | The client's word. |
| SKIN / Paragon Hub (cosmetic market) | SKIN / centre Paragon | The client's words; its journal writes « Achat de SKIN du centre Paragon ». A design element (SKINR) is an « élément de design ». |
| PLEX / New Eden Store | PLEX / Magasin New Eden | The client's words. |
| HyperNet Relay / HyperNode / HyperNet offer | relais Hypernet / Hyper nœud / offre Hypernet | The client's words. ESI calls it Flux: the flux_* journal types are the client's HyperNet names (« Coût du relais Hypernet »). |
| ESS | SSA | The client's abbreviation. |
| LP offer: required items | objets requis | The client's LP store column. |
| security tags | insignes de sécurité | The client's item group (its journal description says « balises »). |
| light year (ly) | année-lumière (a.l.) | The client's words: « 5,00 a.l. », with U+00A0. |
| waypoint / route | étape / itinéraire | The client's words. |
| jump fatigue | épuisement de saut | The client's word. |
| jump bridge (Upwell) | pont interstellaire | The client's word. A titan's bridge: « pont de titan », « pont » in running text. |
| Faction Warfare | guerre de factions (GF) | The client's words (« Complexes GF »). Systems: capturé / contesté / non contesté / vulnérable; occupier: occupant. |
| incursion / staging system | incursion / système concerné | The client's words; the map caption shortens it to « concerné ». States: établie / en mobilisation / en retraite. |
| Activity Defense Multiplier | ADM | Kept as players write it; the client only spells it out (« Multiplicateur de défense d'activité »). |
| cost index (industry) | indice de coût | The client's word. |
| customs office | bureau de douane | The client's word. |
| clone bay | centre médical | The client's word. |
| freighter / jump freighter | transport de fret / transport de fret hyperspatial | The client's words. |
| turret / launcher / shuttle / pod | tourelle / lanceur / navette / capsule | The client's words. « Pod » stays where the user types it in a filter. |
| fuel bay / tethered | cuve de carburant / arrimé | The client's words (for tethered it also says « accosté », « connecté »). |
| supercapital / capital / subcapital | supercapital / capital / sous-capital (pl. -aux) | |
| slots: high / mid / low / rig / service / subsystem | emplacement haute puissance / intermédiaire / basse puissance / d'optimisation / de service / de sous-système | The client's words. The fitting ring's short labels: Haute / Interm. / Basse / Optim. / Services / Sous-syst. |
| reaction formula / datacore / decryptor | formule / banque de données / décrypteur | The client's words. |
| raw materials / intermediates / reagents | matières premières / intermédiaires / réactifs | The client's words for raw materials and reagents. |
| reprocess / decompress | retraiter / décomprimer | The client's words. |
| Asset Safety Wrap | emballage de sécurité des biens | The client's word. |
| stack (of items) | tas | The client's « Diviser le tas ». Location flag: position; singleton: non empilable. |
| industry job statuses | actif / en pause / prêt / livré / annulé / rétabli | The client's words (rétabli = reverted). Activities: Recherche ME, Recherche TE, rétro-ingénierie. |
| expert system | système expert | The client's word. |
| director / accountant (roles) | directeur / comptable | The client's words. |
| corp application / applicant | candidature / candidat | The client's « Candidature ». |
| ticker / CEO / bloodline / headquarters | indicatif / PDG / lignée / quartier général | The client's words. Executor corporation: corporation exécutrice; locator: localisation. |
| Planetary Interaction | interaction planétaire | The client's word. |
| Standup service modules | Standup Manufacturing Plant, Standup Capital Shipyard, Standup Supercapital Shipyard, Standup Research Lab, Standup Invention Lab (kept in English); Usine de retraitement 'Standup', Centre de clonage 'Standup', Centre d'affaires 'Standup', Réacteur biochimique / composite / hybride Standup | The client's names, word for word, mixed as the client has them: players see them on their structures. A reactor in general: « réacteur Standup ». |

## The app's own words (settled in waves 1 to 3)

Names of screens, tabs and ideas of EVE Console itself, as the navigation and Settings now show
them — other screens that mention them should use the same words.

| English | Français | Notes |
|---|---|---|
| Overview (the app's home) | vue d'ensemble | The client's « Vue d'ensemble »; its in-space overview is « Écran radar ». |
| Item Browser / Structure Browser | explorateur d'objets / explorateur de structures | |
| Asset Browser / entity browser | explorateur de biens / explorateur d'entités | |
| Inventory Levels / Market Levels | niveaux de stock / niveaux de marché | |
| Universe Map / Jump Planner | carte de l'univers / planificateur de sauts | |
| Industry Jobs / Indy Parks | travaux d'industrie / parcs industriels | |
| Production Calc / calculator | calcul de production / calculateur | |
| Price Overrides (an override) | prix personnalisés (un prix personnalisé) | A "… OVR" column: « … perso. » (Stock perso., Prod. perso.). |
| Industry / Trade Opportunities | opportunités industrielles / commerciales | |
| Market Overview / Item Valuation | aperçu du marché / estimation d'objets | Appraise: estimer / estimation. |
| LP Market Values | valeur marchande des PL | |
| standing (= permanent): Standing Buy Orders, Standing Projects, standing instructions | ordres d'achat permanents, projets permanents, instructions permanentes | Not « réputation », which is standings with factions. |
| Order Tracker / Sales Tracker | suivi des commandes / suivi des ventes | |
| Sale Posting / Sale Listing | annonces de vente / liste de vente | One posting: une annonce (« Annonce de vente » as a section type). |
| Stores / Net Worth / Income & Expense | boutiques / valeur nette / revenus et dépenses | Cashflow: flux de trésorerie. |
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
| Asset Safety | système de protection des biens | The client's word, on every screen. The Worklist's task kind says « Protection des biens »: its Type filter holds 114 px. |
| Market Orders (Item Browser tab) | ordres du marché | |
| price source methods: Fuzzwork / Region / Player Structure | Fuzzwork / Région / Structure de joueur | A price source is « une source de prix » (« Activée »). |
| UI scale | échelle de l'interface | |
| token (AI model) / prompt | token / prompt | « jeton » stays for ESI and Slack tokens. |
| AI model roles: conversation / data / summaries; falling over / coming back | conversation / données / résumés; bascule / retour | |
| client (one running copy of the app) / build | client / version | |
| worker (the client doing the background work) | le client qui effectue le travail d'arrière-plan; the Windows or systemd one: le service | |
| Dogma | Dogma | CCP's name for the attribute system, kept (the client once says « Dogme »). |
| channel(s) | canal (pl. canaux) | « canal(aux) » when counted in code. |
| alt / main (character) | alt / personnage principal | Market alt: alt de marché. |
| scope (what a view, an import or a rule covers: which characters, corporations, stations) | périmètre | Asset Browser, Worklist, Inventory Levels, zKillboard import. « Portée » is a market order's or a jump's range. |
| a column of dates headed "When" | Date | |
| Change (a signed change in a value) | variation | Monthly Summary columns: Poste / Montant / Variation / %. |
| Sales ISK | ISK des ventes | The chart line and the column alike. |
| Chart by; grains Daily / Weekly / Monthly | Regrouper par; Jour / Semaine / Mois | Backup intervals are « Tous les jours / Toutes les semaines / Tous les mois ». |
| Alarms: check / fire / acknowledge / cooldown / stage | condition / se déclencher (déclenchement) / acquitter / délai de réarmement / étape | |
| Continuous / One shot; mute; wake-up call | En continu / Une seule fois; mettre en sourdine; réveil | |
| placeholders (in a message) | variables | |
| intel report; NV (no visual) | rapport de renseignements; NV | « NV » as players write it; spoken: « pas de visuel ». |
| post (to Slack); post block; render (a chart) | publier; bloc de publication; générer | « Publier sur Slack », « Publié(e) sur {0} ». |
| store order / label (on an order or sale) | commande de boutique / étiquette | |
| mail command (PRICES, ORDER…) / usage message | mot-clé / message d'aide (the Usage tab: Aide) | « Commande » is a customer's order. |
| shop front / markup / tag / callback URL | vitrine / mise en forme / balise / URL de rappel | |
| sale price basis | base du prix de vente: Construction / Prix des contrats / Marché spécifique | |
| not for profit / restore to profit | sans but lucratif / réintégrer aux bénéfices | |
| Worklist: snooze / raise (a task) | reporter / créer | |
| slot pool / bottleneck / all-busy share | groupe de créneaux / goulot d'étranglement / saturation | |
| buffer / cover / ceiling / surge / wave | stock tampon (tampon) / couverture / plafond / pic / vague | |
| print (a blueprint or formula) | plan | |
| on order (market) | en cours d'achat | |
| Item Contention / Final Products / Inventory Rules / Station Levels | Pénuries d'objets / Produits finaux / Règles de stock / Niveaux par station | |
| leftover / shopping list / slot days | surplus / liste d'achats / jours-créneau | |
| catch-all facility / production assignments / item exceptions / unlink | installation fourre-tout / attributions de production / exceptions par objet / délier | |
| installer / success chance | installateur / probabilité de réussite | |
| collection / target (total) / avail / diff | collection / cible (total cible) / dispo. / écart | |
| ratting tax / donations / donor | taxe de chasse aux PNJ / dons / donateur | A narrow column: « Taxe chasse PNJ ». |
| ISK efficiency / net / net position | efficacité ISK / solde / solde net | |
| speech out / speech in / rates | synthèse vocale / saisie vocale / tarifs | |
| zKillboard: firehose / interval poll / gap-fill / daily dumps | flux (« Flux continu ») / collecte périodique / comblement / archives quotidiennes | |
| sweep / idle / Name Cache | balayage / en veille / cache des noms | |
| ESI Call Schedule / Order Fulfilment / Online Status / Member Tracking | planning des appels ESI / exécution des commandes / état de connexion / suivi des membres | |
| map overlay / true security | calque / sécurité réelle | |
| midpoint / Jump through | escale / Escales autorisées | |
| victory points | PV (points de victoire) | |
| "any" (a filter hint) | indifférent | |
| Cloudflare and other third-party screens | kept in English | Workers & Pages, Create Token, Client ID…, as the site shows them. |

## Short forms

The shortened wordings of columns and labels the app laid out narrower than the full text
(measured in the app's own fonts). Where the full form has room, it stays; where a column is this
narrow, use these rather than inventing another.

| English | Full form | Short form | Notes |
|---|---|---|---|
| Build cost | coût de construction | Coût de constr. / Coût constr. | « Coût constr. » only in the narrowest columns (Sale Posting). |
| Build price | prix de construction | Prix de constr. | |
| Build value | valeur de construction | Val. constr. | |
| Market value | valeur marchande | Val. marché | |
| Profit / Profit % | bénéfice / bénéfice % | Bénéf. / Bénéf. % | U+00A0 before %. Not « Marge », which is Margin. |
| Short value / short volume | valeur manquante / volume manquant | Val. manquante / Vol. manquant | |
| Pod value | valeur capsule | Val. capsule | |
| Buy orders | ordres d'achat | Ord. d'achat | |
| Skill queue | file d'attente de compétences | File compét. | « File d'attente » alone could be read as an industry queue. |
| Skill Queue (Worklist task kind, Type filter) | file d'attente de compétences | File d'apprentissage | The client's short name for the skill queue; 105 px of the filter's 114. |
| Asset Safety (Worklist task kind, Type filter) | système de protection des biens | Protection des biens | 107 px of the filter's 114; the tooltip and the source keep the full name. |
| Why (Worklist column) | pourquoi | Motif | |
| Sender type / owner type | type d'expéditeur / type de propriétaire | Type d'expéd. / Type de propr. | |
| Trigger below (%) | déclencher sous (%) | Seuil (%) | « Seuil » is the client's word for threshold; a rule editor's field keeps the full label. |
| Order # | n° de commande | Commande | The column holds the numbers. |
| Req (quantity requested) | demandé | Dem. | Beside « Produit ». |
| vs best | vs meilleur | Écart | Its cells read « meilleur » or the gap in %. |
| Slot days | jours-créneau | Jour-créneau | The unit, as « Bénéf./jour-créneau » writes it. |
| Pri | priorité | Prio | No full stop: « Prio. » does not fit its 21 px column. |
| Success chance | probabilité de réussite | Taux de réussite | |
| Last remap | dernière reprogrammation | Dern. reprogrammation | Keeps the client's « reprogrammation ». |
| Jump Drive Calibration / Jump Fuel Conservation (skills, as Jump Planner labels) | Étalonnage du propulseur hyperspatial / Économie de carburant de saut hyperspatial | Étalonnage du propulseur / Économie de carburant de saut | The client's skill names without « hyperspatial ». |

## For a native speaker to check

Open questions from the translators and the review, most important first; each line is the entry
(`Area.Key`, the area without "Text"), the English, the draft, and the doubt.

### 1. Meaning guessed

**Wallet journal types**
- `RefType.AllignmentBasedGateToll` — "Alignment-Based Gate Toll" → « Péage de portail selon l'alignement » — no client journal name; guessed from ESI's key (probably the Zarzakh gate toll, the client's « Péage du portail stellaire de Zarzakh »).
- `RefType.OperationBonus` — "Operation Bonus" → « Bonus d'opération » — no client name; what "operation" means was not found.
- `RefType.MarketProviderTax` — "Market Provider Tax" → « Taxe du fournisseur de marché » — no client name; sense guessed.
- `RefType.CosmeticMarketSkinSaleBrokerFee`, `CosmeticMarketSkinSaleTax` → « Frais d'annonce au centre Paragon », « Frais de vendeur au centre Paragon » — the client's two Paragon Hub fee names, paired with ESI's broker fee and sale tax by meaning (listing fee = broker fee); they may be the other way round.
- `RefType.FluxPayout`, `FluxTax`, `FluxTicketRepayment`, `FluxTicketSale` → « Crédit relais Hypernet terminé », « Coût du relais Hypernet », « Remboursement d'Hyper nœud pour l'offre Hypernet arrivée à expiration », « Transaction d'Hyper nœuds » — the client's HyperNet journal names word for word, paired with the four flux_* types by meaning.
- `RefType.SecurityProcessingFee` — "Security Processing Fee" → « Frais de traitement d'insignes de sécurité » — the client's journal describes it as a fee for processing security tags; the client names the tags « insignes » but « balises » in that description.

**Sales, Overview, Worklist**
- `Sales.BookIt`, `Sales.OutcomeBooked` — "Book it" / "Booked" → « Accepter » / « Acceptée » — taking a web-store order rendered as accepting it.
- `Overview.ColOrderJobs`, `Worklist.ColOrderJobs` — "Order jobs" → « Commandes » — the jobs that build customer orders; « Commandes » alone may read as a count of orders (80–110 px columns).
- `Overview.BriefCharToCorp` — "{0} to {1}" → « {0} pour {1} » — has to read right for an application to a corporation and for an invitation into one.
- `Worklist.BlueprintVerdictContended`, `BlueprintVerdictBuilding`, `ContentionVerdictHolding` — "Contended" / "Building" / "Holding" → « Disputé » / « Accumulation » / « Stable » — verdict words; each key's comment says what it means.

**Data, Finance, Common**
- `Data.HistoryFilling` — "Filling" → « Remplissage » — a region's price history is partly refreshed, partly still queued.
- `Finance.AutoRange` — "Auto Range" → « Échelle auto » — switches the chart's Y-axis minimum between automatic and zero.
- `Common.PriceTypeSplit` — "Split" → « Médian » — halfway between the best buy and the best sell; may be read as a statistical median (« Moyen », « Mi-chemin »?).

### 2. Different from the client's word

- `Settings.NotePrivateConversationsAppearNote` — keeps « Private Chat (2) », « Private Chat (3) » — the French client says « Discussion privée »: check what a French client names private-chat channels and their log files.
- `Overview.Dismiss`, `Alarms.Dismiss` — "Dismiss" → « Ignorer » — the client's Dismiss is « Rejeter ».
- `Comms.Compose` — "+ Compose" → « + Rédiger » — the client once says « Composer ».
- `Corp.SlotGasHold` — "Gas Hold" → « Soute à gaz » — the client's label says « Soute à carburant », which looks like a client error.
- `Corp.SlotShipHold`, `SlotSmallShipHold`, `SlotMediumShipHold`, `SlotLargeShipHold`, `SlotIndustrialShipHold` → « Soute du vaisseau », « Petite soute du vaisseau »… — follow the client's odd names for the holds that carry ships.
- `RefType.CorporateRewardPayout`, `RefType.EssEscrowTransfer` — "Corp Reward" / "ESS Transfer" → « Récompense de corporation » / « Transfert SSA » — follow the app's short English; the client's journal says « Paiement de récompense de corporation » and « Paiement d'un dépôt du SSA ».
- `Map.NodeStaging` — "staging" → « concerné » — from the client's « Système concerné » (incursion staging system), itself an odd translation; a very short map caption.
- `Map.SeriesAdm` and the other ADM entries — "ADM" kept — the client only spells it out.
- `Settings.SlackNoteToSelf` — "Note to Self" → « Note personnelle » — use Slack's own French name for the self-DM if it has one.

### 3. Fit

Every view was laid out headless in the app's fonts, and the columns and labels found cut short are
shortened (see "Short forms"). The first line here fits with little to spare; the rest were not
measured (the Settings window and the drawn fitting ring are not in that pass, and journal types
are data, not headers).

- `Sales.ColProfit2` « Bénéf. % » fits the Sale Listing's 64 px column with no margin (42 px of 42); `Worklist.ColShortValue` « Val. manquante » with 1 px (76 of 77).
- `Settings.ColSkillQueue` — « File compét. » (60 px) — the same header as the character viewer's, in a 90 px Settings column (about 67 px of room, estimated).
- `RefType.FluxTicketRepayment` — the client's 69-character name in the wallet journal's 180 px Type column (several client journal names are as long).
- `Settings.IntelLabel` — "INTEL" → « Renseignements » — a 70 px column at 9 pt; players also say « Intel ».
- `Map.FittingBandHigh` … `FittingBandSubsystems` — « Haute », « Interm. », « Basse », « Optim. », « Services », « Sous-syst. » beside the fitting ring (drawn, not measured).

### 4. Style

- Short forms chosen to fit (see "Short forms"): `Sales.ColOrder` « Commande », `Industry.ColReq` « Dem. », `Assets.ColVsBest` « Écart », `Industry.ColSlotDays` « Jour-créneau », `Sales.ColPri` « Prio » — check each still says what its column holds; `Sales.ColProfit` « Bénéf. » also heads wide columns (Order Tracker, Sales Tracker), where « Bénéfice » would fit.
- `Alarms.HullA`, `HullAn` — "a {0}" / "an {0}" → « un {0} », giving `UndockTitle` « … a appareillé avec un Rifter » and `IntelSaidFlying` « Pilotant un Rifter. » — players may say « en Rifter ».
- `Alarms.ButtonImAwake` — "I'm awake" → « Je suis là » — avoids the gendered « Je suis réveillé(e) ».
- `Sales.To`, `UnitSOf`, `Per`, `LimitScope*`, `LimitPeriod*` — one row built from pieces: « Limiter chaque acheteur à [N] unité(s) de chaque type d'objet sur [N] jour(s) »; check it reads well with every choice (« dans toute la boutique », « sans limite de durée »).
- `Finance.OwnerPersonal` — "Personal" → « Personnel » — an adjective used as a noun in the owner picker.
- `Worklist.SlotsFreeOfTotal` — "M {0}/{1} · R … · S …" → « P … · R … · S … » — P for production.
- `Market.From`, `Market.To` — « Lieu » / « Destination » — also label the Trade Opportunities route (where to buy, where to sell), where « Départ » might read better.
- `Characters.TabDetail` « Détails » vs `Data.TabDetail`, `Sales.TabDetail` « Détail » — kept apart on purpose (one character's details vs a line-by-line breakdown beside a summary).
- `Settings.AgentHelpAgentName` and the other help texts — call the assistant « il » (l'assistant), whatever the persona's name.
- `Common.NoMatchesPressEnterNote` and other key names — « Enter » kept; AZERTY keyboards print « Entrée », « Échap », « Suppr ».
- `RefType.Cspaofflinerefund` — « Remboursement hors-ligne CSPA » — the client's hyphen; the app writes « hors ligne » elsewhere.
- `Map.IntelNoVisual` — "NV" kept, as players write it in intel channels.
- `Comms.MailFolderInbox` — « Boîte aux lettres » is the client's word; « Boîte de réception » is the usual mail word.
- `Comms.NotifLabelReady` — "Ready" → « Prêt » beside a date — may read as « prêt » (a loan); « Prêt le » or « Disponible le »?
- `Data.ZkbPostingDisabled`, `ZkbPostingOff` — « Publication zKillboard : import désactivé » / « … : désactivée » — two states told apart (the whole import off vs posting off).
