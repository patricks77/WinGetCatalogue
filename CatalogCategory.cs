using System.Text.RegularExpressions;

// Classifies packages using WinGet metadata and category rules.
namespace WinGetCatalogue;

public sealed partial record CatalogCategory(string Key, string Name, string Glyph)
{
    internal static readonly CatalogCategory All = new("", "All software", "\uE8A9");
    private static readonly CatalogCategory Other = new("autres", "Other software", "\uE9CE");

    private static readonly CategoryRule[] Rules =
    [
        new(new("internet", "Internet", "\uE774"),
            "browser web-browser webbrowser browser-extension gecko firefox chromium chrome internet-browser email e-mail mail-client imap smtp rss rss-reader feed-reader torrent bittorrent ftp sftp messenger messaging instant-messaging chat video-conferencing voip videoconference webmail webpage 网页浏览器 浏览器 电子邮件 邮件",
            "internet chat network web mail online communication discord"),
        new(new("bureautique", "Office & productivity", "\uE8A5"),
            "office office-suite word-processor word-processing word spreadsheet spreadsheets presentation presentations pdf pdf-editor pdf-reader e-book ebook e-reader document documents document-management note-taking notes notebook markdown-editor kanban task-manager calendar mind-map mind-mapping bibliography reference-manager project-management accounting invoicing finance budget time-tracking personal-finance notepad markdown 笔记 办公 记事本 思维导图",
            "productivity collaboration todo text writing work business annotation reader text-editor resume cv resume-builder invoicing"),
        new(new("creation", "Creative", "\uE790"),
            "photo-editor photo-editing image-editor image-editing graphic-design vector drawing draw painting paint sketching pixel-art digital-art animation animation-3d 3d-modeling modeling modelling rendering sculpting photography video-editing video-editor video-production audio-editing audio-editor audio-production daw music-production midi tracker music-composition recording screen-recording screencast streaming-encoder blender cad audio-plugin recording-studio 绘画 绘图 视频编辑 剪辑 设计",
            "art graphics image picture photo svg design render creative 3d sketch editor capture production video-downloadhelper"),
        new(new("developpement", "Development", "\uE943"),
            "developer-tools developer development develop programming compiler sdk ide code-editor source-code version-control git debugger build-tool build-system devtools programming-language interpreter language-server visual-studio java jvm jre jdk openjdk dotnet python javascript nodejs typescript rust golang php ruby powershell terminal terminal-emulator containers docker kubernetes api-testing package-manager api-client linter formatter github vscode 编程 代码 开发 wsl visual-cpp cpp assembler npm npmjs npm-desktop-manager terraform arduino logcat",
            "coding code cli tui shell command-line scripting api automation git-client library framework json yaml"),
        new(new("utilitaires", "Utilities", "\uE713"),
            "archiver archive compression backup file-manager file-management file-search file-sync synchronization clipboard clipboard-manager file-recovery disk disk-management disk-imaging disk-partition partition smart disk-health iso usb bootable boot bootloader fs filesystem system-utility windows-utilities productivity-tool system-monitor monitoring window-manager desktop-enhancement customization launcher hotkey automation-tool macro screenshot accessibility clipboard-history file-converter uninstaller installer package-installer cleanup renamer file-renamer powertoys shell-extension registry taskbar 系统工具 文件管理 截图 剪贴板 压缩 sysinternals windows-commandpalette-extension disk-space disk-usage duplicates extractor unzip unarchiver archives decompressing unpacking input-method ime 输入法 pinyin 拼音 recovery file-recovery benchmark",
            "utility utilities tool file files folder windows desktop automation cli sysadmin system management search sync"),
        new(new("jeux", "Games", "\uE7FC"),
            "gaming game games video-game game-launcher game-launchers minecraft steam steam-client gog epic-games retro-gaming console-emulator game-emulator retrogaming nintendo playstation xbox arcade simulator-game chess puzzle racing-game rpg fps game-controller joystick handheld launcher-games mod-manager gaming-platform 游戏 游戏启动器 模拟器",
            "emulator emulation controller launcher mods simulation racing retro esport"),
        new(new("multimedia", "Multimedia", "\uE8D6"),
            "media-player video-player audio-player music-player vlc multimedia podcast podcasts media-center media-server transcoder video-converter audio-converter video-downloader music-downloader media-downloader streaming-media music-library subtitles subtitle-editor video-streaming iptv radio audiobook karaoke audiobooks 音乐播放器 视频播放器 媒体播放器 dvd audio-analysis video-downloadhelper",
            "media music audio video streaming player codec youtube sound mp3 mp4 movie movies"),
        new(new("securite", "Security", "\uE72E"),
            "security antivirus anti-virus anti-malware antimalware firewall password-manager passwords password-generator cryptography encryption encrypted authenticator 2fa totp passkey privacy-protection penetration-testing vulnerability-scanner forensics endpoint-protection security-tools cyber-security cybersecurity 安全 密码 加密",
            "privacy malware certificate certificates signed signing password vault security-tool"),
        new(new("blockchain", "Crypto & blockchain", "\uE8C7"),
            "blockchain cryptocurrency crypto-currency crypto-wallet cryptocurrency-wallet bitcoin bitcoin-wallet ethereum ethereum-wallet decentralized-finance crypto-mining cryptocurrency-mining bitcoin-mining blockchain-node crypto-exchange",
            "web3 defi"),
        new(new("reseaux", "Networks & servers", "\uE839"),
            "vpn network-monitor network-scanner network-tools networking server web-server reverse-proxy proxy ssh ssh-client remote-desktop rdp vnc dns dns-client tcp-ip network-analyzer packet-capture wireshark router firewall-management self-hosted server-management sysadmin rmm vpn-client remote-access 网管 远程桌面 网络工具",
            "network tcp tunnel remote cloud-hosting hosting tcpip server-tools administrator"),
        new(new("donnees", "AI & data", "\uEA86"),
            "ai artificial-intelligence llm large-language-model machine-learning deep-learning chatbot chatgpt generative-ai ai-agent agentic mcp model-context-protocol inference ai-assistant database databases sql no-sql nosql dbms business-intelligence analytics data-analysis data-science data-visualization etl mongodb postgresql sqlite mysql 人工智能 大语言模型 智能体 聊天机器人 数据库",
            "agent data ml sql-client database-client language-model intelligent models"),
        new(new("sciences", "Education & science", "\uE821"),
            "education educational learning e-learning language-learning classroom school mathematics math chemistry physics biology astronomy science scientific research molecular geospatial gis mapping cartography weather meteorology engineering circuit simulation-science electronics calculator scientific-calculator flashcards anki 学习 教育 科学 数学 地图 translate translation translator dictionary 翻译 词典",
            "university student academic calculation measurement experiment microscope laboratory language geography"),
        new(new("materiel", "Drivers & hardware", "\uE772"),
            "driver drivers firmware bios motherboard hardware hardware-monitor gpu-driver printer scanner printing 3d-printing 3d-printer slicing rgb rgb-control peripheral peripherals mouse keyboard webcam touchscreen smart-card calibration device-manager 硬件 驱动 打印机 nvmexpress solidstatedrive solid-state-drive",
            "gpu cpu device chipset usb-device hardware-tools keyboard-lighting display hardware-info"),
    ];

