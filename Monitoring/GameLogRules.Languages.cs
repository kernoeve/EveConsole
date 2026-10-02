using System.Text.RegularExpressions;

namespace EveConsole.Monitoring;

/// <summary>
/// The game log lines of the other seven client languages — German, Spanish, French, Japanese,
/// Korean, Russian and Chinese.
///
/// The patterns are the game's own text, read from the client's localization tables (the message
/// ids are in brackets), with each placeholder turned into a capture. They give the same rows as
/// the English lines: the kinds, the hit quality, the ewar Quality and the "you!" target stay in
/// English, because that is what the alarms and queries read. Names — pilots, ships, stations,
/// systems, weapons — stay as the client wrote them.
///
/// ⚠ Only English logs have been read so far. The phrases come from the game, but how the client
///   assembles a combat line around them (the amount, " - weapon - quality") is taken from the
///   English logs and assumed to be the same everywhere.
/// </summary>
public static partial class GameLogRules
{
    /// <summary>The target of an ewar line aimed at the listener, as the English line writes it.</summary>
    private const string YouTarget = "you!";

    private const string Scramble   = "Warp scramble attempt";
    private const string Disruption = "Warp disruption attempt";

    /// <summary>"Jumping from {gate} to {system}" [239314].</summary>
    private static readonly Regex[] OtherJumpRx =
    [
        new(@"^Springe\s+von\s+(?<from>.+?)\s+nach\s+(?<to>.+?)\s*$", Opts),
        new(@"^Saltando\s+de\s+(?<from>.+?)\s+a\s+(?<to>.+?)\s*$", Opts),
        new(@"^Saute\s+de\s+(?<from>.+?)\s+à\s+(?<to>.+?)\s*$", Opts),
        new(@"^(?<from>.+?)\s*から\s*(?<to>.+?)\s*へジャンプ中\s*$", Opts),
        new(@"^점프\s*중\s*-\s*(?<from>.+?)\s+출발\s*,\s*(?<to>.+?)\s+도착\s*$", Opts),
        new(@"^Осуществляется\s+прыжок\s+из\s+(?<from>.+?)\s+в\s+(?<to>.+?)\s*$", Opts),
        new(@"^从\s*(?<from>.+?)\s*跳到\s*(?<to>.+?)\s*$", Opts),
    ];

    /// <summary>"Undocking from {station} to {system} solar system." [238851].</summary>
    private static readonly Regex[] OtherUndockRx =
    [
        new(@"^Abdocken\s+von\s+(?<loc>.+?)\s+zum\s+Sonnensystem\s+(?<sys>.+?)\.?\s*$", Opts),
        new(@"^Desacoplando\s+nave\s+de\s+(?<loc>.+?)\s+en\s+dirección\s+al\s+sistema\s+solar\s+(?<sys>.+?)\.?\s*$", Opts),
        new(@"^Part\s+de\s+(?<loc>.+?)\s+pour\s+rejoindre\s+le\s+système\s+solaire\s+(?<sys>.+?)\.?\s*$", Opts),
        new(@"^(?<loc>.+?)\s*から\s*(?<sys>.+?)\s*へ出港[。.]?\s*$", Opts),
        new(@"^(?<loc>.+?)에서\s+도킹\s*해제하여\s+(?<sys>.+?)\s+항성계로\s+이동합니다\.?\s*$", Opts),
        new(@"^Выход\s+из\s+дока\s+(?<loc>.+?)\s+в\s+звездную\s+систему\s+(?<sys>.+?)\.?\s*$", Opts),
        new(@"^正在从\s*(?<loc>.+?)\s*离站进入\s*(?<sys>.+?)\s*恒星系[。.]?\s*$", Opts),
    ];

