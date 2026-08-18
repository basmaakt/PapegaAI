namespace Parrot.Platform;

/// <summary>`which`, without shelling out to it. Used all over the Linux port,
/// where "is this helper installed?" decides which backend gets picked.</summary>
static class Which
{
    public static bool Exists(string tool) => Find(tool) is not null;

    public static string? Find(string tool)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        foreach (string dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, tool);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
