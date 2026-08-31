using System.Security.Principal;

namespace QSurfer.Avalonia.Services;

internal static class GlobalRuleAuthorization
{
    public static bool CanManageGlobalRules()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User == null)
            {
                return false;
            }

            if (!IsDomainAccount(identity.Name))
            {
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }

            var domainSid = identity.User.AccountDomainSid;
            if (domainSid == null)
            {
                return false;
            }

            var domainAdminsSid = new SecurityIdentifier($"{domainSid.Value}-512");
            return identity.Groups?.Any(group => group.Equals(domainAdminsSid)) == true;
        }
        catch
        {
            return false;
        }
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
