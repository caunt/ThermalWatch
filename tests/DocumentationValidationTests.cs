using System.Text.RegularExpressions;

namespace ThermalWatch.Tests;

public sealed class DocumentationValidationTests
{
    private const int MaximumAgentGuideLines = 150;
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly Regex MarkdownLinkPattern = new(
        """!?\[[^\]]*\]\(\s*(?<target><[^>]+>|[^\s\)]+)(?:\s+(?:"[^"]*"|'[^']*'|\([^\)]*\)))?\s*\)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UriSchemePattern = new(
        @"^[a-z][a-z0-9+.-]*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex AdrFileNamePattern = new(
        @"^(?<id>[0-9]{4})-.+\.md$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex FencedBlockPattern = new(
        @"^```(?:bash|sh|shell|zsh|powershell|pwsh|cmd|bat|console)(?:[ \t][^\r\n]*)?\r?\n(?<body>.*?)^```\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
            | RegexOptions.Multiline | RegexOptions.Singleline);
    private static readonly Regex CommandPathPattern = new(
        @"(?<![A-Za-z0-9_./\\-])(?<path>(?:\.{1,2}[/\\])?(?:[A-Za-z0-9_.-]+[/\\])*[A-Za-z0-9_.-]+\.(?:slnx|sln|csproj|fsproj|vbproj|sh|bash|zsh|ps1|cmd|bat|js|mjs|cjs|py))(?![A-Za-z0-9_.-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex PlaceholderPattern = new(
        @"\b(?:TODO|TBD|FIXME|XXX)\b|lorem\s+ipsum|coming\s+soon|replace\s+me|insert\s+.+?\s+here",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly string[] RequiredAdrTemplateHeadings =
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

        foreach (var document in GetDocumentationFiles())
        {
            foreach (var target in GetMarkdownLinkTargets(document))
            {
                if (!TryResolveLocalTarget(document, target, out var resolvedPath, out var error))
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

        AssertNoFailures("Unresolved local Markdown links", failures);
    }

    [Fact]
    public void DocumentationIndexCoversDomainAndComponentDocuments()
    {
        var indexPath = Path.Combine(RepositoryRoot, "docs", "README.md");
        Assert.True(File.Exists(indexPath), "docs/README.md is required.");

        var indexedFiles = GetMarkdownLinkTargets(indexPath)
            .Select(target => TryResolveLocalTarget(indexPath, target, out var path, out var error)
                && error is null
                    ? path
                    : null)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var routedFiles = new[] { "domain", "components" }
            .SelectMany(directory => EnumerateMarkdownFiles(Path.Combine(RepositoryRoot, "docs", directory)))
            .Where(path => !Path.GetFileName(path).Equals("README.md", StringComparison.OrdinalIgnoreCase));
        var missing = routedFiles
            .Where(path => !indexedFiles.Contains(path))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToList();

        AssertNoFailures("Domain or component documents missing from docs/README.md", missing);
    }

    [Fact]
    public void AdrIdentifiersAreFourDigitsAndUnique()
    {
        var decisionsDirectory = Path.Combine(RepositoryRoot, "docs", "decisions");
        var adrFiles = EnumerateMarkdownFiles(decisionsDirectory)
            .Where(path => !Path.GetFileName(path).Equals("README.md", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var malformed = new List<string>();
        var identifiers = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var file in adrFiles)
        {
            var match = AdrFileNamePattern.Match(Path.GetFileName(file));
            if (!match.Success)
            {
                malformed.Add(Relative(file));
                continue;
            }

            var identifier = match.Groups["id"].Value;
            if (!identifiers.TryGetValue(identifier, out var files))
                identifiers.Add(identifier, files = []);

            files.Add(Relative(file));
        }

        AssertNoFailures("ADR filenames without a four-digit identifier", malformed);

        var duplicates = identifiers
            .Where(pair => pair.Value.Count > 1)
            .Select(pair => $"{pair.Key}: {string.Join(", ", pair.Value.Order(StringComparer.Ordinal))}")
            .Order(StringComparer.Ordinal)
            .ToList();
        AssertNoFailures("Duplicate ADR identifiers", duplicates);
    }

    [Fact]
    public void AdrTemplateContainsRequiredHeadingsInOrder()
    {
        var templatePath = Path.Combine(RepositoryRoot, "docs", "decisions", "0000-template.md");
        Assert.True(File.Exists(templatePath), "docs/decisions/0000-template.md is required.");

        var headings = File.ReadLines(templatePath)
            .Select(line => line.TrimEnd())
            .Where(line => line.StartsWith('#'))
            .ToList();
        var nextSearchIndex = 0;
        var missingOrOutOfOrder = new List<string>();

        foreach (var requiredHeading in RequiredAdrTemplateHeadings)
        {
            var index = headings.FindIndex(
                nextSearchIndex,
                heading => heading.Equals(requiredHeading, StringComparison.Ordinal));
            if (index < 0)
            {
                missingOrOutOfOrder.Add(requiredHeading);
                continue;
            }

            nextSearchIndex = index + 1;
        }

        AssertNoFailures("Missing or out-of-order ADR template headings", missingOrOutOfOrder);
    }

    [Fact]
    public void RootAgentGuideRemainsConcise()
    {
        var agentGuidePath = Path.Combine(RepositoryRoot, "AGENTS.md");
        Assert.True(File.Exists(agentGuidePath), "AGENTS.md is required.");

        var lineCount = File.ReadLines(agentGuidePath).Count();
        Assert.True(
            lineCount <= MaximumAgentGuideLines,
            $"AGENTS.md has {lineCount} lines; the limit is {MaximumAgentGuideLines}.");
    }

    [Fact]
    public void DurableDocumentationContainsNoObviousPlaceholders()
    {
        var failures = new List<string>();

        foreach (var document in GetDocumentationFiles().Where(path => !IsDeliberateTemplate(path)))
        {
            var lines = File.ReadAllLines(document);
            for (var index = 0; index < lines.Length; index++)
            {
                if (PlaceholderPattern.IsMatch(lines[index]))
                    failures.Add($"{Relative(document)}:{index + 1}: {lines[index].Trim()}");
            }
        }

        AssertNoFailures("Obvious unfinished documentation markers", failures);
    }

    [Fact]
    public void RepositoryPathsInFencedCommandsResolve()
    {
        var failures = new List<string>();

        foreach (var document in GetDocumentationFiles().Where(path => !IsDeliberateTemplate(path)))
        {
            var content = File.ReadAllText(document);
            foreach (Match block in FencedBlockPattern.Matches(content))
            {
                foreach (Match pathMatch in CommandPathPattern.Matches(block.Groups["body"].Value))
                {
                    var target = pathMatch.Groups["path"].Value;
                    if (target.Contains('$') || target.Contains('*'))
                        continue;

                    var resolvedPath = Path.GetFullPath(Path.Combine(
                        RepositoryRoot,
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

        AssertNoFailures("Missing repository paths referenced by fenced commands", failures);
    }

    private static IEnumerable<string> GetDocumentationFiles()
    {
        foreach (var fileName in new[] { "README.md", "AGENTS.md" })
        {
            var path = Path.Combine(RepositoryRoot, fileName);
            if (File.Exists(path))
                yield return path;
        }

        foreach (var directory in new[] { "docs", ".agent", ".agents" })
        {
            foreach (var path in EnumerateMarkdownFiles(Path.Combine(RepositoryRoot, directory)))
                yield return path;
        }
    }

    private static IEnumerable<string> EnumerateMarkdownFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.md", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
            : [];

    private static IEnumerable<string> GetMarkdownLinkTargets(string document) =>
        MarkdownLinkPattern.Matches(File.ReadAllText(document))
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
            || target.StartsWith("//", StringComparison.Ordinal)
            || UriSchemePattern.IsMatch(target))
        {
            return false;
        }

        var path = target.Split('#', 2)[0].Split('?', 2)[0];
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
        var relative = Path.GetRelativePath(RepositoryRoot, path);
        return !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool IsDeliberateTemplate(string path)
    {
        var relative = Relative(path).Replace(Path.DirectorySeparatorChar, '/');
        return relative.Equals("docs/decisions/0000-template.md", StringComparison.OrdinalIgnoreCase)
            || relative.Equals(".agent/PLANS.md", StringComparison.OrdinalIgnoreCase);
    }

    private static string Relative(string path) => Path.GetRelativePath(RepositoryRoot, path);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ThermalWatch.slnx")))
                return directory.FullName;
        }

        throw new InvalidOperationException(
            $"Could not locate ThermalWatch.slnx above '{AppContext.BaseDirectory}'.");
    }

    private static void AssertNoFailures(string description, IReadOnlyCollection<string> failures) =>
        Assert.True(
            failures.Count == 0,
            failures.Count == 0
                ? null
                : $"{description}:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
}
