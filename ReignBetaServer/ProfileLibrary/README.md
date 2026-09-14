# Reign character profile library

`profile_catalog.json` is generated from the currently installed Sandbox noble data and Reign's fixed court roster. Do not edit the catalog by hand.

From `ReignBetaServer`, rebuild it with:

```powershell
dotnet build -c Release
..\ReignBeta\server\app\ReignBetaServer.exe --generate-profile-library `
  --sandbox-data "D:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\Modules\SandBox\ModuleData" `
  --reign-data "..\ReignBeta\ModuleData" `
  --court-manifest "..\ReignBeta\staging\court_nobles\court_nobles_manifest.json" `
  --output ".\ProfileLibrary\profile_catalog.json"
```

Validate without starting the HTTP server:

```powershell
..\ReignBeta\server\app\ReignBetaServer.exe --validate-profile-library
```

The manifest derives expected counts and source hashes from the inputs. Rebuild the catalog whenever the native game version or fixed Reign roster changes.
