# NoteScribe — architecture

Local speech-to-text note taking. Captures a chosen Windows audio endpoint (typically the
loopback of whatever device Teams is playing through), runs local Whisper over it, and writes
timestamped notes into an organised directory tree. Also ingests video files via ffmpeg.

Everything runs offline. No audio leaves the machine.

## Projects

| Project | Type | Responsibility |
|---|---|---|
| `NoteScribe.Core` | class library, `net10.0-windows` | Audio capture, transcription, ffmpeg, note storage. No UI. |
| `NoteScribe.Cli` | console, `net10.0-windows` | Headless transcription, `--video` ingest, session listing. |
| `NoteScribe.App` | Avalonia desktop, `net10.0-windows` | Channel picker, live dictation view, notes browser. |

`Cli` and `App` both depend on `Core`. They never depend on each other.

## The live pipeline

```
WASAPI endpoint
   -> WasapiLoopbackCapture / WasapiCapture     (device-native format, e.g. 48kHz stereo float)
   -> downmix to mono + resample to 16 kHz      (AudioResampler)
   -> AudioFrame stream                          (IAudioCaptureSource.CaptureAsync)
   -> silence-aware chunker                      (ChunkingOptions: min 2s, max 15s, 700ms silence)
   -> Whisper.net decode                         (ITranscriber.TranscribeAsync)
   -> TranscriptSegment stream                   (ILiveTranscriptionEngine.RunAsync)
   -> NoteEntry append (jsonl, fsync'd)          (INoteRepository.AppendEntryAsync)
   -> UI observable collection
```

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

## Contracts

The interfaces in `Core` are the seams between components. They are already defined in:

- `Audio/AudioContracts.cs` — `IAudioChannelEnumerator`, `IAudioCaptureSource`, `AudioFrame`
- `Transcription/TranscriptionContracts.cs` — `ITranscriber`, `IWhisperModelStore`, `ILiveTranscriptionEngine`
- `Media/MediaContracts.cs` — `IMediaConverter`, `IWavReader`
- `Notes/NoteContracts.cs` — `INoteRepository`, `INoteExporter`
- `Configuration/AppSettings.cs` — `AppSettings`, `ISettingsStore`

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

Loopback captures remote participants *and* your own voice as rendered by Teams. It does not
capture your microphone directly — to also get your own side cleanly, run a second session on
the mic endpoint, or rely on Teams echoing you into the mix.
