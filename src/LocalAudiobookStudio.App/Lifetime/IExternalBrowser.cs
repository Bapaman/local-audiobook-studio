namespace LocalAudiobookStudio.App.Lifetime;

public interface IExternalBrowser
{
    Task OpenAsync(Uri uri, CancellationToken cancellationToken);
}