    internal static CatalogCategory[] Categories { get; } = [All, .. Rules.Select(rule => rule.Category), Other];

    internal static CatalogCategory Find(string key) =>
        Categories.FirstOrDefault(category => category.Key == key) ?? Other;

    internal static (string Category, string Subcategory) Classify(string id, string name, string tags, string? categoryOverride)
    {
        var normalizedTags = tags.Split('\u001f', StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize).ToHashSet(StringComparer.Ordinal);
        string packageName = id[(id.IndexOf('.') + 1)..];
        string title = $"-{Normalize(name)}-{Normalize(packageName)}-";
        var best = Other;
        int bestScore = 0;

        foreach (var rule in Rules)
        {
            int score = rule.Strong.Count(normalizedTags.Contains) * 5
                + rule.Weak.Count(normalizedTags.Contains) * 2;
            if (score > bestScore)
            {
                best = rule.Category;
                bestScore = score;
            }
        }

        if (bestScore < 5 && categoryOverride is not null)
        {
            return CatalogSubcategory.Classify(categoryOverride, title, normalizedTags);
        }

        if (bestScore < 5 && CatalogNameClassifier.Classify(id, name) is { } nameClassification)
        {
            return nameClassification;
        }

        if (bestScore == 0)
        {
            foreach (var rule in Rules)
            {
                int score = rule.TitleTerms.Count(term => title.Contains(term, StringComparison.Ordinal)) * 3;
                if (score > bestScore)
                {
                    best = rule.Category;
                    bestScore = score;
                }
            }
        }

        return CatalogSubcategory.Classify(best.Key, title, normalizedTags);
    }

    // Keep the platform name distinct from the ordinary word "net" when stripping punctuation.
    private static string Normalize(string text) =>
        NonWordPattern().Replace(DotNetPattern().Replace(text.ToLowerInvariant(), "dotnet"), "-").Trim('-');

    [GeneratedRegex(@"(?<![\p{L}\p{Nd}])\.net(?![\p{L}\p{Nd}])", RegexOptions.CultureInvariant)]
    private static partial Regex DotNetPattern();

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonWordPattern();

    private sealed class CategoryRule(CatalogCategory category, string strong, string weak)
    {
        internal CatalogCategory Category { get; } = category;
        internal string[] Strong { get; } = strong.Split(' ');
        internal string[] Weak { get; } = weak.Split(' ');
        internal string[] TitleTerms { get; } = strong.Split(' ').Select(term => $"-{term}-").ToArray();
    }
}
