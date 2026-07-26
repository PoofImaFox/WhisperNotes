using WhisperNotes.Core.Transcription;

namespace WhisperNotes.Core.Tests.Transcription;

/// <summary>
/// Covers the one piece of <see cref="WhisperRuntime"/> that is not a process-global side effect:
/// recovering the adapter list from ggml's log. whisper.cpp exposes no API for which device it
/// bound to, so those lines are the only source there is — and the two backends do not print them
/// the same way, which is the whole reason this needs pinning.
/// </summary>
public sealed class WhisperRuntimeTests
{
    [Fact]
    public void TryReadDevice_ReadsAVulkanEntry()
    {
        WhisperDevice device = Require(
            "ggml_vulkan: 0 = NVIDIA GeForce RTX 3080 (NVIDIA) | uma: 0 | fp16: 1 | bf16: 1");

        Assert.Equal(0, device.Index);
        Assert.Equal("NVIDIA GeForce RTX 3080 (NVIDIA) | uma: 0 | fp16: 1 | bf16: 1", device.Description);
    }

    [Fact]
    public void TryReadDevice_ReadsASecondVulkanEntryWithItsOwnIndex()
    {
        WhisperDevice device = Require(
            "ggml_vulkan: 1 = Intel(R) UHD Graphics 770 (Intel Corporation) | uma: 1 | fp16: 1");

        Assert.Equal(1, device.Index);
        Assert.StartsWith("Intel(R) UHD Graphics 770", device.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// ggml-cuda indents its entries and does not repeat its own <c>ggml_cuda_init:</c> prefix on
    /// them, so a reader keyed on that prefix would take the header and miss every device.
    /// </summary>
    [Fact]
    public void TryReadDevice_ReadsAnIndentedCudaEntry()
    {
        WhisperDevice device = Require(
            "  Device 0: NVIDIA GeForce RTX 3080, compute capability 8.6, VMM: yes, VRAM: 10240 MiB");

        Assert.Equal(0, device.Index);
        Assert.Equal(
            "NVIDIA GeForce RTX 3080, compute capability 8.6, VMM: yes, VRAM: 10240 MiB",
            device.Description);
    }

    [Theory]
    [InlineData("ggml_vulkan: Found 2 Vulkan devices:")]
    [InlineData("ggml_cuda_init: found 1 CUDA devices (Total VRAM: 10240 MiB):")]
    public void TryReadDevice_DropsTheHeaderThatOnlyCountsDevices(string line) =>
        Assert.Null(WhisperRuntime.TryReadDevice(line));

    [Theory]
    [InlineData("ggml_cuda_init: GGML_CUDA_FORCE_MMQ:    no")]
    [InlineData("whisper_model_load: n_vocab = 51866")]
    [InlineData("whisper_init_with_params_no_state: use gpu    = 1")]
    [InlineData("ggml_vulkan: ")]
    [InlineData("ggml_vulkan: 0 = ")]
    [InlineData("Device : no index")]
    [InlineData("")]
    public void TryReadDevice_IgnoresEverythingThatIsNotADeviceEntry(string line) =>
        Assert.Null(WhisperRuntime.TryReadDevice(line));

    private static WhisperDevice Require(string line)
    {
        WhisperDevice? device = WhisperRuntime.TryReadDevice(line);
        Assert.NotNull(device);
        return device.Value;
    }
}
