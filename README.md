# WhisperNotes

Live dictation and meeting notes from any Windows audio channel, using local Whisper.

Built for the case where Teams' own transcription isn't available, isn't enabled, or just doesn't
run — and you still need a declarative record of what was agreed. Point it at the audio endpoint
Teams is playing through and it transcribes the call as it happens, into an organised notes tree
you own. It also ingests recorded video after the fact, via ffmpeg.

**Capture and transcription run entirely locally.** No audio, transcript, or metadata leaves the
machine. There is no account and no API key, and the only network call is the one-time model
download.

The one exception is the optional AI note assistant, and it is off the network by default: it
ships pointed at [Ollama](https://ollama.com) on `localhost`, so the offline guarantee above
still holds out of the box. Switching the provider to Anthropic in Settings is what sends note
text to a third party — that is opt-in, requires a key you supply, and is the only way anything
leaves the machine. Nothing is sent implicitly, and the audio itself is never sent under either
provider.

## Quick start

```powershell
# Build
dotnet build

# See what you can listen to
dotnet run --project src/WhisperNotes.Cli -- devices

# Pre-fetch the weights (do this BEFORE a meeting, not during one)
dotnet run --project src/WhisperNotes.Cli -- models download base

# Transcribe a call live; Ctrl+C to stop and write the notes
dotnet run --project src/WhisperNotes.Cli -- listen --project "Acme Corp" --title "Sprint review"

# Or transcribe a recording you already have
dotnet run --project src/WhisperNotes.Cli -- transcribe --video "meeting.mp4" --project "Acme Corp"

# Or use the window
dotnet run --project src/WhisperNotes.App
```

Full command reference: [`docs/CLI.md`](docs/CLI.md). Design notes: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## The UI

Three pages, on a nav rail down the left: **Meeting** (`Ctrl+1`), **Notes** (`Ctrl+2`), and
**Inputs** (`Ctrl+3`). The
capture toolbar and the status bar sit outside the page switch, so a recording in progress stays
visible and stoppable from any page.

### Meeting

Configure one or more sources on the Inputs page, confirm the level meter is moving, then press
Start. Every enabled source starts together and is transcribed independently in parallel.
Dictation streams into the
centre pane with timestamps; you can type notes, flag action items, and drop markers alongside
it. Past sessions are browsable on the left, grouped by project and date.

Already recorded the meeting? Choose **Import video…** beside the recording button and pick the
file. WhisperNotes extracts the first audio stream, transcribes it immediately with the selected
Whisper model, identifies anonymous speaker changes, and opens the finished transcript in the same
Meeting view. Progress and cancellation stay visible in the status bar; cancelling preserves any
lines already decoded.

Transcript lines are editable in place — Whisper reliably mangles proper nouns, and these notes
are your record, so fixing "Dan Whitfield" once beats finding it wrong three weeks later.

When a meeting has more than one voice, the finished transcript uses anonymous, session-local
labels such as `Speaker 1` and `Speaker 2`. A return to an earlier voice reuses its original label
when the voice model can match it; otherwise a new anonymous label still makes the speaker change
visible. Nothing is looked up against an account or people database. The labels can be renamed
across the whole session from the Meeting header after recording stops.

### Notes

A markdown editor for writing the thing you actually send: the plan, the brief, the decision log.
It's AvaloniaEdit with VS Code's TextMate grammars, so it highlights markdown the way your editor
does, with a live preview pane beside it.

Down the right is the assistant. Quick actions run against the note — meeting summary, action
items, decision log, implementation plan, step list, risk register, stakeholder update, executive
brief, follow-up email, requirements extraction, transcript cleanup — plus a free-text
instruction box for anything not on the list. **"New note from meeting…"** seeds a note from a
finished session's transcript, which is the usual starting point.

Transcript-backed notes expose their detected speakers in the library pane. Choose a label and
rename it once to update every structured transcript and action-item occurrence without replacing
the same words in ordinary prose. The change is saved as a normal revision, so it can be restored.

The open note can be exported as plain Markdown, a self-contained HTML page, or a paginated PDF.
**Export all for Obsidian (.zip)** packages the entire authored-note library with project folders
and Obsidian-compatible YAML properties. Extract it and either open the folder as a vault or move
the folders into an existing vault; the files remain ordinary UTF-8 Markdown and require no
WhisperNotes plugin.

Two things about the assistant are deliberate:

- **It never edits your note directly.** Output streams into a preview and you choose Apply,
  Insert below, Copy, or Discard. Nothing changes until you say so.
- **Every change is versioned.** The History tab lists each revision with a `+N −M` stat and a
  unified diff, marks AI-authored ones distinctly, and restores any of them in one click —
  including undoing a restore. Whatever the assistant does to a note, you can get the previous
  text back verbatim.

The provider is configurable (gear icon): Ollama on localhost by default, Anthropic if you supply
a key. See the note on what leaves the machine at the top of this file.

### Inputs

Add any loopback, microphone, or single-application endpoint that belongs in a session, give each
a useful display name, and enable the set to record. Configured devices persist between launches;
an unplugged device stays visible as unavailable instead of silently falling back to a different
endpoint. Disabled inputs remain configured for later. If an application input is configured on a
Windows build that can't isolate it (see [Per-application capture](#per-application-capture)
below), the page says so.

