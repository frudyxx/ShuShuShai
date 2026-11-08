using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ShuShuShai.App.Services;

internal static class InstallerSignature
{
    private static readonly Guid Authenticode = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    internal static void Verify(string path, string certificateHash)
    {
        var file = new TrustFile { Size = Marshal.SizeOf<TrustFile>(), Path = path };
        var filePointer = Marshal.AllocHGlobal(file.Size);
        Marshal.StructureToPtr(file, filePointer, false);
        var data = new TrustData
        {
            Size = Marshal.SizeOf<TrustData>(), UiChoice = 2, RevocationChecks = 1,
            UnionChoice = 1, File = filePointer, StateAction = 1, ProviderFlags = 0x80 | 0x2000
        };
        try
        {
            if (WinVerifyTrust(new IntPtr(-1), Authenticode, ref data) != 0)
                throw new InvalidDataException("安装包的 Windows 数字签名无效或无法核验。");
            IntPtr provider = WTHelperProvDataFromStateData(data.State);
            IntPtr signer = provider == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            IntPtr certificate = signer == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvCertFromChain(signer, 0);
            if (certificate == IntPtr.Zero) throw new InvalidDataException("无法读取安装包发布者。");
            var context = Marshal.PtrToStructure<ProviderCertificate>(certificate).Context;
            if (context == IntPtr.Zero) throw new InvalidDataException("无法读取安装包发布者。");
            using var publisher = new X509Certificate2(context);
            if (!publisher.GetCertHashString(HashAlgorithmName.SHA256).Equals(certificateHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装包签名与舒舒筛的预设发布者不符。");
        }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), Authenticode, ref data);
            Marshal.DestroyStructure<TrustFile>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        public int Size;
        public string Path;
        public IntPtr Handle, KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public int Size;
        public IntPtr PolicyData, SipData;
        public int UiChoice, RevocationChecks, UnionChoice;
        public IntPtr File;
        public int StateAction;
        public IntPtr State, UrlReference;
        public int ProviderFlags, UiContext;
        public IntPtr SignatureSettings;
    }

    // Only the first two fields of CRYPT_PROVIDER_CERT are needed.
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificate { public int Size; public IntPtr Context; }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, in Guid action, ref TrustData data);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint index, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterIndex);
    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint index);
}
