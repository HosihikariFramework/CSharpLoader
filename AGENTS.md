# AGENTS.md

C# source loader for the Hosihikari Framework. It compiles `.cs` files with Roslyn at load and runs
the result in a collectible assembly load context.

## Repository map

- `src/` — loader implementation and cache.
- `host/` — assertion host for compilation, caching, isolation, and unload.
- `test/` — fixture packages and private dependencies.
- `pack.ps1`, `pack.sh` — build the `hosihikari-csharp` lip package.
- `tooth.json` — lip package metadata.

## Build and verify

```bash
dotnet run --project host/Hosihikari.PluginLoader.CSharp.Host.csproj -c Release --nologo
pwsh ./pack.ps1 -Version 1.0.0 -OutputRoot artifacts
```

The host expects sibling `PluginManager`, `Plugin.Abstractions`, and
`Hosihikari.McMaster.NETCore.Plugins` checkouts. `pack.ps1` calls the core packer and needs the same
`PluginManager` checkout.

## Invariants

- The package is a plugin package named `hosihikari-csharp`; its manifest routes on
  `extraInfo.loader: csharp` and entry `main.cs`.
- Compile-time references follow the runtime order: framework shared layer, package-local DLLs,
  then trusted platform assemblies.
- Cache keys cover source text, package-local DLLs, Roslyn version, and runtime version. Keep the
  checksum check in sync.
- A failed load releases its ALC; one package exposes exactly one `IPlugin` type.
- Public default branches are protected. Work on a branch, open a pull request, and squash merge.

## Context

- [Design record](https://github.com/HosihikariFramework/Design) — GLOSSARY and ADRs.
- [C# plugin guide](https://hf.srcz.one/authoring-csharp/).
