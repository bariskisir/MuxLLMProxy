namespace MuxLlmProxy.Core.Configuration;

/// <summary>
/// Resolves shared data paths for local development and published deployments.
/// </summary>
public static class ProxyPathResolver
{
    /// <summary>
    /// Resolves the directory that stores proxy data files.
    /// </summary>
    /// <param name="contentRootPath">The application content root.</param>
    /// <returns>The resolved data directory path.</returns>
    public static string ResolveDataDirectory(string contentRootPath)
    {
        var preferredHostDataDirectory = ResolvePreferredHostDataDirectory(contentRootPath);
        if (preferredHostDataDirectory is not null)
        {
            return preferredHostDataDirectory;
        }

        foreach (var candidateRoot in GetCandidateRoots(contentRootPath))
        {
            var candidate = Path.Combine(candidateRoot, ProxyConstants.Paths.DataDirectoryName);
            if (Directory.Exists(candidate) || File.Exists(Path.Combine(candidate, ProxyConstants.Paths.AccountsFileName)))
            {
                return candidate;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, ProxyConstants.Paths.DataDirectoryName);
    }

    private static string? ResolvePreferredHostDataDirectory(string contentRootPath)
    {
        foreach (var hostRoot in new[]
        {
            ResolveSiblingHostProjectPath(contentRootPath),
            ResolveSiblingHostProjectPath(Directory.GetCurrentDirectory()),
            ResolveSiblingHostProjectPath(AppContext.BaseDirectory)
        }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            return Path.Combine(hostRoot!, ProxyConstants.Paths.DataDirectoryName);
        }

        return null;
    }

    private static IEnumerable<string> GetCandidateRoots(string contentRootPath)
    {
        var roots = new[]
        {
            ResolveSiblingHostProjectPath(contentRootPath),
            ResolveSiblingHostProjectPath(Directory.GetCurrentDirectory()),
            ResolveSiblingHostProjectPath(AppContext.BaseDirectory),
            contentRootPath,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        return roots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)!;
    }

    private static string? ResolveSiblingHostProjectPath(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        try
        {
            foreach (var candidateRoot in EnumerateAncestorDirectories(rootPath))
            {
                foreach (var candidate in GetHostProjectCandidates(candidateRoot))
                {
                    if (Directory.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateAncestorDirectories(string startPath)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startPath));
        while (current is not null)
        {
            yield return current.FullName;
            current = current.Parent;
        }
    }

    private static IEnumerable<string> GetHostProjectCandidates(string rootPath)
    {
        yield return Path.Combine(rootPath, ProxyConstants.Paths.HostProjectDirectoryName);
        yield return Path.Combine(rootPath, "src", ProxyConstants.Paths.HostProjectDirectoryName);
    }
}
