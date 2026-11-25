// Refines each main category using package metadata and classification rules.
namespace WinGetCatalogue;

public sealed record CatalogSubcategory(string Key, string Name)
{
    internal static readonly CatalogSubcategory All = new("", "All subcategories");
    internal static readonly CatalogSubcategory Other = new("autres", "Uncategorized");

    private static readonly Dictionary<string, SubcategoryRule[]> Rules = new(StringComparer.Ordinal)
    {
        ["internet"] =
        [
            new(new("navigateurs", "Browsers & extensions"), "browser web-browser webbrowser browser-extension gecko chromium firefox chrome browser-plugin"),
            new(new("messagerie", "Email & messaging"), "email e-mail mail smtp imap chat chat-client messaging messenger xmpp telegram irc"),
            new(new("transferts", "File transfer & sharing"), "ftp sftp ftp-client torrent bittorrent peer-to-peer file-sharing download-manager scp"),
            new(new("communications", "Calls & video meetings"), "voip video-conferencing videoconference web-conferencing softphone sip video-call"),
            new(new("flux", "Feeds & web reading"), "rss rss-reader feed-reader news-reader web-reader"),
        ],
        ["bureautique"] =
        [
            new(new("suites", "Office suites & documents"), "office office-suite documents word-processor word-processing spreadsheet presentations tex latex odf docx"),
            new(new("pdf", "PDF & ebooks"), "pdf pdf-editor pdf-reader epub ebook e-book e-reader xournal xournalapp"),
            new(new("notes", "Notes & writing"), "note-taking notes notebook markdown markdown-editor outliner text-editor mind-map obsidian"),
            new(new("organisation", "Organization & collaboration"), "collaboration kanban project-management task-manager todo calendar time-tracking productivity planner"),
            new(new("reference", "References & bibliographies"), "bibliography reference-manager citation-manager citations zotero mendeley"),
            new(new("finances", "Accounting & finance"), "accounting finance invoicing budget personal-finance payroll"),
        ],
        ["creation"] =
        [
            new(new("image", "Images & drawing"), "image image-editor image-editing photo photography photo-editing graphic-design draw drawing painting paint illustration vector pixel-art art svg graphics"),
            new(new("video", "Video editing & capture"), "video-editing video-editor recording video-production video screen-recording screencast obs screen-capture streaming-encoder"),
            new(new("audio", "Audio production & editing"), "audio-editing audio-editor audio-production daw midi music-production music-composition audio-plugin recording-studio audacity audio"),
            new(new("troisd", "3D & animation"), "3d cad model modeling modelling animation rendering sculpting blender mesh godot-engine vtk"),
        ],
        ["developpement"] =
        [
            new(new("editeurs", "Editors & environments"), "editor code-editor ide visual-studio vscode notepad editorconfig vim emacs debugger source-code coding"),
            new(new("langages", "Languages & SDKs"), "programming-language java jre jdk openjdk jvm python ruby perl php dotnet nodejs node-js javascript typescript sdk runtime golang compiler interpreter language-server language"),
            new(new("versions", "Version control & build tools"), "git github version-control subversion vcs build-tool build-system cmake make build developer-workflow"),
            new(new("terminaux", "Terminals & scripts"), "terminal terminal-emulator cli tui shell command-line powershell scripting bash command-shell"),
            new(new("conteneurs", "Containers & deployment"), "docker containers container kubernetes k8s orchestration devops ci-cd"),
            new(new("apis", "APIs & testing"), "api-testing api-client rest-client test-runner api postman unit-test benchmarking"),
        ],
        ["utilitaires"] =
        [
            new(new("fichiers", "Files & archives"), "file files file-manager file-search file-management archiver archive compression zip file-renamer bulk-rename synchronization file-sync extractor unzip unarchiver archives decompressing unpacking"),
            new(new("disques", "Storage & backup"), "backup disk disk-management disk-imaging disk-partition partition disk-health smart bootable usb iso data-recovery file-recovery storage"),
            new(new("bureau", "Desktop & personalization"), "window-manager desktop desktop-enhancement customization taskbar wallpaper icon-theme launcher shell-extension clipboard clipboard-manager hotkey tray windows-commandpalette-extension input-method ime pinyin 输入法 拼音"),
            new(new("entretien", "System maintenance"), "system-monitor monitoring cleanup uninstaller installer windows-utilities system-utility registry benchmarking diagnostic diagnostics sysinternals benchmark"),
            new(new("accessibilite", "Accessibility & automation"), "accessibility screen-reader keyboard-mapping macro automation-tool automation speech-to-text text-to-speech magnifier"),
        ],
        ["jeux"] =
        [
            new(new("plateformes", "Platforms & launchers"), "game-launcher game-launchers gaming-platform steam-client steam gog epic-games minecraft prism-launcher game-store"),
            new(new("emulation", "Emulation & retro"), "game-emulator console-emulator emulator emulation retro-gaming retrogaming nintendo playstation dolphin retroarch arcade"),
            new(new("titres", "Games to play"), "game games gaming video-game rpg fps strategy-game racing-game roguelike puzzle-game chess card-game adventure-game simulation-game"),
            new(new("accessoires", "Mods & controllers"), "mod-manager gaming-controller game-controller joystick handheld controller mods minecraft-mod"),
        ],
        ["multimedia"] =
        [
            new(new("lecture", "Audio & video players"), "media-player video-player audio-player music-player vlc player multimedia media-center video playback audio music"),
            new(new("conversion", "Conversion & encoding"), "transcoder video-converter audio-converter ffmpeg handbrake codecs codec mp3 mp4 encoder"),
            new(new("diffusion", "Streaming & broadcasting"), "video-streaming streaming-media media-server iptv radio streaming podcast podcasts"),
            new(new("collections", "Libraries & downloads"), "music-library media-library media-downloader video-downloader youtube-downloader music-downloader subtitle subtitles karaoke audiobook audiobooks media-organizer"),
        ],
        ["securite"] =
        [
            new(new("protection", "Protection & scanning"), "antivirus antimalware anti-malware firewall endpoint-protection malware vulnerability-scanner forensics security-tools"),
            new(new("comptes", "Passwords & authentication"), "password-manager passwords password-generator authenticator totp 2fa passkey vault 1password bitwarden keepass keepassxc"),
            new(new("chiffrement", "Encryption & privacy"), "cryptography encryption encrypted certificate certificates privacy-protection privacy encryption-tool"),
        ],
        ["reseaux"] =
        [
            new(new("vpn", "VPNs, tunnels & DNS"), "vpn vpn-client wireguard openvpn tunnel proxy reverse-proxy dns dns-client tailscale zerotier"),
            new(new("distance", "Remote access & control"), "remote-desktop remote-access rdp vnc ssh ssh-client rmm citrix remote-control"),
            new(new("serveurs", "Servers & hosting"), "server web-server hosting self-hosted server-management server-tools nginx apache"),
            new(new("diagnostic", "Network monitoring & diagnostics"), "network-monitor network-scanner packet-capture network-analyzer wireshark router network-tools tcp-ip network"),
        ],
        ["donnees"] =
        [
            new(new("ia", "AI & assistants"), "ai llm chatbot large-language-model machine-learning deep-learning mcp ai-agent agentic ai-assistant generative-ai inference model-context-protocol"),
            new(new("bd", "Databases"), "database databases sql sql-client database-client dbms nosql mongodb postgresql mysql sqlite dbeaver"),
            new(new("analyses", "Analysis & visualization"), "analytics data-analysis data-visualization data-science etl business-intelligence statistics dashboard"),
        ],
        ["sciences"] =
        [
            new(new("apprentissage", "Learning & languages"), "educational education learning language-learning classroom school flashcards anki translate translation translator dictionary 翻译 词典"),
            new(new("calcul", "Mathematics & calculation"), "mathematics math calculator scientific-calculator statistics algebra geometry maths"),
            new(new("laboratoire", "Research & engineering"), "chemistry physics biology astronomy science scientific engineering molecular electronics circuit laboratory"),
            new(new("cartographie", "Maps & weather"), "geospatial gis mapping cartography geography weather meteorology gps map"),
        ],
        ["materiel"] =
        [
            new(new("pilotes", "Drivers & firmware"), "driver drivers firmware bios gpu-driver chipset motherboard device-manager"),
            new(new("peripheriques", "Peripherals & RGB"), "rgb rgb-control peripheral peripherals mouse keyboard webcam touchscreen smart-card display"),
            new(new("impression", "Printing & scanning"), "printer scanner printing 3d-printing 3d-printer slicing slicer scanning"),
            new(new("diagnostic", "Hardware diagnostics"), "hardware-monitor hardware-info gpu cpu measurement calibration hardware-tools"),
        ],
        ["blockchain"] =
        [
            new(new("wallets", "Cryptocurrency wallets"), "crypto-wallet cryptocurrency-wallet bitcoin-wallet ethereum-wallet wallet cryptocurrency-wallets"),
            new(new("mining", "Mining & validation"), "mining miner cryptocurrency-mining crypto-mining bitcoin-mining staking validator smeshing smesher"),
            new(new("nodes", "Blockchain nodes & networks"), "blockchain-node full-node crypto-node node blockchain-network"),
            new(new("trading", "Trading & portfolio"), "crypto-exchange cryptocurrency-exchange crypto-trading cryptocurrency-trading crypto-portfolio cryptocurrency-portfolio defi decentralized-finance"),
        ],
        ["autres"] =
        [
            new(new("versions", "Beta versions & variants"), "beta alpha nightly preview canary pre-release snapshot dev-build"),
            new(new("portables", "Portable versions"), "portable standalone"),
            new(new("langues", "Extensions & languages"), "language-pack localization language-extension translations language-support"),
        ],
    };

