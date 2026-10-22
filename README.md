# Hosihikari.PluginLoader.CSharp

[English](README.md) | [简体中文](README.zh_hans.md)

The **C# plugin loader** of the Hosihikari framework. Like F#, C# runs on the CLR, so this
loader **compiles at load time**: Roslyn turns a `.cs` file into a real assembly, which then goes
into its own collectible ALC.

That buys C# plugins the same promises as any assembly plugin: per-plugin dependency isolation, and an
unload that is verified rather than assumed. This is an in-process loader: it compiles source
to IL before the plugin runs, and the loader loads that IL into a collectible ALC.

**In-process .NET.** The framework keeps the assembly loader, this C# loader and the F# loader
in-process. Other languages go through the generic out-of-process loader
(`Hosihikari.PluginLoader.Rpc`, ADR-0031).

## Templates

Start from [CSharpPluginTemplate](https://github.com/HosihikariFramework/CSharpPluginTemplate): copy
it into `plugins/`, rename the plugin, and restart. For a precompiled assembly, use the
[`dotnet new` template](https://www.nuget.org/packages/Hosihikari.Plugin.Templates).

## What a plugin package looks like

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
  Hello.conf          ← the author's defaults, shipped with the package
```

The file defines a type implementing `IPlugin` with a parameterless constructor; finding it is the
loader's job.

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

Because the result is a real assembly referencing the contract, a C# plugin reaches everything any
plugin does — including the type-keyed channels.

## File-based app directives

`main.cs` may start with `#:package Id@Version` and `#:include path.cs`, matching .NET file-based
apps. The loader strips them before Roslyn compiles. The packer restores packages at pack time
into the plugin package and copies included files; the server stays offline. `#:include` supports
`*`, `**`, and `?` globs. `#:project`, `#:property`, `#:sdk`, and top-level statements are not
supported.

## How it compiles

`CSharpCompilation` from Roslyn (`Microsoft.CodeAnalysis.CSharp`). References follow the same
priority as the runtime: assemblies already loaded in the **framework's own load context** (the
contract and its abstractions), private DLLs in the plugin package root, then the host's trusted
platform assemblies. This keeps compile-time and runtime on the same version when the host and the
package carry different builds of the same dependency; shared framework assemblies keep their
existing identity.
Parsing passes an encoding, without which Roslyn refuses to emit debugging information (CS8055).

The output is cached by content (ADR-0029): `<data directory>/bin/<hash>/<name>.dll`, the hash covering
the source text, package-local private DLL names and contents, the Roslyn version and the runtime version. A checksum sits next to it and is verified
before loading — a mismatch means recompile, never load anyway. Compilation goes into a staging
directory and is moved into place only on success, and a portable PDB is emitted alongside so logged
stacks carry `file:line`.

The checksum guards against accidents, not against a hostile plugin: plugins share this process and
are trusted (ADR-0011).

## Running it

```
dotnet run --project host/Hosihikari.PluginLoader.CSharp.Host.csproj -c Release
```

The harness drives this loader through the real `PluginManager`, so sibling
`PluginManager`, the contract checkout (package `Hosihikari.Plugin.Abstractions`) and the
McMaster fork checkouts are required. It
checks these things: the loader is registered by a plugin package, the artifact lands in a
content-addressed directory with its checksum and PDB, the source is compiled at load and reads the
operator's override, enable hooks reach it, a channel message from the host reaches it, a
package-local third-party dependency is usable, native libraries resolve from the RID directory,
disable and unload hooks arrive, the unload verifies, a second load hits the cache, and a tampered
artifact is detected and recompiled. A separate fixture DLL is built without
referencing it from the host, so the dependency check also covers a genuinely private assembly.

## How it reaches the host process

As a plugin package, named `hosihikari-csharp` — the same way as the other in-process loaders. Source packages
declare the dependency, and the dependency graph orders the loader first.

**Registration cannot be undone**: the types this loader hands out live in its load context.

## Packaging

```
pwsh ./pack.ps1 -Version 1.0.0 -OutputRoot artifacts
```

It assembles the loader DLL, its private dependencies and the manifest into
`hosihikari-csharp/`; the shared layer is not copied. Pushing a `v*` tag attaches the zip to the
GitHub release (ADR-0036).

The script invokes the core packager (`PluginManager/tools/Pack`), which owns the
shared-layer list. Check out the core repository alongside this one, or set
`HOSIHIKARI_PLUGINMANAGER`.

After release: `lip install github.com/HosihikariFramework/CSharpLoader` (install the Shim
variant for the carrier first).

## Licence

LGPL-3.0-or-later, see [LICENSE.md](LICENSE.md).
