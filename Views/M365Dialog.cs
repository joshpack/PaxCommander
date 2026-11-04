using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PaxCommander.Core;

namespace PaxCommander.Views;

/// <summary>
/// Add or edit a saved Microsoft 365 admin PowerShell connection. Passwords and the sign-in code link go to Windows
/// Credential Manager; nothing secret is written to PAX Commander's own files.
/// </summary>
public static class M365Dialog
{
    private static readonly (string Label, M365Auth Auth)[] Methods =
    [
        ("Browser sign-in (MFA, one-time codes, passkeys)", M365Auth.Browser),
        ("Device code (sign in from any device)", M365Auth.DeviceCode),
        ("Certificate (app-only, no prompts)", M365Auth.Certificate),
        ("Saved password (accounts without MFA)", M365Auth.Password),
    ];

    public static Task<M365Profile?> ShowAsync(M365Profile? existing) => Dialogs.ShowAsync<M365Profile?>(tcs =>
    {
        var editing = existing is not null && M365.All.Any(p => p.Id == existing.Id);
        var p = existing ?? new M365Profile();
        var hasPassword = editing && M365.HasPassword(p);
        var savedLink = editing ? M365.GetOtpLink(p) : null;

        var name = new TextBox { Text = p.Name, PlaceholderText = "Contoso" };
        var upn = new TextBox { Text = p.Upn, PlaceholderText = "admin@contoso.com" };
        var tenant = new TextBox { Text = p.Tenant, PlaceholderText = "contoso.onmicrosoft.com (needed for certificates)" };
        var services = Enum.GetValues<M365Service>().ToDictionary(s => s, s => new CheckBox { Content = M365.ServiceName(s), IsChecked = p.Services.Contains(s), MinWidth = 190 });
        var method = new ComboBox { ItemsSource = Methods.Select(m => m.Label).ToList(), SelectedIndex = Math.Max(0, Array.FindIndex(Methods, m => m.Auth == p.Auth)), HorizontalAlignment = HorizontalAlignment.Stretch };
        var appId = new TextBox { Text = p.AppId, PlaceholderText = "Application (client) ID" };
        var thumb = new TextBox { Text = p.CertThumbprint, PlaceholderText = "Certificate thumbprint (CurrentUser\\My)" };
        var password = new TextBox { PasswordChar = '●', PlaceholderText = hasPassword ? "•••••• (saved — leave empty to keep)" : "password" };
        var scopes = new TextBox { Text = p.GraphScopes, PlaceholderText = "User.Read.All Group.Read.All" };
        var spoUrl = new TextBox { Text = p.SharePointAdminUrl, PlaceholderText = "https://contoso-admin.sharepoint.com" };
        var otp = new TextBox { Text = savedLink ?? "", PlaceholderText = "https://… (your authenticator page or password-manager entry)" };
        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };

        var certBlock = new StackPanel { Spacing = 10, Children = { Field("APP ID", appId), Field("CERTIFICATE THUMBPRINT", thumb) } };
        var passwordBlock = Field("PASSWORD", password);
        var scopesBlock = Field("GRAPH SCOPES", scopes);
        var spoBlock = Field("SHAREPOINT ADMIN URL", spoUrl);
        var methodHint = new TextBlock { FontSize = 11.5, Foreground = Skin.Faint, TextWrapping = TextWrapping.Wrap };

        void Refresh()
        {
            var auth = Methods[Math.Max(0, method.SelectedIndex)].Auth;
            certBlock.IsVisible = auth == M365Auth.Certificate;
            passwordBlock.IsVisible = auth == M365Auth.Password;
            scopesBlock.IsVisible = services[M365Service.Graph].IsChecked == true && auth != M365Auth.Certificate;
            spoBlock.IsVisible = services[M365Service.SharePoint].IsChecked == true;
            methodHint.Text = auth switch
            {
                M365Auth.Browser => "Each service opens your browser to sign in. When Windows is signed in to the same account it's usually one click; otherwise you'll be asked for your one-time code.",
                M365Auth.DeviceCode => "The terminal shows a code to enter at microsoft.com/devicelogin, on this PC or your phone. SharePoint and Security & Compliance fall back to the browser.",
                M365Auth.Certificate => "Unattended app-only sign-in with an app registration and a certificate in your Windows certificate store. Nothing to type, nothing secret saved here.",
                _ => "Only works for accounts without MFA (Microsoft blocks saved passwords on MFA accounts). Stored encrypted in Windows Credential Manager; Microsoft Graph doesn't support it.",
            };
        }
        method.SelectionChanged += (_, _) => Refresh();
        foreach (var box in services.Values) box.IsCheckedChanged += (_, _) => Refresh();
        Refresh();

        var save = Dialogs.Btn(editing ? "Save" : "Save and connect", "primary", () =>
        {
            var chosen = services.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
            var auth = Methods[Math.Max(0, method.SelectedIndex)].Auth;
            string? error = null;
            if (chosen.Count == 0) error = "Pick at least one service.";
            else if (auth != M365Auth.Certificate && (upn.Text ?? "").Trim().Length == 0 && auth == M365Auth.Password) error = "Enter the account to sign in as.";
            else if (auth == M365Auth.Certificate && ((appId.Text ?? "").Trim().Length == 0 || (thumb.Text ?? "").Trim().Length == 0 || (tenant.Text ?? "").Trim().Length == 0))
                error = "Certificate sign-in needs the tenant, app ID and certificate thumbprint.";
            else if (auth == M365Auth.Password && !hasPassword && string.IsNullOrEmpty(password.Text)) error = "Enter the password to save.";
            else if (chosen.Contains(M365Service.SharePoint) && !(spoUrl.Text ?? "").Trim().StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                error = "Enter the SharePoint admin URL (https://yourtenant-admin.sharepoint.com).";
            var link = (otp.Text ?? "").Trim();
            if (error is null && link.Length > 0 && !Uri.TryCreate(link, UriKind.Absolute, out _)) error = "The sign-in code link must be a full link, like https://…";
            if (error is not null)
            {
                status.Text = error;
                status.Foreground = Skin.Bad;
                return;
            }

            var result = new M365Profile
            {
                Id = p.Id,
                Name = (name.Text ?? "").Trim(),
                Upn = (upn.Text ?? "").Trim(),
                Tenant = (tenant.Text ?? "").Trim(),
                Services = chosen,
                Auth = auth,
                AppId = (appId.Text ?? "").Trim(),
                CertThumbprint = (thumb.Text ?? "").Trim().Replace(" ", ""),
                GraphScopes = (scopes.Text ?? "").Trim(),
                SharePointAdminUrl = (spoUrl.Text ?? "").Trim().TrimEnd('/'),
            };
            M365.Save(result);
            if (auth == M365Auth.Password) { if (!string.IsNullOrEmpty(password.Text)) M365.SetPassword(result, password.Text); }
            else M365.SetPassword(result, null);
            M365.SetOtpLink(result, link);
            tcs.TrySetResult(result);
        });

        var serviceGrid = new WrapPanel();
        foreach (var box in services.Values) serviceGrid.Children.Add(box);

        return new ScrollViewer
        {
            MaxHeight = 700,
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    Dialogs.Title(editing ? $"Edit {p.Display}" : "New Microsoft 365 connection"),
                    Field("NAME", name),
                    Field("ACCOUNT", upn),
                    Field("TENANT", tenant),
                    Field("SERVICES", serviceGrid),
                    Field("SIGN-IN", method),
                    methodHint,
                    certBlock,
                    passwordBlock,
                    scopesBlock,
                    spoBlock,
                    Field("SIGN-IN CODE LINK", otp),
                    new TextBlock
                    {
                        Text = "Optional: a link to wherever you get your one-time code. PAX Commander only opens the link (a button in the terminal while you sign in); it never reads or stores the codes. The link is kept in Windows Credential Manager.",
                        FontSize = 11.5,
                        Foreground = Skin.Faint,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    status,
                    Dialogs.Buttons(Dialogs.Btn("Cancel", null, () => tcs.TrySetResult(null)), save),
                },
            },
        };
    }, null, width: 560);

    private static StackPanel Field(string label, Control input) => new()
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, Classes = { "label" } }, input },
    };
}
