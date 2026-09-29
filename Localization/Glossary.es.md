# Glosario en español

The terms EVE Console's Spanish text uses, so the same word reads the same on every screen and
matches the game where the game has a word for it. The client's words were read from the Spanish
EVE client's own text. Corrections from Spanish-speaking players are welcome — this list is where
they belong, so every screen picks them up.

## Style

- **Spanish of Spain, as the EVE client writes it**: coste, ajustes, vale, añadir, quitar, borrar,
  ratón. No Latin American variants.
- **Register: tú**, as the client ("Acopla la nave.", "¿Seguro que quieres continuar?", "Haz clic
  aquí…", "Te damos la bienvenida…"). Instructions in the tú imperative (Selecciona, Pulsa, Haz clic,
  Añade); possessives tu / tus ("Tu oferta" for the English "Our bid"). Impersonal forms where they
  read better: "No se ha podido…", "Se actualiza cada hora".
- **Buttons and menu items** in the infinitive: Guardar, Añadir región, Actualizar ahora.
- **Sentence case** for titles, tabs, headers and buttons — only the first word and proper names
  capitalised: "Añadir objetos desde un plano", "Explorador de estructuras". English written in
  capitals is a small heading; in Spanish it is simply the words in normal case
  ("SEARCH FOR ITEM" → "Buscar objeto").
- Labels ending in `:` keep `:` (no space before it). Column headers are short; abbreviate with a
  full stop when needed ("Cant.", "Estado de seg."). A narrow column shortens the same words its
  label spells out: "Imp. industrial" / "Impuesto industrial", "Ped. compra" / "Pedidos de compra",
  "Coste de fabr." / "Coste de fabricación". Where that is still too long, drop the "de" ("Coste
  fabr.", "Precio venta") or a word the cells make obvious ("Necesarios" over a list of items).
  Measure a header before settling it: the short forms in use are listed under **Short forms for
  narrow columns** below. "Obj." is already "objetivo" ("Total obj."), so objetos are not "Obj.".
- **Opening ¿ and ¡** always. Quotation marks « », as the client; " " inside them.
- Messages are whole sentences with a full stop where the English has one. Failures: "No se ha
  podido cargar…", "Error al…" only where the English is as terse: a status line "Import failed:
  {0}" is "Error al importar: {0}", "Save failed: {0}" is "Error al guardar: {0}" ("Error en la…"
  with a noun is fine too: "Error en la restauración: {0}"). One failure, one wording, on every
  screen that shows it.
- A space between a number and its unit or %: `30 días`, `20 %`, `5 m³`, `{1} %`. Short time units
  `d`, `h`, `min`, `s`: "2 d 5 h 30 min", "hace 12 s". Numbers written into the text take a decimal
  comma, as the client ("1,0 – 0,5", "90,6 %"); four digits take no separator ("1000").
- **Placeholders stay exactly as written**: `{0}`, `{1:N0}`, `{alarm}`. They may move in the
  sentence; none may be dropped or added. Where `{0}` could be a word of either gender, rephrase so
  nothing has to agree with it ("Estado: {0}").
- **Plural families**: One and Other. A count that is not a family must not read "1 activas":
  write "(s)" ("{0} activo(s)", "{0} lista(s)") or rephrase ("(ocultas: {0})").
- **Short labels agree with what they stand for.** A filter or a count is plural and agrees with
  the things listed ("Activos" contracts, "Todas" for compras y ventas, "Bloqueadas" tasks); a
  status agrees with the one thing ("Activo" job, "Bloqueada" task). A Worklist task (tarea) is
  feminine: Lista, Bloqueada, En curso; a job (trabajo) masculine: Listo, Activo, Cancelado.
- **Ships are feminine**, as the client: "la Rifter", "una Keepstar", "la {2} de {1}". A pod is
  "una cápsula". Undock is intransitive: "ha desacoplado".
- **Dates**: day before month, 24-hour clock: `d MMM yyyy`, `d MMM, HH:mm`; the long form
  `d 'de' MMMM 'de' yyyy` (literal words in single quotes, as .NET needs). Month and weekday names
  come from the culture, in lower case, as Spanish writes them; the app's own month and weekday
  entries are lower case too.
- **Abbreviations as the client**: PNJ (NPC), PH (SP, skill points), PL (LP, loyalty points).
- **Unchanged**: EVE Console, EVE, ESI, SDE, SSO, CCP, ISK, m³, ME, TE, ADM, zKillboard, Fuzzwork,
  Hoboleaks, Slack, Discord, GitHub, Tranquility, PostgreSQL, SQLite, keyboard keys (Enter, Ctrl+C),
  file formats, URLs and anything that is typed exactly (`yyyy-MM-dd`, scope names). Names on other
  services' pages stay as those pages show them: Slack's buttons, Cloudflare's, and the EVE
  developer site's Client ID and Secret Key.

## Terms

