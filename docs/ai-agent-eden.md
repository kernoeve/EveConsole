# AI Agent (Eden)

Eve Cortex ships with an optional built-in conversational assistant — **Eden** by
default (you can rename it). It has read access to the data in your local database
via tool calls, so it can answer questions the UI isn't explicitly designed for.

> The agent is entirely optional and inactive until you set it up. If you don't
> configure it, you can ignore it completely. Its settings live in
> **Settings** (the **⚙** gear button, top-right) on the **AI Agent** tab.

## What it can do

- Answer questions about your character/corp data by querying the local database.
- Run against an **external** provider (Claude or OpenAI) or a **local** model
  (Ollama / LM Studio).
- Optional **text-to-speech** so it can talk back, and **speech-to-text** (with a
  global push-to-talk key) so you can talk to it hands-free while the game has
  focus.
- Customizable name, response verbosity, and voice.

Once enabled, a **✦ {name}** button appears in the title bar; click it to toggle
the chat panel.

## Setup

All settings are on the **AI Agent** tab. Click **Save** at the bottom when done.

1. **Personalisation** — optionally set an **Agent Name** (blank = *Eden*) and a
   **Response Verbosity** (*Concise*, *Balanced*, or *Detailed*).
2. **Enable** — tick **Enable {name} AI companion**. This just makes the panel
   available; you still need a provider configured below.
3. **Provider** — choose the **LLM Provider**, then fill in the section that
   appears:
    - **Claude (Anthropic)** — paste an API key (`sk-ant-…`) and optionally a model
      (default `claude-sonnet-4-6`; `claude-opus-4-8` and `claude-haiku-4-5` also
      work). Get a key at
      [console.anthropic.com](https://console.anthropic.com/settings/keys).
    - **OpenAI** — paste an API key (`sk-…`) and optionally a model (default
      `gpt-4o`). Get a key at
      [platform.openai.com](https://platform.openai.com/api-keys).
    - **Local LLM (Ollama / LM Studio)** — set the **API Endpoint** (default
      `http://localhost:11434`) and **Model Name** (default `llama3.1`). The runner
      must expose an OpenAI-compatible `/v1/chat/completions` endpoint, which
      Ollama and LM Studio do by default.
4. **Context Management** — optionally persist chat history to disk across restarts
   and set a **summarization threshold** (estimated tokens) at which older messages
   are silently compacted into a summary. Lower values cut cost per message but
   drop older context.

<!--
  SCREENSHOT SLOTS (add files to docs/images/, then uncomment):

  ![Agent configuration](images/agent-config.png)
-->

## Voice (optional)

- **Voice (TTS)** — pick a provider to have the agent speak:
    - **OpenAI** — cloud voices (shares the OpenAI API key above); choose voice,
      model, and speed.
    - **ElevenLabs** — cloud voices; needs its own API key and a voice ID.
    - **Kokoro** — high-quality neural TTS that runs fully offline; the model
      (~320 MB of neural-network weights, no executable code) downloads on first use.
    - **Piper** — fast offline neural TTS; the runtime is bundled, and only the
      chosen voice (a data file) downloads on demand.

    Use **Test Voice** to preview. Volume and mute are also available in the agent
    panel itself.
- **Speech Input (Push-to-Talk)** — pick a provider to talk to the agent:
    - **OpenAI Whisper** — cloud transcription (shares the OpenAI key); Windows and
      Linux.
    - **Local Whisper** — runs on this machine, no API key; Windows only, and a model
      file must be downloaded first.

    Then choose a **Microphone Device** (click **↺** to refresh the list) and a
    **Global Push-to-Talk Key** — hold it to record even when EVE has focus. F13–F20
    are rarely captured by games and make good PTT keys. The mic button in the panel
    works regardless of this setting.

## Related

- [Getting Started](getting-started.md)
- [Tools Reference](tools-reference.md)
