namespace Volco.Core.Tests;

/// <summary>Finds files that live outside the test project (the shared golden frames, the icon
/// manifest) by walking up from the test binary to the folder holding Volco.sln.</summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string Combine(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Volco.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Volco.sln not found above " + AppContext.BaseDirectory);
    }
}
