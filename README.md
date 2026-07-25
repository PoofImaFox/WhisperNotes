# NoteScribe

Live dictation and meeting notes from any Windows audio channel, using local Whisper.

Built for the case where Teams' own transcription isn't available, isn't enabled, or just doesn't
run — and you still need a declarative record of what was agreed. Point it at the audio endpoint
Teams is playing through and it transcribes the call as it happens, into an organised notes tree
you own. It also ingests recorded video after the fact, via ffmpeg.

**Everything runs locally.** No audio, transcript, or metadata leaves the machine. There is no
account, no API key, and no network call except the one-time model download.

## Quick start

```powershell
# Build
dotnet build

# See what you can listen to
dotnet run --project src/NoteScribe.Cli -- devices

# Pre-fetch the weights (do this BEFORE a meeting, not during one)
dotnet run --project src/NoteScribe.Cli -- models download base

# Transcribe a call live; Ctrl+C to stop and write the notes
dotnet run --project src/NoteScribe.Cli -- listen --project "Acme Corp" --title "Sprint review"

# Or transcribe a recording you already have
dotnet run --project src/NoteScribe.Cli -- transcribe --video "meeting.mp4" --project "Acme Corp"

# Or use the window
dotnet run --project src/NoteScribe.App
```

Full command reference: [`docs/CLI.md`](docs/CLI.md). Design notes: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## The UI

Pick a channel, confirm the level meter is moving, press Start. Dictation streams into the
centre pane with timestamps; you can type notes, flag action items, and drop markers alongside
it. Past sessions are browsable on the left, grouped by project and date.

Transcript lines are editable in place — Whisper reliably mangles proper nouns, and these notes
are your record, so fixing "Dan Whitfield" once beats finding it wrong three weeks later.

## Choosing the right channel

This is the one thing worth getting right, so read this bit.

There is no "Teams audio device" to select. What you capture is a **render endpoint in loopback
mode** — everything Windows is playing out of that device, which includes Teams. Two setups:

**Simple.** Capture your default output device. You get Teams, plus anything else making noise
— Spotify, notification dings, a YouTube tab. Fine if you're not playing anything else.

**Clean.** You already have Voicemeeter installed, so you can isolate Teams properly: set Teams'
output device to `Voicemeeter In 1`, then capture that endpoint in NoteScribe. Now the transcript
contains the call and nothing else. VB-Audio Cable works the same way if you'd rather not run
Voicemeeter.

Either way, **watch the level meter before you trust a recording.** Silently capturing the wrong
endpoint for an hour is the worst failure this tool has, and the meter is a two-second check
against it. The UI shows `silent — is this the endpoint Teams plays through?` when a channel is
producing nothing.

Loopback captures the remote participants *and* your own voice as Teams renders it back. It does
not tap your microphone directly.

## Choosing a model

| Model | Size | Roughly |
|---|---|---|
| `tiny` | 78 MB | Fast, noticeably error-prone. Fine for "what did they say just then". |
| `base` | 148 MB | Default. Good balance for clear meeting audio. |
| `small` | 488 MB | Better on accents and crosstalk. A reasonable default if your CPU allows. |
| `medium` | 1.5 GB | Noticeably better; slower. |
| `large-v3` | 3.1 GB | Best accuracy. |
| `large-v3-turbo` | 1.6 GB | Near-large accuracy, much faster. Good if you have the disk. |

Transcription runs on CPU by default. Download weights ahead of time — the first use of an
un-fetched `medium` would otherwise stall on a 1.5 GB download exactly when your meeting starts.

If your calls are full of client names, product names, or acronyms, set a vocabulary hint:

```powershell
dotnet run --project src/NoteScribe.Cli -- config set InitialPrompt "Acme Corp, Northwind, Entra ID, SCCM, Intune"
```

## Where notes go

Default root is `%USERPROFILE%\Documents\NoteScribe`. Sessions are filed so they sort correctly
in Explorer and read fine without the app:

```
NoteScribe/
  Acme Corp/
    2026/
      2026-07-25/
        143022-sprint-review/
          session.json        metadata
          transcript.jsonl    append-only log — the crash-safe write path
          notes.md            the artefact you actually share
          audio/session.wav   only with --keep-audio
```

`notes.md` leads with an **Action items** section, then the transcript with `[hh:mm:ss]` offsets.

The `.jsonl` is written append-only and flushed per utterance, so if the app dies mid-meeting you
lose at most the last sentence. `notes.md` is regenerated from it on finalize, and closing the
window mid-session finalizes rather than abandons.

## Requirements

- Windows 10/11
- .NET 10 SDK
- ffmpeg on `PATH` — only needed for `transcribe`. Set `--ffmpeg` or `config set FfmpegPath` if
  it lives somewhere unusual.
- **Visual Studio 2026 (18.x)** if you want an IDE. See below.

### Opening in Visual Studio

**Visual Studio 2022 cannot load this solution.** The .NET 10 SDK requires MSBuild 18, and
VS 2022 ships MSBuild 17.14, so all three projects fail to load with a misleading error:

```
error MSB4236: The SDK 'Microsoft.NET.Sdk' specified could not be found.
```

The real cause is further up the same output:

```
Version 10.0.301 of the .NET SDK requires at least version 18.0.0 of MSBuild.
The current available version of MSBuild is 17.14.23.42201.
```

Use **VS 2026** instead. If double-clicking sends it to the wrong version, launch VS 2026 first
and open `NoteTakingSpeechToText.slnx` from within it.

Note the solution is a `.slnx` — the newer XML format that `dotnet new sln` now emits by default.
VS 2026 reads it natively; VS 2022 needs an opt-in preview flag even before the MSBuild issue.
It only lists the three projects, so nothing about the build depends on it.

`dotnet build` from the command line works regardless of which Visual Studio is installed.

## Known limits

- **No speaker attribution yet.** Whisper is speech-to-text only; it has no notion of who is
  talking. Stable speaker labels with in-app renaming are planned — see the roadmap below.
- **Loopback can't separate participants.** Teams delivers one mixed stream. Where a recording
  has per-participant audio streams, `transcribe --list-streams` will show them and `--stream`
  will pick one.
- **CPU decode.** GPU acceleration exists in Whisper.net via separate CUDA/Vulkan runtime
  packages but isn't wired up.
- Windows only, because it's built on WASAPI.

## Roadmap

- Speaker labelling: stable `Speaker A/B/C` labels via voice-embedding clustering, renameable in
  the UI mid-session and applied retroactively.
- Optional dual capture (loopback + microphone) to separate "them" from "you" without any ML.
- GPU decode.

## Layout

| Project | What it is |
|---|---|
| `src/NoteScribe.Core` | Audio capture, transcription, ffmpeg, note storage. No UI. |
| `src/NoteScribe.Cli` | `notescribe` — headless capture, video ingest, session listing. |
| `src/NoteScribe.App` | Avalonia desktop app. |
