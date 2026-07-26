# WhisperNotes — architecture

Local speech-to-text note taking. Captures one or more configured Windows audio endpoints
(typically Teams loopback plus an optional microphone), runs local Whisper over them, and writes
timestamped notes into an organised directory tree. Also ingests video files via ffmpeg.

Capture, transcription and storage run offline. Audio never leaves the machine under any
configuration. The optional AI note assistant is the sole egress point and defaults to a local
Ollama endpoint; the Anthropic provider is opt-in and sends note *text* only. See
[The AI assistant](#the-ai-assistant) below.

## Projects

| Project | Type | Responsibility |
|---|---|---|
| `WhisperNotes.Core` | class library, `net10.0-windows` | Audio capture, transcription, ffmpeg, note storage. No UI. |
| `WhisperNotes.Cli` | console, `net10.0-windows` | Headless transcription, `--video` ingest, session listing. |
| `WhisperNotes.App` | Avalonia desktop, `net10.0-windows` | Multi-input settings, live dictation, notes browser/editor. |

`Cli` and `App` both depend on `Core`. They never depend on each other.

## The live pipeline

```
enabled input 1 -> capture -> resample -> chunk -> independent Whisper decoder --\
enabled input 2 -> capture -> resample -> chunk -> independent Whisper decoder ----> sourced merge
enabled input N -> capture -> resample -> chunk -> independent Whisper decoder --/       |
                                                                                          v
                                                                    NoteEntry append -> UI
```

`ParallelLiveTranscriptionEngine` starts every configured source concurrently. Each source owns a
separate `LiveTranscriptionEngine` and native `ITranscriber`; Whisper instances are stateful and
must not be called concurrently through one shared object. Results enter a single
`SourcedTranscriptSegment` stream carrying the durable input id/name and physical channel, so
storage remains serialized even though capture and decoding are parallel. A source failure cancels
its siblings after already decoded results are drained. Normal stop flushes every source's captured
tail.

The chunker exists because Whisper decodes a whole buffer at once. Cutting on silence rather
than on a fixed clock keeps sentences intact, which matters a lot for readable meeting notes.

`AudioFrame` is the normalisation boundary: **everything downstream of capture assumes
16 kHz mono float**. Capture implementations own the resampling.

## The video path

```
video file
   -> ffprobe: list audio streams          (IMediaConverter.ProbeAudioStreamsAsync)
   -> ffmpeg -map 0:<n> -ar 16000 -ac 1    (IMediaConverter.ExtractAudioAsync)
   -> 16 kHz mono WAV
   -> chunked read                          (IWavReader.ReadChunksAsync)
   -> same ITranscriber as the live path
   -> same INoteRepository
```

Both paths converge on `ITranscriber` and `INoteRepository`, so a session transcribed from a
recording is indistinguishable from a live one once written.

Recorded-media ingest for the desktop is exposed by `IRecordedMediaTranscriptionService`, using
the same probe → ffmpeg extraction → chunked decode → diarization → finalize lifecycle as the CLI.
Cancellation after session creation returns a finalized partial result: decoded lines remain
durable, the temporary WAV is removed, and `notes.md` is still rendered.

## Speaker attribution

Diarization runs on both live capture and imported media. It rides on the transcriber's segment
boundaries rather than doing its own voice-activity pass:

```
decode loop (per live or imported audio chunk)
   -> for each decoded segment:
        append the line                        (INoteRepository.AppendEntryAsync)
        embed the same audio it came from      (ISpeakerAttributor.Observe)
   -> once the session is complete:
        cluster the voice prints               (ISpeakerAttributor.Build -> SpeakerTimeline)
        stamp Speaker onto the written lines   (SpeakerAttribution.ApplyAsync)
   -> finalize, so notes.md renders with the labels
```

Three decisions are worth recording, because each has an obvious-looking alternative:

**Reusing whisper's segment boundaries.** A separate VAD would give a second, independent estimate
of where speech is, and the two would drift against each other — leaving lines whose speaker came
from a turn that does not quite line up with them. Riding on the boundaries that already exist means
the timeline and the transcript are aligned by construction. It also costs no second pass over the
audio: at the moment a segment is decoded, the buffer it was decoded from is still in memory.
For live capture the engine observes that PCM before yielding the stitched segment, which avoids
retaining the complete meeting in memory.

**Labelling afterwards rather than during.** Which voice a line belongs to cannot be known until the
whole recording has been heard — the second speaker is only recognisable as a second speaker once
there is a first to compare against. So lines are written unattributed as they are decoded, which is
what keeps the crash-safety guarantee, and the speaker is applied afterwards as an ordinary edit
(a new jsonl record with the same entry id, last-write-wins).

**One embedding per line, not several.** A line is the smallest thing that can carry a label, since
splitting its text would need word-level timings we do not have. Sampling a long line repeatedly
could only produce several votes for the one label it was going to get anyway, at quadratic cost in
the clustering.

The result is a `SpeakerTimeline` of numbered speakers — never names. Clustering knows that two
stretches of audio came from the same voice and nothing more, so the export says "Speaker 2" until a
human recognises the voice and renames it. A recording with one voice throughout is left unlabelled
rather than having every line prefixed with "Speaker 1:", which would be clutter for no information.

## On-disk layout

The notes tree is designed to be readable without the app, and to sort correctly in Explorer.

```
<NotesRoot>/
  <Project or "_unfiled">/
    2026/
      2026-07-25/
        143022-sprint-review/
          session.json          # NoteSession metadata
          transcript.jsonl      # append-only NoteEntry log, one JSON object per line
          notes.md              # rendered on finalize; the artefact you actually share
          audio/session.wav     # only when KeepSessionAudio is on
```

Why `jsonl` plus a rendered `md`: the jsonl is the crash-safe write path — appending a line is
atomic enough that killing the process mid-meeting costs you at most the last utterance. The
markdown is a derived view, regenerated on finalize and safely re-renderable at any time.

`session.json` is rewritten (temp file + atomic move) whenever metadata changes, which is rare.
`transcript.jsonl` is opened once per session and appended with a flush per entry.

### Authored notes

Notes written on the Notes page live beside the session tree, under a sibling `_documents/`
directory so the recursive `session.json` scan can never pick them up:

```
<NotesRoot>/
  _documents/
    <slug>-<8-hex>/
      document.json                                  # metadata only; the body is NOT duplicated here
      content.md                                     # the live body
      revisions/
        20260725T1911013484493Z-ead0ac54.json        # NoteRevision; filename sort == chronological
```

Same reasoning as the transcript: `content.md` is the artefact you can read without the app, and
the revision files are the crash-safe history. A revision is written **before** `content.md` is
replaced, so the worst a crash can do is leave one redundant revision rather than lose text.

Portable export is deliberately downstream of this store. `INoteExportService` takes immutable
`NoteDocument` values and produces in-memory artifacts:

- Markdown preserves the live body as UTF-8.
- HTML renders that body into a complete, self-contained HTML5 document.
- PDF lays the note out as a shareable, paginated document.
- Obsidian packages every authored note into a ZIP of plain Markdown files, grouped by project,
  with YAML properties and collision-safe filenames. The extracted directory can be opened as a
  vault or copied into one; no proprietary plugin or database is embedded.

The desktop view owns the save picker because Avalonia's `StorageProvider` belongs to the window.
The view model only asks for an artifact, flushes pending edits, and reports the result. The CLI
uses the same Core exporter and writes atomically through a sibling temporary file.

## The AI assistant

The only component that can talk to the network on purpose. `Ai/AiContracts.cs` defines
`IAiAssistant`; two implementations sit behind it:

| Provider | Transport | Default |
|---|---|---|
| `OllamaAiAssistant` | `HttpClient` → `POST /api/chat` (newline-delimited JSON stream) at `http://localhost:11434` | ✅ |
| `AnthropicAiAssistant` | official `Anthropic` SDK, `claude-opus-5`, adaptive thinking, streaming | opt-in |

Ollama is the default so the offline guarantee in the header holds without configuration.
Selecting Anthropic requires a key the user supplies (`AiSettings.AnthropicApiKey`, falling back
to `ANTHROPIC_API_KEY`) and is the only path by which note text leaves the machine. Audio is
never sent under either provider.

`AiActionCatalog.BuiltIn` holds the quick actions (meeting summary, decision log, implementation
plan, risk register, transcript cleanup, …) as data, not code — an action is a prompt pair plus a
`ReplacesTarget` flag, so adding one is a catalog entry, not a UI change.

**AI output never mutates a note directly.** A run streams into a preview; the user accepts it,
and only then does `INoteDocumentStore.SaveAsync` land it with origin `ai:<actionId>` — which
pushes the pre-change body onto the revision stack. That is what makes "undo the AI" a guarantee
rather than a hope.

## Contracts

The interfaces in `Core` are the seams between components. They are already defined in:

- `Audio/AudioContracts.cs` — `IAudioChannelEnumerator`, `IAudioCaptureSource`, `AudioFrame`
- `Transcription/TranscriptionContracts.cs` — `ITranscriber`, `IWhisperModelStore`, `ILiveTranscriptionEngine`
- `Transcription/ParallelTranscriptionContracts.cs` — `IParallelLiveTranscriptionEngine`, sourced input/output records
- `Media/MediaContracts.cs` — `IMediaConverter`, `IWavReader`
- `Diarization/DiarizationContracts.cs` — `ISpeakerAttributor`, `ISpeakerAttributorFactory`, `ISpeakerEmbedder`, `SpeakerTimeline`
- `Notes/NoteContracts.cs` — `INoteRepository`, `INoteExporter`
- `Notes/Documents/NoteDocumentContracts.cs` — `INoteDocumentStore`, `NoteDocument`, `NoteRevision`
- `Notes/Exporting/NoteExportContracts.cs` — `INoteExportService`, `NoteExportArtifact`, `NoteExportFormat`
- `Ai/AiContracts.cs` — `IAiAssistant`, `IAiAssistantFactory`, `AiRequest`, `AiException`
- `Configuration/AppSettings.cs` — `AppSettings`, `AiSettings`, `ISettingsStore`

Implementations must satisfy these as written. If an implementation genuinely needs a contract
change, change it in one place and note it — do not add a parallel abstraction.

## Capturing Teams specifically

Teams has no dedicated audio device. You capture the **render endpoint Teams is playing
through** in loopback mode, which yields everything the machine is outputting on that device.
Two practical setups:

1. **Simple** — capture the default render endpoint. Picks up Teams plus any other system
   audio. Fine when you're not playing anything else.
2. **Clean** — install a virtual cable (VB-Audio Cable, or Voicemeeter), set Teams' output
   device to it, and capture that endpoint's loopback. Isolates Teams from all other audio.

The desktop Inputs page can enable that loopback endpoint and a microphone endpoint together.
They are captured in one session through independent pipelines; microphone segments use the
configured input name while mixed loopback audio can still receive anonymous diarization labels.
