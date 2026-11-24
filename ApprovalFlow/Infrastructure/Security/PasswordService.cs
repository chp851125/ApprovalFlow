using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Domain.Entities;
using Microsoft.AspNetCore.Identity;

namespace ApprovalFlow.Infrastructure.Security;

/// <summary>以 ASP.NET Core Identity 的 PasswordHasher（PBKDF2）實作密碼雜湊</summary>
public sealed class PasswordService : IPasswordService
{
    private static readonly PasswordHasher<Employee> Hasher = new();
    private static readonly Employee Dummy = new();

    public string Hash(string password) => Hasher.HashPassword(Dummy, password);

    public PasswordCheckResult Verify(string hash, string password)
    {
        if (string.IsNullOrEmpty(hash)) return PasswordCheckResult.Failed;
        try
        {
            return Hasher.VerifyHashedPassword(Dummy, hash, password) switch
            {
                PasswordVerificationResult.Success => PasswordCheckResult.Success,
                PasswordVerificationResult.SuccessRehashNeeded => PasswordCheckResult.SuccessRehashNeeded,
                _ => PasswordCheckResult.Failed
            };
        }
        catch (FormatException)
        {
            return PasswordCheckResult.Failed;
        }
    }
}
