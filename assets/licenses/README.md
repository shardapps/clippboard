# Licenses and third-party notices

Clippboard source is licensed under Apache-2.0 (LICENSE-CLIPPBOARD.txt). That license does not relicense the bundled Microsoft runtime, Windows SDK projection or other third-party components.

This Windows distribution includes .NET 10.0.13, WPF/Windows Forms, and Microsoft.Windows.SDK.NET.Ref 10.0.22000.87 (including WinRT.Runtime). The accompanying MIT and third-party notices are copied from their upstream projects. Microsoft runtime and SDK binaries remain subject to their respective license terms.

.NET on Windows contains components under additional terms: coreclr.dll and the runtime in single-file binaries, Microsoft.DiaSymReader.Native, PresentationNative_cor3.dll, vcruntime140_cor3.dll and wpfgfx_cor3.dll use the .NET Library License. D3DCompiler_47_cor3.dll uses the Windows SDK License. See DOTNET-WINDOWS-LICENSE-INFO.md for Microsoft's inventory.

Authoritative terms and provenance:
- .NET Library License: https://dotnet.microsoft.com/dotnet_library_license.htm
- Windows SDK License (also specified by the SDK projection package): https://learn.microsoft.com/legal/windows-sdk/license
- .NET runtime license/notices: https://github.com/dotnet/runtime/tree/v10.0.13
- WPF notices: https://github.com/dotnet/wpf/blob/v10.0.13/THIRD-PARTY-NOTICES.TXT
- Windows Forms notices: https://github.com/dotnet/winforms/blob/v10.0.13/THIRD-PARTY-NOTICES.TXT
- C#/WinRT license: https://github.com/microsoft/CsWinRT/blob/master/LICENSE
- SDK projection package: https://www.nuget.org/packages/Microsoft.Windows.SDK.NET.Ref/10.0.22000.87

These notices are informational and do not expand the permissions granted by the upstream licenses. OS-provided Segoe UI fonts and Windows OCR are used through Windows APIs; font files, OCR models and the Windows SDK tools are not redistributed by Clippboard.

The same notices are embedded in the app and available offline under Settings > Help > Licenses & notices. Portable folders, installers and Store layouts also include the notices directory. Build verification compares these files with the source copies.