| English | Español | Notes |
|---|---|---|
| capsuleer | capsulista | The client's word. |
| character | personaje | The client's word. |
| corporation / corp | corporación / corp. | The client's word; "corp." only where space is tight. A name not known yet is "Corporación {0}", spelt out like "Personaje {0}", "Alianza {0}", "Estación {0}". |
| alliance | alianza | The client's word. |
| faction | facción | The client's word. |
| NPC corporation | corporación PNJ | PNJ is the client's abbreviation for NPC. |
| agent (NPC) | agente | The client's word. |
| AI agent / companion (this app's) | asistente de IA / asistente | Not "agente", which is the NPC agent. |
| standings | prestigio | The client's word. |
| ticker / executor corp / title / bloodline / gender / locator | código / corporación ejecutiva / cargo / linaje / sexo / localizador | The client's words (entity facts). |
| corporation history (a pilot's) / career | historial de empleo / trayectoria | The client's words ("trayectoria profesional"). |
| solar system | sistema solar / sistema | The client's word. |
| constellation | constelación | The client's word. |
| region | región | The client's word. |
| station | estación | The client's word. |
| structure (Upwell) | estructura | The client's word. |
| citadel | ciudadela | The client's word. |
| engineering complex | complejo de ingeniería | The client's word. |
| refinery | refinería | The client's word. |
| Standup service modules | planta de fabricación / astillero / laboratorio de investigación, de invención / centro de reprocesamiento / centro de clonación / centro mercantil, each followed by "Standup" | The client's words. |
| service module | módulo de servicio | The client's word. |
| facility | instalación | A catch-all facility is "instalación comodín". |
| security status | estado de seguridad | The client's word. |
| true security | seguridad real | |
| high-sec / low-sec / null-sec | seguridad alta / seguridad baja / seguridad nula | The client's words ("espacio de seguridad baja"). |
| wormhole | agujero de gusano | The client's word. |
| stargate | portal estelar | The client's word. |
| cosmic signature | señal (cósmica) | The client's word ("Escanear señales"). Not "firma". |
| jump / jumps | salto / saltos | "5 jumps" = 5 saltos. |
| jump drive / jump range | motor de salto / alcance de salto | The client's words. |
| light year (ly) | AL | Ours, to keep route lines short; the client spells out "años luz". |
| waypoint / route midpoint | punto de ruta / escala | "Punto de ruta" is the client's word. "JUMP THROUGH" = "Escalas en". |
| jump clone | clon de salto | The client's word. |
| capital ship | nave capital | The client's word ("naves capitales"). |
| supercarrier / titan / supercapital | superportanaves / titán / supercapital | The client's words. |
| ship | nave | The client's word. |
| hull | casco | The client's word ("Casco actual"). |
| fitting | equipamiento | The client's word. |
| module | módulo | The client's word. |
| rig | complemento | The client's word. |
| high / mid / low / rig / service / subsystem slot | ranura superior / media / inferior / de complemento / de servicio / de subsistema | The client's words. |
| drone | dron | The client's word. |
| charges / ammo | cargas / munición | The client's words. |
| fuel | combustible | The client's word. |
| item | objeto | The client's word ("Objetivo" among the candidates means target). |
| type (of item) | tipo | The client's word. |
| group / category | grupo / categoría | The client's words. |
| market group | grupo de mercado | |
| assets | bienes | The client's word. |
| hangar | hangar | The client's word. |
| corp hangar | hangar corporativo | The client's word. |
| container | contenedor | The client's word. |
| cargo hold | bodega de carga / bodega | The client's word. |
| holds and bays | muelle de drones, plataforma para cazas, almacén de combustible, arsenal, hangar de naves, hangar de la flota, bodega de hielo / de gas / de minerales / de restos, bodega de nave pequeña / mediana / grande / industrial | The client's names. |
| volume (of an item, m³) | volumen | The client's word. |
| volume (traded) | volumen negociado | "Volumen" alone as a market column. |
| packaged / assembled | empaquetado / ensamblado | The client's words. |
| stack | pila | The client's word. |
| blueprint | plano | The client's word. |
| BPO / BPC (in a sentence) | plano original / copia de plano | The client's words; a bare "BPO" / "BPC" label stays BPO / BPC. |
| print (the blueprint a job runs on) | plano; original on the BPO / Formula tab | That tab counts only originals: "+1 print" = "+1 original". "Every copy busy" = "todos sus ejemplares ocupados", so it is not read as a BPC. |
| runs (of a job or copy) | iteraciones | The client's word. "10 runs" = 10 iteraciones. |
| manufacturing | fabricación | The client's word. |
| reaction | reacción | The client's word. |
| invention | invención | The client's word. |
| formula / decryptor / datacore | fórmula / desencriptador / núcleo de datos | The client's words. |
| copying | copia | The client's word. |
| research | investigación | The client's word. |
| material efficiency / time efficiency | eficiencia de materiales / eficiencia temporal | The client's words; ME / TE stay as abbreviations. |
| industry job | trabajo de industria | The client's word. |
| job slot | ranura de trabajo | "Ranura" is the client's word for a slot ("ranura de fabricación"). |
| production categories (Indy Parks) | the client's Standup rig names | "naves grandes avanzadas", "componentes avanzados", "reacciones compuestas"… |
| raw materials / intermediate / leftover | materias primas / intermedio / sobrante | "Materias primas" is the client's word. |
| build cost | coste de fabricación | The client's "coste de construcción" is planetary links; ours is the cost to manufacture. build = fabricar. "Coste de fabr." in a narrow column, "Coste fabr." in the narrowest. |
| reprocessing / refining | reprocesamiento / refinado | The client's words. |
| batch / portion size / scrap metal | lote / tamaño de lote / chatarra | "Chatarra" is the client's word. |
| compression | compresión | The client's word. |
| mining | minería | The client's word. |
| ore / ice / moon ore | mena / hielo / mena lunar | The client's words ("ORE" in the candidates is the corporation). |
| moon mining ledger | diario de minería lunar | The client's "Diario de minería" for the mining ledger. |
| planetary industry | industria planetaria | The client's word. |
| market | mercado | The client's word. |
| order (market) | pedido | The client's word ("pedidos del mercado"). Outstanding orders are "abiertos" (the client's "pedidos abiertos"). |
| buy order / sell order | pedido de compra / pedido de venta | The client's words. |
| bid (a buy order's price) | oferta | "Our bid" = "Tu oferta"; "Station best bid" = "Mejor oferta de la estación". |
| order (a customer's, in the Order Tracker) | encargo | Kept apart from a market order (pedido). |
| trade hub | centro mercantil | The client's word. |
| price / cost / profit / margin | precio / coste / beneficio / margen | Unit price = precio unitario. |
| median / mean | mediana / media | |
| broker fee / sales tax | comisión / impuesto de venta | The client's words. |
| multibuy | compra simultánea | The client's word. |
| contract | contrato | The client's word. |
| issuer / assignee / acceptor | anunciante / asignado a / aceptante | "Anunciante" and "Asignado a" are the client's; the client has no word for acceptor. The "Finished Contractor" status uses the client's "contratista". |
| From / To | Desde / Hasta (dates) / Origen / Destino (a courier's or a trade route's places) / De (a contract's issuer) | "De:" is a mail's sender, "Desde:" a date filter's start. |
| item exchange / courier / auction | intercambio de objetos / reparto / subasta | The client's words. |
| buyout | compra | The client's word. |
| collateral / reward | garantía / retribución | The client's words. |
| accept / reject (a contract) | aceptar / rechazar | The client's words. |
| wallet | cartera | The client's word. |
| journal (wallet) | diario | The client's word. |
| transaction | transacción | The client's word. |
| income / expense | ingresos / gastos | |
| amount (of money) | importe | The client's "Cantidad" also means quantity, so money is importe. A count, or the game log's amount (damage, units), stays cantidad. |
| balance (wallet) / cashflow | saldo / flujo de caja | The Worklist's "Balance" (made per unit consumed) is a ratio and stays balance. |
| net worth | valor neto | The client's word ("Valor total neto"). |
| loyalty points / LP store | puntos de lealtad (PL) / tienda de PL | The client's words. |
| skill / skill queue / skill points | habilidad / cola de habilidades / puntos de habilidad (PH) | The client's words. To train a skill = desarrollar, training = desarrollo, training queue = cola de desarrollo (all the client's); never "entrenar". |
| implant | implante | The client's word. |
| killmail | informe de muertes | The client's word (its Kill Report window). |
| kill / loss | muerte / pérdida | The client's words. |
| victim / attacker / final blow / top damage | víctima / atacante / golpe definitivo / mayor daño | The client's words; the short tags are "★ GD" and "▲ MD". |
| ship / pod / NPC kills (map) | naves destruidas / cápsulas destruidas / PNJ destruidos | The client's star-map words. |
| ratting | matarratas | The client's word as a label; in a sentence "cazar piratas". |
| ratting tax | impuesto sobre recompensas | "Imp. recomp." in a narrow column. |
| mission | misión | The client's word. |
| sovereignty | soberanía | The client's word. |
| faction warfare: contested / uncontested / captured / vulnerable | en disputa / no disputado / capturado / vulnerable | The client's words. Threshold = límite (client). Occupier = ocupante, held by = ocupado por. |
| victory points | puntos de victoria (PV) | The client spells it out; PV is our short form. |
| incursion: staging system / boss / established / mobilizing / withdrawing | sistema de operaciones / jefe / establecida / en movilización / en retirada | The client's words. |
| undock / dock | desacoplar / acoplar | The client's words. |
| online / offline | en línea / sin conexión | The client's words (a character, the server). A structure or module: conectado / desconectado. |
| downtime | periodo de inactividad | The client's word. |
| notification | notificación | |
| tax rate / vulnerability window | tasa impositiva / ventana de vulnerabilidad | The client's words. |
| asset safety | seguridad de los bienes | The client's word; the wrap is "paquete de seguridad de los bienes". The Worklist's task kind is "Seguridad de bienes", to fit its filter box. |
| timestamp | marca temporal | The client's word. |
| mail / EVE mail | correo / correo de EVE | The client's words. |
| compose / reply / forward | redactar / responder / reenviar | The client's words. |
| inbox / sent / sender / recipient / subject / body | bandeja de entrada / enviados / remitente / destinatario / asunto / cuerpo del mensaje | |
| channel | canal | The client's word. |
| intel (channel) | canal de inteligencia | |
| hostile / no visual | hostil / sin visual | "NV" stays NV. |
| hauling / haul / trip | transporte / transportar / viaje | "Transporte" is the client's word. |
| worklist | lista de tareas | |
| Indy Park (this app's) | parque industrial | |
| inventory level / stock / on hand | nivel de inventario / existencias / disponible | |
| store (this app's shop) | tienda | |
| buyer | comprador | The client's word. |
| alarm / alert | alarma / alerta | |
| scheduler / report | programador / informe | "Denunciar" among the candidates is reporting a player. |
| background process | proceso en segundo plano | |
| polling | sondeo | "sondear ESI" |
| token | token | |
| scope (ESI) | ámbito | Scope names stay as they are. A scope's shown name: "Leer …", Write = Editar. |
| scope (not ESI: assets, industry, killmails) | alcance | "Alcance de bienes". Kept apart from the ESI ámbito. |
| settings / refresh / save / cancel | ajustes / actualizar / guardar / cancelar | The client's words. |
| add / remove / delete / edit | añadir / quitar / eliminar / editar | The client's words. |
| export / import / filter / search / clear | exportar / importar / filtrar / buscar / borrar | The client's words. |
| apply / OK / close / copy | aplicar / vale / cerrar / copiar | The client's words. A status that went well is "Correcto", not "Vale". |
| dismiss | descartar | |
| task (a Worklist row) | tarea | Distinct from job (trabajo). |
| hull (hit-point layer, beside shield and armour) | estructura | escudo / blindaje / estructura, as the client; casco stays for a ship's hull. |
| CONCORD | CONCORD | The client's word. |
| starbase / control tower | base estelar / torre de control | The client's words. |
| reinforced / reinforcement | reforzado / refuerzo | The client's words. "Reinforced until" → "Reforzado hasta". |
| anchoring / unanchoring | anclaje / desanclaje | The client's words. |
| high power / low power (Upwell) | alta potencia / baja potencia | The client's words. |
| moon extraction / fracture / moon drill / moon chunk | extracción lunar / fractura / perforación lunar / fragmento lunar | The client's words. |
| war: aggressor / defender / ally | agresor / defensor / aliado | mutual war guerra mutua; war eligible apta para la guerra; war HQ cuartel general de guerra (the client's words). |
| kill right | derecho de asesinato | The client's word. |
| bounty / insurance payout | recompensa / pago del seguro | The client's words. |
| bill / broker fee / office rental | factura / comisión / alquiler de oficina | The client's words. |
| price types Buy / Sell / Split | compra / venta / intermedio | Split is the midpoint of the best buy and sell. |
| master wallet / wallet division | cartera principal / división de cartera | The client's words. |
| transaction tax | impuesto de transacción | The client's word. The journal type; sales tax is impuesto de venta. |
| industry tax | impuesto industrial | Singular, as the journal type, the chart series and the expense slice; "Imp. industrial" in a narrow column. The client's plural "Impuestos industriales" names a structure-profile setting. |
| contract statuses | pendiente / en curso / finalizado / rechazado / fallido / eliminado / revertido / caducado | outstanding / in progress / finished / rejected / failed / deleted / reversed / expired. The client's words. |
| Active (contract filter) | activo | Not "en curso", which is In Progress in the same list. |
| loan (contract type) | préstamo | The client's word. |
| public / private (availability) | público / privado | The client's words. |
| escrow (market, contract) | depósito | The client's word. |
| freelance job / project | trabajo por libre | The client's word. |
| corporation project / contributors | proyecto corporativo / colaboradores | "Colaboradores" is the client's word. |
| ISK efficiency | eficiencia en ISK | |
| medal | medalla | The client's word. |
| Project Discovery | Proyecto Discovery | The client's word. |
| SKIN / Paragon Hub (cosmetic market) | SKIN / Centro de Paragon | The client's words. |
| PLEX / New Eden Store | PLEX / tienda de Nuevo Edén | The client's words. |
| HyperNet Relay / HyperNode | relé de HyperNet / HyperNode | The client's words; the journal's "Flux" types are named after them. |
| Item Trader | comerciante de artículos | The client's word; the one place "artículo" stands for an item. |
| CSPA / ESS (journal types) | LPSC / SVE | The client's abbreviations. |

## This app's own terms

| English | Español | Notes |
|---|---|---|
| Overview (the app's home screen) | Resumen | Not "vista general", the client's in-space overview. |
| Item Browser / Structure Browser | Explorador de objetos / Explorador de estructuras | The client's "Explorador de estructuras". |
| price override | precio manual | An override column is "man.": "Exist. man.", "Fabr. man.", "Reserv. man."; a name override is "Nombre manual". |
| client (one running copy of the app) | cliente | One doing the background work without a window: "(sin ventana)". |
| build (a version of the app) | compilación | "dev build" = compilación de desarrollo; the short title-bar badge is "versión dev". |
| endpoint (ESI) | endpoint | |
| backup | copia de seguridad | |
| push-to-talk / hotkey | pulsar para hablar / tecla de acceso rápido | |
| TTS / STT | TTS / STT (voz / entrada de voz) | Abbreviations stay. Text-to-speech in a sentence: síntesis de voz (TTS). |
| auth character | personaje de autenticación | The character whose token a request uses. |
| armed (an alarm) | activada | "Alarmas — 3 activadas". |
| alarm check / fire, firing / cooldown / acknowledge / stage | comprobación / dispararse, disparo / espera / confirmar / fase | "Espera" is the client's word for cooldown. |
| wake-up call / Continuous / One shot | despertador / Continua / Un solo disparo | |
| backfill | recuperar (el historial) | "Recuperar ahora", "Iniciar recuperación". |
| shrink / compact (the database) | compactar | "Compactar base de datos". |
| purge | purgar | |
| sighting (intel) | avistamiento | |
| outbid | superado | "un pedido superado"; the Worklist switch is "Superado" too. |
| standing projects / standing instructions | proyectos permanentes / instrucciones permanentes | Like "pedidos de compra permanentes". |
| roles (of the AI models) / fallback | funciones / de reserva | "Funciones" is the client's word for roles. |
| polling cycle | ciclo de sondeo | |
| Personalisation (Settings tab) | Personalización | The client's word. |
| collection (Inventory Levels) | colección | |
| appraise / appraisal | tasar / tasación | "Tasación de objetos". |
| shopping list / slot days | lista de la compra / días-ranura | A compound unit, like horas-hombre: "Días-ranura", "Beneficio / día-ranura". |
| production assignments / item exceptions | asignaciones de producción / excepciones por objeto | |
| store order / to book one | encargo de la tienda / registrar | "la tienda registra o actualiza un encargo"; the button is "Registrar". |
| sale posting / post block / section / tag | publicación (de venta) / bloque de publicación / sección / etiqueta | |
| Serve (a store setting) / usage (HELP) message | Atender (Lista / Cualquiera) / mensaje de instrucciones | The tab is "Instrucciones". |
| not for profit | sin ánimo de lucro | |
| deploy / secret / callback URL | desplegar (despliegue) / secreto / URL de callback | Worker, Client ID and Secret Key stay in English. |
| snooze | posponer (pospuesta) | |
| short / shortfall | déficit | "short {0} of {1}" = "déficit: {0} de {1}". |
| bottleneck / item contention | cuello de botella / competencia por objetos | |
| pipeline / buffer / surge / cover / ceiling | cadena de producción / reserva / pico / cobertura / techo | |
| slot pool | grupo de ranuras | |
| manifest / stop (hauling) | manifiesto / parada | |
| market alt / surplus | alt de mercado / excedentes | |
| a task's states | lista / bloqueada / en curso / en espera | Feminine, agreeing with tarea; counts of them are plural: "Listas", "Bloqueadas", "Atascadas". |
| Order Fulfilment / ESI Call Schedule | Preparación de encargos / Programación de llamadas a ESI | |
| zKillboard firehose / interval poll / live poll | flujo en tiempo real / sondeo periódico / sondeo en directo | |
| daily dump / gap fill / posting (kills to zKillboard) | volcado diario / relleno de huecos / envío a zKillboard | |
| sweep / pass / idle | barrido / pasada / en espera | |
| listener (chat log) | oyente | The client's word. |
| AI rates / cache read, write | tarifas / lectura, escritura de caché | |
| map overlay | capa | |
| ESI scope groups | Acceso / Búsqueda / Interfaz … | Track Members = Rastrear miembros. |

## Short forms for narrow columns

The shortened headers in use, each measured in the app's own fonts to fit its column. Spell a
header out wherever it has room; use these only where it does not, and a second form in brackets
or after a semicolon only where the first is still too long.

| English | Full | Short |
|---|---|---|
| Build Cost | Coste de fabricación | Coste de fabr.; Coste fabr. where even that is too long |
| Build Value / Build Time / Build Price | Valor / Tiempo / Precio de fabricación | Valor de fabr. (Valor fabr.) / Tiempo de fabr. / Precio de fabr. |
| In Build | En fabricación | En fabr. |
| Mat Cost / Job Cost / Unit Cost | coste de materiales / del trabajo / unitario | Coste mat. / Coste trab. / Coste unit. |
| Sale Price / Market Price | Precio de venta / Precio de mercado | Precio venta / Precio mercado |
| Profit / Profit % | Beneficio / Beneficio % | Benef. / Benef. % |
| Industry Tax / Ratting Tax | Impuesto industrial / Impuesto sobre recompensas | Imp. industrial / Imp. recomp. |
| Remaining Reward | Retribución restante | Retrib. rest. |
| Inventory levels / Station levels | Niveles de inventario / Niveles por estación | Niv. inventario / Niv. estación |
| Short volume | Volumen del déficit | Vol. del déficit |
| Buy orders | Pedidos de compra | Ped. compra |
| Stock / stock override | Existencias / existencias manuales | Exist. / Exist. man. |
| Qty / Min Qty | Cantidad / Cantidad mínima | Cant. / Cant. mín. |
| Units Sold 30d | Unidades vendidas 30 d | Unid. vend. 30 d |
| Skill queue / Pod value | Cola de habilidades / Valor de la cápsula | Cola de hab. / Valor cápsula |
| Runs | Iteraciones | Iter. |
| Produced | Producido | Produc. ("Prod." would read as Producto) |
| Priority | Prioridad | Prio. |
| Calls / Avg / Took (AI usage) | Llamadas / Media / Duración | Llam. / Med. / Tiempo |
| Fired (an alarm's count) | Disparos | Veces |
| Required Items (LP store) | Objetos necesarios | Necesarios |
| Items pulled (contracts) | Objetos descargados | Descargados |
| Trigger below (%) / Fill to (%) | Si baja de (%) / Rellenar hasta (%) | Rellenar a (%) in the rules grid |
| Success Chance / End Date / Owner Type (detail labels) | Probabilidad de éxito / Fecha de finalización / Tipo de propietario | Prob. de éxito / Fecha de fin / Tipo propietario |
| Asset Safety (Worklist task kind, in its 114 px Type filter box) | Seguridad de los bienes | Seguridad de bienes (a closed drop-down cuts its value with no "…") |

## For a native speaker to check

Open questions from the drafts, grouped by area; the most important first. Each line: the entry,
the English, the Spanish draft, and the doubt — **meaning** (the sense was inferred), **client**
(differs from the client's word, or the client has none), **fit** (may be too long for its space),
**style**.

### Wallet journal types (RefTypeText)

- `AgentsPreward` — "Agents Pre-reward" — «Retribución anticipada de agente» — **meaning**: no client text; ESI's "agents_preward" read as an advance on a mission reward.
- `AllignmentBasedGateToll` — "Alignment-Based Gate Toll" — «Peaje de portal según la alineación» — **meaning**: no client text beyond «Peaje de portal» for a gate toll.
- `MarketProviderTax` — "Market Provider Tax" — «Impuesto del proveedor del mercado» — **meaning**: no client text.
- `SecurityProcessingFee` — "Security Processing Fee" — «Tasa de procesamiento de seguridad» — **meaning**: no client text.
- `OperationBonus` — "Operation Bonus" — «Bonificación de operación» — **meaning**: no client text; which "operation" is not known.
- `StructureGateJump` — "Structure Gate Jump" — «Salto por portal de estructura» — **meaning**: taken to be the Ansiblex jump fee.
- `ItemTraderPayment` — "Item Trader Payment" — «Pago de comerciante de artículos» — **meaning**: Item Trader is the client's «Comerciante de artículos»; what the payment is was inferred.
- `GmPlexFeeRefund` — "GM PLEX Fee Refund" — «Devolución de comisiones de PLEX de GM» — **meaning**: no client text.
- `FluxPayout`, `FluxTax`, `FluxTicketRepayment`, `FluxTicketSale` — "Flux Payout / Tax / Ticket Repayment / Ticket Sale" — «Pago del relé de HyperNet», «Tasa del relé de HyperNet», «Devolución de HyperNodes», «Venta de HyperNodes» — **meaning**: ESI's "Flux" taken to be the HyperNet Relay, named as the client names it.

### Worklist

- `BlueprintVerdictBuilding` — "Building" — «En cola» — **meaning**: from the comment "jobs are queued for it"; the English could also mean pressure building up.
- `ContentionVerdictNotTheShelf`, `ContentionVerdictOnOrder`, `ContentionVerdictHolding`, `ContentionVerdictWave`, `BlueprintVerdictContended` — "Not the shelf", "On order", "Holding", "Wave", "Contended" — «No son las existencias», «Ya pedido», «Estable», «Oleada», «Disputado» — **meaning**: one-word verdicts written from their comments; do they read as verdicts?
- `TabItemContention` — "Item Contention" — «Competencia por objetos» — **style**: is the tab's name clear?
- `Balance` — "Balance" (made per unit consumed) — «Balance» — **style**: a ratio; «Balance» may read as money (the wallet's is «Saldo»).
- `StatusReady`, `HiddenList`, `ReadyNow` — "{0:N0} ready{1}", "({0} hidden)", "ready now" — «{0:N0} lista(s){1}», «(ocultas: {0})», «para empezar ya» — **style**: "(s)" or a rephrasing where the English is not a counted family; the same in other status lines.
- `ValueAt`, `ChartBy`, `GrainDaily`/`Weekly`/`Monthly` — "Value at", "Chart by", "Daily" — «Valorar según», «Gráfico», «Por día» — **style**: written to read with the picker after them.

### Assets

- `ColFlag` — "Flag" — «Posición» — **meaning**: a guess for the game's location flag (the slot or hangar an item is in).
- `AssetColIsSingleton` — "Is Singleton" — «Ensamblado» — **meaning**: says what the flag means (1 when assembled) rather than the word.
- `IncludeIjShort` — "IJ" — «TI» — **style**: an invented abbreviation of «trabajos de industria».

### Settings

- `NotePrivateConversationsAppearNote` — "…appear as "Private Chat (2)"…" — keeps «Private Chat (2)» — **meaning**: the Spanish client calls these «Chat privado (…)»; quote whatever name a Spanish client's chat logs really have.
- `SlackNoteToSelf` — "Note to Self" — «Nota personal» — **client**: use Slack's own Spanish name for the self-DM if it has one.
- `N1OpenApiSlackNote`, `N2GoToOauthNote`, `N3ClickInstallToNote` — the Slack app steps — Slack's buttons kept in English — **style**: api.slack.com is in English.
- `PiperKind*`, `KokoroVoice*` — "US Female", "(American Female)" — «EE. UU., femenina», «(estadounidense, femenina)» — **style**: agrees with «voz».
- `MapStatsCoverageRow` — "… buckets …" — «franjas» — **style**: the hourly buckets of the map history.

### Market

- `Acceptor`, `AcceptorLabel`, `ColAcceptor`, `AllAcceptors` — "Acceptor" — «Aceptante» — **client**: the client has no word; its closest is «contratista» (used for the "Finished Contractor" status).
- `Buyout` — "Buyout" — «Compra» — **client**: the client's word, and it fits the 82 px label column, but it is terse.
- `ModeSellToBuyOrder`, `ModeUndercutSellOrder` — "Buy Sell → Sell to Buy Order", "… → Undercut Sell Order" — «Comprar en venta → vender a pedido de compra», «… → rebajar el pedido de venta» — **style**: trader jargon, rephrased.

### Corp

- `ProjectTypeRemoteBoostShield`, `ProjectTypeRemoteRepairArmor` — "Remote Boost Shield", "Remote Repair Armor" — «Potenciar escudos remotamente», «Reparar blindaje remotamente» — **client**: the client's names are nouns (Escudo potenciador remoto, Reparación de blindaje remota); verbs match the other project types.
- `SlotShipHold` — "Ship Hold" — «Bodega de naves» — **client**: the client's "Ship Hold" is «Bodega de la nave»; this one is a hold for carrying ships, like «Bodega de nave pequeña».
- `RattingLabel` — "RATTING" — «Matarratas» — **client**: the client's word, but it may sound odd as the heading over the list of ratters.
- `RoleFinalBlow`, `RoleTopDamage` — "★ FB", "▲ TD" — «★ GD», «▲ MD» — **style**: invented from the client's «golpe definitivo» and «mayor daño».

### Map

- `RangePerJump`, `CandidateCost`, `CandidateCostVs`, `RouteTotalsOne`/`Other`, `AlternativeDetail`, `RouteNone`, `RouteNoneKeepstar`, `RouteNoneStations` — "ly" — «AL» — **client**: the client writes «años luz» in full; «AL» is ours.
- `NodeVictoryPoints` — "{0:N0}/{1:N0} VP" — «{0:N0}/{1:N0} PV» — **client**: the client spells out «puntos de victoria».
- `NodeStaging` — "staging" — «operaciones» — **fit**: the client's word (sistema de operaciones), for a caption asked to be very short.
- `DetailRegionGateways` — "Region gateways" — «Accesos a otras regiones» — **style**.
- `ProvenanceNeverWritten`, `ProvenanceLastWritten` — "Never written · {0}", "Last written by {0} at …" — «Nunca editado · {0}», «Editado por última vez por {0} el …» — **style**: «editado» for "written".
- `IntelNoVisual` — "NV" — «NV» — **style**: do Spanish players write NV?

### Common and Shell

- `Common.PriceTypeSplit` (also `Assets.CopyBasisSplit`, `Assets.StatusPricesSplit`) — "Split" — «Intermedio» — **client**: the client has no word for the midpoint price.
- `Shell.NavKillmails`, `Settings.TabKillMails` — "Killmails" — «Informes de muertes» — **client**: the client's Kill Report window; players may simply say «killmails».
- `Common.Ok` (and every OK button) — "OK" — «Vale» — **client**: the client's button, though much Spanish software says «Aceptar».
- `Common.Duration*`, `Shell.Ago*`, `Characters.Span*` — "2d 5h" — «2 d 5 h» — **style**: spaced like the client; unspaced would be more compact.
- `Common.Date*` — "MMM d, yyyy" — «d MMM yyyy» — **style**: day before month, MMM shows «sept.», `Common.DateMonthDayTime12` uses the 24-hour clock.
- `Shell.FmtStaticDataExport` — "Static Data Export: {0}" — «Datos estáticos (SDE): {0}» — **style**: CCP's product name paraphrased.

### Finance and Comms

- `Finance.ColAmount`, `Finance.SortAmount*`, `Comms.NotifLabelAmount`, `Corp.SeriesAmount` — "Amount" — «Importe» — **client**: the client says «Cantidad»; kept apart from quantity on purpose.

### Alarms

- `CooldownSecLabel` — "COOLDOWN (SEC)" — «Espera (s)» — **client**: the client's word for a cooldown, but it could be misread; «Enfriamiento» is the alternative.
- `ButtonImAwake` (quoted in `AdriftBodyLanded`, `AdriftBodyUndocked`) — "I'm awake" — «Estoy aquí» — **style**: avoids despierto/despierta; does it answer a wake-up call?
- `HullA`, `HullAn`, `HullPod`, `HullShip`, `UndockTitle`, `UndockSaid` and the `Adrift*` texts — "a {0}", "a pod", "{0} undocked in {1}" — «una {0}», «una cápsula», «{0} ha desacoplado con {1}», «La {1} de {0}» — **style**: ships feminine, as the client; undock intransitive, and "in a Rifter" as «con una Rifter».
- `DaySun`…`DaySat`, `MonthJanuary`…`MonthDecember` — "Sun", "January" — «dom», «enero» — **style**: lower case so they read right in a sentence; the pick lists show them lower case too.

### Sales

- `ColStockOvr`, `ColBuildOvr`, `ColRsrvOvr` — "STOCK OVR", "BUILD OVR", "RSRV OVR" — «Exist. man.», «Fabr. man.», «Reserv. man.» — **style**: do the "man." (manual) abbreviations explain themselves?
- `ColBuildCost2`, `ColBuildPrice`, `ColProfit`, `ColProfit2`, `ColProfit4`, `ColSalePrice`, `ColMarketPrice` — "BUILD COST", "Build Price", "Profit", "Profit %", "SALE PRICE", "Market Price" — «Coste fabr.», «Precio de fabr.», «Benef.», «Benef. %», «Precio venta», «Precio mercado» — **style**: shortened to fit their columns; «Benef.» also heads the wider profit columns of the Order Tracker and Sales Tracker.
- `LimitEachBuyer`, `To`, `UnitSOf`, `LimitScope*`, `Per`, `LimitPeriod*` — "Limit each buyer to [n] unit(s) of [item type] per [n] [day(s)]" — «Limitar a cada comprador a … unidad(es) de cada tipo de objeto cada … día(s)» — **style**: a sentence built from separate controls; «cada sin límite de tiempo» when "all time" is picked.
- `BookIt`, `OutcomeBooked` — "Book it", "Booked" — «Registrar», «Registrado» — **style**: matches «la tienda registra o actualiza un encargo» in the Alarms; «Aceptar» may be more natural on the button.
- `Serve`, `TabUsage`, `UsageMessage` — "Serve", "Usage", "Usage message" — «Atender», «Instrucciones», «Mensaje de instrucciones» — **style**.
- `BannerLabel`, `ClientId`, `SecretKey` — "BANNER", "Client ID", "Secret Key" — «Banner», «Client ID», «Secret Key» — **style**: an anglicism, and two names left as the EVE developer site shows them.

### Data (Background Processes, logs)

- `GameLogField*` — "Source ship", "Source alli", "Target corp"… — «Nave origen», «Alian. origen», «Corp. objetivo»… — **fit**: abbreviated for a 16-character label column.
- `ErrorClientWorker` — "{0} (worker)" — «{0} (sin ventana)» — **style**: a copy of the app doing the background work without a window, worded as the title bar words it.
- `ZkbPostingOff`, `ZkbPostingDisabled` — "zKillboard posting: off", "…: disabled" — «desactivado», «deshabilitado» — **style**: the app's only «deshabilitado» (the firehose's and poll's "disabled" are «desactivado»); it marks that the whole import is off.

### Headers shortened to fit (measured in the app's fonts)

- `Map.ColRuns`, `Data.ColAvg`, `Data.ColCalls`, `Industry.ColProduced`, `Corp.ColRemReward`, `Corp.ColRattingTax`, `Characters.ColSkillQueue` — "Runs", "Avg", "Calls", "Produced", "REM. REWARD", "RATTING TAX", "Skill queue" — «Iter.», «Med.», «Llam.», «Produc.», «Retrib. rest.», «Imp. recomp.», «Cola de hab.» — **style**: abbreviations made to fit; are they clear over their columns?
- `Data.ColFired`, `Data.ColTook`, `Data.ColItemsPulled`, `Assets.ColRequiredItems`, `Map.JumpFuelConservationLabel` — "Fired", "Took", "Items pulled", "Required Items", "JUMP FUEL CONSERVATION" — «Veces», «Tiempo», «Descargados», «Necesarios», «Conservación de combustible» — **style**: reworded to fit; clear in their place?
- `Worklist.TriggerBelow`, `Worklist.ColTriggerBelow`, `Worklist.ColFillTo` — "Trigger below (%)", "Fill to (%)" — «Si baja de (%)», «Rellenar a (%)» — **style**: the inventory rules' pair of headers.
- `Industry.ColSlotDays`, `Industry.ColProfitSlotDay` — "Slot Days", "Profit / Slot Day" — «Días-ranura», «Beneficio / día-ranura» — **style**: slot-days written as a compound unit, like horas-hombre.
