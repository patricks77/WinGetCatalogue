using System.Text.RegularExpressions;

// Infers categories from package identifiers when metadata is insufficient.
namespace WinGetCatalogue;

internal static class CatalogNameClassifier
{
    private static readonly (Regex Pattern, string Category, string Subcategory)[] Rules =
    [
        // Use explicit product or cryptocurrency terms; a generic "wallet" can mean passwords.
        Rule(@"\bspacemesh\b", "blockchain", "mining"),
        Rule(@"\b(bitcoin wallet|ethereum wallet|crypto wallet|cryptocurrency wallet)\b", "blockchain", "wallets"),
        Rule(@"\b(bitcoin miner|crypto miner|cryptocurrency miner|crypto mining|bitcoin mining)\b", "blockchain", "mining"),
        Rule(@"\b(password|passphrase|passwd)\b", "securite", "comptes"),
        Rule(@"\b(antivirus|antimalware|anti malware|firewall|malware scanner|security scanner|file encrypt|disk encrypt)\b", "securite", "protection"),
        Rule(@"\b(pdf[a-z]*|epub)\b", "bureautique", "pdf"),
        Rule(@"\b(spreadsheet|word processor|document editor|document scanner)\b", "bureautique", "suites"),
        Rule(@"\b(note taking|note keeper|notebook|mind map|mind mapping)\b", "bureautique", "notes"),
        Rule(@"\b(video editor|video cutter|video trimmer|video capture|video effects|video enhancer|screen recorder|screen capture)\b", "creation", "video"),
        Rule(@"\b(photo editor|photo studio|photo maker|photo edit|image editor|image editing|image resizer|graphic editor|drawing tool)\b", "creation", "image"),
        // Voice packs are production assets even when the title also mentions AI.
        Rule(@"\b(voice (?:package|pack|bank|bundle)|voicebank|singing voice synthesis)\b", "creation", "audio"),
        Rule(@"\b(audio editor|audio recorder|audio analyser|audio analyzer|audio analysis|sound editor|music producer|music production)\b", "creation", "audio"),
        Rule(@"\b(video player|media player|music player|audio player|audio playback|video playback|audio switcher|audio relay)\b", "multimedia", "lecture"),
        Rule(@"\b(video converter|audio converter|video encoder|audio encoder|video to mp3|dvd ripper|dvd burner)\b", "multimedia", "conversion"),
        Rule(@"\b(video downloader|music downloader|video download|music download|youtube downloader)\b", "multimedia", "collections"),
        Rule(@"\b(web browser|internet browser)\b", "internet", "navigateurs"),
        Rule(@"\b(email client|mail client|email checker)\b", "internet", "messagerie"),
        Rule(@"\bvpn\b", "reseaux", "vpn"),
        Rule(@"\b(ssh client|remote desktop|remote access|remote control)\b", "reseaux", "distance"),
        Rule(@"\b(network scanner|network monitor|network analyzer|network analyser|wireless network)\b", "reseaux", "diagnostic"),
        Rule(@"\b(file manager|file explorer|file finder|file search|file rename|file renamer|file recovery|file rescue|archive manager|zip extractor|duplicate file|file sync)\b", "utilitaires", "fichiers"),
        Rule(@"\b(backup|backupper|disk manager|disk management|disk utility|disk space|disk usage|disk boss|disk wipe|kill disk|data recovery)\b", "utilitaires", "disques"),
        Rule(@"\b(clipboard manager|clipboard history|keyboard remapper|key remapper|text paster|tray minimizer|window manager|desktop launcher|desktop tile launcher)\b", "utilitaires", "bureau"),
        Rule(@"\b(system monitor|system optimizer|system cleaner|registry cleaner|process killer|driver updater|software updater)\b", "utilitaires", "entretien"),
        Rule(@"\b(code editor|source editor|code analyser|code analyzer|debugger|compiler|disassembler|hex editor|sql editor|api testing|api client|app installer file builder)\b", "developpement", "editeurs"),
        Rule(@"\b(database manager|database viewer|sql server|sqlite browser|database browser)\b", "donnees", "bd"),
        Rule(@"\b(printer driver|scanner driver|graphics driver|sound driver|device driver|hardware monitor|ssd toolbox)\b", "materiel", "pilotes"),
        Rule(@"\b(language translator|translation tool|dictionary|language learning)\b", "sciences", "apprentissage"),
    ];

    internal static (string Category, string Subcategory)? Classify(string id, string name)
    {
        int firstDot = id.IndexOf('.');
        string package = firstDot < 0 ? id : id[(firstDot + 1)..];
        string words = SplitWords(name) + " " + SplitWords(package) + " " +
            Regex.Replace(name.ToLowerInvariant(), @"[^\p{L}\p{Nd}]+", " ");
        foreach (var rule in Rules)
        {
            if (rule.Pattern.IsMatch(words))
            {
                return (rule.Category, rule.Subcategory);
            }
        }
        return null;
    }

    private static string SplitWords(string text)
    {
        text = Regex.Replace(text, @"([\p{Ll}\p{Nd}])(\p{Lu})", "$1 $2");
        text = Regex.Replace(text, @"(\p{Lu})(\p{Lu}\p{Ll})", "$1 $2");
        return Regex.Replace(text, @"[^\p{L}\p{Nd}]+", " ").ToLowerInvariant();
    }

    private static (Regex, string, string) Rule(string pattern, string category, string subcategory) =>
        (new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled), category, subcategory);
}
