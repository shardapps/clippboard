using System;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace Clippboard.App.Services;

internal static class PasswordBuffer
{
    internal static byte[] Encode(SecureString password)
    {
        if (password.Length is < 12 or > 256) throw new ArgumentException("Use a passphrase of 12–256 characters.");
        var pointer = Marshal.SecureStringToGlobalAllocUnicode(password); var characters = new char[password.Length];
        try { Marshal.Copy(pointer, characters, 0, characters.Length); return new UTF8Encoding(false, true).GetBytes(characters); }
        finally { Array.Clear(characters); Marshal.ZeroFreeGlobalAllocUnicode(pointer); }
    }
}
