# Third-party components

CardGrinder uses unmodified, separately distributed libraries. Steam and game artwork belong to their respective owners. CardGrinder is not affiliated with Valve.

| Component | Version | License | Source |
| --- | --- | --- | --- |
| SteamKit2 | 3.4.0 | LGPL-2.1-only (NuGet metadata) | https://github.com/SteamRE/SteamKit/tree/3.4.0 |
| HtmlAgilityPack | 1.12.4 | MIT | https://github.com/zzzprojects/html-agility-pack |
| QRCoder | 1.6.0 | MIT | https://github.com/codebude/QRCoder |
| protobuf-net / protobuf-net.Core | 3.2.56 | Apache-2.0 | https://github.com/protobuf-net/protobuf-net |
| ZstdSharp.Port | 0.8.7 | MIT | https://github.com/oleg-st/ZstdSharp |
| System.IO.Hashing | 10.0.1 | MIT | https://github.com/dotnet/runtime |
| System.Security.Cryptography.ProtectedData | 8.0.0 | MIT | https://github.com/dotnet/runtime |
| System.Drawing.Common / Microsoft.Win32.SystemEvents | 6.0.0 | MIT | https://github.com/dotnet/runtime |
| .NET / Windows Desktop runtime | 10.0.14 | MIT and included notices | https://github.com/dotnet/runtime |

License texts and upstream copyright notices are included in `licenses/`. SteamKit2.dll is not statically linked or embedded; a compatible replacement can be placed beside the executable. Its corresponding unmodified source is the SteamKit2 3.4.0 release linked above. The publish folder includes these notices and licenses.
