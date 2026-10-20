using System.IO.Hashing;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;

using Hosihikari;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.Logging;

namespace Hosihikari.PluginProvider.CSharp;

/// <summary>
/// C# 型插件（ADR-0002 的 Provider 扩展点）。
///
/// 与 F# 那条同形：C# 直接跑在 CLR 上，所以这里**装载时编译** —— Roslyn 把 <c>.cs</c> 编成
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

        // 内容寻址：目录名把入口、#:include 的文件、依赖、编译器与运行时一起钉死。
        var sources = LoadSources(entry, packageDirectory, context);
        if (sources is null)
        {
            return null;
        }

        var cacheDirectory = Path.Combine(context.DataDirectory, "bin", CacheKey(sources, packageDirectory));
        var assemblyPath = Path.Combine(cacheDirectory, manifest.Name + ".dll");

        if (!IsUsable(assemblyPath))
        {
            // 没有、或校验和对不上 —— 都丢弃重编，绝不"验不过也照跑"。
            if (Directory.Exists(cacheDirectory))
            {
                try
                {
                    Directory.Delete(cacheDirectory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 还有 ALC 钉着这份产物时删不掉；不要把坏缓存当成能编译。
                    context.Logger.LogError(ex, "清不掉损坏的缓存目录 {Directory}", cacheDirectory);
                    return null;
                }
            }

            if (!Compile(entry, packageDirectory, cacheDirectory, manifest.Name, context, sources))
            {
                return null;
            }
        }

        var loadContext = new ScriptLoadContext(assemblyPath, cacheDirectory, packageDirectory);
        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            var candidates = assembly.GetTypes()
                .Where(candidate => typeof(IPlugin).IsAssignableFrom(candidate)
                                    && candidate is { IsAbstract: false, IsInterface: false }
                                    && candidate.GetConstructor(System.Type.EmptyTypes) is not null)
                .ToArray();
            if (candidates.Length != 1)
            {
                context.Logger.LogError(
                    "{Assembly} 需要恰好一个实现 IPlugin 且有无参构造的类型，实际 {Count} 个",
                    assemblyPath,
                    candidates.Length);
                loadContext.Unload();
                return null;
            }

            if (Activator.CreateInstance(candidates[0]) is not IPlugin plugin)
            {
                context.Logger.LogError("造不出 {Type} 的实例", candidates[0].FullName);
                loadContext.Unload();
                return null;
            }

            return new CompiledPluginInstance(plugin, loadContext);
        }
        catch (Exception ex)
        {
            context.Logger.LogError(ex, "装载 {Name} 的产物失败", manifest.Name);
            try
            {
                loadContext.Unload();
            }
            catch (Exception unloadEx)
            {
                context.Logger.LogWarning(unloadEx, "回收 {Name} 的失败产物时抛出", manifest.Name);
            }

            return null;
        }
    }

    /// <summary>
    /// 读入口与它通过 <c>#:include</c> 引入的文件；<c>#:package</c> 与 <c>#:include</c> 行
    /// 在编译前换成空行，保住原始行号。文件必须留在插件包内。
    /// </summary>
    private static List<SourceFile>? LoadSources(
        string entry,
        string packageDirectory,
        IPluginContext context)
    {
        var root = Path.GetFullPath(packageDirectory);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var visited = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        var sources = new List<SourceFile>();
        string? error = null;

        void Visit(string path)
        {
            if (error is not null)
            {
                return;
            }

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                error = $"源码路径无效：{path}";
                return;
            }

            if (!full.StartsWith(prefix, comparison))
            {
                error = $"#:include 出界：{path}";
                return;
            }

            if (!visited.Add(full))
            {
                return;
            }

            if (!full.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                error = $"#:include 只支持 .cs：{path}";
                return;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"读不了源码 {path}：{ex.Message}";
                return;
            }

            var stripped = new string[lines.Length];
            for (var index = 0; index < lines.Length; index++)
            {
                var trimmed = lines[index].TrimStart();
                if (IsDirective(trimmed, "#:package"))
                {
                    stripped[index] = "";
                    continue;
                }

                if (IsDirective(trimmed, "#:include"))
                {
                    stripped[index] = "";
                    var include = trimmed["#:include".Length..].Trim();
                    if (include.Length == 0)
                    {
                        error = $"{path}: #:include 缺少路径";
                        return;
                    }

                    var baseDirectory = Path.GetDirectoryName(full)!;
                    if (include.IndexOfAny(['*', '?']) >= 0)
                    {
                        var matches = ExpandGlob(baseDirectory, include);
                        if (matches.Length == 0)
                        {
                            error = $"{path}: #:include 没有匹配到文件：{include}";
                            return;
                        }

                        foreach (var match in matches)
                        {
                            Visit(match);
                        }
                    }
                    else
                    {
                        Visit(Path.Combine(baseDirectory, include));
                    }

                    continue;
                }

                stripped[index] = lines[index];
            }

            sources.Add(new SourceFile(full, string.Join(Environment.NewLine, stripped)));
        }

        Visit(entry);
        if (error is not null)
        {
            context.Logger.LogError("{Error}", error);
            return null;
        }

        return sources;
    }

    private static bool IsDirective(string line, string directive) =>
        line.StartsWith(directive, StringComparison.Ordinal)
        && (line.Length == directive.Length || char.IsWhiteSpace(line[directive.Length]));

    /// <summary>按 file-based apps 的常见 glob 语义匹配：<c>*</c> 段内、<c>**</c> 跨目录、<c>?</c> 单字符。</summary>
    private static string[] ExpandGlob(string baseDirectory, string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        var regex = new Regex(GlobToRegex(normalized), RegexOptions.CultureInvariant);
        return Directory
            .EnumerateFiles(baseDirectory, "*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(file => regex.IsMatch(Path.GetRelativePath(baseDirectory, file).Replace('\\', '/')))
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToArray();
    }

    private static string GlobToRegex(string pattern)
    {
        var builder = new StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '*')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    index++;
                    if (index + 1 < pattern.Length && pattern[index + 1] == '/')
                    {
                        index++;
                        builder.Append("(?:.*/)?");
                    }
                    else
                    {
                        builder.Append(".*");
                    }
                }
                else
                {
                    builder.Append("[^/]*");
                }
            }
            else if (character == '?')
            {
                builder.Append("[^/]");
            }
            else
            {
                builder.Append(Regex.Escape(character.ToString()));
            }
        }

        return builder.Append('$').ToString();
    }

    private sealed record SourceFile(string Path, string Text);

    /// <summary>全部源码文件 + 包内自带的依赖 + Roslyn 版本 + 运行时版本，xxhash128 后取十六进制。</summary>
    private static string CacheKey(IReadOnlyList<SourceFile> sources, string packageDirectory)
    {
        var hasher = new XxHash128();
        hasher.Append(Encoding.UTF8.GetBytes("csharp-cache-3"));
        foreach (var source in sources)
        {
            hasher.Append(Encoding.UTF8.GetBytes(Path.GetRelativePath(packageDirectory, source.Path)));
            hasher.Append(Encoding.UTF8.GetBytes(source.Text));
        }

        hasher.Append(Encoding.UTF8.GetBytes(typeof(CSharpCompilation).Assembly.GetName().Version?.ToString() ?? "?"));
        hasher.Append(Encoding.UTF8.GetBytes(typeof(IPlugin).Assembly.GetName().Version?.ToString() ?? "?"));
        hasher.Append(Encoding.UTF8.GetBytes(typeof(CSharpPluginProvider).Assembly.GetName().Version?.ToString() ?? "?"));
        hasher.Append(Encoding.UTF8.GetBytes(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier));
        hasher.Append(Encoding.UTF8.GetBytes(Environment.Version.ToString()));

        // 包内自带的依赖也参与寻址 —— 换了 dll 就该重编。
        foreach (var path in LocalAssemblies(packageDirectory))
        {
            hasher.Append(Encoding.UTF8.GetBytes(Path.GetFileName(path)));
            hasher.Append(XxHash128.Hash(File.ReadAllBytes(path)));
        }

        return Convert.ToHexStringLower(hasher.GetCurrentHash());
    }

    /// <summary>插件目录里自带的依赖；框架已经有的那些不算（共享层，ADR-0004）。</summary>
    private static IReadOnlyList<string> LocalAssemblies(string packageDirectory) =>
        Directory.EnumerateFiles(packageDirectory, "*.dll")
            .Where(IsManagedAssembly)
            .Where(path => !IsFrameworkAssembly(Path.GetFileNameWithoutExtension(path)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    /// <summary>包根可以有本机库（ADR-0034）；它们不能当 Roslyn 的元数据引用。</summary>
    private static bool IsManagedAssembly(string path)
    {
        try
        {
            AssemblyName.GetAssemblyName(path);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static bool IsFrameworkAssembly(string name) =>
        FrameworkContext?.Assemblies.Any(loaded => string.Equals(loaded.GetName().Name, name, StringComparison.Ordinal))
        ?? false;

    /// <summary>产物与 PDB 都在、且两行校验和都对得上才算能用。</summary>
    private static bool IsUsable(string assemblyPath)
    {
        var checksumPath = assemblyPath + ChecksumSuffix;
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
        if (!File.Exists(assemblyPath) || !File.Exists(pdbPath) || !File.Exists(checksumPath))
        {
            return false;
        }

        var expected = File.ReadAllLines(checksumPath);
        if (expected.Length != 2)
        {
            return false;
        }

        var assemblyHash = Convert.ToHexStringLower(XxHash128.Hash(File.ReadAllBytes(assemblyPath)));
        var pdbHash = Convert.ToHexStringLower(XxHash128.Hash(File.ReadAllBytes(pdbPath)));
        return string.Equals(expected[0].Trim(), assemblyHash, StringComparison.OrdinalIgnoreCase)
               && string.Equals(expected[1].Trim(), pdbHash, StringComparison.OrdinalIgnoreCase);
    }

    private bool Compile(
        string scriptPath,
        string packageDirectory,
        string cacheDirectory,
        string name,
        IPluginContext context,
        IReadOnlyList<SourceFile> sources)
    {
        // 编到唯一的临时目录，成功了再原子改名 —— 崩在中间不会留下半个产物，
        // 两个进程同时编同一份内容也不会踩同一个 .tmp。
        var staging = cacheDirectory + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            Directory.CreateDirectory(staging);

            var assemblyPath = Path.Combine(staging, name + ".dll");
            if (!CompileTo(packageDirectory, assemblyPath, context, sources))
            {
                return false;
            }

            var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
            var checksum = Convert.ToHexStringLower(XxHash128.Hash(File.ReadAllBytes(assemblyPath)))
                           + Environment.NewLine
                           + Convert.ToHexStringLower(XxHash128.Hash(File.ReadAllBytes(pdbPath)));
            File.WriteAllText(assemblyPath + ChecksumSuffix, checksum);

            Directory.CreateDirectory(Path.GetDirectoryName(cacheDirectory)!);
            try
            {
                Directory.Move(staging, cacheDirectory);
            }
            catch (IOException) when (Directory.Exists(cacheDirectory) && IsUsable(Path.Combine(cacheDirectory, name + ".dll")))
            {
                // 另一个进程已经先落好了同一份内容。
                return true;
            }

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

    private static bool CompileTo(
        string packageDirectory,
        string assemblyPath,
        IPluginContext context,
        IReadOnlyList<SourceFile> sources)
    {
        // 与 SDK 的 ImplicitUsings 对齐：作者不该为 System / IO / Linq 写样板。
        // 单独一棵 global using 的树 —— 编译选项上的 Usings 只对 script 模式生效，
        // 而把 using 拼进脚本会把它的行号整体推移，PDB 里的 file:line 就不准了。
        var imports = CSharpSyntaxTree.ParseText(
            string.Join(Environment.NewLine, DefaultUsings.Select(name => $"global using {name};")),
            new CSharpParseOptions(LanguageVersion.Latest),
            "implicit-usings.cs",
            Encoding.UTF8);

        // 第四个参数是编码：不给它，Roslyn 拒绝为这段源码发 PDB（CS8055）。
        // 每份源码都用调用方读过、且参与缓存键的同一份文本。
        var trees = sources
            .Select(source => CSharpSyntaxTree.ParseText(
                source.Text,
                new CSharpParseOptions(LanguageVersion.Latest),
                source.Path,
                Encoding.UTF8))
            .ToArray();

        // 引用 = 宿主的运行时 + 框架自己 ALC 里的那些（契约与它的抽象包都在里面）。
        // 引用 = 宿主的运行时 + 框架自己 ALC 里的那些 + **插件包里自带的依赖**。
        var references = CollectReferences(packageDirectory)
            .Select(path => MetadataReference.CreateFromFile(path));

        var compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(assemblyPath),
            [imports, ..trees],
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
            context.Logger.LogError("{Message}", diagnostic.ToString());
        }

        return false;
    }

    private static readonly string[] DefaultUsings =
    [
        "System",
        "System.Collections.Generic",
        "System.IO",
        "System.Linq",
        "System.Net.Http",
        "System.Threading",
        "System.Threading.Tasks",
    ];

    /// <summary>宿主的运行时 + 框架自己的 ALC + 插件包里自带的依赖。</summary>
    private static IEnumerable<string> CollectReferences(string packageDirectory)
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

        foreach (var path in LocalAssemblies(packageDirectory))
        {
            if (seen.Add(Path.GetFileName(path)))
            {
                yield return path;
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清不掉就留着 —— 它命中不了新的内容寻址，不会再被加载。
            }
        }
    }

    /// <summary>每个插件一个 collectible ALC：自己的依赖从自己的目录解析，契约走框架的 ALC。</summary>
    private sealed class ScriptLoadContext(string mainAssemblyPath, string directory, string packageDirectory)
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
                       ?? Existing(Path.Combine(directory, assemblyName.Name + ".dll"))
                       ?? Existing(Path.Combine(packageDirectory, assemblyName.Name + ".dll"));
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            // 本机库随进程存活；这里只负责按 RID → 包根的顺序找到它（ADR-0034）。
            var path = FindNativeLibrary(packageDirectory, unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }

        private static string? FindNativeLibrary(string root, string unmanagedDllName)
        {
            string[] prefixes = OperatingSystem.IsWindows() ? [""] : ["", "lib"];
            string[] extensions = OperatingSystem.IsWindows() ? [".dll"]
                : OperatingSystem.IsMacOS() ? [".dylib"] : [".so", ".so.1"];
            var directories = new[]
            {
                Path.Combine(root, "runtimes", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "native"),
                root,
            };

            foreach (var directory in directories)
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var prefix in prefixes)
                {
                    foreach (var extension in extensions)
                    {
                        var candidate = Existing(Path.Combine(directory, prefix + unmanagedDllName + extension));
                        if (candidate is not null)
                        {
                            return candidate;
                        }

                        candidate = Existing(Path.Combine(directory, prefix + unmanagedDllName));
                        if (candidate is not null)
                        {
                            return candidate;
                        }
                    }
                }
            }

            return null;
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
