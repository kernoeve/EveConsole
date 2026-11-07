namespace EveConsole.Services;

/// <summary>An emoji and the English words to find it by.</summary>
public sealed record EmojiEntry(string Emoji, string Name);

/// <summary>A group of emojis, as pickers on phones arrange them.</summary>
public sealed record EmojiGroup(string Key, IReadOnlyList<EmojiEntry> Emojis);

/// <summary>
/// The standard emojis offered for reactions: the ones in common use, grouped as phones group them, each with its
/// Unicode name to search by. Reactions are Unicode emojis by the protocol, so any client can show them.
/// </summary>
public static class EmojiCatalog
{
    /// <summary>The ones offered first, as other clients do.</summary>
    public static readonly IReadOnlyList<string> Quick = ["👍", "❤️", "😂", "😮", "😢", "🙏", "🔥", "✅"];

    private static EmojiGroup Group(string key, string pairs)
    {
        var entries = new List<EmojiEntry>();
        foreach (var line in pairs.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var space = line.IndexOf(' ');
            if (space > 0) entries.Add(new EmojiEntry(line[..space], line[(space + 1)..]));
        }
        return new EmojiGroup(key, entries);
    }

    public static readonly IReadOnlyList<EmojiGroup> Groups =
    [
        Group("smileys", """
            😀 grinning face
            😃 grinning face with big eyes
            😄 grinning face with smiling eyes
            😁 beaming face with smiling eyes
            😆 grinning squinting face laugh
            😅 grinning face with sweat
            🤣 rolling on the floor laughing
            😂 face with tears of joy laugh
            🙂 slightly smiling face
            🙃 upside down face
            😉 winking face
            😊 smiling face with smiling eyes blush
            😇 smiling face with halo angel
            🥰 smiling face with hearts love
            😍 smiling face with heart eyes love
            🤩 star struck
            😘 face blowing a kiss
            😋 face savoring food yum
            😛 face with tongue
            😜 winking face with tongue
            🤪 zany face crazy
            😝 squinting face with tongue
            🤑 money mouth face isk
            🤗 smiling face with open hands hug
            🤭 face with hand over mouth
            🤫 shushing face quiet
            🤔 thinking face hmm
            🤐 zipper mouth face
            🤨 face with raised eyebrow
            😐 neutral face
            😑 expressionless face
            😶 face without mouth
            😏 smirking face
            😒 unamused face
            🙄 face with rolling eyes
            😬 grimacing face
            😮‍💨 face exhaling sigh
            🤥 lying face
            😌 relieved face
            😔 pensive face
            😪 sleepy face
            🤤 drooling face
            😴 sleeping face zzz
            😷 face with medical mask
            🤒 face with thermometer sick
            🤕 face with head bandage
            🤢 nauseated face
            🤮 face vomiting
            🥵 hot face
            🥶 cold face
            🥴 woozy face
            😵 face with crossed out eyes dead
            🤯 exploding head mind blown
            🤠 cowboy hat face
            🥳 partying face party
            😎 smiling face with sunglasses cool
            🤓 nerd face
            🧐 face with monocle
            😕 confused face
            😟 worried face
            🙁 slightly frowning face
            😮 face with open mouth wow
            😯 hushed face
            😲 astonished face
            😳 flushed face
            🥺 pleading face
            😦 frowning face with open mouth
            😧 anguished face
            😨 fearful face
            😰 anxious face with sweat
            😥 sad but relieved face
            😢 crying face sad
            😭 loudly crying face sob
            😱 face screaming in fear
            😖 confounded face
            😣 persevering face
            😞 disappointed face
            😓 downcast face with sweat
            😩 weary face
            😫 tired face
            🥱 yawning face
            😤 face with steam from nose
            😡 enraged face pouting angry
            😠 angry face
            🤬 face with symbols on mouth swearing
            😈 smiling face with horns devil
            👿 angry face with horns
            💀 skull dead
            ☠️ skull and crossbones pirate
            💩 pile of poo
            🤡 clown face
            👹 ogre
            👻 ghost
            👽 alien
            👾 alien monster space invader
            🤖 robot
            """),
        Group("people", """
            👍 thumbs up yes agree
            👎 thumbs down no disagree
            👌 ok hand
            🤌 pinched fingers
            ✌️ victory hand peace
            🤞 crossed fingers luck
            🤟 love you gesture
            🤘 sign of the horns rock
            🤙 call me hand
            👈 backhand index pointing left
            👉 backhand index pointing right
            👆 backhand index pointing up
            👇 backhand index pointing down
            ☝️ index pointing up
            ✋ raised hand stop
            🤚 raised back of hand
            🖐️ hand with fingers splayed
            🖖 vulcan salute
            👋 waving hand hello bye
            👏 clapping hands applause
            🙌 raising hands celebration
            👐 open hands
            🤲 palms up together
            🤝 handshake deal
            🙏 folded hands please thanks
            ✍️ writing hand
            💪 flexed biceps strong
            🫡 saluting face o7
            🫠 melting face
            🫣 face with peeking eye
            👀 eyes looking
            👁️ eye
            🧠 brain
            🫶 heart hands
            🙇 person bowing
            🤦 person facepalming facepalm
            🤷 person shrugging shrug
            🙋 person raising hand
            🙅 person gesturing no
            🙆 person gesturing ok
            💁 person tipping hand
            🧑‍🚀 astronaut pilot
            🧑‍✈️ pilot
            🥷 ninja
            🧙 mage wizard
            🧛 vampire
            🧟 zombie
            👑 crown king
            🎩 top hat
            """),
        Group("hearts", """
            ❤️ red heart love
            🧡 orange heart
            💛 yellow heart
            💚 green heart
            💙 blue heart
            💜 purple heart
            🖤 black heart
            🤍 white heart
            🤎 brown heart
            💔 broken heart
            ❤️‍🔥 heart on fire
            💕 two hearts
            💖 sparkling heart
            💯 hundred points 100
            💢 anger symbol
            💥 collision boom
            💫 dizzy
            💦 sweat droplets
            💨 dashing away
            💬 speech balloon
            💭 thought balloon
            💤 zzz sleep
            """),
        Group("nature", """
            🐶 dog face
            🐱 cat face
            🐭 mouse face
            🦊 fox
            🐻 bear
            🐼 panda
            🐨 koala
            🐯 tiger face
            🦁 lion
            🐮 cow face
            🐷 pig face
            🐸 frog
            🐵 monkey face
            🙈 see no evil monkey
            🙉 hear no evil monkey
            🙊 speak no evil monkey
            🐔 chicken
            🐧 penguin
            🐦 bird
            🦅 eagle
            🦆 duck
            🦉 owl
            🦇 bat
            🐺 wolf
            🐴 horse face
            🦄 unicorn
            🐝 honeybee
            🐛 bug
            🦋 butterfly
            🐌 snail
            🐞 lady beetle
            🦂 scorpion
            🕷️ spider
            🐢 turtle
            🐍 snake
            🦎 lizard skink
            🦖 t rex dinosaur
            🐙 octopus
            🦑 squid
            🦀 crab
            🐟 fish
            🐬 dolphin
            🐳 spouting whale
            🦈 shark
            🐊 crocodile
            🌵 cactus
            🌲 evergreen tree
            🌴 palm tree
            🍀 four leaf clover luck
            🌹 rose
            🌸 cherry blossom
            🌞 sun with face
            🌙 crescent moon
            ⭐ star
            🌟 glowing star
            ✨ sparkles
            ⚡ high voltage lightning
            🔥 fire lit
            🌈 rainbow
            ☀️ sun
            ☁️ cloud
            🌧️ cloud with rain
            ❄️ snowflake
            🌊 water wave
            🌍 globe earth
            🪐 ringed planet
            ☄️ comet
            🌌 milky way galaxy
            """),
        Group("food", """
            🍏 green apple
            🍎 red apple
            🍌 banana
            🍉 watermelon
            🍇 grapes
            🍓 strawberry
            🍒 cherries
            🍑 peach
            🥑 avocado
            🌶️ hot pepper
            🥕 carrot
            🌽 ear of corn
            🥔 potato
            🍞 bread
            🧀 cheese wedge
            🥓 bacon
            🍳 cooking egg
            🍔 hamburger
            🍟 french fries
            🍕 pizza
            🌭 hot dog
            🌮 taco
            🌯 burrito
            🍣 sushi
            🍜 steaming bowl noodles
            🍿 popcorn
            🍩 doughnut
            🍪 cookie
            🎂 birthday cake
            🍰 shortcake
            🧁 cupcake
            🍫 chocolate bar
            🍬 candy
            ☕ hot beverage coffee
            🍵 teacup tea
            🍺 beer mug
            🍻 clinking beer mugs cheers
            🥂 clinking glasses
            🍷 wine glass
            🥃 tumbler glass whisky
            🍸 cocktail glass
            🧃 beverage box
            🧉 mate
            🧊 ice
            """),
        Group("activities", """
            🎉 party popper tada
            🎊 confetti ball
            🎈 balloon
            🎁 wrapped gift
            🏆 trophy
            🥇 first place medal gold
            🥈 second place medal silver
            🥉 third place medal bronze
            🏅 sports medal
            🎖️ military medal
            ⚽ soccer ball
            🏀 basketball
            🏈 american football
            ⚾ baseball
            🎾 tennis
            🎯 bullseye direct hit target
            🎮 video game
            🕹️ joystick
            🎲 game die
            ♟️ chess pawn
            🃏 joker
            🎰 slot machine
            🎨 artist palette
            🎬 clapper board
            🎤 microphone
            🎧 headphone
            🎵 musical note
            🎶 musical notes
            🎸 guitar
            🥁 drum
            🎻 violin
            """),
        Group("travel", """
            🚀 rocket launch
            🛸 flying saucer ufo
            🛰️ satellite
            ✈️ airplane
            🚁 helicopter
            🚂 locomotive
            🚗 automobile car
            🚓 police car
            🚑 ambulance
            🚒 fire engine
            🏎️ racing car
            🚲 bicycle
            ⛵ sailboat
            🚢 ship
            ⚓ anchor
            🚧 construction
            🚨 police car light siren
            🗺️ world map
            🧭 compass
            🏠 house
            🏰 castle
            🏭 factory
            🏗️ building construction
            🗼 tokyo tower
            🌋 volcano
            🏝️ desert island
            ⏰ alarm clock
            ⌛ hourglass done
            ⏳ hourglass not done
            ⌚ watch
            """),
        Group("objects", """
            💰 money bag isk
            💸 money with wings
            💎 gem stone
            🔑 key
            🔒 locked
            🔓 unlocked
            🔨 hammer
            🛠️ hammer and wrench
            ⚙️ gear
            🔧 wrench
            🔩 nut and bolt
            ⚔️ crossed swords fight
            🗡️ dagger
            🔫 water pistol gun
            💣 bomb
            🧨 firecracker
            🛡️ shield
            🔭 telescope
            🔬 microscope
            💡 light bulb idea
            🔦 flashlight
            📱 mobile phone
            💻 laptop
            🖥️ desktop computer
            ⌨️ keyboard
            🖱️ computer mouse
            📷 camera
            📺 television
            📻 radio
            📡 satellite antenna
            🔋 battery
            🔌 electric plug
            📦 package
            📫 mailbox
            📝 memo
            📌 pushpin
            📎 paperclip
            📊 bar chart
            📈 chart increasing stonks
            📉 chart decreasing
            🗑️ wastebasket trash
            🧲 magnet
            🧪 test tube
            💊 pill
            🩹 adhesive bandage
            🪦 headstone
            ⚰️ coffin
            🏴‍☠️ pirate flag
            🏳️ white flag surrender
            🏁 chequered flag
            🚩 triangular flag
            """),
        Group("symbols", """
            ✅ check mark button yes done
            ☑️ check box with check
            ✔️ check mark
            ❌ cross mark no
            ❎ cross mark button
            ❓ red question mark
            ❔ white question mark
            ❗ red exclamation mark
            ‼️ double exclamation mark
            ⁉️ exclamation question mark
            ⚠️ warning
            🚫 prohibited
            ⛔ no entry
            🛑 stop sign
            ♻️ recycling symbol
            ➕ plus
            ➖ minus
            ➗ divide
            ✖️ multiply
            🟢 green circle
            🟡 yellow circle
            🟠 orange circle
            🔴 red circle
            🔵 blue circle
            🟣 purple circle
            ⚫ black circle
            ⚪ white circle
            🔺 red triangle pointed up
            🔻 red triangle pointed down
            ⬆️ up arrow
            ⬇️ down arrow
            ⬅️ left arrow
            ➡️ right arrow
            🔄 counterclockwise arrows button
            🔁 repeat button
            🆗 ok button
            🆕 new button
            🆒 cool button
            🆘 sos button
            🔞 no one under eighteen
            ℹ️ information
            🔔 bell
            🔕 bell with slash
            📢 loudspeaker
            📣 megaphone
            ♾️ infinity
            ☢️ radioactive
            ☣️ biohazard
            """),
    ];

    /// <summary>Every emoji whose name holds all the words typed, in the catalog's order. All of them for none.</summary>
    public static IReadOnlyList<EmojiEntry> Search(string words)
    {
        var terms = words.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var all = Groups.SelectMany(g => g.Emojis);
        return terms.Length == 0 ? [.. all]
            : [.. all.Where(e => terms.All(t => e.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || e.Emoji == t)).DistinctBy(e => e.Emoji)];
    }
}
