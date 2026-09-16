# Reign character profile library

`profile_catalog.json` is generated from the installed Sandbox noble data and Reign's fixed court roster. Do not edit the catalog by hand.

Build through the paired workspace's canonical `ReignMcp/scripts/reign-validate.ps1 -LinuxServer` command. Inside WSL, use the resulting Linux artifact for these offline operations:

```sh
dotnet /path/to/validated/ReignBetaServer.dll --generate-profile-library \
  --sandbox-data /path/to/Bannerlord/Modules/SandBox/ModuleData \
  --reign-data /path/to/Reign/ReignBeta/ModuleData \
  --court-manifest /path/to/Reign/ReignBeta/staging/court_nobles/court_nobles_manifest.json \
  --output /path/to/ReignServer/ReignBetaServer/ProfileLibrary/profile_catalog.json

dotnet /path/to/validated/ReignBetaServer.dll --validate-profile-library
```

Use Linux paths for Windows-mounted inputs. The manifest derives expected counts and source hashes from the inputs. Rebuild the catalog whenever the native game version or fixed Reign roster changes.