    /// <summary>"Your cloak deactivates due to proximity to a nearby {type}." [581419], and the
    /// "…nearby object." form [581396]. What dropped it goes in LocationName.</summary>
    private static readonly Regex[] OtherDecloakRx =
    [
        new(@"^Ihr\s+Tarnmodul\s+wird\s+aufgrund\s+des\s+Abstands\s+zu\s+einem\s+nahe\s+gelegenen\s+(?<what>.+?)\s+deaktiviert\.?\s*$", Opts),
        new(@"^Tu\s+camuflaje\s+se\s+desactiva\s+(?:por\s+la\s+cercanía\s+de\s+lo\s+siguiente:\s*(?<what>.+?)|por\s+proximidad\s+a\s+un\s+(?<what>objeto)\s+cercano)\.?\s*$", Opts),
        new(@"^Votre\s+camouflage\s+est\s+désactivé\s+à\s+cause\s+(?:de\s+la\s+proximité\s+avec\s+(?<what>.+?)|d['’]un\s+(?<what>objet)\s+à\s+proximité)\.?\s*$", Opts),
        new(@"^付近の(?<what>.+?)に接近したため[、,]\s*クロークが解除されます[。.]?\s*$", Opts),
        new(@"^(?:주변\s+(?<what>물체)로|(?<what>.+?)\s*\(으\)로)\s+인해\s+클로킹이\s+해제됩니다\.?\s*$", Opts),
        new(@"^Маскировка\s+выключается:\s*вы\s+подлетели\s+к\s+объекту(?:\s+(?<what>.+?))?\.?\s*$", Opts),
        new(@"^由于附近(?:的(?<what>.+?)|(?<what>物体))的影响[，,]\s*你的隐形状态已解除[。.]?\s*$", Opts),
    ];

    /// <summary>"{source} misses you completely" [285203, 260411] and "{weapon} belonging to {owner}
    /// misses you completely" [285202, 260412] — a drone's — with the weapon after it as in
    /// English. The whole of what comes before "misses" is the entity, as in English.</summary>
    private static readonly Regex[] OtherMissTakenRx =
    [
        new(@"^(?<entity>.+?)\s+(?:verfehlt\s+Sie\s+völlig|hat\s+Sie\s+völlig\s+verfehlt)\.?(?:\s*-\s*(?<weapon>.+?))?\.?\s*$", Opts),
        new(@"^(?<entity>.+?)\s+falla\s+por\s+mucho\.?(?:\s*-\s*(?<weapon>.+?))?\.?\s*$", Opts),
        new(@"^(?<entity>.+?)\s+vous\s+a\s+complètement\s+manquée?\.?(?:\s*-\s*(?<weapon>.+?))?\.?\s*$", Opts),
        new(@"^(?<entity>.+?)\s*(?:の攻撃)?はあなたを完[全璧]に外した[。.]?(?:\s*-\s*(?<weapon>.+?))?\s*$", Opts),
        new(@"^(?<entity>.+?)(?:이\(가\)|\s*의?\s*공격이)\s+(?:당신을\s+)?완전히\s+빗나(?:감|갔습니다)\.?(?:\s*-\s*(?<weapon>.+?))?\.?\s*$", Opts),
        new(@"^(?<entity>.+?)(?::\s*промах\s+мимо\s+вашего\s+корабля|\s+промахнул(?:ось|ась|ся)\s+мимо\s+вашего\s+корабля|\s+ведет\s+огонь\s+по\s+вашему\s+кораблю(?:\s*\([^)]*\))?:\s*далекий\s+промах)\.?(?:\s*-\s*(?<weapon>.+?))?\.?\s*$", Opts),
        new(@"^(?<entity>.+?)\s*完全没有[打击]中你[。.]?(?:\s*-\s*(?<weapon>.+?))?\s*$", Opts),
    ];

    /// <summary>"Your {weapon} misses {target} completely" [285200] and "Your group of {weapon}
    /// misses…" [285201], with the weapon after it as in English.</summary>
    private static readonly Regex[] OtherMissDealtRx =
    [
        new(@"^Ihre?\s+(?<weapon>.+?)(?:-Gruppe)?\s+hat\s+(?<target>.+?)\s+völlig\s+verfehlt\.?\s*-\s*(?<weapon2>.+?)\s*$", Opts),
        new(@"^Tu\s+(?:grupo\s+de\s+)?(?<weapon>.+?)\s+no\s+acierta\s+en\s+(?<target>.+?)\s+por\s+mucho\.?\s*-\s*(?<weapon2>.+?)\s*$", Opts),
        new(@"^Votre\s+(?:groupe\s+de\s+)?(?<weapon>.+?)\s+a\s+complètement\s+manqué\s+(?<target>.+?)\s*-\s*(?<weapon2>.+?)\s*$", Opts),
        new(@"^あなたの(?<weapon>.+?)(?:グループ)?の攻撃は(?<target>.+?)を完全に外した[。.]?\s*-\s*(?<weapon2>.+?)\s*$", Opts),
        new(@"^(?<weapon>.+?)(?:이\(가\)|\s+그룹이)\s+(?<target>.+?)을\(를\)\s+완전히\s+빗나감\.?\s*-\s*(?<weapon2>.+?)\s*$", Opts),
        new(@"^(?:Ваше\s+орудие|Ваша\s+группа)\s+(?<weapon>.+?)\s+промахнул(?:ось|ась)\s+мимо\s+(?<target>.+?)\.?\s*-\s*(?<weapon2>.+?)\s*$", Opts),
        new(@"^你的(?:一组)?(?<weapon>.+?)完全没有打中(?<target>.+?)[。.]?\s*-\s*(?<weapon2>.+?)\s*$", Opts),
    ];

