namespace EveConsole.Tools.NoticesCheck;

/// <summary>How a package's license is told when its own metadata says nothing, or too little.</summary>
/// <param name="Texts">Keys of the license texts: a file name in texts/ without .txt, "GPL-3.0-or-later" for the
/// repository's own LICENSE, or "pkg:Id" for the license file a package carries.</param>
/// <param name="Version">The package version a version-specific source link was written for. Another version fails the
/// check, so a package bump cannot leave the notices pointing at the old version's source.</param>
sealed record Override(string? License = null, string[]? Texts = null, string? Copyright = null, string? Source = null,
                       string? Note = null, string? Version = null);

/// <summary>Something that ships but is not a package of its own: a native library inside one, a font, the build's own downloads.</summary>
/// <param name="Requires">A package that must ship for this to be listed. A component whose package has gone fails the check
/// instead of being listed for something no longer there.</param>
/// <param name="RequiresVersion">The version of that package this entry's versions and source link were read from. Another
/// version fails the check until the entry is looked at again.</param>
sealed record Component(string Name, string Where, string License, string Copyright, string Source, string[] Texts,
                        string? Platforms = null, string? Note = null, string? Requires = null, string? RequiresVersion = null);

static class Known
{
    /// <summary>Packages in the restore that put nothing into a release.</summary>
    public static readonly Dictionary<string, string> NotShipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Avalonia.BuildServices"] = "build-time only",
        // ExcludeAssets="all" in EveConsole.csproj: the WebRTC extension depends on it, and none of it is used or shipped.
        ["SoundFlow"] = "excluded from the build",
    };

    public static bool IsNotShipped(string id) =>
        NotShipped.ContainsKey(id) || id.EndsWith(".NativeAssets.WebAssembly", StringComparison.OrdinalIgnoreCase);

    /// <summary>Notice files a package carries that describe things this app does not ship.</summary>
    public static readonly HashSet<string> NoticeSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        // Covers SoundFlow's own natives (MiniAudio, PortMidi, FFmpeg), which are left out; its WebRTC part is listed below.
        "SoundFlow.Extensions.WebRtc.Apm",
    };

    // ⚠️ Before Overrides: static fields initialise in the order they are written, and Overrides reads these.
    private static readonly Override OpenTk = new("MIT", ["OpenTK"], Source: "https://github.com/opentk/opentk",
        Note: "The package names no license; its project is MIT.");

    private static readonly Override Npgsql = new("PostgreSQL", ["Npgsql"]);

    // ⚠️ The source links name the exact version each package carries, read from its libvlccore's own version string: the
    // package version is not the VLC version (the Windows package's 3.0.21 is VLC's 3.0.21-1 tag; the Mac package's
    // 3.1.3.2 is VLC 3.0.24). Copyleft code is passed on with its source, and linking upstream is enough only when the
    // link leads to the source of what ships.
    private static Override Vlc(string source, string version) => new("LGPL-2.1-or-later (libVLC); some plugins GPL-2.0-or-later",
        ["LGPL-2.1-or-later", "GPL-2.0-or-later"], "VideoLAN and the VLC authors", source,
        "libVLC and its plugins, used for alarm sounds and video. On Linux the system's own libVLC is used instead.", version);

    public static readonly Dictionary<string, Override> Overrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OpenTK.Audio.OpenAL"] = OpenTk,
        ["OpenTK.Core"] = OpenTk,
        ["OpenTK.Mathematics"] = OpenTk,
        ["PortAudioSharp2"] = new("Apache-2.0", ["Apache-2.0"], Source: "https://github.com/csukuangfj/PortAudioSharp2",
            Note: "The package names no license; its project is Apache-2.0."),
        // The PostgreSQL license names its holder in its first line, so the generic text would credit the wrong one.
        ["Npgsql"] = Npgsql,
        ["Npgsql.EntityFrameworkCore.PostgreSQL"] = Npgsql,
        ["LibVLCSharp"] = new(Copyright: "VideoLAN and the LibVLCSharp authors",
            Source: "https://code.videolan.org/videolan/LibVLCSharp/-/tree/3.9.1", Version: "3.9.1"),
        ["VideoLAN.LibVLC.Windows"] = Vlc("VLC 3.0.21-1, https://code.videolan.org/videolan/vlc/-/tree/3.0.21-1", "3.0.21"),
        ["VideoLAN.LibVLC.Mac"] = Vlc("VLC 3.0.24, https://download.videolan.org/pub/videolan/vlc/3.0.24/vlc-3.0.24.tar.xz", "3.1.3.2"),
        ["SoundFlow.Extensions.WebRtc.Apm"] = new(Note: "The .NET wrapper and its C layer. The native library is WebRTC's audio processing, listed under \"Inside the packages\"."),
    };

    private const string WebRtcPackage = "SoundFlow.Extensions.WebRtc.Apm";
    private const string WebRtcWhere = "the webrtc-apm native library in SoundFlow.Extensions.WebRtc.Apm";

    public static readonly Component[] Components =
    [
        new("WebRTC audio processing 2.1 (echo cancelling for Mumble voice)", WebRtcWhere, "BSD-3-Clause, with WebRTC's patent grant",
            "Copyright (c) 2011, The WebRTC project authors",
            "https://gitlab.freedesktop.org/pulseaudio/webrtc-audio-processing/-/tree/v2.1 (PulseAudio's standalone copy of WebRTC's audio code)",
            ["WebRTC", "WebRTC-PATENTS"], Requires: WebRtcPackage, RequiresVersion: "1.4.2"),
        new("Abseil 20240722.0", WebRtcWhere + ", built in", "Apache-2.0", "Copyright The Abseil Authors",
            "https://github.com/abseil/abseil-cpp/tree/20240722.0", ["Apache-2.0"], Requires: WebRtcPackage, RequiresVersion: "1.4.2"),
        new("PFFFT", WebRtcWhere, "FFTPACK license (BSD-style)", "Copyright (c) 2013 Julien Pommier; (c) 2004 UCAR",
            "https://webrtc.googlesource.com/src/+/refs/heads/main/third_party/pffft", ["PFFFT"], Requires: WebRtcPackage),
        new("RNNoise", WebRtcWhere, "BSD-3-Clause", "Copyright (c) 2017 Mozilla; (c) 2007-2017 Jean-Marc Valin; (c) 2005-2017 Xiph.Org Foundation; (c) 2003-2004 Mark Borgerding",
            "https://gitlab.xiph.org/xiph/rnnoise", ["RNNoise"], Requires: WebRtcPackage),
        new("Ooura FFT", WebRtcWhere, "Ooura's license (free use with attribution)", "Copyright Takuya OOURA, 1996-2001",
            "http://www.kurims.kyoto-u.ac.jp/~ooura/fft.html", ["Ooura-FFT"], Requires: WebRtcPackage),
        new("FFT routines", WebRtcWhere, "Mark Olesen's license (permissive)", "Copyright (c) 1995, 1997 Mark Olesen, Queen's University at Kingston",
            "https://webrtc.googlesource.com/src/+/refs/heads/main/modules/third_party/fft", ["Olesen-FFT"], Requires: WebRtcPackage),
        new("Square root routine", WebRtcWhere, "Public domain", "Written by Wilco Dijkstra, 1996",
            "https://webrtc.googlesource.com/src/+/refs/heads/main/common_audio/third_party/spl_sqrt_floor", ["spl_sqrt_floor"], Requires: WebRtcPackage),

        new("whisper.cpp and ggml (local speech recognition)", "the native libraries in Whisper.net.Runtime, Whisper.net.Runtime.Vulkan and Whisper.net.Runtime.Metal",
            "MIT", "Copyright (c) 2023-2026 The ggml authors", "https://github.com/ggml-org/whisper.cpp", ["whisper.cpp"],
            Requires: "Whisper.net.Runtime"),
        new("PortAudio (microphone and speakers)", "the native libraries in the org.k2fsa.portaudio.runtime packages", "MIT-style PortAudio license",
            "Copyright (c) 1999-2006 Ross Bencina and Phil Burk", "https://github.com/PortAudio/portaudio", ["PortAudio"],
            Requires: "PortAudioSharp2"),
        new("SQLite", "the e_sqlite3 native library in SQLitePCLRaw.lib.e_sqlite3", "Public domain", "The SQLite authors",
            "https://sqlite.org/copyright.html", [], Note: "SQLite's authors have dedicated it to the public domain.",
            Requires: "SQLitePCLRaw.lib.e_sqlite3"),
        new("Inter typeface", "Avalonia.Fonts.Inter", "SIL Open Font License 1.1", "Copyright (c) 2016 The Inter Project Authors",
            "https://github.com/rsms/inter", ["OFL-1.1-Inter"], Requires: "Avalonia.Fonts.Inter"),

        // The version is read from the libraries themselves: KokoroSharp does not say which eSpeak NG it carries.
        new("eSpeak NG 1.52.0 (pronunciation for Kokoro speech)", "the espeak folder, from KokoroSharp's content", "GPL-3.0-or-later",
            "Jonathan Duddington, Reece H. Dunn and the eSpeak NG authors", "https://github.com/espeak-ng/espeak-ng/tree/1.52.0",
            ["GPL-3.0-or-later"], Requires: "KokoroSharp", RequiresVersion: "0.6.7"),
        new("Kokoro voices", "the voices folder, from KokoroSharp's content", "Apache-2.0", "hexgrad (Kokoro-82M)",
            "https://huggingface.co/hexgrad/Kokoro-82M", ["Apache-2.0"], Requires: "KokoroSharp"),

        new("Piper 2023.11.14-2 (local speech)", "the piper folder; the build adds Piper's own release", "MIT", "Copyright (c) 2022 Michael Hansen",
            "https://github.com/rhasspy/piper/tree/2023.11.14-2", ["Piper"], Platforms: "Windows and Linux"),
        // ⚠️ Piper's release built its dependencies from their master branches on the day (2023-11-14), so these are
        // the commits those branches were at then. The version strings in Piper's own libraries agree.
        new("piper-phonemize 2023.11.14-4", "the piper folder", "MIT", "Copyright (c) 2023 Michael Hansen",
            "https://github.com/rhasspy/piper-phonemize/tree/2023.11.14-4", ["piper-phonemize"], Platforms: "Windows and Linux"),
        new("eSpeak NG 1.52.0.1, Piper's copy", "the piper folder", "GPL-3.0-or-later", "Jonathan Duddington, Reece H. Dunn and the eSpeak NG authors",
            "https://github.com/rhasspy/espeak-ng/tree/0f65aa301e0d6bae5e172cc74197d32a6182200f", ["GPL-3.0-or-later"],
            Platforms: "Windows and Linux"),
        new("ONNX Runtime 1.14.1, Piper's copy", "the piper folder", "MIT", "Copyright (c) Microsoft Corporation",
            "https://github.com/microsoft/onnxruntime/tree/v1.14.1", ["pkg:Microsoft.ML.OnnxRuntime"], Platforms: "Windows and Linux",
            Note: "Its other notices are in the ONNX Runtime package's notice file, listed above."),
        new("libtashkeel model", "the piper folder", "MIT", "Copyright (c) 2023 Musharraf",
            "https://github.com/mush42/libtashkeel", ["libtashkeel"], Platforms: "Windows and Linux"),
    ];

    /// <summary>Not in the download: fetched the first time the feature that needs it is used, each under its own license.</summary>
    public static readonly string[] DownloadedLater =
    [
        "Whisper speech recognition models (MIT, OpenAI), from https://huggingface.co/ggerganov/whisper.cpp",
        "The Kokoro speech model (Apache-2.0, hexgrad), from https://github.com/taylorchu/kokoro-onnx",
        "Piper voices, each under the license shown on its model card at https://huggingface.co/rhasspy/piper-voices",
    ];

    /// <summary>Used where the system has it; not part of any release.</summary>
    public static readonly string[] FromTheSystem =
    [
        "libVLC on Linux: the distribution's own package (vlc or libvlc).",
        "The graphics driver's Vulkan loader, when speech recognition runs on the GPU.",
        "A PostgreSQL server, when one is set up as the shared database.",
    ];

    public static readonly string[] Trademarks =
    [
        "EVE Online and the EVE logo are the registered trademarks of CCP hf. EVE Console is not affiliated with or endorsed by CCP hf.",
        "The PostgreSQL and SQLite logos are shown only to name the database in use; they belong to the PostgreSQL Community Association and the SQLite authors.",
    ];
}
