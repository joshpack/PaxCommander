using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaxCommander.Core;

/// <summary>One suggestion for the terminal: the command, a short description, and where it came from.</summary>
public sealed record CommandSuggestion(string Command, string Description, string Source);

/// <summary>
/// What the terminal suggests as you type: your saved snippets, the commands you've run before (most used first),
/// and a built-in library of common admin commands for whatever you're connected to (Exchange Online, Graph, …).
/// History and snippets live in terminal-commands.json; lines that look like they carry a password are never kept.
/// </summary>
public static partial class CommandLibrary
{
    /// <summary>Contexts a terminal can be in: "powershell", "ssh", or a connected Microsoft 365 service.</summary>
    public static readonly Dictionary<string, (string Title, (string Command, string Description)[] Commands)> Library = new()
    {
        ["ExchangeOnline"] = ("Exchange Online",
        [
            ("Get-EXOMailbox -Identity <user@domain>", "A mailbox's details"),
            ("Get-EXOMailbox -ResultSize Unlimited | Select DisplayName,PrimarySmtpAddress,RecipientTypeDetails", "All mailboxes"),
            ("Get-EXOMailboxStatistics -Identity <user@domain> | Select DisplayName,TotalItemSize,ItemCount", "Mailbox size"),
            ("Get-EXOMailboxPermission -Identity <mailbox>", "Who has access to a mailbox"),
            ("Add-MailboxPermission -Identity <mailbox> -User <user@domain> -AccessRights FullAccess -AutoMapping $true", "Give full access"),
            ("Remove-MailboxPermission -Identity <mailbox> -User <user@domain> -AccessRights FullAccess -Confirm:$false", "Remove full access"),
            ("Add-RecipientPermission -Identity <mailbox> -Trustee <user@domain> -AccessRights SendAs -Confirm:$false", "Give Send As"),
            ("Set-Mailbox -Identity <mailbox> -GrantSendOnBehalfTo @{Add='<user@domain>'}", "Give Send on Behalf"),
            ("Set-Mailbox -Identity <user@domain> -Type Shared", "Convert to shared mailbox"),
            ("Set-Mailbox -Identity <user@domain> -ForwardingSmtpAddress <target@domain> -DeliverToMailboxAndForward $true", "Forward mail"),
            ("Set-MailboxAutoReplyConfiguration -Identity <user@domain> -AutoReplyState Enabled -InternalMessage '<message>' -ExternalMessage '<message>'", "Out of office on"),
            ("Get-MailboxAutoReplyConfiguration -Identity <user@domain>", "Out of office status"),
            ("Get-DistributionGroupMember -Identity <group>", "Members of a distribution group"),
            ("Add-DistributionGroupMember -Identity <group> -Member <user@domain>", "Add to a distribution group"),
            ("Get-InboxRule -Mailbox <user@domain>", "Inbox rules (check for odd forwarding)"),
            ("Get-MessageTraceV2 -SenderAddress <sender@domain> -StartDate (Get-Date).AddDays(-2) -EndDate (Get-Date)", "Message trace"),
            ("Get-CASMailbox -Identity <user@domain> | Select *Enabled", "Mail protocols on/off"),
            ("Get-EXOMailbox -Identity <user@domain> | Select -Expand EmailAddresses", "Every address of a mailbox"),
            ("Disconnect-ExchangeOnline -Confirm:$false", "Disconnect"),
        ]),
        ["Graph"] = ("Microsoft Graph",
        [
            ("Get-MgUser -UserId <user@domain> | Select DisplayName,UserPrincipalName,AccountEnabled", "A user"),
            ("Get-MgUser -All -Property DisplayName,UserPrincipalName,AccountEnabled | Select DisplayName,UserPrincipalName,AccountEnabled", "All users"),
            ("Get-MgUserLicenseDetail -UserId <user@domain> | Select SkuPartNumber", "A user's licenses"),
            ("Get-MgSubscribedSku | Select SkuPartNumber,ConsumedUnits,@{n='Total';e={$_.PrepaidUnits.Enabled}}", "Licenses in the tenant"),
            ("Update-MgUser -UserId <user@domain> -AccountEnabled:$false", "Block sign-in"),
            ("Revoke-MgUserSignInSession -UserId <user@domain>", "Sign the user out everywhere"),
            ("Get-MgUserMemberOf -UserId <user@domain> | Select -Expand AdditionalProperties", "A user's groups"),
            ("Get-MgGroup -Filter \"displayName eq '<group>'\"", "Find a group"),
            ("Get-MgGroupMember -GroupId <group-id>", "Group members"),
            ("New-MgGroupMember -GroupId <group-id> -DirectoryObjectId <user-id>", "Add to a group"),
            ("Get-MgUserAuthenticationMethod -UserId <user@domain>", "A user's MFA methods"),
            ("Get-MgAuditLogSignIn -Filter \"userPrincipalName eq '<user@domain>'\" -Top 20", "Recent sign-ins"),
            ("Get-MgContext", "Who and what Graph is connected as"),
            ("Disconnect-MgGraph", "Disconnect"),
        ]),
        ["Teams"] = ("Teams",
        [
            ("Get-Team | Select DisplayName,GroupId,Visibility", "All teams"),
            ("Get-TeamUser -GroupId <group-id>", "Team members"),
            ("Add-TeamUser -GroupId <group-id> -User <user@domain> -Role Member", "Add to a team"),
            ("Get-CsOnlineUser -Identity <user@domain> | Select DisplayName,LineURI,EnterpriseVoiceEnabled", "Voice settings"),
            ("Set-CsPhoneNumberAssignment -Identity <user@domain> -PhoneNumber <+15555550100> -PhoneNumberType DirectRouting", "Assign a phone number"),
            ("Get-CsTeamsMeetingPolicy | Select Identity", "Meeting policies"),
            ("Disconnect-MicrosoftTeams", "Disconnect"),
        ]),
        ["SharePoint"] = ("SharePoint Online",
        [
            ("Get-SPOSite -Limit All | Select Url,Owner,StorageUsageCurrent", "All sites"),
            ("Get-SPOSite -Identity <site-url> | Select *", "A site's settings"),
            ("Set-SPOUser -Site <site-url> -LoginName <user@domain> -IsSiteCollectionAdmin $true", "Make site admin"),
            ("Get-SPOUser -Site <site-url> -Limit All", "Site users"),
            ("Set-SPOSite -Identity <site-url> -SharingCapability ExternalUserSharingOnly", "External sharing"),
            ("Request-SPOPersonalSite -UserEmails <user@domain>", "Create a OneDrive"),
            ("Disconnect-SPOService", "Disconnect"),
        ]),
        ["Compliance"] = ("Security & Compliance",
        [
            ("New-ComplianceSearch -Name '<name>' -ExchangeLocation All -ContentMatchQuery '<query>'", "New content search"),
            ("Start-ComplianceSearch -Identity '<name>'", "Run a content search"),
            ("Get-ComplianceSearch -Identity '<name>' | Select Name,Status,Items", "Search status"),
            ("New-ComplianceSearchAction -SearchName '<name>' -Purge -PurgeType SoftDelete", "Purge what a search found"),
            ("Get-RetentionCompliancePolicy", "Retention policies"),
            ("Get-DlpCompliancePolicy", "DLP policies"),
        ]),
        ["Azure"] = ("Azure",
        [
            ("Get-AzContext", "Current subscription and account"),
            ("Get-AzSubscription", "Subscriptions"),
            ("Set-AzContext -Subscription <name-or-id>", "Switch subscription"),
            ("Get-AzResourceGroup | Select ResourceGroupName,Location", "Resource groups"),
            ("Get-AzVM -Status | Select Name,ResourceGroupName,PowerState", "Virtual machines"),
            ("Start-AzVM -ResourceGroupName <rg> -Name <vm>", "Start a VM"),
            ("Stop-AzVM -ResourceGroupName <rg> -Name <vm> -Force", "Stop a VM"),
        ]),
        ["powershell"] = ("PowerShell",
        [
            ("Get-ChildItem -Recurse -Filter <*.log> | Sort Length -Descending | Select -First 20 FullName,Length", "Biggest matching files"),
            ("Get-Process | Sort CPU -Descending | Select -First 15", "Busiest processes"),
            ("Get-Service | Where Status -eq Running", "Running services"),
            ("Test-NetConnection <host> -Port <443>", "Can I reach a host and port?"),
            ("Resolve-DnsName <name>", "DNS lookup"),
            ("Get-NetIPAddress -AddressFamily IPv4 | Select InterfaceAlias,IPAddress", "IP addresses"),
            ("Get-FileHash <file> -Algorithm SHA256", "File checksum"),
            ("Get-Module -ListAvailable | Sort Name | Select Name,Version", "Installed modules"),
            ("Install-Module <ExchangeOnlineManagement> -Scope CurrentUser", "Install a module"),
            ("Get-History", "This session's commands"),
        ]),
        ["ssh"] = ("Linux",
        [
            ("df -h", "Disk space"),
            ("du -sh * | sort -h", "Folder sizes here"),
            ("free -h", "Memory"),
            ("top -b -n 1 | head -20", "Busiest processes"),
            ("systemctl status <service>", "Service status"),
            ("sudo systemctl restart <service>", "Restart a service"),
            ("journalctl -u <service> -n 100 --no-pager", "Service log"),
            ("tail -n 100 <file>", "End of a file"),
            ("grep -rn '<text>' .", "Find text in files"),
            ("ip -br addr", "IP addresses"),
            ("ss -tulpn", "Listening ports"),
            ("uptime", "Uptime and load"),
        ]),
    };

