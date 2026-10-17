using Hosihikari;
using Hosihikari.PluginManagement;
using Hosihikari.PluginProvider.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// 载体中立检查（ADR-0015）：普通 .NET 控制台驱动核心，不碰 BDS、Shim、任何载体。
//
// 宿主不直接注册 C# Provider —— 它把 Provider 当成一个插件包装进去，由那个包在 OnLoad
// 里把自己登记给框架（ADR-0002）。第二步才是 C# 的本体：装载时用 Roslyn 编译，进 collectible ALC。
var stage = Path.Combine(Path.GetTempPath(), "hosihikari-csharp-spike");
if (Directory.Exists(stage))
{
    Directory.Delete(stage, true);
}

var pluginsRoot = Path.Combine(stage, "plugins");
var dataRoot = Path.Combine(stage, "data");
var configRoot = Path.Combine(stage, "config", "hosihikari");
Directory.CreateDirectory(configRoot);

StageProviderPackage();
StageScriptPackage();

// 运营方的覆盖放包外（ADR-0018）—— 脚本读到的应当是这一份。
File.WriteAllText(Path.Combine(configRoot, "Hello.conf"), "greeting = \"overridden\"\n");

var collection = new ServiceCollection();
collection.AddLogging(builder => builder
    .AddSimpleConsole(options => options.SingleLine = true)
    .SetMinimumLevel(LogLevel.Information));
var services = collection.BuildServiceProvider();

var reported = new List<string>();
var host = new PluginManagerHost(
    pluginsRoot,
    dataRoot,
    configRoot,
    entry =>
    {
        reported.Add(entry);
        Console.WriteLine($"[框架] {entry}");
    },
    services);
host.AddProvider(new AssemblyPluginProvider());

Console.WriteLine("=== 加载（含一次 C# 编译） ===");
host.LoadAll();
Console.WriteLine($"已加载：{string.Join(", ", host.LoadedNames)}");

// 程序集这一侧发一条通道消息 —— 与 C# 脚本用的是同一个载荷类型（ADR-0026）。
host.Events.Publish(new ChannelMessage("ping", "\"from-host\""));
Thread.Sleep(200);

var dataDirectory = Path.Combine(dataRoot, "Hello");
var checks = new List<(string Name, bool Passed, string Detail)>();

checks.Add((
    "Provider 由插件包登记，随后的 C# 包被它接管",
    host.LoadedNames.Contains("hosihikari-csharp") && host.LoadedNames.Contains("Hello"),
    string.Join(", ", host.LoadedNames)));

// A26：内容寻址 —— 产物落在 bin/<脚本+编译器+运行时 的哈希>/ 下，校验和与 PDB 在旁边。
var artifact = Artifact();
var artifactPdb = artifact is null ? null : Path.ChangeExtension(artifact, ".pdb");
checks.Add((
    "产物按内容寻址落盘，带校验和与 PDB",
    artifact is not null && File.Exists(artifact + ".xxh128") && artifactPdb is not null && File.Exists(artifactPdb),
    artifact ?? "（没找到产物）"));

checks.Add(("脚本装载时执行，且读到包外覆盖的配置", Read("body.txt") == "body:overridden", Read("body.txt")));
checks.Add(("启用钩子到达脚本", Read("enabled.txt") == "enabled", Read("enabled.txt")));
checks.Add(("程序集发的通道消息被 C# 插件收到", Read("ping.txt") == "ping:\"from-host\"", Read("ping.txt")));

var unloaded = host.Unload("Hello");
checks.Add((
    "卸载时停用与卸载钩子都到达",
    Read("disabled.txt") == "disabled" && Read("unloaded.txt") == "unloaded",
    $"disabled={Read("disabled.txt")} unloaded={Read("unloaded.txt")}"));
checks.Add(("卸载验证通过（ALC 被真正回收）", unloaded.Released, unloaded.Error ?? "已回收"));

// 同一个脚本再装载一次 —— 命中缓存，产物不该被重写。
var before = artifact is null ? default : File.GetLastWriteTimeUtc(artifact);
var reloaded = host.Load(Path.Combine(pluginsRoot, "Hello"));
Thread.Sleep(50);
var after = artifact is null ? default : File.GetLastWriteTimeUtc(artifact);
checks.Add(("同一个脚本再装载命中缓存", reloaded && before == after, $"{before:O} → {after:O}"));
host.Unload("Hello");

// 把产物改坏 —— 校验和对不上就该丢弃重编，而不是照跑。
if (artifact is not null)
{
    File.AppendAllText(artifact, "// tampered");
}

var recovered = host.Load(Path.Combine(pluginsRoot, "Hello"));
var repaired = artifact is not null && IsChecksumValid(artifact);
checks.Add(("产物被改坏后重编并通过校验", recovered && repaired, repaired ? "已重编" : "校验仍不符"));

