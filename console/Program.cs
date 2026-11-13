// ProxyNodeHub.Console: 一次性发现入口，供 GitHub Actions 定时运行。
// 复用 core/ 的公开 API，流程照抄 web/DiscoveryWorker.cs 的 DiscoverAsync /
// AnalyzeRepositoriesAsync；不启动 Web 服务，不需要管理员密码，不需要 subs-check。
// 产出写到 OUT_DIR（subscriptions.txt / nodes.txt / nodes.b64 / clash.yaml），
// 精简状态写到 DATA_DIR/state-slim.json，特征库写到 DATA_DIR/features.json。
// 退出码：0 = 本轮产出有效订阅；1 = 无有效订阅（不覆盖上一轮输出）；2 = 启动检查失败。
using System.Text.Json;
using ProxyNodeHub;

// 本地转换模式：dotnet ProxyNodeHub.Console.dll convert <节点文件> <输出 clash.yaml>
// 不跑发现流程，只把节点 URI 行转成 Clash Meta (mihomo) 可用的 clash.yaml。
if (args.Length == 3 && args[0] == "convert")
{
    var inputLines = await File.ReadAllLinesAsync(args[1]);
    var (yaml, converted, skipped, _) = ClashYaml.ConvertLines(inputLines);
    if (converted == 0)
    {
        Console.Error.WriteLine("没有可转换的节点。");
        return 1;
    }
    await File.WriteAllTextAsync(args[2], yaml);
    Console.WriteLine($"已转换 {converted} 个节点，跳过 {skipped} 个，写入 {args[2]}。");
    return 0;
}

var dataDir = Env("DATA_DIR", "data");
var outDir = Env("OUT_DIR", "out");
var repoCount = EnvInt("REPO_COUNT", 10, 1, 100);
var inactiveDays = EnvInt("INACTIVE_DAYS", 7, 1, 60);
var concurrency = EnvInt("ANALYSIS_CONCURRENCY", 4, 1, 10);
var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");

Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(outDir);

var logGate = new object();
void Log(string message)
{
    lock (logGate)
        Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] {message}");
}

// known_repos.json 缺失会让整轮崩溃（KnownRepoLoader 用 File.ReadAllText 且不捕获
// FileNotFoundException），先断言，缺了就明确报错而不是跑一半死掉。
var knownReposPath = Path.Combine(AppContext.BaseDirectory, "known_repos.json");
if (!File.Exists(knownReposPath))
{
    Console.Error.WriteLine($"缺少 known_repos.json：{knownReposPath}。请确认 core 的 Content 项已随发布复制。");
    return 2;
}

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(45));
var ct = cts.Token;

