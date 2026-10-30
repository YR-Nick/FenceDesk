using System.IO;
namespace FenceDesk.Services;

public sealed class CategoryDefinition
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Accent { get; init; }
    public required int Order { get; init; }
}

public static class CategoryRules
{
    private static readonly string[] DevKeywords =
    {
        "keil", "arduino", "esp", "idf", "espressif", "canmv", "k210", "kicad", "godot",
        "hbuilder", "hbx", "mounriver", "stm32", "cubemx", "cubeide", "platformio",
        "visual studio", "vscode", "vs code", "python", "node", "nodejs", "git", "putty",
        "winscp", "ssh", "terminal", "powershell", "cmd", "serial", "flash", "download",
        "isp", "programmer", "proteus", "quartus", "vivado", "modelsim", "matlab", "labview",
        "freertos", "rt-thread", "ai-app", "ch340", "ch341", "ch9121", "ch9329", "kflash",
        "esptool", "openocd", "jlink", "st-link", "烧录", "下载器", "串口", "编程", "开发",
        "调试", "编译", "仿真", "单片机", "嵌入式", "代码", "intellij", "pycharm", "webstorm",
        "clion", "android studio", "unity", "unreal", "docker", "postman", "navicat",
        "dbeaver", "xshell", "mobaxterm", "sourcetree", "gitkraken", "eclipse",
    };

    private static readonly string[] ChatKeywords =
    {
        "微信", "wechat", "weixin", "qq", "钉钉", "dingtalk", "飞书", "feishu", "lark",
        "telegram", "discord", "whatsapp", "line", "teams", "zoom", "腾讯会议", "wemeet",
        "陌陌", "soul", "探探", "聊天", "通讯",
    };

    private static readonly string[] OfficeKeywords =
    {
        "word", "excel", "powerpoint", "wps", "office", "acrobat", "foxit", "福昕",
        "阅读器", "reader", "notion", "obsidian", "typora", "onenote", "笔记", "文献",
        "zotero", "endnote", "xmind", "mindmaster", "visio",
    };

    private static readonly string[] ToolKeywords =
    {
        "微信", "wechat", "weixin", "qq", "钉钉", "dingtalk", "飞书", "feishu", "lark",
        "百度网盘", "阿里云盘", "夸克", "360", "7-zip", "7zip", "winrar", "bandizip",
        "网易云", "cloudmusic", "qq音乐", "酷狗", "spotify", "迅雷", "thunder", "抖音",
        "tiktok", "快手", "剪映", "capcut", "腾讯会议", "wemeet", "zoom", "teams",
        "edge", "chrome", "firefox", "浏览器", "browser", "输入法", "sogou", "搜狗",
        "驱动", "driver", "管家", "安全", "截图", "snipaste", "录屏", "obs", "播放器",
        "potplayer", "vlc", "格式工厂", "everything", "listary", "utools", "quicker",
        "clash", "v2ray", "telegram", "discord", "notepad", "计算器", "calculator",
    };

    private static readonly string[] DocAppKeywords =
    {
        "word", "excel", "powerpoint", "wps", "office", "acrobat", "foxit", "福昕",
        "阅读器", "reader", "notion", "obsidian", "typora", "onenote", "笔记", "文献",
        "zotero", "endnote", "xmind", "mindmaster", "visio",
    };

    private static readonly string[] MediaAppKeywords =
    {
        "photoshop", "illustrator", "premiere", "after effects", "lightroom", "figma",
        "blender", "c4d", "cinema 4d", "audition", "audacity", "剪映", "capcut",
        "potplayer", "vlc", "mpc-hc", "音乐", "视频", "播放器",
    };

    private static readonly string[] GameKeywords =
    {
        "steam", "game", "游戏", "evil genius", "加速器", "epic", "origin", "battle.net",
        "wegame", "xbox", "minecraft", "英雄联盟", "原神", "崩坏", "永劫", "csgo", "dota",
        "lol", "魔兽", "星际", "模拟器",
    };

    private static readonly string[] DocExtensions =
    {
        ".doc", ".docx", ".pdf", ".txt", ".md", ".rtf", ".xls", ".xlsx", ".csv",
        ".ppt", ".pptx", ".epub", ".ofd", ".wps", ".pages", ".numbers", ".key",
    };

