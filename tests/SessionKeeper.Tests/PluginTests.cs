using System.Text.Json;
using System.Text.RegularExpressions;

namespace SessionKeeper.Tests;

/// <summary>The Claude Code plugin in plugin/ and the marketplace entry that points at it.</summary>
public sealed class PluginTests
{
    private static readonly string Repo = FindRepo();
    private static string Plugin => Path.Combine(Repo, "plugin");

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SessionKeeper.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static string AppVersion() =>
        Regex.Match(File.ReadAllText(Path.Combine(Repo, "src", "SessionKeeper", "SessionKeeper.csproj")), "<Version>([^<]+)</Version>").Groups[1].Value;

    [Fact]
    public void ManifestAndMarketplaceAgreeWithTheApp()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Plugin, ".claude-plugin", "plugin.json")));
        using var market = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo, ".claude-plugin", "marketplace.json")));
        var entry = market.RootElement.GetProperty("plugins")[0];
        Assert.Equal("session-keeper", manifest.RootElement.GetProperty("name").GetString());
        Assert.Equal("session-keeper", entry.GetProperty("name").GetString());
        Assert.Equal("./plugin", entry.GetProperty("source").GetString());
        Assert.Equal(AppVersion(), manifest.RootElement.GetProperty("version").GetString());
        Assert.Equal(AppVersion(), entry.GetProperty("version").GetString());
    }

    [Fact]
    public void TheSkillLivesInThePluginAndTheInstallerCopiesItFromThere()
    {
        Assert.True(File.Exists(Path.Combine(Plugin, "skills", "session-keeper", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(Plugin, "skills", "session-keeper", "sessionkeeper.ps1")));
        Assert.Contains(@"'plugin\skills\session-keeper'", File.ReadAllText(Path.Combine(Repo, "install.ps1")));
    }

    /// <summary>A frontmatter value starting with [ or { is YAML syntax: unquoted, it breaks the whole frontmatter.</summary>
    [Fact]
    public void FrontmatterValuesThatLookLikeYamlAreQuoted()
    {
        var files = Directory.GetFiles(Path.Combine(Plugin, "commands"), "*.md").Append(Path.Combine(Plugin, "skills", "session-keeper", "SKILL.md"));
        foreach (var f in files)
        {
            var text = File.ReadAllText(f).Replace("\r\n", "\n");
            Assert.StartsWith("---\n", text);
            var front = text[4..text.IndexOf("\n---\n", 4, StringComparison.Ordinal)];
            foreach (var line in front.Split('\n'))
            {
                var m = Regex.Match(line, @"^[a-z-]+:\s*(.+)$");
                if (m.Success) Assert.False(m.Groups[1].Value.StartsWith('[') || m.Groups[1].Value.StartsWith('{'), $"{Path.GetFileName(f)}: quote '{line}'");
            }
            Assert.Matches(@"(?m)^description: \S", front);
        }
    }

    [Theory]
    [InlineData("running.md", "--list --no-messages")]
    [InlineData("recent.md", "--recent --no-messages")]
    [InlineData("snapshots.md", "--snapshots")]
    [InlineData("snapshot.md", "--snapshot")]
    [InlineData("open.md", " open")]
    public void EachCommandRunsTheBundledScriptOnly(string file, string options)
    {
        var text = File.ReadAllText(Path.Combine(Plugin, "commands", file));
        const string script = "\"${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1\"";
        Assert.Contains(script + " " + options.TrimStart(), text);
        // Every allowed tool is that script, nothing broader.
        foreach (Match m in Regex.Matches(text, @"(?m)^  - (Bash|PowerShell)\((.+)\)$"))
            Assert.StartsWith("powershell -NoProfile -ExecutionPolicy Bypass -File " + script + " ", m.Groups[2].Value);
        Assert.DoesNotContain("!`", text); // no commands are run before Claude reads the instructions
    }

    [Fact]
    public void TheSkillPreApprovesOnlyTitlesOnlyListings()
    {
        var skill = File.ReadAllText(Path.Combine(Plugin, "skills", "session-keeper", "SKILL.md"));
        foreach (Match m in Regex.Matches(skill, @"(?m)^  - (?:Bash|PowerShell)\(.+sessionkeeper\.ps1"" (--list|--recent)(.*)\)$"))
            Assert.StartsWith(" --no-messages", m.Groups[2].Value);
        Assert.Equal(8, Regex.Matches(skill, @"(?m)^  - (?:Bash|PowerShell)\(.+sessionkeeper\.ps1"" (--list|--recent) --no-messages").Count);
    }

    [Fact]
    public void ListingCommandsAndTheSkillNeverAskForMessages()
    {
        Assert.Contains("--list --no-messages", File.ReadAllText(Path.Combine(Plugin, "skills", "session-keeper", "SKILL.md")));
        Assert.Contains("--recent --no-messages", File.ReadAllText(Path.Combine(Plugin, "skills", "session-keeper", "SKILL.md")));
        foreach (var f in Directory.GetFiles(Path.Combine(Plugin, "commands"), "*.md"))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), @"sessionkeeper\.ps1"" (--list|--recent)\b[^\n)]*"))
                Assert.Contains("--no-messages", m.Value);
    }

    private static readonly string[] CommandNames = { "running", "recent", "snapshots", "snapshot", "open" };

    /// <summary>Every command file, the skill and the plugin README list the same five commands, with the same wording.</summary>
    [Fact]
    public void EveryFileListsTheSameFiveCommands()
    {
        Assert.Equal(CommandNames.Order(), Directory.GetFiles(Path.Combine(Plugin, "commands"), "*.md").Select(Path.GetFileNameWithoutExtension).Order());
        var files = Directory.GetFiles(Path.Combine(Plugin, "commands"), "*.md")
            .Append(Path.Combine(Plugin, "skills", "session-keeper", "SKILL.md")).Append(Path.Combine(Plugin, "README.md"));
        string[]? expected = null;
        foreach (var f in files)
        {
            var list = Regex.Matches(File.ReadAllText(f), @"(?m)^\s*- `/session-keeper:[^\r\n]+").Select(m => m.Value.Trim()).ToArray();
            Assert.Equal(CommandNames.Select(c => "/session-keeper:" + c), list.Select(l => Regex.Match(l, @"/session-keeper:[a-z]+").Value));
            expected ??= list;
            Assert.Equal(expected, list);
        }
        // Claude never sees the user-only commands in its own list, so the files say they exist.
        foreach (var f in files.SkipLast(1))
            Assert.Contains("never say they don't exist", File.ReadAllText(f).Replace("\r\n", " ").Replace("\n", " "));
    }

    [Fact]
    public void StatusesIncludeShell()
    {
        foreach (var f in new[] { Path.Combine(Plugin, "commands", "running.md"), Path.Combine(Plugin, "skills", "session-keeper", "SKILL.md") })
        {
            var text = File.ReadAllText(f).Replace("\r\n", " ").Replace("\n", " ");
            Assert.Contains("*shell* (running a background shell command, such as a dev", text);
            Assert.Contains("*needs input*", text);
        }
    }

    /// <summary>Listings hide untitled temp-folder sessions unless the user adds "all".</summary>
    [Fact]
    public void ListingsHideThrowawaySessionsByDefault()
    {
        const string script = "\"${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1\"";
        var running = File.ReadAllText(Path.Combine(Plugin, "commands", "running.md"));
        Assert.Contains(script + " --list --no-messages --hide-temp\n", running.Replace("\r\n", "\n"));
        Assert.Contains("argument-hint: \"[all]\"", running);
        var recent = File.ReadAllText(Path.Combine(Plugin, "commands", "recent.md"));
        Assert.Contains(script + " --recent --no-messages --hide-temp\n", recent.Replace("\r\n", "\n"));
        Assert.Contains("argument-hint: \"[days] [all]\"", recent);
        foreach (var allowed in new[] { "--recent --no-messages --hide-temp)", "--recent --no-messages --hide-temp --days *)", "--recent --no-messages)", "--recent --no-messages --days *)" })
            Assert.Contains(script + " " + allowed, recent);
        var skill = File.ReadAllText(Path.Combine(Plugin, "skills", "session-keeper", "SKILL.md"));
        Assert.Contains("`--list --no-messages --hide-temp`", skill);
        Assert.Contains("`--recent --no-messages --hide-temp`", skill);
    }

    [Fact]
    public void CommandsThatChangeSomethingAreOnlyRunByTheUser()
    {
        foreach (var f in new[] { "snapshot.md", "open.md" })
            Assert.Contains("disable-model-invocation: true", File.ReadAllText(Path.Combine(Plugin, "commands", f)));
    }
}
