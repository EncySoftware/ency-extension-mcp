using System.Text.RegularExpressions;

namespace EncyExtensionMcp;

/** What an extension's files say about the SDK, and what a fix would change them to. */
public sealed record SdkPinState(string? InfoJsonVersion, string? CsprojVersion, bool NeedsFix);

// Расширение получает СНИМОК шаблона в момент создания и дальше живёт своей жизнью: правка шаблона
// уже созданные репозитории не догоняет. Поэтому нужен обратный ход — сверить чужую папку с тем,
// что советует стор, и поправить ровно то, что принадлежит шаблону. Чинится только пин ИЗ БУДУЩЕГО:
// собранное под старый SDK грузится в новых версиях, обратное - никогда, поэтому «старее
// рекомендованного» не дефект и трогать его нельзя.
/** Reading and fixing the SDK pin in an extension's own files. */
public static class SdkPin
{
    private static readonly Regex InfoJson = new("\"sdkVersion\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex Csproj = new(
        "(<PackageReference\\s+Include=\"EncySoftware\\.CAMAPI\\.Sdk\\.Net\"\\s+Version=\")\\[?([^\"\\]]+)\\]?(\"\\s*/>)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /** The numeric head of a version, so 3.0.9 and 3.0.1-rc.22 compare by their releases. */
    private static Version? Numeric(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var head = new string(v.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).TrimEnd('.');
        return Version.TryParse(head.Count(c => c == '.') == 1 ? head + ".0" : head, out var parsed) ? parsed : null;
    }

    /** Is the pinned version ahead of what the store recommends — the only case worth touching. */
    public static bool IsFromTheFuture(string? pinned, string? recommended)
    {
        var a = Numeric(pinned);
        var b = Numeric(recommended);
        return a != null && b != null && a > b;
    }

    public static string? ReadInfoJson(string text) => InfoJson.Match(text) is { Success: true } m ? m.Groups[1].Value : null;

    public static string? ReadCsproj(string text) => Csproj.Match(text) is { Success: true } m ? m.Groups[2].Value : null;

    /** The same text with the pin set to <paramref name="version"/>; unchanged when there is no pin. */
    public static string WriteInfoJson(string text, string version) =>
        InfoJson.Replace(text, $"\"sdkVersion\": \"{version}\"", 1);

    // Квадратные скобки — не украшение: без них NuGet читает версию как «не ниже», и следующий
    // restore тихо поднимет её до свежайшей на фиде. Ровно так пин и уползает.
    /** The same text with an EXACT pin, brackets included. */
    public static string WriteCsproj(string text, string version) =>
        Csproj.Replace(text, m => m.Groups[1].Value + "[" + version + "]" + m.Groups[3].Value, 1);
}
