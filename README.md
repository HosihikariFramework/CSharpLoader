# Hosihikari.PluginProvider.CSharp

[English](README.md) | [简体中文](README.zh_hans.md)

The **C# plugin Provider** of the Hosihikari framework. Like F#, C# has no VM of its own, so this
provider **compiles at load time**: Roslyn turns a `.cs` file into a real assembly, which then goes
into its own collectible ALC.

That buys C# plugins the same promises as any assembly plugin: per-plugin dependency isolation, and an
unload that is verified rather than assumed.

**In-process .NET.** The framework keeps the assembly provider, this C# provider and the F# provider
in-process. Other languages go through the generic out-of-process provider
(`Hosihikari.PluginProvider.Rpc`, ADR-0031).

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
      "extraInfo": { "api": "2", "format_version": "1", "provider": "csharp" }
    }
  main.cs
  Hello.conf          ← the author's defaults, shipped with the package
```

The file defines a type implementing `IPlugin` with a parameterless constructor; finding it is the
provider's job.

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

## How it compiles

`CSharpCompilation` from Roslyn (`Microsoft.CodeAnalysis.CSharp`). The references are the host's
trusted platform assemblies plus the assemblies in the **framework's own load context** — the host is
loaded into an isolated ALC, so the contract and its abstractions are not in the default context.
Private DLLs in the plugin package root are also referenced at compile time and resolved from that
directory at runtime; shared framework assemblies keep their existing identity.
Parsing passes an encoding, without which Roslyn refuses to emit debugging information (CS8055).

The output is cached by content (ADR-0029): `<data directory>/bin/<hash>/<name>.dll`, the hash covering
the script text, package-local private DLL names and contents, the Roslyn version and the runtime version. A checksum sits next to it and is verified
before loading — a mismatch means recompile, never load anyway. Compilation goes into a staging
directory and is moved into place only on success, and a portable PDB is emitted alongside so logged
stacks carry `file:line`.

The checksum guards against accidents, not against a hostile plugin: plugins share this process and
are trusted (ADR-0011).

## Running it

```
dotnet run --project host/Hosihikari.PluginProvider.CSharp.Host.csproj -c Release
```

The harness drives this Provider through the core's real `PluginManagerHost`, so sibling
`Hosihikari.PluginManager`, `Hosihikari.Abstractions` and McMaster fork checkouts are required. It
checks these things: the Provider is registered by a plugin package, the artifact lands in a
content-addressed directory with its checksum and PDB, the script runs at load and reads the
operator's override, enable hooks reach it, a channel message from the host reaches it, a
package-local third-party dependency is usable, native libraries resolve from the RID directory,
disable and unload hooks arrive, the unload verifies, a second load hits the cache, and a tampered
artifact is detected and recompiled. A separate fixture DLL is built without
referencing it from the host, so the dependency check also covers a genuinely private assembly.

## How it reaches the host process

As a plugin package, named `hosihikari-csharp` — the same way as the other in-process providers. Script packages
declare the dependency, and the dependency graph orders the provider first.

**Registration cannot be undone**: the types this provider hands out live in its load context.

## Licence

LGPL-3.0-or-later, see [LICENSE.md](LICENSE.md).
