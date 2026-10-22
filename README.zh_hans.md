# Hosihikari.PluginLoader.CSharp

[English](README.md) | [简体中文](README.zh_hans.md)

星光框架的 **C# 型插件加载器**。与 F# 一样，C# 直接跑在 CLR 上，所以这个加载器 **装载时编译**：
Roslyn 把 `.cs` 编成真程序集，再进一个 collectible ALC。

C# 插件因此拿到与其他程序集插件一样的承诺：每插件的依赖互相隔离，卸载是真的验证过而不是假定的。
这是进程内加载器：在插件开始运行前把源码编译为 IL，加载器把 IL 装进 collectible ALC。

**进程内只留 .NET。** 框架在进程内保留程序集型、C# 与 F# 三个加载器；其他语言走
通用的进程外加载器（`Hosihikari.PluginLoader.Rpc`，ADR-0031）。

## 模板

从 [CSharpPluginTemplate](https://github.com/HosihikariFramework/CSharpPluginTemplate) 开始：复制到
`plugins/`，改名后重启。预编译程序集用
[`dotnet new` 模板](https://www.nuget.org/packages/Hosihikari.Plugin.Templates)。

## 插件包长什么样

```
plugins/Hello/
  manifest.json
    {
      "name": "Hello",
      "entry": "main.cs",
      "type": "hosihikari",
      "version": "1.0.0",
      "dependencies": [
        { "name": "hosihikari" },
        { "name": "hosihikari-csharp" }
      ],
      "extraInfo": { "api_version": "2", "manifest_version": "1", "loader": "csharp" }
    }
  main.cs
  Hello.conf          ← 作者给的默认值，随包更新
```

文件里定义一个实现 `IPlugin`、有无参构造的类型；找到它是加载器的事。

```csharp
using System.IO;
using Hosihikari;

public sealed class HelloPlugin : IPlugin
{
    private string _data = "";

    public void OnLoad(IPluginContext context)
    {
        _data = context.DataDirectory;
        File.WriteAllText(Path.Combine(_data, "body.txt"), "body:" + context.Configuration["greeting"]);
    }

    public PluginHookResult OnEnable()
    {
        File.WriteAllText(Path.Combine(_data, "enabled.txt"), "enabled");
        return PluginHookResult.Allow();
    }

    public PluginHookResult OnDisable() => PluginHookResult.Allow();
    public void OnUnload() { }
}
```

编出来的是一个引用了契约的真程序集，所以 C# 插件拿得到任何插件都拿得到的东西 —— 包括那些按类型做键的通道。

## file-based 指令

`main.cs` 开头可以写 `#:package Id@Version` 与 `#:include path.cs`，与 .NET file-based apps
对齐。加载器在 Roslyn 编译前把它们换成空行；打包器在打包期把包还原进插件包并复制被 include
的文件，服务端保持离线。`#:include` 支持 `*`、`**`、`?` 通配符。`#:project`、`#:property`、
`#:sdk` 与顶层语句不支持。

## 它怎么编译

用 Roslyn 的 `CSharpCompilation`（`Microsoft.CodeAnalysis.CSharp`）。引用的优先级与运行期一致：
**框架自己那个装载上下文**里已加载的程序集（契约与它的抽象包）、包根目录内的私有 DLL、最后才是宿主的 TPA。
宿主与包带同名依赖的不同版本时，编译与运行会选同一份；共享框架程序集仍保持原来的类型身份。
解析源码时要给编码，否则 Roslyn 拒绝发调试信息（CS8055）。

产物按内容寻址缓存（ADR-0029）：`<数据目录>/bin/<哈希>/<名字>.dll`，哈希覆盖源码内容、Roslyn 版本与
运行时版本，以及包内私有 DLL 的名字和内容。产物旁存一份校验和，加载前对一遍 —— 对不上就重编，绝不照跑。编译先落到临时目录、成功了才
改名进去；同时产出 portable PDB，日志里的栈因此带 `file:line`。

校验和防的是**事故**，不防**恶意**：插件与本框架同进程、且完全受信（ADR-0011）。

## 跑一遍

```
dotnet run --project host/Hosihikari.PluginLoader.CSharp.Host.csproj -c Release
```

验证宿主借插件管理者的 `PluginManager` 驱动它，所以需要并列的 `PluginManager`、
契约仓检出（契约包 `Hosihikari.Plugin.Abstractions`）与 McMaster 的 fork checkout。它验证：加载器由插件包登记、产物按内容寻址落盘且带校验和
与 PDB、源码装载时编译并读到包外覆盖、启用钩子到达插件、宿主发的通道消息它能收到、包内自带的第三方依赖
可用、本机库按 RID 目录解析、停用与卸载钩子到达、卸载验证通过、同一份源码再装载命中缓存、以及产物被改坏后会被发现并重编。
测试还会构建宿主没有引用的独立 DLL，验证真正私有的依赖也能在插件中使用。

## 它怎么进宿主进程

作为插件包，名字是 `hosihikari-csharp` —— 与其他进程内加载器同一套。源码包声明依赖，依赖图先排它。

**登记之后不能撤销**：它造出来的类型住在它的装载上下文里。

## 打包

```
pwsh ./pack.ps1 -Version 1.0.0 -OutputRoot artifacts
```

它把加载器 DLL、私有依赖与清单装进 `hosihikari-csharp/`，共享层不随包走；打 `v*` 标签时 CI 把 zip 附到 release（ADR-0036）。

打包脚本调用插件管理者打包器（`PluginManager/tools/Pack`），共享层清单由插件管理者统一给出；需要并列检出插件管理者仓，或设置 `HOSIHIKARI_PLUGINMANAGER`。

发布后：`lip install github.com/HosihikariFramework/CSharpLoader`（先按载体装 Shim）。

## 许可

LGPL-3.0-or-later，见 [LICENSE.md](LICENSE.md)。
