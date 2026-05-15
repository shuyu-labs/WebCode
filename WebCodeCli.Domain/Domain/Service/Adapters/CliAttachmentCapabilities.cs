using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service.Adapters;

public class CliAttachmentCapabilities
{
    public bool SupportsNativeAttachments { get; init; }

    public bool SupportsMultipleNativeAttachments { get; init; }

    public HashSet<MessageAttachmentKind> NativeKinds { get; init; } = new();

    public bool AllowsReferenceFallback { get; init; } = true;

    public static CliAttachmentCapabilities ReferenceOnly() => new();

    public static CliAttachmentCapabilities ForNativeKinds(params MessageAttachmentKind[] kinds) =>
        new()
        {
            SupportsNativeAttachments = kinds.Length > 0,
            SupportsMultipleNativeAttachments = true,
            NativeKinds = new HashSet<MessageAttachmentKind>(kinds)
        };
}
