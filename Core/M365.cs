using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using pxdCommander.Platform.Win32;

namespace pxdCommander.Core;

public enum M365Service { ExchangeOnline, Graph, Teams, SharePoint, Compliance, Azure }

public enum M365Auth
{
    /// <summary>Sign in in the browser (MFA, one-time codes, passkeys all work).</summary>
    Browser,
    /// <summary>A code shown in the terminal, entered at microsoft.com/devicelogin from any device.</summary>
    DeviceCode,
    /// <summary>App-only with a certificate in the Windows certificate store: no prompts at all.</summary>
    Certificate,
    /// <summary>A saved password, for accounts without MFA (service accounts).</summary>
    Password,
}

/// <summary>A saved Microsoft 365 admin PowerShell connection: which services, which account, how to sign in.</summary>
public sealed class M365Profile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    /// <summary>The admin account, e.g. admin@contoso.com.</summary>
    public string Upn { get; set; } = "";
    /// <summary>Tenant domain or id (contoso.onmicrosoft.com); needed for certificate sign-in.</summary>
    public string Tenant { get; set; } = "";
    public List<M365Service> Services { get; set; } = [M365Service.ExchangeOnline];
    public M365Auth Auth { get; set; } = M365Auth.Browser;
    public string AppId { get; set; } = "";
    public string CertThumbprint { get; set; } = "";
    public string GraphScopes { get; set; } = "User.Read.All Group.Read.All Directory.Read.All";
    /// <summary>https://contoso-admin.sharepoint.com</summary>
    public string SharePointAdminUrl { get; set; } = "";

    [JsonIgnore] public string Display => Name.Length > 0 ? Name : Upn.Length > 0 ? Upn : "Microsoft 365";
}

/// <summary>
/// Saved Microsoft 365 PowerShell connections (m365.json). Passwords and sign-in code links are kept in Windows
/// Credential Manager, never in the file. The connect script runs in the docked PowerShell terminal.
/// </summary>
public static class M365
{
    private static readonly List<M365Profile> List = [];
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static string FilePath => AppPaths.File("m365.json");

    public static event Action? Changed;

    public static IReadOnlyList<M365Profile> All
    {
        get { lock (List) return List.ToList(); }
    }