    /// <summary>Which library context a typed or run command switches on, e.g. Connect-ExchangeOnline.</summary>
    public static string? ContextForConnect(string line)
    {
        var l = line.TrimStart();
        if (l.StartsWith("Connect-ExchangeOnline", StringComparison.OrdinalIgnoreCase)) return "ExchangeOnline";
        if (l.StartsWith("Connect-IPPSSession", StringComparison.OrdinalIgnoreCase)) return "Compliance";
        if (l.StartsWith("Connect-MgGraph", StringComparison.OrdinalIgnoreCase)) return "Graph";
        if (l.StartsWith("Connect-MicrosoftTeams", StringComparison.OrdinalIgnoreCase)) return "Teams";
        if (l.StartsWith("Connect-SPOService", StringComparison.OrdinalIgnoreCase)) return "SharePoint";
        if (l.StartsWith("Connect-AzAccount", StringComparison.OrdinalIgnoreCase)) return "Azure";
        return null;
    }

    // ---- Remembered history and snippets ------------------------------------------------------------------------------

    private sealed class Entry
    {
        public string Text { get; set; } = "";
        public int Count { get; set; }
        public DateTime Last { get; set; }
    }

    private sealed class Store
    {
        public Dictionary<string, List<Entry>> History { get; set; } = [];
        public List<string> Snippets { get; set; } = [];
    }