Each source owns an independent capture and Whisper pipeline. A microphone source is labelled
with its configured display name in the transcript; loopback sources continue to use anonymous
voice attribution when diarization is enabled.

## Choosing the right inputs

This is the one thing worth getting right, so read this bit.

There is no "Teams audio device" to select. For the remote side, capture a **render endpoint in
loopback mode** — everything Windows is playing out of that device, which includes Teams. Two
setups:

**Simple.** Capture your default output device. You get Teams, plus anything else making noise
— Spotify, notification dings, a YouTube tab. Fine if you're not playing anything else.

**Clean.** You already have Voicemeeter installed, so you can isolate Teams properly: set Teams'
output device to `Voicemeeter In 1`, then capture that endpoint in WhisperNotes. Now the transcript
contains the call and nothing else. VB-Audio Cable works the same way if you'd rather not run
Voicemeeter.

### Per-application capture

**Cleaner still, if your machine supports it.** WhisperNotes can capture a single running
application's audio directly, without routing anything through Voicemeeter or VB-Cable first. Pick
`teams.exe` (or Outlook, or whatever else is talking) as an input and only that process's audio
goes into the session — everything else playing on the machine is left out. `whispernotes devices`
lists running applications in a third section, and the CLI's `--channel` takes the same slug, raw
id, or bare executable name documented in [`docs/CLI.md`](docs/CLI.md). Application inputs run
alongside any other input, so a Teams-audio input and a microphone input, or two different
applications, are captured and transcribed in parallel like any other pair of sources.

Be honest with yourself about the requirement, though: this uses WASAPI process loopback, which
Windows only exposes starting at build 20348 — in practice that's **Windows 11** (or Server 2022).
Microsoft's own documentation lists the floor as "Windows 10 Build 20348", which reads like a
Windows 10 update but isn't one — 20348 is the Windows Server 2022 RTM build, and retail Windows 10
tops out at 19045 (22H2), never reaching it. If you're on Windows 10, this feature is not available
to you in practice.

That said, picking an application input on an unsupported build does not fail or silently do
nothing: it falls back to recording the whole machine's audio, same as a loopback endpoint, and
says so wherever the choice is visible — in `whispernotes devices`, on the Inputs page, and in the
capture bar's status text. You still get a transcript; it just isn't scoped to the one app the way
you asked for.

Either way, **watch the level meter before you trust a recording.** Silently capturing the wrong
endpoint for an hour is the worst failure this tool has, and the meter is a two-second check
against it. The toolbar monitors the first enabled input before recording and combines peaks from
all active inputs while recording.

Add your microphone as a second enabled input when you want a clean local track. It is captured
and transcribed at the same time as the loopback input, without relying on Teams to echo your voice
into the playback mix.

## Choosing a model

| Model | Size | Roughly |
|---|---|---|
| `tiny` | 78 MB | Fast, noticeably error-prone. Fine for "what did they say just then". |
| `base` | 148 MB | Default. Good balance for clear meeting audio. |
| `small` | 488 MB | Better on accents and crosstalk. A reasonable default if your CPU allows. |
| `medium` | 1.5 GB | Noticeably better; slower. |
| `large-v3` | 3.1 GB | Best accuracy. |
| `large-v3-turbo` | 1.6 GB | Near-large accuracy, much faster. Good if you have the disk. |