Console.WriteLine();
Console.WriteLine("=== 断言 ===");
foreach (var (name, passed, detail) in checks)
{
    Console.WriteLine($"{(passed ? "PASS" : "FAIL")}  {name}  （{detail}）");
}

return checks.All(check => check.Passed) ? 0 : 1;

string? Artifact()
{
    var bin = Path.Combine(dataDirectory, "bin");
    return !Directory.Exists(bin)
        ? null
        : Directory.EnumerateDirectories(bin).Select(directory => Path.Combine(directory, "Hello.dll")).FirstOrDefault(File.Exists);
}

static bool IsChecksumValid(string path)
{
    var checksumPath = path + ".xxh128";
    if (!File.Exists(path) || !File.Exists(checksumPath))
    {
        return false;
    }

    var expected = File.ReadAllText(checksumPath).Trim();
    var actual = Convert.ToHexStringLower(System.IO.Hashing.XxHash128.Hash(File.ReadAllBytes(path)));
    return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
}

string Read(string file)
{
    var path = Path.Combine(dataDirectory, file);
    return File.Exists(path) ? File.ReadAllText(path) : "（没写出来）";
}

// Provider 包：普通程序集型插件，入口在 OnLoad 里登记 C# Provider。
void StageProviderPackage()
{
    var package = Path.Combine(pluginsRoot, "hosihikari-csharp");
    Directory.CreateDirectory(package);

    File.WriteAllText(Path.Combine(package, "manifest.json"), """
        {
          "name": "hosihikari-csharp",
          "entry": "Hosihikari.PluginProvider.CSharp.dll",
          "type": "hosihikari",
          "version": "0.1.0",
          "dependencies": [ { "name": "hosihikari" } ],
          "extraInfo": { "api": "1", "format_version": "1", "provider": "assembly" }
        }
        """);

    // 整个构建输出随包走，只把共享层剔掉（ADR-0004）。
    var output = Path.Combine(RepoRoot(), "src", "bin", ConfigurationName(), "net10.0");
    foreach (var file in Directory.EnumerateFiles(output, "*.dll"))
    {
        if (Path.GetFileName(file) is "Hosihikari.Abstractions.dll")
        {
            continue;
        }
        File.Copy(file, Path.Combine(package, Path.GetFileName(file)));
    }

    var deps = Path.Combine(output, "Hosihikari.PluginProvider.CSharp.deps.json");
    if (File.Exists(deps))
    {
        File.Copy(deps, Path.Combine(package, Path.GetFileName(deps)));
    }
}

// C# 包：一个 .cs，里面定义实现 IPlugin 的类型 —— 编译与入口发现都是 Provider 的事。
void StageScriptPackage()
{
    var package = Path.Combine(pluginsRoot, "Hello");
    Directory.CreateDirectory(package);

    File.WriteAllText(Path.Combine(package, "manifest.json"), """
        {
          "name": "Hello",
          "entry": "main.cs",
          "type": "hosihikari",
          "version": "1.0.0",
          "dependencies": [
            { "name": "hosihikari" },
            { "name": "hosihikari-csharp" }
          ],
          "extraInfo": { "api": "1", "format_version": "1", "provider": "csharp" }
        }
        """);

    // 作者给的默认值，随包更新（ADR-0018）。
    File.WriteAllText(Path.Combine(package, "Hello.conf"), "greeting = \"package\"\n");

    File.WriteAllText(Path.Combine(package, "main.cs"), """
        using System.IO;
        using Hosihikari;
        using Microsoft.Extensions.Logging;

        public sealed class HelloPlugin : IPlugin
        {
            private string _data = "";

            public void OnLoad(IPluginContext context)
            {
                _data = context.DataDirectory;
                context.Logger.LogInformation("csharp plugin loaded in {Name}", context.Name);
                File.WriteAllText(Path.Combine(_data, "body.txt"), "body:" + context.Configuration["greeting"]);
                context.Events.Subscribe<ChannelMessage>(message =>
                {
                    if (message.Topic == "ping")
                    {
                        File.WriteAllText(Path.Combine(_data, "ping.txt"), "ping:" + message.Json);
                    }
                });
            }

            public void OnEnable() => File.WriteAllText(Path.Combine(_data, "enabled.txt"), "enabled");
            public void OnDisable() => File.WriteAllText(Path.Combine(_data, "disabled.txt"), "disabled");
            public void OnUnload() => File.WriteAllText(Path.Combine(_data, "unloaded.txt"), "unloaded");
        }
        """);
}

static string RepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null &&
           !File.Exists(Path.Combine(directory.FullName, "src", "Hosihikari.PluginProvider.CSharp.csproj")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? throw new InvalidOperationException("找不到仓库根");
}

static string ConfigurationName()
{
#if DEBUG
    return "Debug";
#else
    return "Release";
#endif
}
