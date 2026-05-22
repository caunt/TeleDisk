using TeleDisk.Application;

namespace TeleDisk.Transport.Nbd;

internal sealed class ClientExportSession(IExportRegistry exportRegistry)
{
    private VirtualDiskService? _virtualDiskService;

    internal VirtualDiskService Resolve(string? exportName) =>
        _virtualDiskService ??= exportRegistry.Resolve(exportName);
}
