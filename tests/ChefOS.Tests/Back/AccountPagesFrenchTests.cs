namespace ChefOS.Tests.Back;

/// <summary>
/// Les pages de compte (Identity) sont entièrement en français : aucun texte anglais visible ne doit rester dans
/// <c>Components/Account</c>. Les noms de routes, identifiants techniques, messages de journal et exceptions sont exclus.
/// </summary>
public class AccountPagesFrenchTests
{
    private static readonly string[] EnglishPhrases =
    [
        "Manage your account", "Change your account settings", "Change password", "Set your password", "Set password",
        "Two-factor authentication", "Two factor authentication", "two-factor authenticator", "Recovery code", "Recovery Code",
        "recovery codes", "Put these codes in a safe place", "Generate Recovery Codes", "Reset recovery codes",
        "Reset authenticator", "Configure authenticator", "Add authenticator app", "Set up authenticator app", "Verification Code",
        "Verification code", "Authenticator code", "Remember this machine", "Forget this browser", "Disable 2FA", "Disable two-factor",
        "Personal Data", "Personal data", "Delete Personal Data", "Delete data and close", "External logins", "Registered Logins",
        "Add another service", "Remove this", "Log in using", "Your profile has been updated", "Failed to set phone number",
        "Phone number", "Username", "Manage email", "New email", "Change email", "Send verification email",
        "Your email is unchanged", "Confirmation link to change email", "Verification email sent", "Enter a new email",
        "Enter your email", "Enter the old password", "Enter the new password", "Old password", "Update password",
        "Your password has been", "Current password", "Confirm new password", "Confirm password", "do not match", "at least {2} and at max",
        "Confirm email change", "Thank you for confirming", "Error changing", "Unable to find user", "Invalid email change confirmation",
        ">Register<", "Register</", "Registered Logins", "Associate your", "successfully authenticated", "Error from external provider", "Error loading external login",
        "Incorrect password", "Invalid authenticator code", "Invalid recovery code", "Your login is protected",
        "Don't have access", "You have requested to log in", "You have no recovery codes left", "You have 1 recovery code",
        "recovery codes left", "generate a new set", "Privacy and cookie policy", "Authenticator app", "has been forgotten",
        "has been disabled", "has been reset", "has been verified", "You have generated new recovery codes",
        ">Download<", "Deleting this data", "Your account contains personal data", "Learn how to", "enable QR code generation",
        "Scan the QR Code", "Download a two-factor", "The external login was", "Error:", "Profile</", ">Email<", ">Password<",
        "Unable to load user", "Log in</", "Please enter", "Choose your username"
    ];

    [Fact]
    public void NoEnglishLeftInAccountPages()
    {
        Assert.True(EnglishPhrases.Length >= 40);

        var folder = Path.Combine(FindSolutionRoot(), "src", "ChefOS.Back", "Components", "Account");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".razor") || f.EndsWith(".cs")))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // Journaux, exceptions et commentaires ne sont pas visibles par l'utilisateur.
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//") || line.Contains("Logger.Log") || line.Contains("throw new") || line.Contains("LogInformation")
                    || trimmed.StartsWith("\"{Name}") || trimmed.StartsWith("$\"Ensure that") || trimmed.StartsWith("\"Ensure"))
                    continue;

                foreach (var phrase in EnglishPhrases)
                    if (line.Contains(phrase, StringComparison.Ordinal))
                        offenders.Add($"{Path.GetRelativePath(folder, file)}:{i + 1} « {phrase} » : {trimmed}");
            }
        }

        Assert.True(offenders.Count == 0, "Textes anglais restants :\n" + string.Join("\n", offenders));
    }

    private static string FindSolutionRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (dir.EnumerateFiles().Any(f => string.Equals(f.Name, "ChefOS.sln", StringComparison.OrdinalIgnoreCase)))
                return dir.FullName;

        throw new InvalidOperationException("ChefOS.sln introuvable en remontant depuis " + AppContext.BaseDirectory);
    }
}
