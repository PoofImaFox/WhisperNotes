# `notescribe` — command line reference

Everything the UI does, headlessly. Useful for batch-transcribing recordings and for running a
capture session from a terminal without the window in the way.

All commands write into the same organised notes tree the UI reads, so a session transcribed
from a recording shows up in the UI alongside live ones.

## `notescribe devices`

Lists the audio endpoints you can capture, with the ids to pass to `--channel`.

```
$ notescribe devices

LOOPBACK (system audio — capture what you HEAR, e.g. Teams)
  * speakers-realtek-hd    Speakers (Realtek High Definition Audio)   48000 Hz / 2ch   [default]
    vb-cable-in            CABLE Input (VB-Audio Virtual Cable)       48000 Hz / 2ch

MICROPHONE (capture what you SAY)
  * headset-mic            Headset Microphone (Jabra Evolve 65)       16000 Hz / 1ch   [default]

  * = system default for that role
```

To transcribe a Teams call you want a **LOOPBACK** entry — the render endpoint Teams is playing
through. See "Isolating Teams audio" in the README.

The short id in the first column is derived from the endpoint's friendly name, because the raw
WASAPI endpoint id looks like `{0.0.0.00000000}.{ab116aac-...}` and nobody is retyping that.
`--channel` accepts **either** form, plus the exact friendly name; ids are matched
case-insensitively. The short id stays stable when you change your default device, and a numeric
suffix (`-2`, `-3`) disambiguates endpoints whose names collide.

Add `--verbose` to also print each endpoint's raw id.

## `notescribe listen`

Live capture and dictation. Runs until Ctrl+C, then finalizes the session and writes `notes.md`.

| Option | Default | Meaning |
|---|---|---|
| `-c, --channel <id>` | last used, else default render loopback | Endpoint from `notescribe devices`. |
| `-t, --title <text>` | local timestamp | Session title; also the folder name. |
| `-p, --project <name>` | from settings | Groups the session into a project folder. |
| `-m, --model <size>` | `base` | `tiny`\|`base`\|`small`\|`medium`\|`large-v3`\|`large-v3-turbo` |
| `-l, --language <code>` | `auto` | ISO code, or `auto` to detect. |
| `--tag <tag>` | none | Repeatable. |
| `--prompt <text>` | from settings | Vocabulary hint — client names, acronyms. |
| `--keep-audio` | off | Also save the captured WAV next to the notes. |
| `--threads <n>` | CPU count, capped | Decoder threads. |
| `--quiet` | off | Suppress the live transcript echo; only print the final path. |

```
$ notescribe listen --channel speakers-realtek-hd --project "Acme Corp" --title "Sprint review" --model small

  model    small (already downloaded)
  channel  Speakers (Realtek High Definition Audio) [loopback]
  session  ~/Documents/NoteScribe/Acme Corp/2026/2026-07-25/143022-sprint-review
  Ctrl+C to stop.

[00:00:04] Right, let's get started. Dan, where are we on the firewall change?
[00:00:11] Still blocked. I've chased the network team twice this week.
^C
  finalized  1 min 12 sec, 18 entries
  notes      ~/Documents/NoteScribe/Acme Corp/2026/2026-07-25/143022-sprint-review/notes.md
```

Ctrl+C is the normal way to end a session, not an abort: the first press stops the capture, lets
the decoder flush the audio still buffered, appends those last segments, finalizes and renders
`notes.md`, then exits **130**. A second Ctrl+C terminates immediately, in case a decode wedges.

The endpoint actually used is written back to `LastChannelId`, which is what "last used" in the
table above reads on the next run. Nothing else about the invocation is persisted — a one-off
`--notes-root` or `--models-root` never ends up in the settings file.

With `--keep-audio` the captured WAV is written to `<session>/audio/session.wav` as 16 kHz mono
16-bit PCM, which is the format `transcribe` can re-read directly.

## `notescribe transcribe`

**This is the `--video` flag.** Converts a video (or any media file) to a 16 kHz mono audio
channel with ffmpeg, then runs the same local Whisper over it.

| Option | Default | Meaning |
|---|---|---|
| `-v, --video <path>` | *required* | Input media. Any container ffmpeg reads. `--input`/`-i` are accepted aliases. |
| `--stream <n>` | first audio stream | Which audio stream to take, from `--list-streams`. |
| `--list-streams` | off | Print the file's audio streams and exit without transcribing. |
| `-t, --title <text>` | input filename | Session title. |
| `-p, --project <name>` | from settings | Project folder. |
| `-m, --model <size>` | `base` | As above. |
| `-l, --language <code>` | `auto` | As above. |
| `--tag <tag>` | none | Repeatable. |
| `--prompt <text>` | from settings | Vocabulary hint. |
| `--keep-audio` | off | Keep the extracted WAV instead of deleting it. |
| `--threads <n>` | CPU count, capped | Decoder threads. |
| `-o, --output <dir>` | notes root | Override where this session is written. |