try
{
    var features = new FeatureLibrary(dataDir);
    using var github = new GitHubService(token, restrictPublicSources: true);
    // 直连；Actions runner 在 GitHub 云上，直连 raw.githubusercontent.com 正常，不需要加速镜像。
    await github.ConfigurePublicMirrorAsync("", ct);

    // --- 搜索阶段（照抄 DiscoveryWorker.DiscoverAsync） ---
    var found = new Dictionary<string, GitHubRepo>(StringComparer.OrdinalIgnoreCase);
    var failedSearches = 0;
    foreach (var query in DiscoveryEngine.Queries(inactiveDays))
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            foreach (var repo in await github.SearchReposAsync(query, 30, ct))
                if (!repo.Fork && !GitHubAnalyzer.IsStale(repo))
                    found.TryAdd(repo.FullName, repo);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
        {
            failedSearches++;
            Log($"搜索失败：{ex.GetType().Name}");
        }
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
    }
    Log($"搜索完成：{found.Count} 个候选仓库（失败 {failedSearches} 条查询）。");
    if (found.Count == 0)
    {
        Log("没有发现仓库，保留上次结果。");
        return 1;
    }

    var candidates = found.Values.Take(repoCount).Select(GitHubAnalyzer.ToRepoInfo).ToArray();

    // --- 分析阶段（照抄 AnalyzeRepositoriesAsync） ---
    using var limit = new SemaphoreSlim(concurrency, concurrency);
    var failedRepos = 0;
    var results = await Task.WhenAll(candidates.Select(async repo =>
    {
        await limit.WaitAsync(ct);
        try
        {
            var outcome = await DiscoveryEngine.AnalyzeAsync(github, repo, ct, Log, features);
            if (!outcome.CommitsSucceeded || !outcome.SubscriptionsSucceeded)
                Interlocked.Increment(ref failedRepos);
            repo.Links = repo.Links
                .Where(l => l.IsAnalyzed && l.IsValid && l.NodeCount > 0 && GitHubService.IsAllowedSubscription(l.Url))
                .ToList();
            repo.TotalNodes = GitHubAnalyzer.BestNodeCount(repo);
            return repo.Links.Count > 0 ? repo : null;
        }
        finally
        {
            limit.Release();
        }
    }));
    var repos = results.OfType<RepoInfo>().OrderByDescending(r => r.Score).ToArray();
    var urls = repos.SelectMany(r => r.Links).Select(l => l.Url).Distinct(StringComparer.Ordinal).ToArray();
    Log($"分析完成：{repos.Length} 个有效仓库，{urls.Length} 条订阅。");
    if (urls.Length == 0)
    {
        Log("本轮没有验证通过的订阅，保留上次有效结果。");
        return 1;
    }

    // --- 合并阶段：逐条下载；Clash YAML 来源只进清单、不进合并订阅 ---
    var contents = new List<string>();
    var yamlSkipped = 0;
    var downloadFailed = 0;
    foreach (var url in urls)
    {
        string? content;
        try
        {
            content = await github.GetUrlAsync(url, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException)
        {
            downloadFailed++;
            Log($"下载失败：{url}（{ex.GetType().Name}）");
            continue;
        }
        if (content is null)
        {
            downloadFailed++;
            Log($"下载为空：{url}");
            continue;
        }
        if (NodeParser.SplitNodeLines(content).Count > 0)
        {
            contents.Add(content);
        }
        else if (NodeParser.CountNodes(content) > 0)
        {
            yamlSkipped++;
            Log($"跳过 Clash YAML 来源（只进清单，不进合并订阅）：{url}");
        }
        else
        {
            Log($"内容无节点：{url}");
        }
    }
    var (plain, b64, total, unique) = NodeParser.MergeNodes(contents);
    if (unique == 0)
    {
        Log("合并后没有可用节点，保留上次有效结果。");
        return 1;
    }

    // 只有成功才写输出，失败不覆盖上一轮。
    await File.WriteAllTextAsync(Path.Combine(outDir, "subscriptions.txt"), string.Join("\n", urls) + "\n", ct);
    await File.WriteAllTextAsync(Path.Combine(outDir, "nodes.txt"), plain + "\n", ct);
    await File.WriteAllTextAsync(Path.Combine(outDir, "nodes.b64"), b64 + "\n", ct);

    // Clash 订阅：把合并后的节点转成 Clash Meta (mihomo) 可用的 clash.yaml。
    // 只有转换出有效节点才写，失败不覆盖上一轮。
    var (clashYaml, clashCount, clashSkipped, clashMapping) = ClashYaml.ConvertLines(NodeParser.SplitNodeLines(plain));
    if (clashCount > 0)
    {
        await File.WriteAllTextAsync(Path.Combine(outDir, "clash.yaml"), clashYaml, ct);
        // 代理名→原始节点链接，供测速脚本按名字找回原始链接。
        var namesJson = JsonSerializer.Serialize(clashMapping.ToDictionary(m => m.name, m => m.uri));
        await File.WriteAllTextAsync(Path.Combine(outDir, "node-names.json"), namesJson + "\n", ct);
        Log($"Clash: {clashCount} 个节点已写入 clash.yaml（跳过 {clashSkipped} 个不支持的节点）。");
    }
    else
    {
        Log("Clash: 没有可转换的节点，未写 clash.yaml。");
    }

    var state = new
    {
        generated_at = DateTimeOffset.UtcNow,
        repositories = repos.Select(r => new
        {
            full_name = r.FullName,
            score = r.Score,
            total_nodes = r.TotalNodes,
            links = r.Links.Select(l => new { name = l.Name, url = l.Url, type = l.Type, node_count = l.NodeCount })
        }),
        subscriptions = urls,
        failed_searches = failedSearches,
        failed_repositories = failedRepos,
        failed_downloads = github.FailedDownloads + downloadFailed,
        yaml_skipped = yamlSkipped,
        total_nodes = total,
        unique_nodes = unique,
        clash_nodes = clashCount,
        clash_skipped = clashSkipped
    };
    await File.WriteAllTextAsync(Path.Combine(dataDir, "state-slim.json"),
        JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }), ct);

    Log($"完成：{repos.Length} 个仓库，{urls.Length} 条订阅，{unique} 个去重节点（共 {total}）。" +
        $"搜索失败 {failedSearches}，仓库失败 {failedRepos}，下载失败 {downloadFailed}，跳过 Clash YAML {yamlSkipped}，" +
        $"Clash 转换 {clashCount} 个节点（跳过 {clashSkipped} 个）。");
    return 0;
}
catch (OperationCanceledException)
{
    Log("运行超时或被取消，保留上次结果。");
    return 1;
}
catch (Exception ex)
{
    Log($"更新失败：{ex.GetType().Name}：{ex.Message}，保留上次结果。");
    return 1;
}

static string Env(string name, string fallback)
{
    var value = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

static int EnvInt(string name, int fallback, int min, int max)
{
    if (int.TryParse(Environment.GetEnvironmentVariable(name), out var value))
        return Math.Clamp(value, min, max);
    return fallback;
}