    /// <summary>"{amount} to|from {entity} - {weapon} - {quality}": the to [285197] and from
    /// [285198] words of six languages. Japanese writes から both ways, so "either" leaves the
    /// direction to the line's colour.</summary>
    private static readonly Regex OtherDamageRx = new(
        @"^(?<dmg>\d+)\s+(?:(?<to>nach|a|à|на|对)|(?<from>von|de|из|来自)|(?<either>から))\s*(?<entity>.+?)\s+-\s+(?<weapon>.+?)(?:\s+-\s+(?<quality>[\p{L}' ]+?))?\s*$",
        Opts);

    /// <summary>Korean wraps the name: "{amount}의 피해를 {target}에게 입힘" [285197] and
    /// "…{source}에 의해 입음" [285198].</summary>
    private static readonly Regex KoreanDamageRx = new(
        @"^(?<dmg>\d+)\s*의\s*피해를\s+(?<entity>.+?)\s*(?:(?<to>에게\s+입힘)|(?<from>에\s+의해\s+입음))\s+-\s+(?<weapon>.+?)(?:\s+-\s+(?<quality>[\p{L}' ]+?))?\s*$",
        Opts);

    /// <summary>The hit qualities [285185–285190], back to the English the rows keep.</summary>
    private static readonly Dictionary<string, string> QualityInEnglish = new(StringComparer.Ordinal)
    {
        ["Leichter Streifschuss"] = "Grazes", ["Streifschuss ohne Schäden"] = "Glances Off",
        ["Treffer"] = "Hits", ["Einschlag"] = "Penetrates",
        ["Schwerer Treffer"] = "Smashes", ["Vernichtender Treffer"] = "Wrecks",

        ["Roza"] = "Grazes", ["Alcanza"] = "Glances Off", ["Impacta"] = "Hits",
        ["Perfora"] = "Penetrates", ["Destroza"] = "Smashes", ["Destruye"] = "Wrecks",

        ["Égratigne"] = "Grazes", ["Effleure"] = "Glances Off", ["Touche"] = "Hits",
        ["Pénètre"] = "Penetrates", ["Frappe"] = "Smashes", ["Détruit"] = "Wrecks",

        ["擦過"] = "Grazes", ["軽微"] = "Glances Off", ["直撃"] = "Hits",
        ["小破"] = "Penetrates", ["中破"] = "Smashes", ["大破"] = "Wrecks",

        ["스침"] = "Grazes", ["도탄"] = "Glances Off", ["명중"] = "Hits",
        ["관통"] = "Penetrates", ["강타"] = "Smashes", ["치명타"] = "Wrecks",

        ["Царапнул"] = "Grazes", ["Скользнул"] = "Glances Off", ["Попал"] = "Hits",
        ["Пробил"] = "Penetrates", ["Раздробил"] = "Smashes", ["Сокрушил"] = "Wrecks",

        ["轻轻擦过"] = "Grazes", ["擦过"] = "Glances Off", ["命中"] = "Hits",
        ["穿透"] = "Penetrates", ["强力一击"] = "Smashes", ["致命一击"] = "Wrecks",
    };