    public static string ServiceName(M365Service s) => s switch
    {
        M365Service.ExchangeOnline => "Exchange Online",
        M365Service.Graph => "Microsoft Graph",
        M365Service.Teams => "Teams",
        M365Service.SharePoint => "SharePoint Online",
        M365Service.Compliance => "Security & Compliance",
        M365Service.Azure => "Azure",
        _ => s.ToString(),
    };

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var items = JsonSerializer.Deserialize<List<M365Profile>>(File.ReadAllText(FilePath), Json) ?? [];
            lock (List)
            {
                List.Clear();
                List.AddRange(items);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("m365 load", ex);
        }
    }

    public static void Save(M365Profile p)
    {
        lock (List)
        {
            var i = List.FindIndex(x => x.Id == p.Id);
            if (i >= 0) List[i] = p;
            else List.Add(p);
        }
        Persist();
        Changed?.Invoke();
    }

    public static void Delete(M365Profile p)
    {
        lock (List) List.RemoveAll(x => x.Id == p.Id);
        CredentialStore.Delete(PasswordKey(p));
        CredentialStore.Delete(OtpKey(p));
        Persist();
        Changed?.Invoke();
    }

    private static void Persist()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(All, Json));
        }
        catch (Exception ex)
        {
            CrashLog.Write("m365 save", ex);
        }
    }

    private static string PasswordKey(M365Profile p) => "m365:" + p.Id;
    private static string OtpKey(M365Profile p) => "m365-otp:" + p.Id;

    public static void SetPassword(M365Profile p, string? password)
    {
        if (string.IsNullOrEmpty(password)) CredentialStore.Delete(PasswordKey(p));
        else CredentialStore.Write(PasswordKey(p), p.Upn, password);
    }

    public static bool HasPassword(M365Profile p) => CredentialStore.Read(PasswordKey(p)) is not null;

    /// <summary>Where the one-time code comes from (authenticator web page, password-manager entry…): a link, not the code.</summary>
    public static void SetOtpLink(M365Profile p, string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) CredentialStore.Delete(OtpKey(p));
        else CredentialStore.Write(OtpKey(p), p.Upn, link.Trim());
    }

    public static string? GetOtpLink(M365Profile p) => CredentialStore.Read(OtpKey(p));

    // ---- Connect script --------------------------------------------------------------------------------------------------

    /// <summary>One terminal step: what the terminal shows, and the line PowerShell runs.</summary>
    public sealed record Step(string Shown, string Command);

    private static string Q(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>
    /// The PowerShell lines that connect every chosen service, each guarded (missing module → how to install it;
    /// failure → the reason) so one service failing doesn't stop the rest.
    /// </summary>
    public static List<Step> ConnectSteps(M365Profile p)
    {
        var steps = new List<Step>();
        var upn = p.Upn.Trim();
        var tenant = p.Tenant.Trim();
        var auth = p.Auth;

        if (auth == M365Auth.Password)
        {
            var password = CredentialStore.Read(PasswordKey(p));
            if (password is null)
            {
                steps.Add(new("# No saved password for this connection — edit it to add one", "Write-Host 'No saved password for this connection. Edit it to add one.'"));
                return steps;
            }
            // The password travels as a Windows-encrypted (DPAPI, this user only) string, the same format
            // ConvertFrom-SecureString writes, so it never appears in plain text on the command line or in history.
            steps.Add(new(
                $"$cred = <saved password for {upn} from Windows Credential Manager>",
                $"$__pxdCred = [pscredential]::new({Q(upn)}, (ConvertTo-SecureString {Q(Dpapi(password))}))"));
        }

        foreach (var s in p.Services)
        {
            var (module, connect) = s switch
            {
                M365Service.ExchangeOnline => ("ExchangeOnlineManagement", auth switch
                {
                    M365Auth.Certificate => $"Connect-ExchangeOnline -CertificateThumbprint {Q(p.CertThumbprint)} -AppId {Q(p.AppId)} -Organization {Q(tenant)} -ShowBanner:$false",
                    M365Auth.DeviceCode => "Connect-ExchangeOnline -Device -ShowBanner:$false",
                    M365Auth.Password => "Connect-ExchangeOnline -Credential $__pxdCred -ShowBanner:$false",
                    _ => $"Connect-ExchangeOnline{Opt("-UserPrincipalName", upn)} -DisableWAM -ShowBanner:$false",
                }),
                M365Service.Compliance => ("ExchangeOnlineManagement", auth switch
                {
                    M365Auth.Certificate => $"Connect-IPPSSession -CertificateThumbprint {Q(p.CertThumbprint)} -AppId {Q(p.AppId)} -Organization {Q(tenant)} -ShowBanner:$false",
                    M365Auth.Password => "Connect-IPPSSession -Credential $__pxdCred -ShowBanner:$false",
                    _ => $"Connect-IPPSSession{Opt("-UserPrincipalName", upn)} -DisableWAM -ShowBanner:$false",
                }),
                M365Service.Graph => ("Microsoft.Graph.Authentication", auth switch
                {
                    M365Auth.Certificate => $"Connect-MgGraph -ClientId {Q(p.AppId)} -CertificateThumbprint {Q(p.CertThumbprint)} -TenantId {Q(tenant)} -NoWelcome",
                    M365Auth.DeviceCode => $"Connect-MgGraph -UseDeviceCode{Scopes(p)}{Opt("-TenantId", tenant)} -NoWelcome",
                    M365Auth.Password => "Write-Host 'Microsoft Graph doesn''t accept saved passwords; use browser sign-in or a certificate.'",
                    _ => $"try {{ Set-MgGraphOption -DisableLoginByWAM $true }} catch {{ }}; Connect-MgGraph{Scopes(p)}{Opt("-TenantId", tenant)} -NoWelcome",
                }),
                M365Service.Teams => ("MicrosoftTeams", auth switch
                {
                    M365Auth.Certificate => $"Connect-MicrosoftTeams -CertificateThumbprint {Q(p.CertThumbprint)} -ApplicationId {Q(p.AppId)} -TenantId {Q(tenant)} | Out-Null",
                    M365Auth.DeviceCode => "Connect-MicrosoftTeams -UseDeviceAuthentication | Out-Null",
                    M365Auth.Password => "Connect-MicrosoftTeams -Credential $__pxdCred | Out-Null",
                    _ => $"Connect-MicrosoftTeams{Opt("-AccountId", upn)}{Opt("-TenantId", tenant)} | Out-Null",
                }),
                M365Service.SharePoint => ("Microsoft.Online.SharePoint.PowerShell", auth switch
                {
                    M365Auth.Certificate => $"Connect-SPOService -Url {Q(p.SharePointAdminUrl)} -ClientId {Q(p.AppId)} -CertificateThumbprint {Q(p.CertThumbprint)} -Tenant {Q(tenant)}",
                    M365Auth.Password => $"Connect-SPOService -Url {Q(p.SharePointAdminUrl)} -Credential $__pxdCred",
                    _ => $"Connect-SPOService -Url {Q(p.SharePointAdminUrl)}",
                }),
                M365Service.Azure => ("Az.Accounts", auth switch
                {
                    M365Auth.Certificate => $"Connect-AzAccount -ServicePrincipal -ApplicationId {Q(p.AppId)} -CertificateThumbprint {Q(p.CertThumbprint)} -Tenant {Q(tenant)} | Out-Null",
                    M365Auth.DeviceCode => $"Connect-AzAccount -UseDeviceAuthentication{Opt("-Tenant", tenant)} | Out-Null",
                    M365Auth.Password => $"Connect-AzAccount -Credential $__pxdCred{Opt("-Tenant", tenant)} | Out-Null",
                    _ => $"try {{ Update-AzConfig -EnableLoginByWam $false -Scope Process | Out-Null }} catch {{ }}; Connect-AzAccount{Opt("-AccountId", upn)}{Opt("-Tenant", tenant)} | Out-Null",
                }),
                _ => ("", ""),
            };
            if (module.Length == 0) continue;
            var name = ServiceName(s);
            // SharePoint's module is Windows PowerShell only; PowerShell 7 loads it through its compatibility layer.
            var import = s == M365Service.SharePoint
                ? $"if ($PSVersionTable.PSEdition -eq 'Core') {{ Import-Module {module} -UseWindowsPowerShell -WarningAction SilentlyContinue }}; "
                : "";
            var line =
                $"if (-not (Get-Module -ListAvailable {module})) {{ Write-Host '✗ {name}: the {module} module isn''t installed. Install it with:  Install-Module {module} -Scope CurrentUser' }} " +
                // Some Connect- cmdlets report failure without throwing; "Stop" makes every failure land in catch.
                $"else {{ $__pxdEap = $ErrorActionPreference; try {{ $ErrorActionPreference = 'Stop'; {import}{connect}; Write-Host '✓ {name} connected' }} " +
                $"catch {{ Write-Host ('✗ {name}: ' + $_.Exception.Message) }} finally {{ $ErrorActionPreference = $__pxdEap }} }}";
            steps.Add(new(connect.Replace("$__pxdCred", "$cred"), line));
        }

        if (auth == M365Auth.Password) steps.Add(new("", "Remove-Variable __pxdCred -ErrorAction SilentlyContinue"));
        return steps;
    }

    private static string Opt(string flag, string value) => value.Length > 0 ? $" {flag} {Q(value)}" : "";

    private static string Scopes(M365Profile p)
    {
        var scopes = p.GraphScopes.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        return scopes.Length == 0 ? "" : " -Scopes " + string.Join(',', scopes.Select(Q));
    }

    // ---- DPAPI: the format ConvertTo-SecureString reads without -Key ----------------------------------------------------

    private static string Dpapi(string secret)
    {
        var plain = Encoding.Unicode.GetBytes(secret);
        var input = new DataBlob { cbData = plain.Length, pbData = Marshal.AllocHGlobal(plain.Length) };
        try
        {
            Marshal.Copy(plain, 0, input.pbData, plain.Length);
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out var output))
                throw new InvalidOperationException("Windows couldn't encrypt the password.");
            try
            {
                var bytes = new byte[output.cbData];
                Marshal.Copy(output.pbData, bytes, 0, output.cbData);
                return Convert.ToHexString(bytes).ToLowerInvariant();
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            Array.Clear(plain);
            Marshal.FreeHGlobal(input.pbData);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);
}
