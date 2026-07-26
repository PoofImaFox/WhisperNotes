using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using WhisperNotes.Core.Transcription;

namespace WhisperNotes.Core.Diarization;

/// <summary>
/// Runs the voice-print model over a window of audio.
/// </summary>
/// <remarks>
/// The model's graph declares the contract this class has to meet, and every model in the
/// sherpa-onnx speaker zoo declares the same one: a single rank-3 input of shape
/// [batch, frames, 80] — kaldi-style filterbank features, time-major — and a single rank-2 output
/// of shape [batch, dimensions]. Only the tensor names and the embedding width differ between them,
/// so both are read off the graph rather than assumed. The output is <em>not</em> unit length as it
/// comes out, so it is normalised here; every comparison downstream is a plain dot product that
/// assumes it already has been.
/// </remarks>
/// <threadsafety>
/// Not thread-safe. It reuses the extractor's scratch buffers between calls, and the diarizer feeds
/// it one window at a time from a single thread.
/// </threadsafety>
internal sealed class OnnxSpeakerEmbedder : ISpeakerEmbedder
{
    /// <summary>
    /// Embedding happens inline with whisper decoding, which is already using every core it was
    /// given. Letting onnxruntime spin up its own full-width pool on top of that costs more in
    /// contention than it wins in throughput on a window this small.
    /// </summary>
    private static readonly int InferenceThreads = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);

    private readonly InferenceSession _session;
    private readonly FbankExtractor _fbank;
    private readonly string _input;
    private readonly string _output;
    private readonly string[] _outputs;

    private bool _disposed;

    public OnnxSpeakerEmbedder(string modelPath, FbankExtractor? fbank = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"Speaker model not found at '{modelPath}'.", modelPath);
        }

        InferenceSession session = CreateSession(modelPath);
        try
        {
            // Taken off the graph rather than hardcoded, because the zoo does not agree on names:
            // 3D-Speaker exports call these "x" and "embedding", WeSpeaker "feats" and "embs".
            // Every one of them declares exactly one of each, so the first is the right one, and
            // failing here says which file is wrong instead of failing deep inside Run.
            _input = session.InputMetadata.Keys.FirstOrDefault()
                     ?? throw new InvalidOperationException($"'{modelPath}' declares no inputs.");

            _output = session.OutputMetadata.Keys.FirstOrDefault()
                      ?? throw new InvalidOperationException($"'{modelPath}' declares no outputs.");

            int[] shape = session.OutputMetadata[_output].Dimensions;
            Dimensions = shape.Length > 0 && shape[^1] > 0 ? shape[^1] : 0;

            _fbank = fbank ?? new FbankExtractor(FeaturesFor(session));
            _session = session;
            _outputs = [_output];
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>Builds the inference session, and releases the options that configured it.</summary>
    /// <remarks>
    /// <see cref="SessionOptions"/> is a <c>SafeHandle</c> over a native <c>OrtSessionOptions</c>, and
    /// onnxruntime copies it into the session at creation rather than holding a reference — so once
    /// the session exists the handle is dead weight. Leaving it to its finalizer leaks native memory
    /// the GC cannot see the cost of, once per attributor built.
    /// </remarks>
    private static InferenceSession CreateSession(string modelPath)
    {
        using SessionOptions options = new()
        {
            IntraOpNumThreads = InferenceThreads,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };

        return new InferenceSession(modelPath, options);
    }

    /// <summary>
    /// Picks the feature convention the loaded model was trained on, from the model's own metadata.
    /// </summary>
    /// <remarks>
    /// The two families in the sherpa-onnx zoo disagree about one thing — whether the waveform is
    /// scaled to int16 range before the filterbank — and each declares its answer as
    /// <c>normalize_samples</c>. Reading it is what makes swapping the model a one-line change to
    /// the download URL instead of a silent halving of accuracy: get this wrong and the features are
    /// still well-formed, the embeddings still look like embeddings, and only the clustering is
    /// quietly ruined.
    /// </remarks>
    private static FbankOptions FeaturesFor(InferenceSession session) =>
        session.ModelMetadata.CustomMetadataMap.TryGetValue("normalize_samples", out string? normalized)
        && string.Equals(normalized?.Trim(), "0", StringComparison.Ordinal)
            ? FbankOptions.WeSpeaker
            : FbankOptions.ThreeDSpeaker;

    /// <summary>Length of the vectors this model produces; 192 for the shipped weights.</summary>
    public int Dimensions { get; }

    public float[] Embed(ReadOnlySpan<float> pcm16kMono)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        float[] features = _fbank.Compute(pcm16kMono, out int frames);
        if (frames <= 0)
        {
            return [];
        }

        int bins = _fbank.Dimensions;
        DenseTensor<float> input = new(features.AsMemory(0, frames * bins), [1, frames, bins]);

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
            _session.Run([NamedOnnxValue.CreateFromTensor(_input, input)], _outputs);

        float[] embedding = results[0].AsTensor<float>().ToArray();
        Normalise(embedding);
        return embedding;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Dispose();
    }

    /// <summary>
    /// Scales to unit length so cosine similarity is a dot product. A vector with no length at all
    /// is left as zeros rather than turned into NaNs; the clustering knows to treat that as
    /// "no evidence" instead of as a direction.
    /// </summary>
    private static void Normalise(float[] embedding)
    {
        double sum = 0;
        foreach (float value in embedding)
        {
            if (!float.IsFinite(value))
            {
                Array.Clear(embedding);
                return;
            }

            sum += (double)value * value;
        }

        double norm = Math.Sqrt(sum);
        if (!double.IsFinite(norm) || norm <= 0)
        {
            Array.Clear(embedding);
            return;
        }

        float scale = (float)(1 / norm);
        for (int i = 0; i < embedding.Length; i++)
        {
            embedding[i] *= scale;
        }
    }
}

/// <summary>Loads the embedding model on first use and hands back an attributor wrapped around it.</summary>
public sealed class OnnxSpeakerAttributorFactory : ISpeakerAttributorFactory
{
    private readonly ISpeakerModelStore _models;
    private readonly IProgress<ModelDownloadProgress>? _downloadProgress;

    /// <param name="downloadProgress">
    /// Wired at construction for the same reason the transcriber factory does it: the first run
    /// after enabling diarization pays for a 26 MB download, and the worst possible time to have no
    /// progress bar is the moment someone has just pointed the tool at a two-hour recording.
    /// </param>
    public OnnxSpeakerAttributorFactory(
        ISpeakerModelStore models,
        IProgress<ModelDownloadProgress>? downloadProgress = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        _models = models;
        _downloadProgress = downloadProgress;
    }

    public async Task<ISpeakerAttributor> CreateAsync(
        DiarizationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        string modelPath = await _models
            .EnsureDownloadedAsync(_downloadProgress, cancellationToken)
            .ConfigureAwait(false);

        // Building the session reads and optimises the whole graph synchronously, so it stays off
        // whatever thread asked for it.
        ISpeakerEmbedder embedder = await Task
            .Run(() => (ISpeakerEmbedder)new OnnxSpeakerEmbedder(modelPath), cancellationToken)
            .ConfigureAwait(false);

        return new SpeakerAttributor(embedder, options);
    }
}
