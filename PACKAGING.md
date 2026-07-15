# Building the Thunderstore package

Build the `ConditionalConfigSync.Plugin` project. The imported `Thunderstore.targets` file uses the version embedded in the compiled plugin assembly as the package version.

The build will automatically:

1. read the actual assembly version from `ConditionalConfigSync.Plugin.dll`;
2. normalize `1.0.0.0` to the Thunderstore-compatible `1.0.0` form;
3. use `ConditionalConfigSync.Plugin/package/thunderstore/README.md` as the publication README and copy it to the repository root;
4. copy the built DLL files, XML documentation, and root `CHANGELOG.md` into `ConditionalConfigSync.Plugin/package/thunderstore`;
5. stage the GitHub release files, including `LICENSE`, `THIRD_PARTY_NOTICES.md`, and `PROJECT_CONTEXT.md`, in `ConditionalConfigSync.Plugin/package/github`;
6. generate `SHA256SUMS.txt` there for both DLL files and copy the same checksum file into the Thunderstore package;
7. update `manifest.json/version_number` through `UpdateThunderstoreManifest.ps1`;
8. create `ConditionalConfigSync.Plugin/package/ConditionalConfigSync.zip`.

The manifest updater reuses the shared publishing helpers from:

```text
../API/CommonPublish.ps1
```

There are no Nexus, localization, ILRepack, PDB, or MDB publishing steps.

Package generation can be disabled for a particular build with:

```text
/p:BuildThunderstorePackageOnBuild=false
```

Release metadata is defined in one place:

```text
ConditionalConfigSync/PluginInfo.cs
```

Update `PluginInfo.PluginVersion` for a new release. `PluginInfo.PluginName` and `PluginInfo.PluginGuid` are the shared display name and BepInEx/Harmony identifier. The BepInEx plugin and file/informational assembly versions use this package version. The core `ConditionalConfigSync.dll` intentionally keeps `AssemblyVersion` at `1.0.0.0` throughout compatible 1.x releases, while the bootstrap assembly follows the package version. The packaging target reads the compiled plugin DLL version and writes it to `manifest.json`.

After a successful package build, `CopyDLLPlugins` also copies both runtime assemblies to the active local r2modman profile:

```text
ConditionalConfigSync.Plugin.dll
ConditionalConfigSync.dll
```

The shared `API` directory is outside this repository, one level above the repository root.

## GitHub release files

The build stages files that are not specific to Thunderstore in `ConditionalConfigSync.Plugin/package/github`:

- `ConditionalConfigSync.Plugin.dll`;
- `ConditionalConfigSync.dll`;
- `ConditionalConfigSync.xml`;
- `SHA256SUMS.txt`;
- `README.md`;
- `CHANGELOG.md`;
- `LICENSE`;
- `THIRD_PARTY_NOTICES.md`;
- `PROJECT_CONTEXT.md`.

The build copies both license files to GitHub release staging. It does not add or remove them in the Thunderstore staging directory, so manually maintained Thunderstore contents are left intact.

Repository: <https://github.com/shudnal/ConditionalConfigSync>
