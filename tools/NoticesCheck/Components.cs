namespace EveConsole.Tools.NoticesCheck;

/// <summary>How a package's license is told when its own metadata says nothing, or too little.</summary>
/// <param name="Texts">Keys of the license texts: a file name in texts/ without .txt, "GPL-3.0-or-later" for the
/// repository's own LICENSE, or "pkg:Id" for the license file a package carries.</param>
sealed record Override(string? License = null, string[]? Texts = null, string? Copyright = null, string? Source = null,
                       string? Note = null);

/// <summary>Something that ships but is not a package of its own: a native library inside one, a font, the build's own downloads.</summary>
/// <param name="Requires">A package that must ship for this to be listed. A component whose package has gone fails the check
/// instead of being listed for something no longer there.</param>
sealed record Component(string Name, string Where, string License, string Copyright, string Source, string[] Texts,
                        string? Platforms = null, string? Note = null, string? Requires = null);

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

    private static readonly Override Vlc = new("LGPL-2.1-or-later (libVLC); some plugins GPL-2.0-or-later",
        ["LGPL-2.1-or-later", "GPL-2.0-or-later"], "VideoLAN and the VLC authors",
        "https://code.videolan.org/videolan/vlc (each release's source: https://download.videolan.org/pub/videolan/vlc/)",
        "libVLC and its plugins, used for alarm sounds and video. On Linux the system's own libVLC is used instead.");

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
        ["LibVLCSharp"] = new(Copyright: "VideoLAN and the LibVLCSharp authors", Source: "https://code.videolan.org/videolan/LibVLCSharp"),
        ["VideoLAN.LibVLC.Windows"] = Vlc,
        ["VideoLAN.LibVLC.Mac"] = Vlc,
        ["SoundFlow.Extensions.WebRtc.Apm"] = new(Note: "The .NET wrapper and its C layer. The native library is WebRTC's audio processing, listed under \"Inside the packages\"."),
    };

    private const string WebRtcPackage = "SoundFlow.Extensions.WebRtc.Apm";
    private const string WebRtcWhere = "the webrtc-apm native library in SoundFlow.Extensions.WebRtc.Apm";

    public static readonly Component[] Components =
    [
        new("WebRTC audio processing 2.1 (echo cancelling for Mumble voice)", WebRtcWhere, "BSD-3-Clause, with WebRTC's patent grant",
            "Copyright (c) 2011, The WebRTC project authors",
            "https://gitlab.freedesktop.org/pulseaudio/webrtc-audio-processing (PulseAudio's standalone copy of WebRTC's audio code)",
            ["WebRTC", "WebRTC-PATENTS"], Requires: WebRtcPackage),
        new("Abseil 20240722.0", WebRtcWhere + ", built in", "Apache-2.0", "Copyright The Abseil Authors",
            "https://github.com/abseil/abseil-cpp", ["Apache-2.0"], Requires: WebRtcPackage),
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

        new("eSpeak NG (pronunciation for Kokoro speech)", "the espeak folder, from KokoroSharp's content", "GPL-3.0-or-later",
            "Jonathan Duddington, Reece H. Dunn and the eSpeak NG authors", "https://github.com/espeak-ng/espeak-ng", ["GPL-3.0-or-later"],
            Requires: "KokoroSharp"),
        new("Kokoro voices", "the voices folder, from KokoroSharp's content", "Apache-2.0", "hexgrad (Kokoro-82M)",
            "https://huggingface.co/hexgrad/Kokoro-82M", ["Apache-2.0"], Requires: "KokoroSharp"),

        new("Piper 2023.11.14-2 (local speech)", "the piper folder; the build adds Piper's own release", "MIT", "Copyright (c) 2022 Michael Hansen",
            "https://github.com/rhasspy/piper", ["Piper"], Platforms: "Windows and Linux"),
        new("piper-phonemize", "the piper folder", "MIT", "Copyright (c) 2023 Michael Hansen",
            "https://github.com/rhasspy/piper-phonemize", ["piper-phonemize"], Platforms: "Windows and Linux"),
        new("eSpeak NG, Piper's copy", "the piper folder", "GPL-3.0-or-later", "Jonathan Duddington, Reece H. Dunn and the eSpeak NG authors",
            "https://github.com/rhasspy/espeak-ng", ["GPL-3.0-or-later"], Platforms: "Windows and Linux"),
        new("ONNX Runtime, Piper's copy", "the piper folder", "MIT", "Copyright (c) Microsoft Corporation",
            "https://github.com/microsoft/onnxruntime", ["pkg:Microsoft.ML.OnnxRuntime"], Platforms: "Windows and Linux",
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
