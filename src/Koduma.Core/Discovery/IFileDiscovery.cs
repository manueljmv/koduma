using Koduma.Core.Config;

namespace Koduma.Core.Discovery;

public interface IFileDiscovery
{
    IReadOnlyList<string> DiscoverFiles(string rootPath, KodumaConfig config);
}
