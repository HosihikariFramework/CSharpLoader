using System.IO.Hashing;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

using Hosihikari;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.Logging;

namespace Hosihikari.PluginProvider.CSharp;

/// <summary>
/// C# 型插件（ADR-0002 的 Provider 扩展点）。
///
/// 与 F# 那条同形：C# 也没有自己的虚拟机，所以这里**装载时编译** —— Roslyn 把 <c>.cs</c> 编成
/// 程序集，再交给一个 collectible ALC，拿到与其他程序集插件一样的隔离与卸载承诺。
///
/// 产物按**内容寻址**缓存（ADR-0029）：目录名是脚本内容、Roslyn 版本与运行时版本的哈希，
/// 所以脚本一改就换目录；产物旁另存一份校验和，加载前对一遍。
///
/// 入口靠约定：编出来的程序集里那个实现 <see cref="IPlugin"/>、有无参构造的类型（ADR-0008）。
/// </summary>
public sealed class CSharpPluginProvider : IPluginProvider
{
    /// <summary>
    /// 框架自己的装载上下文。组件托管给核心的是一个**隔离**的 ALC，契约与它的抽象包都在里面 ——
    /// 不在默认上下文里，所以"返回 null 让父级兜底"在这里兜不到。
    /// </summary>
    private static readonly AssemblyLoadContext? FrameworkContext =
        AssemblyLoadContext.GetLoadContext(typeof(IPlugin).Assembly);

    /// <summary>产物校验和的落地名：内容寻址管"该不该编"，它管"编出来的还是不是那个"。</summary>
    private const string ChecksumSuffix = ".xxh128";

    public string Type => "csharp";

    public bool CanLoad(PluginManifest manifest) => manifest.Provider == Type;

    public PluginInstance? Load(PluginManifest manifest, string packageDirectory, IPluginContext context)
    {
        var entry = Path.Combine(packageDirectory, manifest.Entry);
        if (!File.Exists(entry))
        {
            context.Logger.LogError("清单 entry 指向的 {Entry} 不存在", manifest.Entry);
            return null;
        }

        // 内容寻址：目录名就把"脚本内容 + 编译器 + 运行时"钉死了。脚本改了就是另一个目录。
        var script = File.ReadAllText(entry);
        var cacheDirectory = Path.Combine(context.DataDirectory, "bin", CacheKey(script));
        var assemblyPath = Path.Combine(cacheDirectory, manifest.Name + ".dll");

        if (!IsUsable(assemblyPath))
        {
            // 没有、或校验和对不上 —— 都丢弃重编，绝不"验不过也照跑"。
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, recursive: true);
            }

