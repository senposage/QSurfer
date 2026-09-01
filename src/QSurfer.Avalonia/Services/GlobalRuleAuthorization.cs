using System.Security.Principal;

namespace QSurfer.Avalonia.Services;

internal static class GlobalRuleAuthorization
{
    public static bool CanManageGlobalRules()
    {
        var status = GetCurrentUserStatus();
        return status.IsDomainAccount ? status.IsDomainAdmin : status.IsLocalAdministrator;
    }

    public static bool IsCurrentUserDomainAdmin()
    {
        return GetCurrentUserStatus().IsDomainAdmin;
    }

    public static CurrentUserAccessStatus GetCurrentUserStatus()
    {
        var fallbackName = string.IsNullOrWhiteSpace(Environment.UserName) ? "Windows user unavailable" : Environment.UserName;
        if (!OperatingSystem.IsWindows())
        {
            return new CurrentUserAccessStatus(fallbackName, false, false, false);
        }
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var userName = string.IsNullOrWhiteSpace(identity.Name) ? fallbackName : identity.Name;
            var isDomainAccount = IsDomainAccount(userName);
            var isDomainAdmin = identity.User != null && isDomainAccount && IsDomainAdmin(identity);
            var localAdministratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var isLocalAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ||
                                       identity.Groups?.Any(group => group.Equals(localAdministratorsSid)) == true;
            return new CurrentUserAccessStatus(userName, isDomainAccount, isLocalAdministrator, isDomainAdmin);
        }
        catch
        {
            return new CurrentUserAccessStatus(fallbackName, false, false, false);
        }
    }

    private static bool IsDomainAdmin(WindowsIdentity identity)
    {
        var domainSid = identity.User?.AccountDomainSid;
        if (domainSid == null)
        {
            return false;
        }

        var domainAdminsSid = new SecurityIdentifier($"{domainSid.Value}-512");
        return identity.Groups?.Any(group => group.Equals(domainAdminsSid)) == true;
    }

    private static bool IsDomainAccount(string? identityName)
    {
        if (string.IsNullOrWhiteSpace(identityName))
        {
            return false;
        }

        var separator = identityName.IndexOf('\\');
        if (separator <= 0)
        {
            return false;
        }

        var authority = identityName[..separator];
        return !authority.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) &&
               !authority.Equals("NT AUTHORITY", StringComparison.OrdinalIgnoreCase) &&
               !authority.Equals("NT SERVICE", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record CurrentUserAccessStatus(
    string UserName,
    bool IsDomainAccount,
    bool IsLocalAdministrator,
    bool IsDomainAdmin)
{
    public string DisplayStatus => IsDomainAdmin
        ? "Domain Admin"
        : IsLocalAdministrator
            ? "Local administrator (not a Domain Admin)"
            : "Standard user";
}
