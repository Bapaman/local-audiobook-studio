namespace LocalAudiobookStudio.Application.Errors;

public sealed class StudioException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
