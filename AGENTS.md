# AGENTS.md

C# source loader for the Hosihikari Framework. It compiles `.cs` files with Roslyn at load and runs
the result in a collectible assembly load context.

## Repository map

- `src/` — loader implementation and cache.
- `host/` — assertion host for compilation, caching, isolation, and unload.
- `test/` — fixture packages and private dependencies.
- `pack.ps1` — build the `hosihikari-csharp` lip package.
- `manifest.json` — lip package metadata (format version 4).

## Build and verify

```bash
dotnet run --project host/Hosihikari.PluginLoader.CSharp.Host.csproj -c Release --nologo
pwsh ./pack.ps1 -Version 1.0.0 -OutputRoot artifacts
```

The host expects sibling `PluginManager`, `Plugin.Abstractions`, and
`McMaster.NETCore.Plugins` checkouts. `pack.ps1` calls the core packer and needs the same
`PluginManager` checkout.

## Invariants

- The loader package is named `hosihikari-csharp`; it routes on `extraInfo.loader: assembly` with
  entry `Hosihikari.PluginLoader.CSharp.dll`. Source packages conventionally use
  `extraInfo.loader: csharp` and entry `main.cs`; the loader follows the manifest's `EntryPath`,
  so nested or renamed source paths remain valid.
- Compile-time references follow the runtime order: framework shared layer, package-local DLLs,
  then trusted platform assemblies.
- Cache keys cover source text, package-local DLLs, Roslyn version, and runtime version. Keep the
  checksum check in sync.
- A failed load releases its ALC. After excluding abstract/interface types and implementations
  without a public parameterless constructor, a package exposes exactly one `IPlugin` candidate.
- Public default branches are protected. Work on a branch, open a pull request, and squash merge.

## Context

- Design decisions and terminology are kept by maintainers outside the public repositories; ask for
  the relevant decision when changing lifecycle or public API.
- [C# plugin guide](https://hf.srcz.one/authoring-csharp/).
