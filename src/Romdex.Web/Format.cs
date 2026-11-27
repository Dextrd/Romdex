using Romdex.Client;

namespace Romdex.Web;

public static class Format
{
    public static string TaskLabel(TaskInfo task) => task.State switch
    {
        TaskState.Queued when task.Paused => "Paused",
        TaskState.Queued => "Waiting",
        TaskState.Running => "Running",
        TaskState.Succeeded => "Done",
        TaskState.Cancelled => "Cancelled",
        _ => "Failed",
    };

    public static string TaskBadge(TaskInfo task) => task.State switch
    {
        TaskState.Queued when task.Paused => "neutral",
        TaskState.Succeeded => "owned",
        TaskState.Failed => "missing",
        TaskState.Cancelled => "neutral",
        TaskState.Queued => "active waiting",
        _ => "active",
    };

    /// <summary>How long ago, roughly: "just now", "5 min ago", "3 h ago", or the date.</summary>
    public static string Ago(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
            : utc.ToLocalTime().ToString("d MMM");
    }

    /// <summary>How soon, roughly: "in 5 min", "in 3 h", or the date.</summary>
    public static string Until(DateTime utc)
    {
        var left = utc - DateTime.UtcNow;
        return left.TotalMinutes < 1 ? "any minute"
            : left.TotalHours < 1 ? $"in {(int)left.TotalMinutes} min"
            : left.TotalDays < 1 ? $"in {(int)left.TotalHours} h {left.Minutes} min"
            : $"on {utc.ToLocalTime():d MMM}";
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} bytes",
    };

    public static string Count(int n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";

    public static string Badge(TitleStatus status) => status switch
    {
        TitleStatus.Owned => "owned",
        TitleStatus.Partial => "partial",
        TitleStatus.Bad => "bad",
        _ => "missing",
    };

    public static string Label(TitleStatus status) => status == TitleStatus.Owned ? "Verified" : status.ToString();

    /// <summary>Whether the library has it, for lists of every game: owned or missing, or the partial or bad between.</summary>
    public static string OwnershipLabel(TitleStatus status) => status switch
    {
        TitleStatus.Owned => "Owned",
        TitleStatus.Partial => "Partial",
        TitleStatus.Bad => "Bad dump",
        _ => "Missing",
    };

    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["En"] = "English", ["Ja"] = "Japanese", ["Fr"] = "French", ["De"] = "German", ["Es"] = "Spanish", ["It"] = "Italian", ["Nl"] = "Dutch",
        ["Pt"] = "Portuguese", ["Sv"] = "Swedish", ["No"] = "Norwegian", ["Da"] = "Danish", ["Fi"] = "Finnish", ["Zh"] = "Chinese", ["Ko"] = "Korean",
        ["Pl"] = "Polish", ["Ru"] = "Russian", ["Cs"] = "Czech", ["Hu"] = "Hungarian", ["El"] = "Greek", ["Tr"] = "Turkish", ["Ar"] = "Arabic",
        ["Ca"] = "Catalan", ["He"] = "Hebrew", ["Hr"] = "Croatian", ["Ro"] = "Romanian", ["Sk"] = "Slovak", ["Sl"] = "Slovenian", ["Uk"] = "Ukrainian",
        ["Th"] = "Thai", ["Vi"] = "Vietnamese", ["Id"] = "Indonesian", ["Ms"] = "Malay", ["Hi"] = "Hindi", ["Bg"] = "Bulgarian", ["Sr"] = "Serbian",
    };

    /// <summary>Languages by name, such as English for <c>En</c>, as the DATs give them in codes.</summary>
    public static string Languages(IEnumerable<string> codes) => string.Join(", ", codes.Select(c => LanguageNames.GetValueOrDefault(c) ?? c));

    /// <summary>A release date as precisely as it's known: <c>9 Sep 1996</c>, <c>Sep 1996</c> or <c>1996</c>.</summary>
    public static string ReleaseDate(string date) => date.Length switch
    {
        10 when DateOnly.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day)
            => day.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
        7 when DateOnly.TryParseExact(date + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var month)
            => month.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
        _ => date.Length >= 4 ? date[..4] : date,
    };

    /// <summary>
    /// A review score out of 100, where it can be read as one: <c>88/100</c>, <c>8.5/10</c>, <c>32/40</c>, <c>4/5</c>,
    /// <c>80%</c>; several, as one reviewer's of several versions, are averaged. Null for one like <c>A-</c>.
    /// </summary>
    public static double? Percent(string score)
    {
        var parts = System.Text.RegularExpressions.Regex.Matches(score, @"(\d+(?:\.\d+)?)\s*(?:/\s*(\d+(?:\.\d+)?)|(%))");
        var values = parts.Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                                       / (m.Groups[3].Success ? 100 : double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .Where(v => v is >= 0 and <= 1).ToList();
        return values.Count == 0 ? null : values.Average() * 100;
    }

    /// <summary>How a score is coloured: green for a good one, amber for a middling one, red for a poor one.</summary>
    public static string ScoreBadge(double percent) => percent >= 75 ? "owned" : percent >= 50 ? "partial" : "missing";

    // How the best-known reviewers are known for short, where their capitals don't say it.
    private static readonly Dictionary<string, string> Monograms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Metacritic"] = "MC", ["OpenCritic"] = "OC", ["GameRankings"] = "GR", ["1Up.com"] = "1UP", ["Game Revolution"] = "GRev",
        ["Famitsu"] = "FAM", ["Edge"] = "EDGE", ["Polygon"] = "POLY", ["Destructoid"] = "DTOID", ["Hyper"] = "HYP", ["Eurogamer"] = "EG",
        ["Jeuxvideo.com"] = "JV", ["Nintendo Power"] = "NP",
    };

    // The reviewers whose logos Romdex has (wwwroot/img/reviewers, credited in img/CREDITS.md): those that reviewed the
    // most games. The rest are shown by their letters.
    private static readonly Dictionary<string, string> ReviewerLogos = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Metacritic"] = "metacritic.svg", ["IGN"] = "ign.svg", ["GameSpot"] = "gamespot.svg", ["Eurogamer"] = "eurogamer.svg",
        ["Game Informer"] = "game-informer.svg", ["GamePro"] = "gamepro.png", ["1Up.com"] = "1up.png", ["Electronic Gaming Monthly"] = "egm.svg",
        ["Famitsu"] = "famitsu.svg",
    };

    /// <summary>A reviewer's logo, where Romdex has it; null for the rest, which are shown by <see cref="Monogram"/>.</summary>
    public static string? ReviewerLogo(string reviewer) => ReviewerLogos.TryGetValue(reviewer, out var file) ? $"img/reviewers/{file}" : null;

    /// <summary>
    /// A reviewer's badge: the letters it's known by, such as MC for Metacritic or OXM for Official Xbox Magazine, from
    /// its capitals and digits where it isn't one of the best known.
    /// </summary>
    public static string Monogram(string reviewer)
    {
        if (Monograms.TryGetValue(reviewer, out var known))
            return known;
        var letters = new string(reviewer.Where(c => char.IsUpper(c) || char.IsDigit(c)).ToArray());
        return letters.Length is >= 2 and <= 4 ? letters
            : letters.Length > 4 ? letters[..4]
            : reviewer.Length <= 4 ? reviewer.ToUpperInvariant()
            : reviewer[..2].ToUpperInvariant();
    }

    /// <summary>A reviewer's colour, as a hue, the same on every page so its badge is known again at a glance.</summary>
    public static int Hue(string reviewer)
    {
        // A stable hash: string.GetHashCode differs from one run to the next.
        var hash = 17u;
        foreach (var c in reviewer.ToLowerInvariant())
            hash = hash * 31 + c;
        return (int)(hash % 360);
    }

    /// <summary>The age rating boards, for choosing whose ratings to show: where they rate, and one of their marks to show them by.</summary>
    public static readonly (RatingBoard Board, string Name, string Where, string Sample)[] RatingBoards =
    [
        (RatingBoard.Esrb, "ESRB", "North America", "esrb-t"), (RatingBoard.Pegi, "PEGI", "Europe", "pegi-12"), (RatingBoard.Usk, "USK", "Germany", "usk-12"),
        (RatingBoard.Cero, "CERO", "Japan", "cero-b"), (RatingBoard.Acb, "ACB", "Australia", "acb-m"),
    ];

    /// <summary>Whose ratings people where a language is spoken know, by its region, such as US in <c>en-US</c>.</summary>
    public static RatingBoard ForLanguage(string? language) =>
        (language?.Split('-', '_').Skip(1).FirstOrDefault()?.ToUpperInvariant(), language?.Split('-', '_')[0].ToLowerInvariant()) switch
        {
            ("US" or "CA" or "MX", _) => RatingBoard.Esrb,
            ("JP", _) or (null, "ja") => RatingBoard.Cero,
            ("DE", _) => RatingBoard.Usk,
            ("AU" or "NZ", _) => RatingBoard.Acb,
            _ => RatingBoard.Pegi,
        };

    /// <summary>
    /// The rating to show for a game: the chosen board's, or by the browser's language; where it has none from them,
    /// the first other board's it has. Null when it has none, or ratings aren't shown.
    /// </summary>
    public static AgeRating? Rating(IReadOnlyList<AgeRating> ratings, RatingBoard choice, string? language)
    {
        if (choice == RatingBoard.None)
            return null;
        var first = choice == RatingBoard.Auto ? ForLanguage(language) : choice;
        return new[] { first }.Concat(RatingBoards.Select(b => b.Board))
            .Select(board => ratings.FirstOrDefault(r => r.Board.Equals(board.ToString(), StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(r => r is not null);
    }

    /// <summary>A rating's mark, as its board draws it (wwwroot/img/ratings, credited in img/CREDITS.md).</summary>
    public static string RatingImage(AgeRating rating) => $"img/ratings/{rating.Board.ToLowerInvariant()}-{rating.Rating}.svg";

    /// <summary>A system's short name, such as PS2, for lists; its whole name where the server is older and has none.</summary>
    public static string Short(SystemSummary system) => system.ShortName ?? system.Name;

    public static string Badge(DumpStatus status) => status switch
    {
        DumpStatus.Good => "owned",
        DumpStatus.Incomplete => "partial",
        DumpStatus.Bad => "bad",
        _ => "missing",
    };

    public static string Label(DumpStatus status) => status == DumpStatus.Good ? "Have" : status.ToString();

    public static string Label(DownloadState state) => state switch
    {
        DownloadState.Fetching => "Fetching",
        DownloadState.Queued => "Waiting",
        DownloadState.Working => "Working",
        DownloadState.NeedsReview => "Needs review",
        DownloadState.Filed => "Filed",
        DownloadState.Discarded => "Not needed",
        DownloadState.Rejected => "Rejected",
        _ => "Failed",
    };

    public static string Badge(DownloadState state) => state switch
    {
        DownloadState.Fetching or DownloadState.Working => "active",
        DownloadState.Queued => "active waiting",
        DownloadState.NeedsReview => "partial",
        DownloadState.Filed => "owned",
        DownloadState.Failed => "missing",
        _ => "neutral",
    };

    public static string RequestLabel(RequestState state) => state switch
    {
        RequestState.Active => "Fetching",
        RequestState.Done => "In library",
        RequestState.Failed => "Failed",
        _ => "Dismissed",
    };

    public static string RequestBadge(RequestState state) => state switch
    {
        RequestState.Active => "active",
        RequestState.Done => "owned",
        RequestState.Failed => "missing",
        _ => "neutral",
    };
}