    private static Store? _store;
    private static readonly object Gate = new();
    private static string FilePath => AppPaths.File("terminal-commands.json");

    private static Store Data
    {
        get
        {
            lock (Gate)
            {
                if (_store is not null) return _store;
                try
                {
                    _store = File.Exists(FilePath) ? JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath)) : null;
                }
                catch (Exception ex)
                {
                    CrashLog.Write("terminal commands load", ex);
                }
                return _store ??= new Store();
            }
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            string json;
            lock (Gate) json = JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            CrashLog.Write("terminal commands save", ex);
        }
    }

    /// <summary>Anything that looks like it carries a secret is never written down.</summary>
    public static bool LooksSecret(string line) => SecretPattern().IsMatch(line);

    [GeneratedRegex(@"(?i)(pass(word|wd)?|secret|token|api[-_]?key|client[-_]?secret)\s*[=:]|-(pass(word)?|secret|token|api[-_]?key|client[-_]?secret|pat)\b|-AsPlainText|ConvertTo-SecureString|\$__pax")]
    private static partial Regex SecretPattern();

    /// <summary>Remembers a command run in a terminal of this kind ("powershell" or "ssh").</summary>
    public static void Remember(string kind, string line)
    {
        line = line.Trim();
        if (line.Length < 2 || LooksSecret(line)) return;
        lock (Gate)
        {
            if (!Data.History.TryGetValue(kind, out var list)) Data.History[kind] = list = [];
            var e = list.FirstOrDefault(x => x.Text == line);
            if (e is null) list.Add(e = new Entry { Text = line });
            e.Count++;
            e.Last = DateTime.UtcNow;
            if (list.Count > 600) list.RemoveAll(x => x != e && list.IndexOf(x) < list.Count - 500 && x.Count < 3);
        }
        Save();
    }

    /// <summary>Past commands, oldest first (for Up/Down).</summary>
    public static List<string> History(string kind)
    {
        lock (Gate) return Data.History.TryGetValue(kind, out var list) ? list.OrderBy(x => x.Last).Select(x => x.Text).ToList() : [];
    }

    public static IReadOnlyList<string> Snippets
    {
        get { lock (Gate) return Data.Snippets.ToList(); }
    }

    public static void AddSnippet(string command)
    {
        command = command.Trim();
        if (command.Length == 0) return;
        lock (Gate)
        {
            Data.Snippets.Remove(command);
            Data.Snippets.Insert(0, command);
        }
        Save();
    }

    public static void RemoveSnippet(string command)
    {
        lock (Gate) Data.Snippets.Remove(command);
        Save();
    }

    public static void ClearHistory(string kind)
    {
        lock (Gate) Data.History.Remove(kind);
        Save();
    }

    /// <summary>
    /// Suggestions for what's typed so far: snippets, then your history (frequent and recent first), then the library
    /// for the connected services. Starts-with matches rank above matches anywhere in the line.
    /// </summary>
    public static List<CommandSuggestion> Suggest(string typed, string kind, IEnumerable<string> contexts, int max = 8)
    {
        typed = typed.Trim();
        if (typed.Length < 2) return [];
        var all = new List<(CommandSuggestion S, double Score)>();
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            foreach (var s in Data.Snippets) all.Add((new(s, "Saved snippet", "★"), 1000));
            if (Data.History.TryGetValue(kind, out var list))
                foreach (var e in list)
                    all.Add((new(e.Text, e.Count > 1 ? $"Run {e.Count} times" : "Run before", "history"), 100 + e.Count * 4 - Math.Min(60, (now - e.Last).TotalDays)));
        }
        foreach (var ctx in contexts.Append(kind).Distinct())
            if (Library.TryGetValue(ctx, out var lib))
                foreach (var (cmd, desc) in lib.Commands)
                    all.Add((new(cmd, desc, lib.Title), 10));

        return all
            .Select(x => (x.S, Score: Match(x.S, typed) is var m and > 0 ? x.Score + m : -1))
            .Where(x => x.Score > 0 && !string.Equals(x.S.Command, typed, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Score)
            .DistinctBy(x => x.S.Command)
            .Take(max)
            .Select(x => x.S)
            .ToList();
    }

    private static double Match(CommandSuggestion s, string typed)
    {
        if (s.Command.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return 500;
        if (s.Command.Contains(typed, StringComparison.OrdinalIgnoreCase)) return 200;
        // Every word typed appears somewhere (command or description): "mailbox perm" finds Get-EXOMailboxPermission.
        var words = typed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.All(w => s.Command.Contains(w, StringComparison.OrdinalIgnoreCase) || s.Description.Contains(w, StringComparison.OrdinalIgnoreCase)) ? 50 : 0;
    }
}