Download weights ahead of time — the first use of an un-fetched `medium` would otherwise stall on
a 1.5 GB download exactly when your meeting starts.

### GPU decode

Transcription runs on the GPU wherever the machine has one, with no toolkit to install and nothing
to switch on. It matters more than the model choice does. Measured here on an RTX 3080 over 162 s
of speech, `large-v3-turbo`, end to end through `transcribe`:

| | Wall clock |
|---|---|
| CPU (`--no-gpu`) | 104 s |
| GPU | 7.6 s |

The backend is **Vulkan**, chosen over CUDA on purpose: it measured the same speed on Ampere
(~80x realtime either way) while CUDA additionally needs a multi-gigabyte CUDA Toolkit install to
supply `cublas64_13.dll`. Vulkan needs `vulkan-1.dll`, which current NVIDIA, AMD and Intel drivers
already install. The reasoning and the numbers are in `Directory.Packages.props`; build with
`dotnet build -p:WhisperCudaRuntime=true` if your hardware prefers CUDA.

Check what yours resolved to:

```powershell
dotnet run --project src/WhisperNotes.Cli -- doctor
```

That prints the backend and every adapter it can see. A desktop with an active integrated GPU
shows two — pick between them with `--gpu-device <n>`, and keep the winner with
`config set Gpu.Device <n>`. `config set Gpu.Enabled false` (or `--no-gpu` for one run) forces the
CPU path, which is only worth doing to work around a bad driver.

The desktop app reports the same thing in the status bar, next to the ffmpeg line, from the moment
the first transcription starts.

If your calls are full of client names, product names, or acronyms, set a vocabulary hint:

```powershell
dotnet run --project src/WhisperNotes.Cli -- config set InitialPrompt "Acme Corp, Northwind, Entra ID, SCCM, Intune"
```

## Where notes go

Default root is `%USERPROFILE%\Documents\WhisperNotes`. Sessions are filed so they sort correctly
in Explorer and read fine without the app:

```
WhisperNotes/
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

Authored notes from the **Notes** page live under `WhisperNotes/_documents/`. That storage folder
also contains revision history, so use the export controls (or `whispernotes export`) when you want
a clean portable copy rather than copying the internal folders directly.

## Requirements

- Windows 10/11. [Per-application capture](#per-application-capture) additionally needs Windows 11
  (build 20348+); it falls back to whole-machine loopback rather than failing on Windows 10.
- .NET 10 SDK
- ffmpeg on `PATH` — needed for CLI `transcribe` and the desktop **Import video…** action. Set
  `--ffmpeg` or `config set FfmpegPath` if it lives somewhere unusual.
- A Vulkan-capable GPU driver for GPU decode. Optional, in that it falls back to the CPU without
  one, but the fallback is about 40x slower. See [GPU decode](#gpu-decode).
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
It lists the three product projects plus the focused Core test project, so nothing about the build
depends on it.

`dotnet build` from the command line works regardless of which Visual Studio is installed.

## Known limits

- **Loopback can't separate participants.** Teams delivers one mixed stream. Where a recording
  has per-participant audio streams, `transcribe --list-streams` will show them and `--stream`
  will pick one. Anonymous speaker labelling distinguishes voices in a mixed stream, but overlapping
  speech can still be ambiguous.
- **GPU decode needs a Vulkan-capable driver.** Every current NVIDIA, AMD and Intel driver
  installs one, but a machine without it silently falls back to the CPU and runs about 40x slower.
  `whispernotes doctor` says which one you got.
- **Per-application capture needs Windows 11 (build 20348+).** On Windows 10 it does not fail —
  it falls back to whole-machine loopback and says so in `devices`, the Inputs page, and the
  capture bar. See [Per-application capture](#per-application-capture).
- Windows only, because it's built on WASAPI.

## Roadmap

- Nothing pending.

## Layout

| Project | What it is |
|---|---|
| `src/WhisperNotes.Core` | Audio capture, transcription, ffmpeg, note storage. No UI. |
| `src/WhisperNotes.Cli` | `whispernotes` — headless capture, video ingest, session listing. |
| `src/WhisperNotes.App` | Avalonia desktop app. |