            if (!Compile(entry, cacheDirectory, manifest.Name, context))
            {
                return null;
            }
        }

        var loadContext = new ScriptLoadContext(assemblyPath, cacheDirectory);
        var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

        var entryType = assembly.GetTypes().FirstOrDefault(candidate =>
            typeof(IPlugin).IsAssignableFrom(candidate)
            && candidate is { IsAbstract: false, IsInterface: false }
            && candidate.GetConstructor(System.Type.EmptyTypes) is not null);
        if (entryType is null)
        {
            context.Logger.LogError("{Assembly} 里没有实现 IPlugin 且有无参构造的类型", assemblyPath);
            loadContext.Unload();
            return null;
        }

        if (Activator.CreateInstance(entryType) is not IPlugin plugin)
        {
            context.Logger.LogError("造不出 {Type} 的实例", entryType.FullName);
            loadContext.Unload();
            return null;
        }

        return new CompiledPluginInstance(plugin, loadContext);
    }

    /// <summary>脚本内容 + Roslyn 版本 + 运行时版本，xxhash128 后取十六进制。</summary>
    private static string CacheKey(string script)
    {
        var hasher = new XxHash128();
        hasher.Append(Encoding.UTF8.GetBytes(script));
        hasher.Append(Encoding.UTF8.GetBytes(typeof(CSharpCompilation).Assembly.GetName().Version?.ToString() ?? "?"));
        hasher.Append(Encoding.UTF8.GetBytes(Environment.Version.ToString()));
        return Convert.ToHexStringLower(hasher.GetCurrentHash());
    }

    /// <summary>产物在、且校验和对得上才算能用。</summary>
    private static bool IsUsable(string assemblyPath)
    {
        var checksumPath = assemblyPath + ChecksumSuffix;
        if (!File.Exists(assemblyPath) || !File.Exists(checksumPath))
        {
            return false;
        }

        var expected = File.ReadAllText(checksumPath).Trim();
        var actual = Convert.ToHexStringLower(XxHash128.Hash(File.ReadAllBytes(assemblyPath)));
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    private bool Compile(string scriptPath, string cacheDirectory, string name, IPluginContext context)
    {
        // 编到临时目录，成功了再原子改名 —— 崩在中间不会留下半个产物。
        var staging = cacheDirectory + ".tmp";

        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
            Directory.CreateDirectory(staging);

            var assemblyPath = Path.Combine(staging, name + ".dll");
            if (!CompileTo(scriptPath, assemblyPath, context))
            {
                return false;
            }

            var checksum = Convert.ToHexStringLower(XxHash128.Hash(File.ReadAllBytes(assemblyPath)));
            File.WriteAllText(assemblyPath + ChecksumSuffix, checksum);

            Directory.CreateDirectory(Path.GetDirectoryName(cacheDirectory)!);
            Directory.Move(staging, cacheDirectory);
            PruneSiblings(cacheDirectory);
            return true;
        }
        catch (Exception ex)
        {
            // 编译期出错只让这一个插件倒下（ADR-0009）。
            context.Logger.LogError(ex, "编译 {Script} 失败", scriptPath);
            return false;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (IOException)
                {
                    // 清理失败不影响结果。
                }
            }
        }
    }

    private static bool CompileTo(string scriptPath, string assemblyPath, IPluginContext context)
    {
        // 第四个参数是编码：不给它，Roslyn 拒绝为这段源码发 PDB（CS8055）。
        var tree = CSharpSyntaxTree.ParseText(
            File.ReadAllText(scriptPath),
            new CSharpParseOptions(LanguageVersion.Latest),
            scriptPath,
            Encoding.UTF8);

        // 引用 = 宿主的运行时 + 框架自己 ALC 里的那些（契约与它的抽象包都在里面）。
        var references = CollectReferences().Select(path => MetadataReference.CreateFromFile(path));

        var compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(assemblyPath),
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

        // 没有 PDB 就没有 file:line，而日志是唯一的诊断通道。
        using var assemblyStream = File.Create(assemblyPath);
        using var pdbStream = File.Create(Path.ChangeExtension(assemblyPath, ".pdb"));
        var result = compilation.Emit(
            assemblyStream,
            pdbStream,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));

        if (result.Success)
        {
            return true;
        }

        foreach (var diagnostic in result.Diagnostics.Where(entry => entry.Severity == DiagnosticSeverity.Error))
        {
            context.Logger.LogError("{File}: {Message}", scriptPath, diagnostic.ToString());
        }

        return false;
    }

    /// <summary>宿主的运行时 + 框架自己的 ALC（契约与它的抽象包，以及框架引用的那些）。</summary>
    private static IEnumerable<string> CollectReferences()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var trusted = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        foreach (var path in (trusted ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (seen.Add(Path.GetFileName(path)))
            {
                yield return path;
            }
        }

        foreach (var assembly in FrameworkContext?.Assemblies ?? [])
        {
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
            {
                continue;
            }

            if (seen.Add(Path.GetFileName(assembly.Location)))
            {
                yield return assembly.Location;
            }
        }
    }

    /// <summary>同一个插件只留最新那一份产物；旧的内容寻址目录攒着没有意义。</summary>
    private static void PruneSiblings(string cacheDirectory)
    {
        var root = Path.GetDirectoryName(cacheDirectory);
        if (root is null || !Directory.Exists(root))
        {
            return;
        }

        foreach (var sibling in Directory.EnumerateDirectories(root))
        {
            if (string.Equals(sibling, cacheDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.Delete(sibling, recursive: true);
            }
            catch (IOException)
            {
                // 清不掉就留着 —— 它命中不了新的内容寻址，不会再被加载。
            }
        }
    }

    /// <summary>每个插件一个 collectible ALC：自己的依赖从自己的目录解析，契约走框架的 ALC。</summary>
    private sealed class ScriptLoadContext(string mainAssemblyPath, string directory)
        : AssemblyLoadContext(name: Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: true)
    {
        private readonly AssemblyDependencyResolver? _resolver = TryCreateResolver(mainAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is null)
            {
                return null;
            }

            // 框架已经有的（契约、它的抽象包……）一律用那一份，别在这里造第二个身份。
            var fromFramework = FrameworkContext?.Assemblies.FirstOrDefault(
                loaded => string.Equals(loaded.GetName().Name, assemblyName.Name, StringComparison.Ordinal));
            if (fromFramework is not null)
            {
                return fromFramework;
            }

            var path = _resolver?.ResolveAssemblyToPath(assemblyName)
                       ?? Existing(Path.Combine(directory, assemblyName.Name + ".dll"));
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        private static string? Existing(string path) => File.Exists(path) ? Path.GetFullPath(path) : null;

        private static AssemblyDependencyResolver? TryCreateResolver(string path)
        {
            // 编译产物没有 deps.json，解析器建不起来也无妨 —— 目录探测兜底。
            try
            {
                return new AssemblyDependencyResolver(path);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}

internal sealed class CompiledPluginInstance(IPlugin plugin, AssemblyLoadContext loadContext) : PluginInstance(plugin)
{
    private AssemblyLoadContext? _loadContext = loadContext;

    /// <summary>编出来的程序集就是搬运单元（ADR-0010）：放下它，回收照常验证（ADR-0023）。</summary>
    public override Func<bool> Release()
    {
        var contextUnderTest = new WeakReference(_loadContext!);
        _loadContext!.Unload();
        _loadContext = null;
        return () => !contextUnderTest.IsAlive;
    }
}