    private static readonly string[] MediaExtensions =
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico", ".psd", ".ai",
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".mp3", ".wav", ".flac",
        ".aac", ".m4a", ".ogg",
    };

    private static readonly string[] ProgramExtensions =
    {
        ".lnk", ".url", ".exe", ".msi", ".appx", ".msix", ".bat", ".cmd", ".ps1", ".msc",
        ".reg", ".jar", ".ahk",
    };

    public static readonly CategoryDefinition Dev = new()
    {
        Key = "dev", Title = "开发工具", Accent = "teal", Order = 0,
    };

    public static readonly CategoryDefinition Tools = new()
    {
        Key = "tools", Title = "常用工具", Accent = "blue", Order = 1,
    };

    public static readonly CategoryDefinition Chat = new()
    {
        Key = "chat", Title = "通讯聊天", Accent = "blue", Order = 2,
    };

    public static readonly CategoryDefinition Office = new()
    {
        Key = "office", Title = "办公工具", Accent = "violet", Order = 3,
    };

    public static readonly CategoryDefinition Documents = new()
    {
        Key = "docs", Title = "文档资料", Accent = "violet", Order = 4,
    };

    public static readonly CategoryDefinition Folders = new()
    {
        Key = "folders", Title = "文件夹", Accent = "green", Order = 5,
    };

    public static readonly CategoryDefinition Games = new()
    {
        Key = "games", Title = "游戏娱乐", Accent = "amber", Order = 6,
    };

    public static readonly CategoryDefinition Media = new()
    {
        Key = "media", Title = "图片素材", Accent = "rose", Order = 7,
    };

    public static readonly CategoryDefinition Other = new()
    {
        Key = "other", Title = "其他", Accent = "slate", Order = 8,
    };

    public static readonly IReadOnlyList<CategoryDefinition> All = new[]
    {
        Dev, Tools, Chat, Office, Documents, Folders, Games, Media, Other,
    };

    public static CategoryDefinition Classify(string path, bool isDirectory, string? targetPath = null)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var lowerName = name.ToLowerInvariant();
        var lowerExt = ext.ToLowerInvariant();
        var targetName = string.IsNullOrWhiteSpace(targetPath)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(targetPath).ToLowerInvariant();
        var targetFull = (targetPath ?? string.Empty).ToLowerInvariant();
        var signature = lowerName + " " + targetName + " " + targetFull;

        if (isDirectory)
        {
            return Folders;
        }

        // 游戏平台的安装目录优先识别，避免被“Program Files”等通用路径干扰。
        if (targetFull.Contains("\\steam\\") || targetFull.Contains("\\steamapps\\") ||
            targetFull.Contains("\\epic games\\") || targetFull.Contains("\\wegame\\") ||
            targetFull.Contains("\\games\\") || targetFull.Contains("\\game\\"))
        {
            return Games;
        }

        if (ContainsAny(signature, DevKeywords))
        {
            return Dev;
        }

        if (ContainsAny(signature, GameKeywords))
        {
            return Games;
        }

        if (ContainsAny(signature, ChatKeywords))
        {
            return Chat;
        }

        if (ContainsAny(signature, OfficeKeywords))
        {
            return Office;
        }

        if (ContainsAny(signature, DocAppKeywords))
        {
            return Documents;
        }

        if (ContainsAny(signature, MediaAppKeywords))
        {
            return Media;
        }

        if (ContainsAny(signature, ToolKeywords))
        {
            return Tools;
        }

        if (DocExtensions.Contains(lowerExt))
        {
            return Documents;
        }

        if (MediaExtensions.Contains(lowerExt))
        {
            return Media;
        }

        if (ProgramExtensions.Contains(lowerExt))
        {
            return Tools;
        }

        return Other;
    }

    public static CategoryDefinition ByKey(string? key)
        => All.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Other;

    private static bool ContainsAny(string value, IEnumerable<string> keywords)
        => keywords.Any(k => value.Contains(k, StringComparison.OrdinalIgnoreCase));
}
