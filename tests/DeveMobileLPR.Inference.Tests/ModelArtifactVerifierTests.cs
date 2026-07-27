using System.Security.Cryptography;
using DeveMobileLPR.Inference.Models;

namespace DeveMobileLPR.Tests;

public sealed class ModelArtifactVerifierTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"DeveMobileLPR-model-{Guid.NewGuid():N}.bin");

    [Fact]
    public async Task IsValidAsync_AcceptsMatchingLengthAndHash()
    {
        byte[] bytes = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(_path, bytes);
        var artifact = new ModelArtifact("test.bin", Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);

        Assert.True(await ModelArtifactVerifier.IsValidAsync(_path, artifact, CancellationToken.None));
        await ModelArtifactVerifier.VerifyAsync(_path, artifact, CancellationToken.None);
    }

    [Fact]
    public async Task IsValidAsync_RejectsLengthAndHashMismatches()
    {
        byte[] bytes = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(_path, bytes);
        var wrongLength = new ModelArtifact("test.bin", Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length + 1);
        var wrongHash = new ModelArtifact("test.bin", new string('0', 64), bytes.Length);

        Assert.False(await ModelArtifactVerifier.IsValidAsync(_path, wrongLength, CancellationToken.None));
        Assert.False(await ModelArtifactVerifier.IsValidAsync(_path, wrongHash, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelArtifactVerifier.VerifyAsync(_path, wrongHash, CancellationToken.None));
    }

    [Fact]
    public async Task VerifyAsync_ReportsMissingModel()
    {
        var artifact = new ModelArtifact("test.bin", new string('0', 64), 1);

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(
            () => ModelArtifactVerifier.VerifyAsync(_path, artifact, CancellationToken.None));

        Assert.Equal(_path, exception.FileName);
    }

    public void Dispose()
    {
        File.Delete(_path);
    }
}