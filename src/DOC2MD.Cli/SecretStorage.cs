using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

internal interface ISecretStore
{
    string Description { get; }

    string Save(string secret);

    string? Load(string? persistedReference);
}

internal static class SecretStoreFactory
{
    public static ISecretStore Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsDpapiSecretStore();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOsKeychainSecretStore();
        }

        return new UnsupportedSecretStore();
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsDpapiSecretStore : ISecretStore
{
    private const string Prefix = "dpapi:v1:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DOC2MD.AzureDocumentIntelligenceKey.v1");

    public string Description => "Windows DPAPI for the current user";

    public string Save(string secret)
    {
        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(secret),
            Entropy,
            DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(protectedBytes);
    }

    public string? Load(string? persistedReference)
    {
        if (string.IsNullOrWhiteSpace(persistedReference))
        {
            return null;
        }

        var payload = persistedReference.StartsWith(Prefix, StringComparison.Ordinal)
            ? persistedReference[Prefix.Length..]
            : persistedReference;
        var protectedBytes = Convert.FromBase64String(payload);
        var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}

[SupportedOSPlatform("macos")]
internal sealed class MacOsKeychainSecretStore : ISecretStore
{
    private const string Reference = "keychain:v1:com.taskscape.doc2md:AzureDocumentIntelligenceKey";
    private static readonly byte[] Service = Encoding.UTF8.GetBytes("com.taskscape.doc2md");
    private static readonly byte[] Account = Encoding.UTF8.GetBytes("AzureDocumentIntelligenceKey");
    private const int ItemNotFoundStatus = -25300;

    public string Description => "the macOS Keychain for the current user";

    public string Save(string secret)
    {
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var status = NativeMethods.SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)Service.Length,
            Service,
            (uint)Account.Length,
            Account,
            out var existingLength,
            out var existingData,
            out var itemReference);

        try
        {
            if (status == 0)
            {
                ThrowIfError(
                    NativeMethods.SecKeychainItemModifyAttributesAndData(
                        itemReference,
                        IntPtr.Zero,
                        (uint)secretBytes.Length,
                        secretBytes),
                    "update");
            }
            else if (status == ItemNotFoundStatus)
            {
                ThrowIfError(
                    NativeMethods.SecKeychainAddGenericPassword(
                        IntPtr.Zero,
                        (uint)Service.Length,
                        Service,
                        (uint)Account.Length,
                        Account,
                        (uint)secretBytes.Length,
                        secretBytes,
                        out var addedItemReference),
                    "add");
                Release(addedItemReference);
            }
            else
            {
                ThrowIfError(status, "find");
            }
        }
        finally
        {
            if (existingData != IntPtr.Zero)
            {
                NativeMethods.SecKeychainItemFreeContent(IntPtr.Zero, existingData);
            }

            Release(itemReference);
        }

        return Reference;
    }

    public string? Load(string? persistedReference)
    {
        if (!string.Equals(persistedReference, Reference, StringComparison.Ordinal))
        {
            return null;
        }

        var status = NativeMethods.SecKeychainFindGenericPassword(
            IntPtr.Zero,
            (uint)Service.Length,
            Service,
            (uint)Account.Length,
            Account,
            out var passwordLength,
            out var passwordData,
            out var itemReference);

        if (status == ItemNotFoundStatus)
        {
            return null;
        }

        ThrowIfError(status, "read");
        try
        {
            var bytes = new byte[passwordLength];
            Marshal.Copy(passwordData, bytes, 0, checked((int)passwordLength));
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            if (passwordData != IntPtr.Zero)
            {
                NativeMethods.SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
            }

            Release(itemReference);
        }
    }

    private static void ThrowIfError(int status, string operation)
    {
        if (status != 0)
        {
            throw new InvalidOperationException($"Could not {operation} the DOC2MD Azure key in macOS Keychain (OSStatus {status}).");
        }
    }

    private static void Release(IntPtr reference)
    {
        if (reference != IntPtr.Zero)
        {
            NativeMethods.CFRelease(reference);
        }
    }

    private static class NativeMethods
    {
        private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(SecurityFramework)]
        internal static extern int SecKeychainFindGenericPassword(
            IntPtr keychainOrArray,
            uint serviceNameLength,
            byte[] serviceName,
            uint accountNameLength,
            byte[] accountName,
            out uint passwordLength,
            out IntPtr passwordData,
            out IntPtr itemReference);

        [DllImport(SecurityFramework)]
        internal static extern int SecKeychainAddGenericPassword(
            IntPtr keychain,
            uint serviceNameLength,
            byte[] serviceName,
            uint accountNameLength,
            byte[] accountName,
            uint passwordLength,
            byte[] passwordData,
            out IntPtr itemReference);

        [DllImport(SecurityFramework)]
        internal static extern int SecKeychainItemModifyAttributesAndData(
            IntPtr itemReference,
            IntPtr attributeList,
            uint passwordLength,
            byte[] passwordData);

        [DllImport(SecurityFramework)]
        internal static extern int SecKeychainItemFreeContent(IntPtr attributeList, IntPtr data);

        [DllImport(CoreFoundationFramework)]
        internal static extern void CFRelease(IntPtr value);
    }
}

internal sealed class UnsupportedSecretStore : ISecretStore
{
    public string Description => "a supported operating-system credential store";

    public string Save(string secret) =>
        throw new PlatformNotSupportedException("DOC2MD secure local key storage supports Windows DPAPI and macOS Keychain.");

    public string? Load(string? persistedReference) => null;
}