```
$ notescribe transcribe --video "Sprint review-20260725.mp4" --list-streams

  #1 aac 2ch 48000Hz [eng] "Mixed audio"
  #2 aac 1ch 48000Hz [und] "Dan Whitfield"

$ notescribe transcribe --video "Sprint review-20260725.mp4" --stream 2 --project "Acme Corp" --model small

  model         small (already downloaded)
  session       ~/Documents/NoteScribe/Acme Corp/2026/2026-07-25/143022-sprint-review-20260725
  extracting    ██████████████████████ 100%   (ffmpeg, stream #2 -> 16 kHz mono)
  transcribing  ████████████░░░░░░░░░░  58%   00:12:31 / 00:21:40
  notes         ~/Documents/NoteScribe/Acme Corp/2026/2026-07-25/143022-sprint-review-20260725/notes.md
```

`--stream` takes the ffmpeg stream index printed by `--list-streams`, which counts every stream in
the container — in a video file the first audio stream is usually `#1`, not `#0`.

Teams meeting recordings download as `.mp4`, so this is the recovery path for the case that
prompted this tool: Teams' own transcription didn't run, but you have the recording.

The extracted WAV goes to a temp file and is deleted when the run finishes; `--keep-audio` writes
it to `<session>/audio/session.wav` instead and leaves it there. `-o/--output` redirects just this
session's tree, overriding the global `--notes-root`.

Chunks that hold nothing but digital silence are not sent to the decoder. Whisper reliably invents
captions for silence, and an invented line in a billable record is worse than a missing one.

## `notescribe sessions`

Lists past sessions from the notes tree.

| Option | Meaning |
|---|---|
| `-p, --project <name>` | Filter to one project. |
| `--since <date>` / `--until <date>` | Date range, `yyyy-MM-dd` or relative like `7d`. |
| `-s, --search <text>` | Match against title and transcript text. |
| `--json` | Machine-readable output. |

Relative bounds take a `h` (hours), `d` (days) or `w` (weeks) suffix and count back from now.
`--until` on a plain `yyyy-MM-dd` includes that whole day.

Each session prints one summary line — start, project, title, duration, entry count — followed by
the path to its `notes.md`. `--json` emits the same records with the session id, tags, model,
source description and the transcript path as well.

## `notescribe models`

| Subcommand | Meaning |
|---|---|
| `models list` | Show each model, its size on disk, and whether it's downloaded. |
| `models download <size>` | Pre-fetch weights with a progress bar. |
| `models path` | Print the models directory. |

Pre-downloading matters: the first `listen` on an un-fetched `medium` would otherwise stall for
a 1.5 GB download at the exact moment your meeting starts.

## `notescribe config`

| Subcommand | Meaning |
|---|---|
| `config show` | Print effective settings and the settings file path. |
| `config set <key> <value>` | Set one value, e.g. `config set NotesRoot "D:\Notes"`. |
| `config path` | Print the settings file path. |

Keys, matched case-insensitively: `NotesRoot`, `ModelsRoot`, `Model`, `Language`, `Threads`,
`LastChannelId`, `DefaultProject`, `InitialPrompt`, `KeepSessionAudio`, `FfmpegPath`,
`Chunking.MinChunkSeconds`, `Chunking.MaxChunkSeconds`, `Chunking.SilenceMilliseconds`,
`Chunking.SilenceThreshold`. Passing an empty value clears an optional setting back to its default.

`config show` prints the *effective* settings — the file merged with this invocation's global
options. `config set` always writes the file itself, never the merged view.

## Global options

| Option | Meaning |
|---|---|
| `--notes-root <dir>` | Override the notes root for this invocation. |
| `--models-root <dir>` | Override the models directory. |
| `--ffmpeg <path>` | Explicit ffmpeg location if it isn't on `PATH`. |
| `--verbose` | Diagnostic logging, including resolved binary paths. |

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success. |
| 1 | Bad usage / invalid argument. Includes an unparseable command line, an unknown `--model`, a `--stream` the file doesn't have, and a `--video` path that does not exist. |
| 2 | Requested audio device not found — or the device the session was capturing died mid-run, in which case the notes are still finalized first. |
| 3 | ffmpeg or ffprobe missing, or the probe/conversion failed (including a file ffmpeg cannot read). |
| 4 | Model missing and could not be downloaded. |
| 130 | Interrupted with Ctrl+C. For `listen` this is the NORMAL exit and notes are still finalized. |

Every command reports failures as one sentence on stderr and one of these codes. Nothing prints a
stack trace; `--verbose` adds the full exception underneath if you need it.

Progress bars redraw in place on a real console and degrade to periodic complete lines when stdout
is redirected to a file or a pipe, so piping a run into a log never produces a wall of overwritten
bars.
