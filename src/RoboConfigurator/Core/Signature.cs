using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace RoboConfigurator.Core
{
    /// <summary>Signature Authenticode d'un exe (API WinVerifyTrust de Windows, sans dépendance).</summary>
    public static class Signature
    {
        /// <summary>Nom du signataire (CN du certificat) si la signature est valide et approuvée par Windows, sinon une exception.</summary>
        public static string VerifiedSigner(string path)
        {
            int code = Verify(path);
            if (code != 0)
                throw new InvalidPayloadException(Lang.T($"invalid or missing digital signature (code 0x{code:X8})", $"signature numérique absente ou invalide (code 0x{code:X8})"));
            var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return cert.GetNameInfo(X509NameType.SimpleName, false);
        }

        private static int Verify(string path)
        {
            var file = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)), pcwszFilePath = path };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            try
            {
                Marshal.StructureToPtr(file, pFile, false);
                var data = new WinTrustData
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                    dwUIChoice = 2,                 // WTD_UI_NONE
                    fdwRevocationChecks = 0,        // WTD_REVOKE_NONE : fonctionne hors ligne
                    dwUnionChoice = 1,              // WTD_CHOICE_FILE
                    pFile = pFile,
                    dwStateAction = 1,              // WTD_STATEACTION_VERIFY
                    dwProvFlags = 0x1000 | 0x10,    // WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE
                };
                int result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
                data.dwStateAction = 2;             // WTD_STATEACTION_CLOSE
                WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data);
                return result;
            }
            finally
            {
                Marshal.DestroyStructure(pFile, typeof(WinTrustFileInfo));
                Marshal.FreeHGlobal(pFile);
            }
        }

        private static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WinTrustData data);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }
    }
}
