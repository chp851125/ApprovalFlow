namespace ApprovalFlow.Domain.Constants;

/// <summary>系統參數鍵值。實際值存放於 systemSettings 資料。</summary>
public static class SettingKeys
{
    public const string CompanyName = "Company.Name";

    public const string PasswordMinLength = "Password.MinLength";
    public const string PasswordRequireDigit = "Password.RequireDigit";
    public const string PasswordRequireUpper = "Password.RequireUppercase";
    public const string PasswordRequireLower = "Password.RequireLowercase";
    public const string PasswordRequireSymbol = "Password.RequireSymbol";

    public const string LockoutThreshold = "Lockout.MaxFailedAttempts";
    public const string LockoutMinutes = "Lockout.Minutes";

    public const string UploadMaxSizeMb = "Upload.MaxSizeMB";
    public const string UploadAllowedExtensions = "Upload.AllowedExtensions";
    public const string UploadMaxFilesPerDocument = "Upload.MaxFilesPerDocument";

    public const string BatchApproveEnabled = "BatchApprove.Enabled";

    public const string SlaDefaultHours = "Sla.DefaultHours";
    public const string SlaReminderBeforeHours = "Sla.ReminderBeforeHours";
    public const string SlaAutoEscalate = "Sla.AutoEscalate";
    public const string SlaEscalateAfterHours = "Sla.EscalateAfterOverdueHours";

    public const string DigestEnabled = "Digest.Enabled";
    public const string DigestHour = "Digest.Hour";

    public const string EmailEnabled = "Email.Enabled";
    public const string EmailFromAddress = "Email.FromAddress";
    public const string SiteBaseUrl = "Site.BaseUrl";
}
