# Third-party notices

Nocturne's original code remains under the root MIT License. Dependency binaries retain their own copyrights and licenses. Pianoteq is an external user-installed product and is not distributed here. Existing Pianoteq, Modartt and OpenAI trademark statements in the README remain applicable.

| Component | Version | License text |
| --- | --- | --- |
| NAudio.Core / NAudio.Wasapi | 2.3.0 | [MIT](third-party-licenses/NAudio.txt) |
| SIPSorcery / SIPSorceryMedia.Abstractions | 10.0.16 | [BSD 3-Clause **with additional use restriction**](third-party-licenses/SIPSorcery.md) |
| Concentus | 2.2.2 | [BSD-style Opus notices](third-party-licenses/Concentus.txt) |
| BouncyCastle.Cryptography | 2.7.0 | [MIT](third-party-licenses/BouncyCastle.md) |
| DnsClient | 1.8.0 | [Apache 2.0](third-party-licenses/DnsClient.txt) |
| Common.Logging / Common.Logging.Core | 3.4.1 | [Apache 2.0](third-party-licenses/Common.Logging.txt) |
| IPNetwork2 | 2.1.2 | [BSD 2-Clause](third-party-licenses/IPNetwork.txt) |
| Makaretu.Dns | 2.0.1 | [MIT](third-party-licenses/Makaretu.Dns.txt) |
| Makaretu.Dns.Multicast | 0.27.0 | [MIT](third-party-licenses/Makaretu.Dns.Multicast.txt) |
| SimpleBase | 1.3.1 | [Apache 2.0](third-party-licenses/SimpleBase.txt) |
| SIPSorcery.WebSocketSharp | 0.0.1 | [MIT](third-party-licenses/WebSocketSharp.txt) |
| Tmds.LibC (transitive Linux runtime assets) | 0.2.0 | [MIT](third-party-licenses/Tmds.LibC.txt) |
| Microsoft.Extensions.* / System.Diagnostics.DiagnosticSource | 10.0.11 | [.NET MIT](third-party-licenses/dotnet.txt), [third-party notices](third-party-licenses/dotnet-notices.txt) |

SIPSorcery's additional geographic/use restriction is reproduced verbatim in its license file. The dependency must not be advertised as unconditionally MIT/BSD licensed. Its bundled license also includes the LGPL section for SIPSorceryMedia.FFmpeg; this project does **not** reference or ship that FFmpeg component.

NuGet may restore compatibility framework packages in addition to the runtime assemblies above. Microsoft .NET components retain the .NET Foundation and contributor copyrights and the included .NET notices. No third-party source files were modified. Process-loopback interop calls the documented Windows WASAPI interface; NAudio wraps the audio client and capture buffers.

When redistributing `App/`, include this notice, the root LICENSE and `third-party-licenses/`; the publish project copies them into the application folder automatically.
