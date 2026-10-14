namespace LyricsSync.Probe.Translation;

/// <summary>Çeviri sonucu: <see cref="Lines"/> girdiyle aynı sırada ve aynı sayıda.</summary>
public sealed record TranslationResult(string? SourceLanguage, IReadOnlyList<string> Lines);

/// <summary>Çevirmenin kullanıcıya gösterilecek hata mesajı.</summary>
public sealed class TranslationException : Exception
{
    public TranslationException(string message) : base(message) { }
}

public interface ITranslator : IDisposable
{
    /// <summary>Arayüzde gösterilecek ad, ör. "Claude (claude-sonnet-5-5)".</summary>
    string Name { get; }

    /// <summary>Önbellek anahtarına girer; sağlayıcı/model değişince eski çeviriler kullanılmaz.</summary>
    string CacheId { get; }

    /// <summary>Satırları tek seferde çevirir. Satır sayısı ve sırası korunmalıdır.</summary>
    Task<TranslationResult> TranslateAsync(
        IReadOnlyList<string> lines,
        string title,
        string artist,
        string targetLanguage,
        CancellationToken ct);
}
