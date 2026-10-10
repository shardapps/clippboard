using System.Security.Cryptography;
using Clippboard.Core;

namespace Clippboard.App.Platform;

internal sealed class WindowsProtector : IContentProtector
{
    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
}
