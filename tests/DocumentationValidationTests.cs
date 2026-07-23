using System.Text.RegularExpressions;

namespace ThermalWatch.Tests;

public sealed class DocumentationValidationTests
{
    private const int MaximumAgentGuideLines = 150;
    private static readonly string s_repositoryRoot = FindRepositoryRoot();
    private static readonly Regex s_markdownLinkPattern = new(
        pattern: """!?\[[^\]]*\]\(\s*(?<target><[^>]+>|[^\s\)]+)(?:\s+(?:"[^"]*"|'[^']*'|\([^\)]*\)))?\s*\)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        matchTimeout: TimeSpan.FromSeconds(seconds: 1));
    private static readonly Regex s_uriSchemePattern = new(
        pattern: @"^[a-z][a-z0-9+.-]*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeout: TimeSpan.FromSeconds(seconds: 1));
    private static readonly Regex s_adrFileNamePattern = new(
        pattern: @"^(?<id>[0-9]{4})-.+\.md$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeout: TimeSpan.FromSeconds(seconds: 1));
    private static readonly Regex s_fencedBlockPattern = new(
        pattern: @"^```(?:bash|sh|shell|zsh|powershell|pwsh|cmd|bat|console)(?:[ \t][^\r\n]*)?\r?\n(?<body>.*?)^```\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
            | RegexOptions.Multiline | RegexOptions.Singleline,
        matchTimeout: TimeSpan.FromSeconds(seconds: 1));
    private static readonly Regex s_commandPathPattern = new(
        pattern: @"(?<![A-Za-z0-9_./\\-])(?<path>(?:\.{1,2}[/\\])?(?:[A-Za-z0-9_.-]+[/\\])*[A-Za-z0-9_.-]+\.(?:slnx|sln|csproj|fsproj|vbproj|sh|bash|zsh|ps1|cmd|bat|js|mjs|cjs|py))(?![A-Za-z0-9_.-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeout: TimeSpan.FromSeconds(seconds: 1));
    private static readonly Regex s_placeholderPattern = new(
        pattern: @"\b(?:TODO|TBD|FIXME|XXX)\b|lorem\s+ipsum|coming\s+soon|replace\s+me|insert\s+.+?\s+here",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeout: TimeSpan.FromSeconds(seconds: 1));

    private static readonly string[] s_routedDocumentationDirectories = ["domain", "components"];
    private static readonly string[] s_requiredAdrTemplateHeadings =
    [
        "# 0000: Title",
        "## Status",
        "## Context",
        "## Decision drivers",
        "## Considered options",
        "## Decision",
        "## Consequences",
        "## Validation or evidence",
        "## Related source files and documents",
        "## Supersedes / Superseded by"
    ];

    [Fact]
    public void LocalMarkdownFileLinksResolve()
    {
        var failures = new List<string>();

        foreach (string document in GetDocumentationFiles())
        {
            foreach (string target in GetMarkdownLinkTargets(document))
            {
                if (!TryResolveLocalTarget(document, target, out string? resolvedPath, out string? error))
                    continue;

                if (error is not null)
                {
                    failures.Add($"{Relative(document)} -> {target}: {error}");
                    continue;
                }

                if (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
                    failures.Add($"{Relative(document)} -> {target}");
            }
        }

        AssertNoFailures(description: "Unresolved local Markdown links", failures);
    }

    [Fact]
    public void DocumentationIndexCoversDomainAndComponentDocuments()
    {
        string indexPath = Path.Combine([s_repositoryRoot, "docs", "README.md"]);
        Assert.True(File.Exists(indexPath), "docs/README.md is required.");

        var indexedFiles = GetMarkdownLinkTargets(indexPath)
            .Select(target => TryResolveLocalTarget(indexPath, target, out string? path, out string? error)
                && error is null
                    ? path
                    : null)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> routedFiles = s_routedDocumentationDirectories
            .SelectMany(directory => EnumerateMarkdownFiles(Path.Combine([s_repositoryRoot, "docs", directory])))
            .Where(path => !Path.GetFileName(path).Equals(value: "README.md", StringComparison.OrdinalIgnoreCase));
        var missing = routedFiles
            .Where(path => !indexedFiles.Contains(path))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToList();

        AssertNoFailures(description: "Domain or component documents missing from docs/README.md", missing);
    }

    [Fact]
    public void AdrIdentifiersAreFourDigitsAndUnique()
    {
        string decisionsDirectory = Path.Combine([s_repositoryRoot, "docs", "decisions"]);
        var adrFiles = EnumerateMarkdownFiles(decisionsDirectory)
            .Where(path => !Path.GetFileName(path).Equals(value: "README.md", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var malformed = new List<string>();
        var identifiers = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (string? file in adrFiles)
        {
            Match match = s_adrFileNamePattern.Match(Path.GetFileName(file));
            if (!match.Success)
            {
                malformed.Add(Relative(file));
                continue;
            }

            string identifier = match.Groups["id"].Value;
            if (!identifiers.TryGetValue(identifier, out List<string>? files))
                identifiers.Add(identifier, files = []);

            files.Add(Relative(file));
        }

        AssertNoFailures(description: "ADR filenames without a four-digit identifier", malformed);

        var duplicates = identifiers
            .Where(pair => pair.Value.Count > 1)
            .Select(pair => $"{pair.Key}: {string.Join(separator: ", ", pair.Value.Order(StringComparer.Ordinal))}")
            .Order(StringComparer.Ordinal)
            .ToList();
        AssertNoFailures(description: "Duplicate ADR identifiers", duplicates);
    }

    [Fact]
    public void AdrTemplateContainsRequiredHeadingsInOrder()
    {
        string templatePath = Path.Combine([s_repositoryRoot, "docs", "decisions", "0000-template.md"]);
        Assert.True(File.Exists(templatePath), "docs/decisions/0000-template.md is required.");

        var headings = File.ReadLines(templatePath)
            .Select(line => line.TrimEnd())
            .Where(line => line.StartsWith('#'))
            .ToList();
        int nextSearchIndex = 0;
        var missingOrOutOfOrder = new List<string>();

        foreach (string requiredHeading in s_requiredAdrTemplateHeadings)
        {
            int index = headings.FindIndex(
                nextSearchIndex,
                heading => heading.Equals(requiredHeading, StringComparison.Ordinal));
            if (index < 0)
            {
                missingOrOutOfOrder.Add(requiredHeading);
                continue;
            }

            nextSearchIndex = index + 1;
        }

        AssertNoFailures(description: "Missing or out-of-order ADR template headings", missingOrOutOfOrder);
    }

    [Fact]
    public void RootAgentGuideRemainsConcise()
    {
        string agentGuidePath = Path.Combine([s_repositoryRoot, "AGENTS.md"]);
        Assert.True(File.Exists(agentGuidePath), "AGENTS.md is required.");

        int lineCount = File.ReadLines(agentGuidePath).Count();
        Assert.True(
            lineCount <= MaximumAgentGuideLines,
            $"AGENTS.md has {lineCount} lines; the limit is {MaximumAgentGuideLines}.");
    }

    [Fact]
    public void DurableDocumentationContainsNoObviousPlaceholders()
    {
        var failures = new List<string>();

        foreach (string? document in GetDocumentationFiles().Where(path => !IsDeliberateTemplate(path)))
        {
            string[] lines = File.ReadAllLines(document);
            for (int index = 0; index < lines.Length; index++)
            {
                if (s_placeholderPattern.IsMatch(lines[index]))
                    failures.Add($"{Relative(document)}:{index + 1}: {lines[index].Trim()}");
            }
        }

        AssertNoFailures(description: "Obvious unfinished documentation markers", failures);
    }

    [Fact]
    public void RepositoryPathsInFencedCommandsResolve()
    {
        var failures = new List<string>();

        foreach (string? document in GetDocumentationFiles().Where(path => !IsDeliberateTemplate(path)))
        {
            string content = File.ReadAllText(document);
            foreach (Match block in s_fencedBlockPattern.Matches(content))
            {
                foreach (Match pathMatch in s_commandPathPattern.Matches(block.Groups["body"].Value))
                {
                    string target = pathMatch.Groups["path"].Value;
                    if (target.Contains('$') || target.Contains('*'))
                        continue;

                    string resolvedPath = Path.GetFullPath(Path.Combine(
                        s_repositoryRoot,
                        target
                            .Replace('/', Path.DirectorySeparatorChar)
                            .Replace('\\', Path.DirectorySeparatorChar)));
                    if (!IsInsideRepository(resolvedPath)
                        || !File.Exists(resolvedPath) && !Directory.Exists(resolvedPath))
                    {
                        failures.Add($"{Relative(document)} -> {target}");
                    }
                }
            }
        }

        AssertNoFailures(description: "Missing repository paths referenced by fenced commands", failures);
    }

    private static IEnumerable<string> GetDocumentationFiles()
    {
        foreach (string? fileName in new[] { "README.md", "AGENTS.md" })
        {
            string path = Path.Combine(s_repositoryRoot, fileName);
            if (File.Exists(path))
                yield return path;
        }

        foreach (string? directory in new[] { "docs", ".agent", ".agents" })
        {
            foreach (string path in EnumerateMarkdownFiles(Path.Combine(s_repositoryRoot, directory)))
                yield return path;
        }
    }

    private static IEnumerable<string> EnumerateMarkdownFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, searchPattern: "*.md", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
            : [];

    private static IEnumerable<string> GetMarkdownLinkTargets(string document) =>
        s_markdownLinkPattern.Matches(File.ReadAllText(document))
            .Cast<Match>()
            .Select(match => match.Groups["target"].Value.Trim('<', '>'));

    private static bool TryResolveLocalTarget(
        string document,
        string target,
        out string resolvedPath,
        out string? error)
    {
        resolvedPath = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(target)
            || target.StartsWith('#')
            || target.StartsWith('/')
            || target.StartsWith(value: "//", StringComparison.Ordinal)
            || s_uriSchemePattern.IsMatch(target))
        {
            return false;
        }

        string path = target.Split('#', count: 2)[0].Split('?', count: 2)[0];
        if (path.Length == 0)
            return false;

        try
        {
            path = Uri.UnescapeDataString(path).Replace('/', Path.DirectorySeparatorChar);
            resolvedPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(document)!, path));
        }
        catch (Exception exception) when (exception is ArgumentException or UriFormatException)
        {
            error = "invalid path";
            return true;
        }

        if (!IsInsideRepository(resolvedPath))
            error = "target escapes the repository";

        return true;
    }

    private static bool IsInsideRepository(string path)
    {
        string relative = Path.GetRelativePath(s_repositoryRoot, path);
        return !relative.Equals(value: "..", StringComparison.Ordinal)
            && !relative.StartsWith(value: $"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool IsDeliberateTemplate(string path)
    {
        string relative = Relative(path).Replace(Path.DirectorySeparatorChar, '/');
        return relative.Equals(value: "docs/decisions/0000-template.md", StringComparison.OrdinalIgnoreCase)
            || relative.Equals(value: ".agent/PLANS.md", StringComparison.OrdinalIgnoreCase);
    }

    private static string Relative(string path) => Path.GetRelativePath(s_repositoryRoot, path);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine([directory.FullName, "ThermalWatch.slnx"])))
                return directory.FullName;
        }

        throw new InvalidOperationException(
            message: $"Could not locate ThermalWatch.slnx above '{AppContext.BaseDirectory}'.");
    }

    private static void AssertNoFailures(string description, List<string> failures) =>
        Assert.True(
            failures.Count == 0,
            failures.Count == 0
                ? null
                : $"{description}:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
}
