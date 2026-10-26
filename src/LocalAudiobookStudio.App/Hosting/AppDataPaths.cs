namespace LocalAudiobookStudio.App.Hosting;

public sealed class AppDataPaths
{
    private AppDataPaths(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        CatalogPath = Path.Combine(Root, "catalog.sqlite");
        ProjectsRoot = Path.Combine(Root, "projects");
        LogsRoot = Path.Combine(Root, "logs");
        TransientRoot = Path.Combine(Root, "transient");
    }

    public string Root { get; }

    public string CatalogPath { get; }

    public string ProjectsRoot { get; }

    public string LogsRoot { get; }

    public string TransientRoot { get; }

    public static AppDataPaths ForCurrentUser()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("The local application-data root is unavailable.");
        }

        return new AppDataPaths(Path.Combine(localApplicationData, "LocalAudiobookStudio"));
    }

    internal static AppDataPaths ForRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return new AppDataPaths(root);
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ProjectsRoot);
        Directory.CreateDirectory(LogsRoot);
        Directory.CreateDirectory(TransientRoot);
    }
}