    internal static CatalogSubcategory[] For(string category) =>
        Rules.TryGetValue(category, out var rules) ? [All, .. rules.Select(rule => rule.Item), Other] : [];

    internal static (string Category, string Subcategory) Classify(string category, string title, HashSet<string> tags)
    {
        if (category == "autres")
        {
            // Specific subcategory keywords can classify packages whose primary tags are sparse.
            string bestCategory = category;
            int bestScore = 2;
            foreach (var family in Rules.Where(family => family.Key != "autres"))
            {
                var (_, score) = Best(family.Value, title, tags);
                if (score > bestScore)
                {
                    bestCategory = family.Key;
                    bestScore = score;
                }
            }

            category = bestCategory;
        }

        return (category, Best(Rules[category], title, tags).Item.Key);
    }

    internal static CatalogSubcategory Find(string category, string key)
    {
        return Rules.TryGetValue(category, out var rules)
            ? rules.FirstOrDefault(rule => rule.Item.Key == key)?.Item ?? Other
            : Other;
    }

    private static (CatalogSubcategory Item, int Score) Best(SubcategoryRule[] rules, string title, HashSet<string> tags)
    {
        CatalogSubcategory best = Other;
        int bestScore = 0;
        foreach (var rule in rules)
        {
            int score = rule.Terms.Count(tags.Contains) * 3 +
                rule.TitleTerms.Count(term => title.Contains(term, StringComparison.Ordinal)) * 2;
            if (score > bestScore)
            {
                best = rule.Item;
                bestScore = score;
            }
        }

        return (best, bestScore);
    }

    private sealed class SubcategoryRule(CatalogSubcategory item, string terms)
    {
        internal CatalogSubcategory Item { get; } = item;
        internal string[] Terms { get; } = terms.Split(' ');
        internal string[] TitleTerms { get; } = terms.Split(' ').Select(term => $"-{term}-").ToArray();
    }
}
