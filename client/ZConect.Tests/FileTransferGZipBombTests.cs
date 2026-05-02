using System.IO.Compression;
using FileTransfer;
using Xunit;

namespace ZConect.Tests;

/// <summary>
/// Регресс-гард для GZip bomb защиты в <see cref="FileTransferService.DecompressGZip"/>.
/// Исходная уязвимость: receiver мог decompress любой размер (например 1KB compressed в 1GB+)
/// → OOM. Fix: лимит <see cref="FileTransferService.MaxDecompressedChunkBytes"/> = 1MB.
/// </summary>
public sealed class FileTransferGZipBombTests
{
    /// <summary>Нормальный 64KB chunk с разнообразными данными — decompress успешен.</summary>
    [Fact]
    public void Decompress_normal_chunk_succeeds()
    {
        var raw = new byte[64 * 1024];
        var rng = new Random(42);
        rng.NextBytes(raw);

        var compressed = CompressForTest(raw);
        var decompressed = FileTransferService.DecompressGZip(compressed);

        Assert.Equal(raw.Length, decompressed.Length);
        Assert.Equal(raw, decompressed);
    }

    /// <summary>
    /// Bomb: 2MB нулей → compress в ~2KB → при decompress ожидаем throw
    /// потому что decompressed size (2MB) превышает лимит (1MB).
    /// </summary>
    [Fact]
    public void Decompress_bomb_exceeding_limit_throws()
    {
        // 2MB нулей — sweet spot для gzip (compressed ratio <0.1%).
        var raw = new byte[2 * 1024 * 1024];
        var bomb = CompressForTest(raw);

        Assert.True(bomb.Length < 10 * 1024, $"compressed bomb {bomb.Length} bytes — must be < 10KB");

        var ex = Assert.Throws<InvalidDataException>(() => FileTransferService.DecompressGZip(bomb));
        Assert.Contains("GZip decompressed chunk exceeded", ex.Message);
    }

    /// <summary>
    /// Edge case: ровно MaxDecompressedChunkBytes (1MB) — граница. Проходит.
    /// </summary>
    [Fact]
    public void Decompress_exactly_at_limit_succeeds()
    {
        var raw = new byte[FileTransferService.MaxDecompressedChunkBytes];
        var rng = new Random(7);
        rng.NextBytes(raw);

        var compressed = CompressForTest(raw);
        var decompressed = FileTransferService.DecompressGZip(compressed);

        Assert.Equal(raw.Length, decompressed.Length);
    }

    /// <summary>
    /// Edge case: лимит + 1 байт — бросает.
    /// </summary>
    [Fact]
    public void Decompress_one_byte_over_limit_throws()
    {
        var raw = new byte[FileTransferService.MaxDecompressedChunkBytes + 1];
        var compressed = CompressForTest(raw);

        Assert.Throws<InvalidDataException>(() => FileTransferService.DecompressGZip(compressed));
    }

    private static byte[] CompressForTest(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(data, 0, data.Length);
        return ms.ToArray();
    }
}
