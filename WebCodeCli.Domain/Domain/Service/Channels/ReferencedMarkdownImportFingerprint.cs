using System.Security.Cryptography;

namespace WebCodeCli.Domain.Domain.Service.Channels;

public static class ReferencedMarkdownImportFingerprint
{
    public static string Compute(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        var fileInfo = new FileInfo(absolutePath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("Markdown 文件不存在。", absolutePath);
        }

        using var stream = File.OpenRead(absolutePath);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        return string.Create(
            absolutePath.Length + hash.Length + 64,
            (absolutePath, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks, hash),
            static (span, state) =>
            {
                var text = $"{state.absolutePath}|{state.Length}|{state.Ticks}|{state.hash}";
                text.AsSpan().CopyTo(span);
            });
    }
}
