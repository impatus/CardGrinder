using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace CardGrinder.Core;

public sealed record BadgePage(IReadOnlyDictionary<uint, int> Drops, int Pages, bool HasGameBadges);

public static partial class BadgeParser
{
    private static string HasClass(string name) => $"contains(concat(' ',normalize-space(@class),' '),' {name} ')";
    public static BadgePage Parse(string html, bool allowMissingDropData = false)
    {
        var doc = new HtmlDocument(); doc.LoadHtml(html);
        if (doc.DocumentNode.SelectSingleNode("//input[@id='steamAccountName' or @name='password']") is not null)
            throw new InvalidDataException("Steam Community requires a new sign-in. Reconnect using a new QR code.");
        var rows = doc.DocumentNode.SelectNodes($"//div[{HasClass("badge_row_inner")}]");
        if (rows is null && doc.DocumentNode.SelectSingleNode($"//*[{HasClass("badges_sheet")}]") is null)
            throw new InvalidDataException("Steam returned an unrecognized Badges page. No card counts were changed.");
        var drops = new Dictionary<uint, int>();
        bool hasGameBadges = false;
        foreach (var row in rows ?? Enumerable.Empty<HtmlNode>())
        {
            var appNode = row.SelectSingleNode($".//div[{HasClass("card_drop_info_dialog")} and @id]");
            if (appNode is null)
            {
                // Steam places the badge overlay beside badge_row_inner, not inside it.
                var outer = row.SelectSingleNode($"ancestor::div[{HasClass("badge_row")}][1]") ?? row;
                if (outer.SelectSingleNode(".//a[contains(@href,'/gamecards/')]") is not null) hasGameBadges = true;
                // Crafted/foil/unowned badges can legitimately omit the dialog even when authenticated.
                // Keep their status unknown; never invent a zero count for these rows.
                continue;
            }
            hasGameBadges = true;
            string id = appNode.GetAttributeValue("id", "");
            var match = AppIdPattern().Match(id);
            if (!match.Success || !uint.TryParse(match.Groups[1].Value, out var appId) || appId == 0)
                throw new InvalidDataException("Steam changed a game identifier in Badges. Farming has been paused.");
            // The final fields are crafted badge level and border type. Level 1 is not a foil marker.
            if (match.Groups[3].Value == "1") continue;
            var countNode = row.SelectSingleNode($".//span[{HasClass("progress_info_bold")}]");
            if (countNode is null) throw new InvalidDataException($"Steam omitted card status for App {appId}. No drops were assumed.");
            string text = HtmlEntity.DeEntitize(countNode.InnerText).Trim();
            int remaining = ParseRemaining(text);
            if (drops.TryGetValue(appId, out int existing) && existing != remaining)
                throw new InvalidDataException("Steam returned conflicting card counts. Refresh later.");
            drops[appId] = remaining;
        }
        int pages = 1;
        // The current page can be a span rather than an anchor, including on the last page.
        foreach (var link in doc.DocumentNode.SelectNodes($"//*[{HasClass("pagelink")} or {HasClass("page_current")}]") ?? Enumerable.Empty<HtmlNode>())
            if (int.TryParse(link.InnerText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int page)) pages = Math.Max(pages, page);
        if (pages > 200) throw new InvalidDataException("The Badges library exceeds the conservative 200-page request limit.");
        if (!allowMissingDropData) ValidateDropVisibility(hasGameBadges, drops.Count);
        return new(drops, pages, hasGameBadges);
    }

    public static void ValidateDropVisibility(bool hasGameBadges, int knownGameCount)
    {
        if (hasGameBadges && knownGameCount == 0)
            throw new InvalidDataException("Steam did not expose any private card-drop status. Sign out and reconnect using a new QR code.");
    }

    public static int ParseRemaining(string text)
    {
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Equals("No card drops remaining", StringComparison.OrdinalIgnoreCase)) return 0;
        var match = RemainingPattern().Match(text);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int count) || count > 1000)
            throw new InvalidDataException("Steam returned unknown card-drop text. Farming has been paused to avoid incorrect progress.");
        return count;
    }
    [GeneratedRegex(@"^card_drop_info_gamebadge_(\d+)_(\d+)_(0|1)$", RegexOptions.CultureInvariant)] private static partial Regex AppIdPattern();
    [GeneratedRegex(@"^(\d+) card drops? remaining$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex RemainingPattern();
}
