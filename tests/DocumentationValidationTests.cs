using System.Text.RegularExpressions;

namespace ThermalWatch.Tests;

/// <summary>Verifies documentation routing, references, and repository conventions.</summary>
public sealed class DocumentationValidationTests
{
    private const int MaximumAgentGuideLines = 150;
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly Regex MarkdownLinkPattern = new(
        pattern: """!?\[[^\]]*\]\(\s*(?<target><[^>]+>|[^\s\)]+)(?:\s+(?:"[^"]*"|'[^']*'|\([^\)]*\)))?\s*\)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(seconds: 1)
    );
    private static readonly Regex UriSchemePattern = new(
        pattern: @"^[a-z][a-z0-9+.-]*:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(seconds: 1)
    );
    private static readonly Regex AdrFileNamePattern = new(
        pattern: @"^(?<id>[0-9]{4})-.+\.md$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(seconds: 1)
    );
    private static readonly Regex FencedBlockPattern = new(
        pattern: @"^```(?:bash|sh|shell|zsh|powershell|pwsh|cmd|bat|console)(?:[ \t][^\r\n]*)?\r?\n(?<body>.*?)^```\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
            | RegexOptions.Multiline | RegexOptions.Singleline,
        TimeSpan.FromSeconds(seconds: 1)
    );
    private static readonly Regex CommandPathPattern = new(
        pattern: @"(?<![A-Za-z0-9_./\\-])(?<path>(?:\.{1,2}[/\\])?(?:[A-Za-z0-9_.-]+[/\\])*[A-Za-z0-9_.-]+\.(?:slnx|sln|csproj|fsproj|vbproj|sh|bash|zsh|ps1|cmd|bat|js|mjs|cjs|py))(?![A-Za-z0-9_.-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(seconds: 1)
    );
    private static readonly Regex PlaceholderPattern = new(
        pattern: @"\b(?:TODO|TBD|FIXME|XXX)\b|lorem\s+ipsum|coming\s+soon|replace\s+me|insert\s+.+?\s+here",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(seconds: 1)
    );

    private static readonly string[] RoutedDocumentationDirectories = ["domain", "components"];
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

    /// <summary>Checks ADR filenames carry unique four-digit identifiers.</summary>
    [Fact]
    public void AdrIdentifiersAreFourDigitsAndUnique()
    {
        string decisionsDirectory = Path.Combine([RepositoryRoot, "docs", "decisions"]);

        var adrFiles = EnumerateMarkdownFiles(decisionsDirectory)
            .Where(path => !Path.GetFileName(path).Equals(value: "README.md", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var malformed = new List<string>();
        var identifiers = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (string? file in adrFiles)
        {
            Match match = AdrFileNamePattern.Match(Path.GetFileName(file));

            if (!match.Success)
            {
                malformed.Add(Relative(file));

                continue;
            }

            string identifier = match.Groups[groupname: "id"].Value;

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

    /// <summary>Checks required ADR headings occur in the expected order.</summary>
    [Fact]
    public void AdrTemplateContainsRequiredHeadingsInOrder()
    {
        string templatePath = Path.Combine([RepositoryRoot, "docs", "decisions", "0000-template.md"]);
        Assert.True(File.Exists(templatePath), userMessage: "docs/decisions/0000-template.md is required.");

        var headings = File.ReadLines(templatePath)
            .Select(line => line.TrimEnd())
            .Where(line => line.StartsWith(value: '#'))
            .ToList();

        int nextSearchIndex = 0;
        var missingOrOutOfOrder = new List<string>();

        foreach (string requiredHeading in RequiredAdrTemplateHeadings)
        {
            int index = headings.FindIndex(nextSearchIndex, heading => heading.Equals(requiredHeading, StringComparison.Ordinal));

            if (index < 0)
            {
                missingOrOutOfOrder.Add(requiredHeading);

                continue;
            }

            nextSearchIndex = index + 1;
        }

        AssertNoFailures(description: "Missing or out-of-order ADR template headings", missingOrOutOfOrder);
    }

    /// <summary>Checks the index routes all domain and component documents.</summary>
    [Fact]
    public void DocumentationIndexCoversDomainAndComponentDocuments()
    {
        string indexPath = Path.Combine([RepositoryRoot, "docs", "README.md"]);
        Assert.True(File.Exists(indexPath), userMessage: "docs/README.md is required.");

        var indexedFiles = GetMarkdownLinkTargets(indexPath)
            .Select(target => TryResolveLocalTarget(indexPath, target, out string? path, out string? error) && error is null ? path : null)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> routedFiles = RoutedDocumentationDirectories
            .SelectMany(directory => EnumerateMarkdownFiles(Path.Combine([RepositoryRoot, "docs", directory])))
            .Where(path => !Path.GetFileName(path).Equals(value: "README.md", StringComparison.OrdinalIgnoreCase));

        var missing = routedFiles
            .Where(path => !indexedFiles.Contains(path))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToList();

        AssertNoFailures(description: "Domain or component documents missing from docs/README.md", missing);
    }

    /// <summary>Rejects unfinished markers outside deliberate documentation templates.</summary>
    [Fact]
    public void DurableDocumentationContainsNoObviousPlaceholders()
    {
        var failures = new List<string>();

        foreach (string? document in GetDocumentationFiles().Where(path => !IsDeliberateTemplate(path)))
        {
            string[] lines = File.ReadAllLines(document);

            for (int index = 0; index < lines.Length; index++)
            {
                if (PlaceholderPattern.IsMatch(lines[index]))
                    failures.Add($"{Relative(document)}:{index + 1}: {lines[index].Trim()}");
            }
        }

        AssertNoFailures(description: "Obvious unfinished documentation markers", failures);
    }

    /// <summary>Checks local Markdown references remain inside the repository and resolve.</summary>
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

    /// <summary>Checks repository file references in shell examples exist.</summary>
    [Fact]
    public void RepositoryPathsInFencedCommandsResolve()
    {
        var failures = new List<string>();

        foreach (string? document in GetDocumentationFiles().Where(path => !IsDeliberateTemplate(path)))
        {
            string content = File.ReadAllText(document);

            foreach (Match block in FencedBlockPattern.Matches(content))
            {
                foreach (Match pathMatch in CommandPathPattern.Matches(block.Groups[groupname: "body"].Value))
                {
                    string target = pathMatch.Groups[groupname: "path"].Value;

                    if (target.Contains(value: '$', StringComparison.Ordinal) || target.Contains(value: '*', StringComparison.Ordinal))
                        continue;

                    string resolvedPath = Path.GetFullPath(
                        Path.Combine(RepositoryRoot, target.Replace(oldChar: '/', Path.DirectorySeparatorChar).Replace(oldChar: '\\', Path.DirectorySeparatorChar))
                    );

                    bool isMissingRepositoryPath = !IsInsideRepository(resolvedPath)
                        || (!File.Exists(resolvedPath) && !Directory.Exists(resolvedPath));

                    if (isMissingRepositoryPath)
                        failures.Add($"{Relative(document)} -> {target}");
                }
            }
        }

        AssertNoFailures(description: "Missing repository paths referenced by fenced commands", failures);
    }

    /// <summary>Keeps the root agent guide within its documented size limit.</summary>
    [Fact]
    public void RootAgentGuideRemainsConcise()
    {
        string agentGuidePath = Path.Combine([RepositoryRoot, "AGENTS.md"]);
        Assert.True(File.Exists(agentGuidePath), userMessage: "AGENTS.md is required.");

        int lineCount = File.ReadLines(agentGuidePath).Count();
        Assert.True(lineCount <= MaximumAgentGuideLines, $"AGENTS.md has {lineCount} lines; the limit is {MaximumAgentGuideLines}.");
    }

    private static void AssertNoFailures(string description, List<string> failures)
    {
        Assert.True(
            failures.Count == 0,
            failures.Count == 0
                ? null
                : $"{description}:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}"
        );
    }

    private static IEnumerable<string> EnumerateMarkdownFiles(string directory)
    {
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, searchPattern: "*.md", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
            : [];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine([directory.FullName, "ThermalWatch.slnx"])))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not locate ThermalWatch.slnx above '{AppContext.BaseDirectory}'.");
    }

    private static IEnumerable<string> GetDocumentationFiles()
    {
        foreach (string? fileName in new[] { "README.md", "AGENTS.md" })
        {
            string path = Path.Combine(RepositoryRoot, fileName);

            if (File.Exists(path))
                yield return path;
        }

        foreach (string? directory in new[] { "docs", ".agent", ".agents" })
        {
            foreach (string path in EnumerateMarkdownFiles(Path.Combine(RepositoryRoot, directory)))
                yield return path;
        }
    }

    private static IEnumerable<string> GetMarkdownLinkTargets(string document)
    {
        return MarkdownLinkPattern.Matches(File.ReadAllText(document))
            .Cast<Match>()
            .Select(match => match.Groups[groupname: "target"].Value.Trim(trimChars: ['<', '>']));
    }

    private static bool IsDeliberateTemplate(string path)
    {
        string relative = Relative(path).Replace(Path.DirectorySeparatorChar, newChar: '/');

        return relative.Equals(value: "docs/decisions/0000-template.md", StringComparison.OrdinalIgnoreCase)
            || relative.Equals(value: ".agent/PLANS.md", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInsideRepository(string path)
    {
        string relative = Path.GetRelativePath(RepositoryRoot, path);

        return !relative.Equals(value: "..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string Relative(string path)
    {
        return Path.GetRelativePath(RepositoryRoot, path);
    }

    private static bool TryResolveLocalTarget(string document, string target, out string resolvedPath, out string? error)
    {
        resolvedPath = string.Empty;
        error = null;

        bool isExternalOrEmptyTarget = string.IsNullOrWhiteSpace(target)
            || target.StartsWith(value: '#')
            || target.StartsWith(value: '/')
            || target.StartsWith(value: "//", StringComparison.Ordinal)
            || UriSchemePattern.IsMatch(target);

        if (isExternalOrEmptyTarget)
            return false;

        string path = target.Split(separator: '#', count: 2)[0].Split(separator: '?', count: 2)[0];

        if (path.Length == 0)
            return false;

        try
        {
            path = Uri.UnescapeDataString(path).Replace(oldChar: '/', Path.DirectorySeparatorChar);
            string? documentDirectory = Path.GetDirectoryName(document);

            if (documentDirectory is null)
            {
                error = "document has no directory";

                return true;
            }

            resolvedPath = Path.GetFullPath(Path.Combine(documentDirectory, path));
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
}
