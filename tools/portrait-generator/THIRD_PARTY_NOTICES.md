# Third-party notices

## TpacTool.Lib

- Project: TpacTool
- Author: szszss
- Source: https://github.com/szszss/TpacTool
- Upstream snapshot: `b56b77ad91ecf5ece1a4d877a45cc5f385cd56fa` (2022-09-08)
- License: MIT; the complete text is preserved at `third_party/TpacTool/LICENSE`.

Local compatibility changes are intentionally small and isolated:

- tolerate metadata records introduced after the upstream parser was last updated;
- recognize Bannerlord's current resident texture segment identifier;
- infer the largest resident mip level when high-resolution mip levels are streamed elsewhere;
- correct raw decoded-row traversal for modern .NET;
- remove use of unsupported `Thread.Abort` from an unused asynchronous package-search path;
- target .NET 8 for this standalone application.

TpacTool code is tooling only. No TaleWorlds assets are included with it or with this project.

## BCnEncoder.NET

- Project: BCnEncoder.NET
- Author: Nominom
- Source: https://github.com/Nominom/BCnEncoder.NET
- NuGet package: `BCnEncoder.Net` 2.3.0
- License: MIT or Unlicense

BCnEncoder.NET is used to decode native BC7 texture payloads in memory. No game textures are included with this project.
