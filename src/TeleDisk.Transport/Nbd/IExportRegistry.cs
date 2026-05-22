using TeleDisk.Application;

namespace TeleDisk.Transport.Nbd;

internal interface IExportRegistry
{
    VirtualDiskService Resolve(string? exportName);

    IReadOnlyList<string> GetExportNames();
}
