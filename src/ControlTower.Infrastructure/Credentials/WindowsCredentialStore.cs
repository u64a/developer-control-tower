using System;
using System.Runtime.InteropServices;
using System.Text;
using ControlTower.Core.Contracts;

namespace ControlTower.Infrastructure.Credentials
{
    public sealed class WindowsCredentialStore : ICredentialStore
    {
        public string GetPassword(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return string.Empty;
            }

            IntPtr credPtr = IntPtr.Zero;
            try
            {
                if (!CredRead(target, CRED_TYPE_GENERIC, 0, out credPtr))
                {
                    return string.Empty;
                }

                var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0)
                {
                    return string.Empty;
                }

                return Marshal.PtrToStringUni(cred.CredentialBlob, (int)(cred.CredentialBlobSize / 2));
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                if (credPtr != IntPtr.Zero)
                {
                    CredFree(credPtr);
                }
            }
        }

        public void SetPassword(string target, string password)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return;
            }

            var passwordBytes = Encoding.Unicode.GetBytes(password ?? string.Empty);

            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                CredentialBlobSize = (uint)passwordBytes.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = target
            };

            cred.CredentialBlob = Marshal.AllocHGlobal(passwordBytes.Length);
            try
            {
                Marshal.Copy(passwordBytes, 0, cred.CredentialBlob, passwordBytes.Length);
                CredWrite(ref cred, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(cred.CredentialBlob);
            }
        }

        public void DeletePassword(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return;
            }

            CredDelete(target, CRED_TYPE_GENERIC, 0);
        }

        private const int CRED_TYPE_GENERIC = 1;
        private const int CRED_PERSIST_LOCAL_MACHINE = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern void CredFree(IntPtr buffer);
    }
}