    /// <summary>"Warp scramble attempt from {attacker} to you!" [258954] and the disruption
    /// [519384]. Only the ones aimed at the listener: those are what the alarm reads.</summary>
    private static readonly (Regex Rx, string Quality)[] OtherEwarOnYouRx =
    [
        (new(@"^Warp-Unterbrechungsversuch\s+von\s+(?<src>.+?)\s+gegen\s+Sie\s*!?\s*$", Opts), Scramble),
        (new(@"^Warp-Störversuch\s+von\s+(?<src>.+?)\s+gegen\s+Sie\s*!?\s*$", Opts), Disruption),
        (new(@"^Intento\s+de\s+distorsión\s+de\s+warp\s+de\s+(?<src>.+?)\s+a\s+ti\s*[.!]?\s*$", Opts), Scramble),
        (new(@"^Intento\s+de\s+disrupción\s+de\s+warp\s+por\s+parte\s+de\s+(?<src>.+?)\s+hacia\s+ti\s*[.!]?\s*$", Opts), Disruption),
        (new(@"^Tentative\s+d['’]inhibition\s+de\s+warp\s+par\s+(?<src>.+?)\s+contre\s+vous\s*!?\s*$", Opts), Scramble),
        (new(@"^Tentative\s+de\s+perturbation\s+de\s+warp\s+par\s+(?<src>.+?)\s+contre\s+vous\s*!?\s*$", Opts), Disruption),
        (new(@"^ワープスクランブル\s*発信源\s*[:：]\s*(?<src>.+?)\s*対象\s*[:：]\s*あなた\s*[!！]?\s*$", Opts), Scramble),
        (new(@"^ワープ妨害\s*発信源\s*[:：]\s*(?<src>.+?)\s*対象\s*[:：]\s*あなた\s*[!！]?\s*$", Opts), Disruption),
        (new(@"^워프\s*스크램블\s*시도\s*[:：]?\s*시전자\s*-\s*(?<src>.+?)\s*,\s*대상\s*-\s*당신\s*[!！]?\s*$", Opts), Scramble),
        (new(@"^워프\s*디스럽트\s*시도\s*[:：]?\s*시전자\s*-\s*(?<src>.+?)\s*,\s*대상\s*-\s*당신\s*[!！]?\s*$", Opts), Disruption),
        (new(@"^Попытка\s+варп-глушения:\s*источник\s+(?<src>.+?)\s*,\s*цель\s+вы\s*!?\s*$", Opts), Scramble),
        (new(@"^Попытка\s+варп-подавления:\s*источник\s+(?<src>.+?)\s*,\s*цель\s+вы\s*!?\s*$", Opts), Disruption),
        (new(@"^(?:来自\s*)?(?<src>.+?)\s*试图跃迁扰频\s*你\s*[!！]?\s*$", Opts), Scramble),
        (new(@"^(?:来自\s*)?(?<src>.+?)\s*试图跃迁扰断\s*你\s*[!！]?\s*$", Opts), Disruption),
    ];

    private sealed record OtherDamageLine(int Amount, bool Inbound, string Entity, string Weapon, string? Quality);

    /// <summary>The English rule's match, else the first other language's; null for none.</summary>
    private static Match? First(string body, Regex english, Regex[] others)
    {
        var m = english.Match(body);
        if (m.Success) return m;
        foreach (var rx in others)
            if ((m = rx.Match(body)).Success) return m;
        return null;
    }

    private static OtherDamageLine? OtherDamage(ParsedLine line)
    {
        var m = OtherDamageRx.Match(line.Body);
        if (!m.Success) m = KoreanDamageRx.Match(line.Body);
        if (!m.Success || !int.TryParse(m.Groups["dmg"].Value, out var amount)) return null;

        // The words where they say it; otherwise the colour — red taken, cyan dealt.
        bool? inbound = m.Groups["from"].Success ? true
                      : m.Groups["to"].Success   ? false
                      : line.LeadColor switch { "0xffcc0000" => true, "0xff00ffff" => false, _ => null };
        if (inbound is null) return null;

        string? quality = null;
        if (m.Groups["quality"].Success)
        {
            var said = m.Groups["quality"].Value.Trim();
            quality = QualityInEnglish.GetValueOrDefault(said, said);
        }
        return new OtherDamageLine(amount, inbound.Value, m.Groups["entity"].Value,
                                   m.Groups["weapon"].Value.Trim(), quality);
    }

    private static (string Quality, string Source)? OtherEwarOnYou(string body)
    {
        foreach (var (rx, quality) in OtherEwarOnYouRx)
            if (rx.Match(body) is { Success: true } m)
                return (quality, m.Groups["src"].Value.Trim());
        return null;
    }
}
